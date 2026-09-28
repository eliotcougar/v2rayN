namespace ServiceLib.Services.AppRouting;

internal sealed record RouteServiceInfo(string Name, string DisplayName, int Pid);

/// <summary>Service short names are stable configuration keys; PIDs are only live observations.</summary>
internal sealed class RouteServiceSnapshot
{
    private sealed record Host(RouteProcessKey Key, string[] Names, bool ContainsSelected);
    private readonly Dictionary<int, Host> _hosts;
    public bool HasSelectedHosts => _hosts.Values.Any(host => host.ContainsSelected);

    public RouteServiceSnapshot(IEnumerable<RouteServiceInfo> services, IEnumerable<RouteProcessInfo> processes,
        IReadOnlySet<string> selected)
    {
        var current = processes.Where(p => p.Exited == null).GroupBy(p => p.Key.Pid)
            .ToDictionary(g => g.Key, g => g.MaxBy(p => p.Key.Started)!.Key);
        _hosts = services.Where(s => s.Pid > 4 && current.ContainsKey(s.Pid)).GroupBy(s => s.Pid)
            .ToDictionary(g => g.Key, g => new Host(current[g.Key],
                g.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                g.Any(s => selected.Contains(s.Name))));
    }

    public bool ContainsSelected(int pid, RouteProcessKey key) =>
        _hosts.TryGetValue(pid, out var host) && host.Key == key && host.ContainsSelected;

    public bool NeedsModule(int pid) => _hosts.TryGetValue(pid, out var host) && host.ContainsSelected && host.Names.Length > 1;

    public string? Resolve(int pid, RouteProcessKey key, string? moduleName = null)
    {
        if (!_hosts.TryGetValue(pid, out var host) || host.Key != key) { return null; }
        if (host.Names.Length == 1) { return host.Names[0]; }
        return host.Names.FirstOrDefault(name => string.Equals(name, moduleName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Read-only SCM inventory; never starts, stops or reconfigures a service.</summary>
[SupportedOSPlatform("windows")]
internal static class RouteServiceCatalog
{
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceWin32 = 0x0030;
    private const uint ServiceStateAll = 0x0003;
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeService
    {
        public IntPtr Name;
        public IntPtr DisplayName;
        public uint Type, State, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, Pid, Flags;
    }

    public static IReadOnlyList<RouteServiceInfo> Read()
    {
        var manager = OpenSCManagerW(null, null, ScManagerEnumerateService);
        if (manager == IntPtr.Zero) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            var result = new List<RouteServiceInfo>();
            uint resume = 0;
            var entrySize = Marshal.SizeOf<NativeService>();
            const int capacity = 256 * 1024;
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                while (true)
                {
                    var before = resume;
                    var success = EnumServicesStatusExW(manager, 0, ServiceWin32, ServiceStateAll,
                        buffer, capacity, out _, out var count, ref resume, IntPtr.Zero);
                    var error = success ? 0 : Marshal.GetLastWin32Error();
                    if (!success && error != ErrorMoreData) { throw new Win32Exception(error); }
                    for (var i = 0; i < count; i++)
                    {
                        var entry = Marshal.PtrToStructure<NativeService>(IntPtr.Add(buffer, checked((int)i * entrySize)));
                        var name = Marshal.PtrToStringUni(entry.Name);
                        if (string.IsNullOrWhiteSpace(name)) { continue; }
                        result.Add(new(name, Marshal.PtrToStringUni(entry.DisplayName) ?? name, checked((int)entry.Pid)));
                    }
                    if (success) { break; }
                    if (resume == before) { throw new IOException("Service enumeration did not advance."); }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return result.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { CloseServiceHandle(manager); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW", SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusExW(IntPtr manager, uint infoLevel, uint serviceType, uint serviceState,
        IntPtr buffer, int bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, IntPtr groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
