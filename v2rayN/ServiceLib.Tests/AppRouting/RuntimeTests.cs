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
            if (End.Task.IsCompleted) { throw new OperationCanceledException("engine stopped"); }
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
    public async Task RetiredCoreCleanupFailureDoesNotRejectACommittedReplacement()
    {
        var events = new List<string>();
        var engine = new Engine(events);
        var runtime = new RouteRuntime(() => engine, () => new Lease());
        var old = new Profile("old", 10001, events) { ThrowOnDispose = true };
        var next = new Profile("next", 10002, events);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await runtime.ApplyAsync(Plan("old", _ => Task.FromResult<IRouteProfile>(old)), timeout.Token);
            await runtime.ApplyAsync(Plan("new", _ => Task.FromResult<IRouteProfile>(next)), timeout.Token);
            await old.Disposed.Should().BeTrue();
            await next.Disposed.Should().BeFalse();
            await engine.Excluded.Should().BeEqualTo(next.ProcessId);
            // A successful return lets the manager replace the old supervisor.
            // Its failure wait must now observe the replacement core.
            var failure = runtime.WaitForFailureAsync(timeout.Token);
            await failure.IsCompleted.Should().BeFalse();
            next.End.SetResult();
            await ((await failure) is IOException).Should().BeTrue();
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedRecoveryRetainsThePlanAndASecondAttemptCanStart(bool cancelled)
    {
        var events = new List<string>();
        var engines = new List<Engine>();
        var cores = new List<Profile>();
        var leases = new List<Lease>();
        var runtime = new RouteRuntime(() =>
        {
            var engine = new Engine(events);
            engines.Add(engine);
            return engine;
        }, () => { var lease = new Lease(); leases.Add(lease); return lease; });
        var attempts = 0;
        var plan = Plan("one", token =>
        {
            if (++attempts == 2 && !cancelled) { throw new IOException("temporarily unavailable"); }
            var core = new Profile("core", 10000 + attempts, events);
            cores.Add(core);
            return Task.FromResult<IRouteProfile>(core);
        });
        try
        {
            await runtime.ApplyAsync(plan, default);
            engines[0].End.SetResult(new IOException("socket history failed"));
            await (await runtime.WaitForFailureAsync(default) is IOException).Should().BeTrue();
            using var cancel = new CancellationTokenSource();
            if (cancelled) { cancel.Cancel(); }
            if (cancelled) { await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.RestartAsync(cancel.Token)); }
            else { await Assert.ThrowsAsync<IOException>(() => runtime.RestartAsync(default)); }
            await runtime.IsRunning.Should().BeFalse();
            await cores.All(core => core.Disposed).Should().BeTrue();
            await leases.All(lease => lease.Disposed).Should().BeTrue();
            await runtime.RestartAsync(default);
            await runtime.IsRunning.Should().BeTrue();
            await engines.Count.Should().BeEqualTo(2);
            await engines[1].Completion.IsCompleted.Should().BeFalse();
            await cores.Last().Disposed.Should().BeFalse();
        }
        finally { await runtime.StopAsync(); }
        await cores.All(core => core.Disposed).Should().BeTrue();
        await leases.All(lease => lease.Disposed).Should().BeTrue();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RestartAsync(default));
    }

    [Test]
    [Arguments("retry")]
    [Arguments("stop-backoff")]
    [Arguments("stop-preparation")]
    [Arguments("replace")]
    public async Task SupervisorRecoversAutomaticallyAndHonorsStopOrReplacement(string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var events = new List<string>();
        var engines = new List<Engine>();
        var attempts = 0;
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RouteRuntime(() =>
        {
            var engine = new Engine(events) { OnCommit = () => ready.TrySetResult() };
            engines.Add(engine);
            return engine;
        }, () => new Lease());
        var manager = new AppRoutingManager(runtime);
        var config = new Config { AppRouting = new() { Enabled = true } };
        await runtime.ApplyAsync(Plan("one", async token =>
        {
            if (++attempts == 2)
            {
                retryStarted.TrySetResult();
                if (scenario == "stop-preparation") { await Task.Delay(Timeout.Infinite, token); }
                if (scenario == "retry") { throw new IOException("temporary restart failure"); }
            }
            return new Profile("core", 10000 + attempts, events);
        }), timeout.Token);
        ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = manager.WatchRuntime(config);
        Task? replacementWatch = null;
        try
        {
            engines[0].End.SetResult(new IOException("observation failed"));
            if (scenario == "retry")
            {
                await ready.Task.WaitAsync(timeout.Token);
                // The commit callback precedes publication of runtime ownership.
                while (!runtime.IsRunning) { await Task.Delay(10, timeout.Token); }
                await attempts.Should().BeEqualTo(3);
                await engines.Count.Should().BeEqualTo(2);
                await config.AppRouting.Enabled.Should().BeTrue();
            }
            else if (scenario == "stop-preparation")
            {
                await retryStarted.Task.WaitAsync(timeout.Token);
            }
            else if (scenario == "replace")
            {
                await runtime.StopAsync();
                await runtime.ApplyAsync(Plan("replacement", _ => Task.FromResult<IRouteProfile>(new Profile("new core", 20000, events))), timeout.Token);
                replacementWatch = manager.WatchRuntime(config);
                await watch.WaitAsync(timeout.Token);
                await runtime.IsRunning.Should().BeTrue();
                await engines.Count.Should().BeEqualTo(2);
                await attempts.Should().BeEqualTo(1);
            }
            config.AppRouting.Enabled = false;
            await manager.RefreshAsync(config).WaitAsync(timeout.Token);
            await watch.WaitAsync(timeout.Token);
            if (replacementWatch != null) { await replacementWatch.WaitAsync(timeout.Token); }
            await runtime.IsRunning.Should().BeFalse();
            await config.AppRouting.Enabled.Should().BeFalse();
            await attempts.Should().BeEqualTo(scenario == "retry" ? 3 : scenario == "stop-preparation" ? 2 : 1);
        }
        finally { await manager.ShutdownAsync(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ApplyingPolicyAfterEngineFailureReplacesTheFailedEngine(bool replaceCore)
    {
        var events = new List<string>();
        var engines = new List<Engine>();
        var lease = new Lease();
        var leases = 0;
        var runtime = new RouteRuntime(() =>
        {
            var engine = new Engine(events);
            engines.Add(engine);
            return engine;
        }, () => { leases++; return lease; });
        var old = new Profile("old", 10001, events);
        var next = new Profile("next", 10002, events);
        try
        {
            await runtime.ApplyAsync(Plan("old", _ => Task.FromResult<IRouteProfile>(old)), default);
            engines[0].End.SetResult(new IOException("observation failed"));
            await runtime.ApplyAsync(Plan(replaceCore ? "new" : "old", _ => Task.FromResult<IRouteProfile>(next)), default);
            await engines.Count.Should().BeEqualTo(2);
            await engines[1].Completion.IsCompleted.Should().BeFalse();
            await engines[1].Excluded.Should().BeEqualTo(replaceCore ? next.ProcessId : old.ProcessId);
            await old.Disposed.Should().BeEqualTo(replaceCore);
            await next.Disposed.Should().BeFalse();
            await lease.Disposed.Should().BeFalse();
            await leases.Should().BeEqualTo(1);
            await events.IndexOf("stop").Should().BeLessThan(events.LastIndexOf("start"));
        }
        finally { await runtime.StopAsync(); }
    }

    [Test]
    public async Task RecoveryBackoffRemainsActiveUntilExplicitStop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<string>();
        var engine = new Engine(events);
        var runtime = new RouteRuntime(() => engine, () => new Lease());
        var firstDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delays = 0;
        Task Delay(TimeSpan _, CancellationToken token)
        {
            if (Interlocked.Increment(ref delays) == 1)
            {
                firstDelay.SetResult();
                return releaseFirst.Task.WaitAsync(token);
            }
            secondDelay.SetResult();
            return Task.Delay(Timeout.Infinite, token);
        }
        var manager = new AppRoutingManager(runtime, Delay);
        var config = new Config { AppRouting = new() { Enabled = true } };
        var attempts = 0;
        await runtime.ApplyAsync(Plan("one", _ => ++attempts == 1
            ? Task.FromResult<IRouteProfile>(new Profile("core", 10001, events))
            : throw new IOException("temporarily unavailable")), timeout.Token);
        var watch = manager.WatchRuntime(config);
        try
        {
            engine.End.SetResult(new IOException("observation failed"));
            await firstDelay.Task.WaitAsync(timeout.Token);
            releaseFirst.SetResult();
            await secondDelay.Task.WaitAsync(timeout.Token);
            await runtime.IsRunning.Should().BeFalse();
            // This is also the guard used by the TUN toggle. Recovery continues
            // owning the routing mode while waiting to acquire fresh resources.
            await manager.IsRunning.Should().BeTrue();
            await config.AppRouting.Enabled.Should().BeTrue();
            config.AppRouting.Enabled = false;
            await manager.RefreshAsync(config).WaitAsync(timeout.Token);
            await watch.WaitAsync(timeout.Token);
            await manager.IsRunning.Should().BeFalse();
            await attempts.Should().BeEqualTo(2);
        }
        finally { await manager.ShutdownAsync(); await watch.WaitAsync(timeout.Token); }
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
