namespace ServiceLib.Services.AppRouting;

[SupportedOSPlatform("windows")]
internal sealed class RouteProfileInstance : IRouteProfile
{
    private readonly ProcessService _process;
    private readonly WindowsJobService _job;
    private readonly string _file;
    public RouteSocksEndpoint Endpoint { get; }
    public int ProcessId => _process.Id;
    public Task Completion { get; }

    private RouteProfileInstance(ProcessService process, WindowsJobService job, string file, RouteSocksEndpoint endpoint)
    {
        _process = process;
        _job = job;
        _file = file;
        Endpoint = endpoint;
        Completion = process.WaitForExitAsync();
    }

    public static async Task<IRouteProfile> StartAsync(string template, string core, Dictionary<string, string>? environment, CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new RouteSocksEndpoint(((IPEndPoint)listener.LocalEndpoint).Port,
            "app-route", Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        listener.Stop();
        var root = JsonNode.Parse(template)!;
        var inbound = root["inbounds"]![0]!;
        inbound["port"] = endpoint.Port;
        inbound["settings"]!["accounts"]![0]!["pass"] = endpoint.Password;
        var file = Utils.GetBinConfigPath("app-route-" + Guid.NewGuid().ToString("N") + ".json");
        ProcessService? process = null;
        WindowsJobService? job = null;
        RouteProfileInstance? instance = null;
        try
        {
            await File.WriteAllTextAsync(file, root.ToJsonString(), token);
            process = new ProcessService(core, $"run -c \"{file}\"", Path.GetDirectoryName(core)!, true, false, environment,
                (_, message) =>
                {
                    Logging.SaveLog(message);
                    NoticeManager.Instance.SendMessage(message);
                    return Task.CompletedTask;
                });
            job = new WindowsJobService();
            token.ThrowIfCancellationRequested();
            await process.StartAsync();
            if (!job.AddProcess(process.Handle)) { throw new IOException("Unable to attach the application-routing core to its lifetime job."); }
            instance = new(process, job, file, endpoint);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                while (true)
                {
                    if (instance.Completion.IsCompleted) { throw new IOException("Application-routing Xray exited during startup; see the core log."); }
                    try
                    {
                        using var ready = await RouteConnector.ConnectProxy(endpoint, timeout.Token);
                        return instance;
                    }
                    catch (SocketException) { await Task.Delay(100, timeout.Token); }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Application-routing Xray did not become ready.");
            }
        }
        catch
        {
            if (instance != null) { await instance.DisposeAsync(); }
            else
            {
                if (process != null)
                {
                    try { await process.StopAsync(); } catch (InvalidOperationException) { }
                    process.Dispose();
                }
                job?.Dispose();
                DeleteConfig(file);
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _process.StopAsync(); }
        catch (Exception ex) { Logging.SaveLog("AppRouting cleanup", ex); }
        finally
        {
            _job.Dispose();
            try { await Completion; } catch (Exception ex) { Logging.SaveLog("AppRouting core exit", ex); }
            _process.Dispose();
            DeleteConfig(_file);
        }
    }

    private static void DeleteConfig(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logging.SaveLog("AppRouting cleanup", ex); }
    }
}
