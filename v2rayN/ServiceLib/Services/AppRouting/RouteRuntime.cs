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
    private RouteProfilePlan? _plan;
    public bool IsRunning => _engine != null;
    public long Generation { get; private set; }

    public async Task<Exception?> WaitForFailureAsync(CancellationToken token)
    {
        var engine = _engine!.Completion;
        var finished = await Task.WhenAny(_core!.Completion, engine).WaitAsync(token);
        if (finished == engine) { return await engine; }
        try { await finished; }
        catch (Exception ex) { return ex; }
        return new IOException("The application-routing Xray core exited unexpectedly.");
    }

    public async Task ApplyAsync(RouteProfilePlan? plan, CancellationToken token)
    {
        // Keeping the preference enabled without capture selectors needs no driver or observers.
        if (plan == null) { await StopAsync(); return; }
        token.ThrowIfCancellationRequested();
        // Main-core reloads can produce the same effective routing configuration.
        // Keep its sessions, attribution state and supervisor generation intact.
        if (_plan?.Key == plan.Key && _core?.Completion.IsCompleted == false && _engine?.Completion.IsCompleted == false)
        {
            _plan = plan;
            return;
        }
        var lease = _lease ?? acquireLease();
        var retired = _core;
        var next = _core;
        IRouteEngine? candidate = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (_plan?.Key != plan.Key || next == null || next.Completion.IsCompleted) { next = await plan.Start(token); }
            // A user reload can beat the recovery timer. A stopped engine cannot
            // accept a policy; drain its native observers before starting another.
            if (_engine?.Completion.IsCompleted == true)
            {
                var failed = _engine;
                _engine = null;
                await failed.DisposeAsync();
            }
            candidate = _engine == null ? createEngine() : null;
            token.ThrowIfCancellationRequested();
            await (candidate ?? _engine!).ApplyAsync(next.SharedPolicy!, [next.ProcessId], token);
            // Cancellation after commit belongs to StopAsync, which drains the new policy's resources.
            candidate?.Start();
            _engine ??= candidate;
            _core = next;
            _plan = plan;
            _lease = lease;
            Generation++;
            candidate = null;
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
        if (retired != null && retired != next)
        {
            // The new policy is already committed. A retirement error must not
            // report a failed apply and prevent the manager from supervising it.
            try { await retired.DisposeAsync(); }
            catch (Exception ex) { Logging.SaveLog("AppRouting retired core cleanup", ex); }
        }
    }

    // Apply already stages replacements and reuses healthy components. Recovery
    // must not tear down a healthy core or release the machine-wide capture lease.
    public Task RecoverAsync(CancellationToken token) =>
        ApplyAsync(_plan ?? throw new InvalidOperationException("No application-routing plan to recover."), token);

    public async Task StopAsync()
    {
        Generation++;
        var engine = _engine;
        var core = _core;
        var lease = _lease;
        _engine = null;
        _core = null;
        _plan = null;
        _lease = null;
        try { if (engine != null) { await engine.DisposeAsync(); } }
        finally
        {
            try { if (core != null) { await core.DisposeAsync(); } }
            finally { lease?.Dispose(); }
        }
    }
}
