using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class RuntimeTests
{
    private sealed class Lease : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }
    private sealed class Engine(List<string> events) : IRouteEngine
    {
        public readonly TaskCompletionSource<Exception?> End = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Exception?> Completion => End.Task;
        public RouteSharedPolicy? Routes;
        public int Excluded;
        public bool Reject;
        public bool ThrowOnDispose;
        public Action? OnCommit;
        public Task ApplyAsync(RouteSharedPolicy routes, IEnumerable<int> excludedProcesses, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Reject) { throw new IOException("apply failed"); }
            Routes = routes;
            Excluded = excludedProcesses.Single();
            events.Add("apply");
            OnCommit?.Invoke();
            return Task.CompletedTask;
        }
        public void Start() => events.Add("start");
        public ValueTask DisposeAsync()
        {
            events.Add("stop"); End.TrySetResult(null);
            if (ThrowOnDispose) { throw new IOException("engine cleanup failed"); }
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Profile(string name, int port, List<string> events) : IRouteProfile
    {
        public readonly TaskCompletionSource End = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => End.Task;
        public RouteSocksEndpoint Endpoint { get; } = new(port);
        public RouteSharedPolicy SharedPolicy { get; } = RouteTestFactory.Policy();
        public int ProcessId => port;
        public bool Disposed;
        public bool ThrowOnDispose;
        public ValueTask DisposeAsync()
        {
            Disposed = true; events.Add("dispose " + name); End.TrySetResult();
            if (ThrowOnDispose) { throw new IOException("core cleanup failed"); }
            return ValueTask.CompletedTask;
        }
    }
    private static RouteProfilePlan Plan(string key, Func<CancellationToken, Task<IRouteProfile>> start) => new(key, start);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NoCaptureSelectorsReleaseAnExistingRuntimeAndNeverStartANewOne(bool running)
    {
        var events = new List<string>();
        var core = new Profile("core", 10001, events);
        var lease = new Lease();
        var runtime = new RouteRuntime(() => new Engine(events), () => lease);
        if (running) { await runtime.ApplyAsync(Plan("one", _ => Task.FromResult<IRouteProfile>(core)), default); }
        await runtime.ApplyAsync(null, default);
        await runtime.IsRunning.Should().BeFalse();
        await core.Disposed.Should().BeEqualTo(running);
        await lease.Disposed.Should().BeEqualTo(running);
        if (!running) { await events.Count.Should().BeEqualTo(0); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CleanupFailureStillReleasesCoreAndCaptureLease(bool engineFailure)
    {
        var events = new List<string>();
        var engine = new Engine(events) { ThrowOnDispose = engineFailure };
        var core = new Profile("core", 10001, events) { ThrowOnDispose = !engineFailure };
        var lease = new Lease();
        var runtime = new RouteRuntime(() => engine, () => lease);
        await runtime.ApplyAsync(Plan("one", _ => Task.FromResult<IRouteProfile>(core)), default);
        await Assert.ThrowsAsync<IOException>(() => runtime.StopAsync());
        await core.Disposed.Should().BeTrue();
        await lease.Disposed.Should().BeTrue();
        await runtime.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task CancellationAtCommitKeepsNewResourcesUntilExplicitStop()
    {
        var events = new List<string>();
        using var stop = new CancellationTokenSource();
        var engine = new Engine(events) { OnCommit = stop.Cancel };
        var profile = new Profile("core", 10001, events);
        var lease = new Lease();
        var runtime = new RouteRuntime(() => engine, () => lease);
        try
        {
            await runtime.ApplyAsync(Plan("one", _ => Task.FromResult<IRouteProfile>(profile)), stop.Token);
            await stop.IsCancellationRequested.Should().BeTrue();
            await runtime.IsRunning.Should().BeTrue();
            await profile.Disposed.Should().BeFalse();
            await lease.Disposed.Should().BeFalse();
        }
        finally { await runtime.StopAsync(); }
        await profile.Disposed.Should().BeTrue();
        await lease.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task ReplacementPreparesWhileOldPolicyRunsAndReusesUnchangedCore()
    {
        var events = new List<string>();
        var engine = new Engine(events);
        var lease = new Lease();
        var runtime = new RouteRuntime(() => engine, () => lease);
        var old = new Profile("old", 10001, events);
        var next = new Profile("next", 10002, events);
        var ready = new TaskCompletionSource<IRouteProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await runtime.ApplyAsync(Plan("one", _ => Task.FromResult<IRouteProfile>(old)), default);
            await runtime.ApplyAsync(Plan("one", _ => throw new Exception("must reuse")), default);
            var apply = runtime.ApplyAsync(Plan("two", _ => ready.Task), default);
            await runtime.IsRunning.Should().BeTrue();
            await engine.Excluded.Should().BeEqualTo(10001);
            await engine.Routes.Should().BeEqualTo(old.SharedPolicy);
            await old.Disposed.Should().BeFalse();
            ready.SetResult(next);
            await apply;
            await engine.Excluded.Should().BeEqualTo(10002);
            await engine.Routes.Should().BeEqualTo(next.SharedPolicy);
            await events.SequenceEqual(new[] { "apply", "start", "apply", "apply", "dispose old" }).Should().BeTrue();
            await lease.Disposed.Should().BeFalse();
        }
        finally { ready.TrySetResult(next); await runtime.StopAsync(); }
        await lease.Disposed.Should().BeTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedReplacementLeavesOldResourcesAndPolicyIntact(bool applyFailure)
    {
        var events = new List<string>();
        var engine = new Engine(events);
        var lease = new Lease();
        var runtime = new RouteRuntime(() => engine, () => lease);
        var old = new Profile("old", 10001, events);
        var candidate = new Profile("candidate", 10002, events);
        try
        {
            await runtime.ApplyAsync(Plan("old", _ => Task.FromResult<IRouteProfile>(old)), default);
            engine.Reject = applyFailure;
            var failed = false;
            try
            {
                await runtime.ApplyAsync(Plan("new", _ => applyFailure ? Task.FromResult<IRouteProfile>(candidate) : throw new IOException("startup failed")), default);
            }
            catch (IOException) { failed = true; }
            await failed.Should().BeTrue();
            await runtime.IsRunning.Should().BeTrue();
            await old.Disposed.Should().BeFalse();
            await lease.Disposed.Should().BeFalse();
            await candidate.Disposed.Should().BeEqualTo(applyFailure);
            await engine.Excluded.Should().BeEqualTo(10001);
        }
        finally { await runtime.StopAsync(); }
    }

    [Test]
    public async Task CancelledInitialPreparationReleasesLeaseWithoutStartingCapture()
    {
        var events = new List<string>();
        var lease = new Lease();
        var runtime = new RouteRuntime(() => new Engine(events), () => lease);
        using var stop = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = runtime.ApplyAsync(Plan("slow", async token =>
        {
            waiting.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new Exception("unreachable");
        }), stop.Token);
        await waiting.Task;
        stop.Cancel();
        var cancelled = false;
        try { await start; } catch (OperationCanceledException) { cancelled = true; }
        await cancelled.Should().BeTrue();
        await events.Count.Should().BeEqualTo(0);
        await lease.Disposed.Should().BeTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BothCoreExitAndEngineFailureAreObservable(bool coreExit)
    {
        var events = new List<string>();
        var engine = new Engine(events);
        var profile = new Profile("core", 10001, events);
        var runtime = new RouteRuntime(() => engine, () => new Lease());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await runtime.ApplyAsync(Plan("one", _ => Task.FromResult<IRouteProfile>(profile)), timeout.Token);
            var failure = runtime.WaitForFailureAsync(timeout.Token);
            if (coreExit) { profile.End.SetResult(); } else { engine.End.SetResult(new IOException("capture failed")); }
            await ((await failure) is IOException).Should().BeTrue();
        }
        finally { await runtime.StopAsync(); }
    }

    [Test]
    public async Task CaptureLeaseIsExclusiveAndCanBeReleasedOnAnotherThread()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var name = @"Global\v2rayN.ApplicationRouting.Test." + Guid.NewGuid().ToString("N");
        var first = new RouteCaptureLease(name);
        try
        {
            var rejected = false;
            try { using var second = new RouteCaptureLease(name); }
            catch (InvalidOperationException) { rejected = true; }
            await rejected.Should().BeTrue();
        }
        finally { await Task.Run(first.Dispose); }
        using var next = new RouteCaptureLease(name);
    }
}
