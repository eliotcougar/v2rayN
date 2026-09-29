using ServiceLib.Services.AppRouting;

namespace ServiceLib.Manager;

public sealed class AppRoutingManager
{
    public static AppRoutingManager Instance { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RouteRuntime _runtime;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay = Task.Delay;
    private CancellationTokenSource? _preparation;
    private CancellationTokenSource? _watch;
    private volatile Task? _watchTask;
    private volatile bool _shuttingDown;
    private volatile bool _starting;
    // Recovery still owns this mode during backoff, even without a live engine.
    public bool IsRunning => _starting || _runtime.IsRunning || _watchTask is { IsCompleted: false };
    private string? _lastError;
    private RouteInterfaceMonitor? _interfaces;
    internal RouteInterfaceMonitor Interfaces => _interfaces ??= new(AppManager.Instance.Config,
        ConfigHandler.SaveConfig, RouteInterfaceCatalog.Read, ex =>
        {
            Logging.SaveLog("Application routing interfaces", ex);
            Report(ex.Message);
        });

    private AppRoutingManager()
    {
        _runtime = new(() => OperatingSystem.IsWindows() ? new AppRouteEngine(Report, () => Interfaces.Policy) : throw new PlatformNotSupportedException(),
            () => OperatingSystem.IsWindows() ? new RouteCaptureLease() : throw new PlatformNotSupportedException());
    }

    internal AppRoutingManager(RouteRuntime runtime, Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        _runtime = runtime;
        if (retryDelay != null) { _retryDelay = retryDelay; }
    }

    internal static bool IsProtectedExecutable(string executable) =>
        CoreInfoManager.Instance.GetCoreInfo().SelectMany(c => c.CoreExes ?? []).Append("v2rayN")
            .Any(name => string.Equals(Path.GetFileNameWithoutExtension(name), Path.GetFileNameWithoutExtension(executable), StringComparison.OrdinalIgnoreCase));

    private async Task StartAsync(Config config)
    {
        if (_shuttingDown || !config.AppRouting.Enabled) { return; }
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture is not (Architecture.X86 or Architecture.X64) ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.X86 or Architecture.X64))
        {
            throw new PlatformNotSupportedException("Application routing requires an x86 or x64 build of v2rayN on x86 or x64 Windows.");
        }
        if (!Utils.IsAdministrator()) { throw new InvalidOperationException(ResUI.AppRoutingAdminRequired); }
        var driver = Environment.Is64BitOperatingSystem ? "WinDivert64.sys" : "WinDivert32.sys";
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "WinDivert.dll")) || !File.Exists(Path.Combine(AppContext.BaseDirectory, driver)))
        {
            throw new FileNotFoundException(ResUI.AppRoutingDriverRequired);
        }
        await _gate.WaitAsync();
        try
        {
            if (_shuttingDown || !config.AppRouting.Enabled) { return; }
            _starting = true;
            if (config.TunModeItem.EnableTun) { throw new InvalidOperationException(ResUI.AppRoutingTunConflict); }
            await Interfaces.RefreshAsync();
            var snapshot = JsonUtils.DeepCopy(config);
            _preparation = new CancellationTokenSource();
            var plan = await PreparePlan(snapshot, _preparation.Token);
            _preparation.Token.ThrowIfCancellationRequested();
            if (_shuttingDown || !config.AppRouting.Enabled) { return; }
            await _runtime.ApplyAsync(plan, _preparation.Token);
            _lastError = null;
            if (_runtime.IsRunning) { _ = WatchRuntime(config); }
            else { _watch?.Cancel(); }
        }
        finally
        {
            _preparation?.Dispose();
            _preparation = null;
            _starting = false;
            _gate.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<RouteProfilePlan?> PreparePlan(Config config, CancellationToken token)
    {
        // All match conditions and destinations come from the active routing table.
        var mainRules = new RouteSharedRules(await ConfigHandler.GetDefaultRouting(config));
        if (!mainRules.HasCaptureSelectors)
        {
            return null;
        }
        token.ThrowIfCancellationRequested();
        var active = await ConfigHandler.GetDefaultServer(config)
            ?? throw new InvalidOperationException("Select an active profile for application routing.");
        if (active.ConfigType == EConfigType.Custom) { throw new InvalidOperationException("Main-table application routing requires a standard active profile."); }
        var mainNode = JsonUtils.DeepCopy(active);
        mainNode.CoreType = ECoreType.Xray;
        var mainConfig = JsonUtils.DeepCopy(config);
        mainConfig.Mux4RayItem.XudpProxyUDP443 = "allow";
        var mainContext = await CoreConfigContextBuilder.Build(mainConfig, mainNode);
        if (!mainContext.Success) { throw new InvalidOperationException(string.Join(Environment.NewLine, mainContext.ValidatorResult.Errors)); }
        var generated = new CoreConfigV2rayService(mainContext.Context).GenerateClientSocksConfig(10808, true, mainRules.Projection);
        if (!generated.Success || generated.Data is not string json) { throw new InvalidOperationException(generated.Msg); }
        var mainCoreInfo = CoreInfoManager.Instance.GetCoreInfo(ECoreType.Xray);
        var mainCore = CoreInfoManager.Instance.GetCoreExecFile(mainCoreInfo, out var mainError);
        if (string.IsNullOrEmpty(mainCore)) { throw new FileNotFoundException(mainError); }
        var mainEnvironment = mainCoreInfo.Environment?.Where(p => p.Value != null).OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value!);
        var mainKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mainCore + "\n" + JsonUtils.Serialize(mainEnvironment)
            + "\n" + json + "\n" + JsonUtils.Serialize(mainRules.Branches))));
        return new(mainKey, cancellation => RouteSharedProfile.StartAsync(json, mainRules, mainCore, mainEnvironment, cancellation));
    }

    public Task RefreshAsync(Config config) => config.AppRouting.Enabled ? StartAsync(config) : StopAsync();

    internal Task WatchRuntime(Config config)
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = new CancellationTokenSource();
        return _watchTask = ObserveRuntime(config, _runtime.Generation, _runtime.WaitForFailureAsync(_watch.Token), _watch.Token);
    }

    private async Task ObserveRuntime(Config config, long generation, Task<Exception?> failure, CancellationToken token)
    {
        try
        {
            var error = await failure;
            var retrySeconds = 1;
            while (!token.IsCancellationRequested)
            {
                Report($"Application routing is recovering; retrying in {retrySeconds} seconds: " +
                    (error?.Message ?? "The capture engine stopped unexpectedly."));
                await _retryDelay(TimeSpan.FromSeconds(retrySeconds), token);
                retrySeconds = Math.Min(retrySeconds * 2, 30);
                var recovered = false;
                await _gate.WaitAsync(token);
                try
                {
                    if (generation != _runtime.Generation || _shuttingDown || !config.AppRouting.Enabled) { return; }
                    _starting = true;
                    _preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    try
                    {
                        await _runtime.RestartAsync(_preparation.Token);
                        const string message = "Application routing recovered after a runtime failure. Existing connections may need to reconnect.";
                        Logging.SaveLog(message);
                        NoticeManager.Instance.SendMessageEx(message);
                        _lastError = null;
                        failure = _runtime.WaitForFailureAsync(token);
                        recovered = true;
                    }
                    catch (OperationCanceledException) when (_preparation.IsCancellationRequested) { return; }
                    catch (Exception ex) { Logging.SaveLog("AppRouting recovery", ex); error = ex; }
                    finally
                    {
                        generation = _runtime.Generation;
                        _preparation.Dispose();
                        _preparation = null;
                        _starting = false;
                    }
                }
                finally { _gate.Release(); }
                if (recovered)
                {
                    var started = Environment.TickCount64;
                    error = await failure;
                    if (Environment.TickCount64 - started >= 60_000) { retrySeconds = 1; }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Logging.SaveLog("AppRouting supervision", ex); }
    }

    private void Report(string message)
    {
        var error = ResUI.AppRoutingRouteError + ": " + message;
        if (_lastError == error) { return; }
        _lastError = error;
        Logging.SaveLog(error);
        NoticeManager.Instance.SendMessageEx(error);
    }

    private async Task StopAsync()
    {
        // Abort a pending core readiness wait before waiting for lifecycle serialization.
        try { _preparation?.Cancel(); } catch (ObjectDisposedException) { }
        await _gate.WaitAsync();
        try
        {
            _watch?.Cancel();
            _watch?.Dispose();
            _watch = null;
            await _runtime.StopAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        if (_interfaces != null) { await _interfaces.DisposeAsync(); }
        await StopAsync();
    }
}
