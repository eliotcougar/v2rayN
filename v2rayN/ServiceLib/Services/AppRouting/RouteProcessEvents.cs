using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace ServiceLib.Services.AppRouting;

/// <summary>One owned, process-only ETW session. Callbacks copy records; attribution consumes batches.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RouteProcessEvents : IDisposable
{
    internal static readonly Guid Provider = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
    private readonly RouteEventQueue<RouteProcessInfo> _events = new("process");
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private readonly Action _changed;
    private TraceEventSession? _session;
    private volatile bool _stopping;
    public long Started { get; private set; }

    public RouteProcessEvents(Action changed)
    {
        _changed = changed;
        _worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ready.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        // The worker owns the mutex for the session's entire lifetime. This permits
        // reclaiming our own orphan after a crash without stopping another installation.
        var installation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..16];
        var name = $"v2rayN.ApplicationRouting.Processes.{installation}";
        Mutex? owner = null;
        TraceEventSession? session = null;
        var acquired = false;
        try
        {
            owner = new Mutex(false, $"Global\\{name}");
            try { acquired = owner.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { throw new IOException("Application-routing process observation is already in use."); }
            session = new TraceEventSession(name, TraceEventSessionOptions.Create | TraceEventSessionOptions.NoPerProcessorBuffering)
            {
                BufferSizeMB = 4,
                BufferQuantumKB = 64,
                EnableProviderTimeoutMSec = 1000
            };
            _session = session;
            var source = session.Source;
            source.AllEvents += data =>
            {
                if (data.ProviderGuid != Provider || (int)data.ID is not (1 or 2)) { return; }
                try { _events.Add(Decode(data)); }
                catch (Exception ex)
                {
                    // Preserve managed decoding errors before crossing the native callback boundary.
                    _events.Fail(ex);
                    source.StopProcessing();
                }
                _changed();
            };
            Started = DateTime.UtcNow.ToFileTimeUtc();
            session.EnableProvider(Provider, TraceEventLevel.Informational, 0x10,
                new TraceEventProviderOptions { EventIDsToEnable = [1, 2] });
            _ready.SetResult();
            source.Process();
            if (!_stopping) { throw new IOException("Application-routing process observation stopped unexpectedly."); }
        }
        catch (Exception ex)
        {
            _events.Fail(ex);
            _ready.TrySetException(ex);
            if (!_stopping) { _changed(); }
        }
        finally
        {
            // Publish the reader's failure before closing the session. Otherwise a
            // concurrent EventsLost query can hide it with ERROR_WMI_INSTANCE_NOT_FOUND.
            try { session?.Dispose(); }
            finally { if (acquired) { owner!.ReleaseMutex(); } owner?.Dispose(); }
        }
    }

    private static RouteProcessInfo Decode(TraceEvent data)
    {
        // WinDivert timestamps are raw QPC values, not relative to the ETW session.
#pragma warning disable CS0618
        var timestamp = data.TimeStampQPC;
#pragma warning restore CS0618
        return RouteProcessEventDecoder.Decode((int)data.ID, data.Version, timestamp, data.EventData());
    }

    public IReadOnlyList<RouteProcessInfo> Drain(bool flush) => _events.Drain(() =>
    {
        if (flush) { _session!.Flush(); }
        if (_session!.EventsLost != 0) { throw new IOException("Application-routing process events were lost."); }
    });

    public void Dispose()
    {
        _stopping = true;
        // Session disposal also closes the consumer to unblock Process(). Drain
        // the worker before a replacement can acquire its ownership mutex.
        try { _session?.Dispose(); }
        finally { _worker.GetAwaiter().GetResult(); }
    }
}

/// <summary>Bounded event ingress. Overflow is an attribution failure, never a silent history gap.</summary>
internal sealed class RouteEventQueue<T>(string observer)
{
    private readonly Queue<T> _items = new();
    private Exception? _failure;
    internal const int Capacity = 32768;

    public void Add(T item)
    {
        lock (_items)
        {
            if (_items.Count == Capacity) { _failure ??= new IOException("Application-routing event queue overflowed."); }
            else if (_failure == null) { _items.Enqueue(item); }
        }
    }

    public void Fail(Exception error) { lock (_items) { _failure ??= error; } }

    public IReadOnlyList<T> Drain(Action? checkHealth = null)
    {
        if (checkHealth != null)
        {
            lock (_items) { ThrowIfFailed(); }
            // Native health checks must not hold the ingress lock: callbacks may
            // arrive during a flush. Preserve a reader failure published meanwhile.
            try { checkHealth(); }
            catch (Exception ex) { Fail(ex); }
        }
        lock (_items)
        {
            ThrowIfFailed();
            var batch = _items.ToArray();
            _items.Clear();
            return batch;
        }
    }

    private void ThrowIfFailed()
    {
        if (_failure != null) { throw new IOException($"Application-routing {observer} observation failed: {_failure.Message}", _failure); }
    }
}

[SupportedOSPlatform("windows")]
internal static class RouteProcessPath
{
    public static string? Normalize(string image)
    {
        if (image.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            image = image[4..];
            if (image.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) { image = @"\\" + image[4..]; }
        }
        if (image.StartsWith(@"\Device\Mup\", StringComparison.OrdinalIgnoreCase)) { return @"\\" + image[12..]; }
        if (image.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), image[12..]); }
        if (Path.IsPathFullyQualified(image) && !image.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) { return image; }
        if (!image.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) { return null; }
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var target = new StringBuilder(32768);
            if (QueryDosDevice(drive[..2], target, target.Capacity) == 0) { continue; }
            var prefix = target.ToString();
            if (image.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)) { return drive[..2] + image[prefix.Length..]; }
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, int length);
}
