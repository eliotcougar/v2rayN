namespace ServiceLib.Services.AppRouting;

/// <summary>Remembers adapters while v2rayN is open, including while routing is off.
/// Discovery and editor commits are serialized; capture reads immutable snapshots.</summary>
internal sealed class RouteInterfaceMonitor(Config config, Func<Config, Task<int>> save,
    Func<IReadOnlyList<RouteInterfaceInfo>> read, Action<Exception> report) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private Task? _watch;
    private RouteInterfacePolicy _policy = RouteInterfacePolicy.All;
    public RouteInterfacePolicy Policy => Volatile.Read(ref _policy);

    public void Start()
    {
        if (_watch != null) { return; }
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        _watch = Task.Run(Watch);
    }

    private void NetworkChanged(object? sender, EventArgs args)
    {
        // Coalesce notifications; avoid doing enumeration or file I/O on the OS callback.
        lock (_changed)
        {
            if (!_stop.IsCancellationRequested && _changed.CurrentCount == 0) { _changed.Release(); }
        }
    }

    private async Task Watch()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await RefreshAsync(); }
                catch (Exception ex) { report(ex); }
                // Also discover inactive adapters, which need not raise address notifications.
                await _changed.WaitAsync(TimeSpan.FromSeconds(5), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task<(AppRouteInterfaceOptions Options, IReadOnlyList<RouteInterfaceInfo> Adapters)> RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var adapters = await Task.Run(read);
            var updated = RouteInterfaceCatalog.Discover(config.AppRouting.InterfaceMonitoring, adapters);
            await Commit(updated, adapters);
            return (updated, adapters);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppRouteInterfaceOptions editor)
    {
        await _gate.WaitAsync();
        try
        {
            var adapters = await Task.Run(read);
            // Any adapters discovered since the dialog opened retain their first-seen
            // state. Only rows actually shown in the editor can overwrite a choice.
            var updated = RouteInterfaceCatalog.Discover(config.AppRouting.InterfaceMonitoring, adapters);
            updated.MonitorNewInterfaces = editor.MonitorNewInterfaces;
            foreach (var row in editor.Interfaces)
            {
                var item = updated.Interfaces.First(i => string.Equals(i.Id, row.Id, StringComparison.OrdinalIgnoreCase));
                item.Monitored = row.Monitored;
            }
            await Commit(updated, adapters);
        }
        finally { _gate.Release(); }
    }

    private async Task Commit(AppRouteInterfaceOptions updated, IReadOnlyList<RouteInterfaceInfo> adapters)
    {
        var previous = config.AppRouting.InterfaceMonitoring;
        if (JsonUtils.Serialize(previous) != JsonUtils.Serialize(updated))
        {
            config.AppRouting.InterfaceMonitoring = updated;
            try
            {
                if (await save(config) != 0) { throw new IOException(ResUI.OperationFailed); }
            }
            catch { config.AppRouting.InterfaceMonitoring = previous; throw; }
        }
        var policy = new RouteInterfacePolicy(updated, adapters, config.AppRouting.BypassLocalTraffic);
        if (!policy.SameAs(Policy)) { Volatile.Write(ref _policy, policy); }
    }

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        lock (_changed) { _stop.Cancel(); }
        if (_watch != null) { await _watch; }
        lock (_changed)
        {
            // An already dispatched callback observes cancellation under this lock
            // before it touches the semaphore.
            _changed.Dispose();
            _stop.Dispose();
        }
    }
}
