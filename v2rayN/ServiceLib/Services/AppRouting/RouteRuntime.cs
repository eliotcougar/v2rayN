namespace ServiceLib.Services.AppRouting;

internal interface IRouteEngine : IAsyncDisposable
{
    Task<Exception?> Completion { get; }
    // Cancellation/failure is allowed before commit only. A successful return owns the new policy.
    Task ApplyAsync(RouteSharedPolicy routes, IEnumerable<int> excludedProcesses, CancellationToken token);
    void Start();
}

internal interface IRouteProfile : IAsyncDisposable
{
    RouteSocksEndpoint Endpoint { get; }
    RouteSharedPolicy? SharedPolicy => null;
    int ProcessId { get; }
    Task Completion { get; }
}

internal sealed record RouteProfilePlan(string Key, Func<CancellationToken, Task<IRouteProfile>> Start);

/// <summary>Stages one shared core before applying its policy to the persistent capture engine.
/// The manager serializes calls; a failed preparation leaves the current runtime intact.</summary>
internal sealed class RouteRuntime(Func<IRouteEngine> createEngine, Func<IDisposable> acquireLease)
{
    private IRouteEngine? _engine;
    private IDisposable? _lease;
    private IRouteProfile? _core;
    private string? _key;
    public bool IsRunning => _engine != null;
    public long Generation { get; private set; }

    public async Task<Exception?> WaitForFailureAsync(CancellationToken token)
    {
        var engine = _engine!.Completion;
        var finished = await Task.WhenAny(_core!.Completion, engine).WaitAsync(token);
        return finished == engine ? await engine : new IOException("The application-routing Xray core exited unexpectedly.");
    }

    public async Task ApplyAsync(RouteProfilePlan? plan, CancellationToken token)
    {
        // Keeping the preference enabled without capture selectors needs no driver or observers.
        if (plan == null) { await StopAsync(); return; }
        var lease = _lease ?? acquireLease();
        var next = _core;
        IRouteEngine? candidate = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (_key != plan.Key || next == null || next.Completion.IsCompleted) { next = await plan.Start(token); }
            candidate = _engine == null ? createEngine() : null;
            token.ThrowIfCancellationRequested();
            await (candidate ?? _engine!).ApplyAsync(next.SharedPolicy!, [next.ProcessId], token);
            // Cancellation after commit belongs to StopAsync, which drains the new policy's resources.
            candidate?.Start();
            var retired = _core;
            _engine ??= candidate;
            _core = next;
            _key = plan.Key;
            _lease = lease;
            Generation++;
            candidate = null;
            if (retired != null && retired != next) { await retired.DisposeAsync(); }
        }
        catch
        {
            try { if (candidate != null) { await candidate.DisposeAsync(); } }
            finally
            {
                try { if (next != null && next != _core) { await next.DisposeAsync(); } }
                finally { if (_lease == null) { lease.Dispose(); } }
            }
            throw;
        }
    }

    public async Task StopAsync()
    {
        Generation++;
        var engine = _engine;
        var core = _core;
        var lease = _lease;
        _engine = null;
        _core = null;
        _key = null;
        _lease = null;
        try { if (engine != null) { await engine.DisposeAsync(); } }
        finally
        {
            try { if (core != null) { await core.DisposeAsync(); } }
            finally { lease?.Dispose(); }
        }
    }
}
