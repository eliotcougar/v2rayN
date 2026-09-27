using System.Security.Principal;

namespace ServiceLib.Services.AppRouting;

internal sealed record RouteLoopbackSelection(IReadOnlyList<string> Families, int Unmatched);

/// <summary>Copies loopback permissions into a package selection. Never changes Windows permissions.</summary>
[SupportedOSPlatform("windows")]
internal static class RouteLoopbackImport
{
    public static RouteLoopbackSelection Read(IReadOnlyList<RoutePackage> packages)
    {
        var exemptions = ReadSids();
        var families = new List<string>();
        foreach (var package in packages)
        {
            Marshal.ThrowExceptionForHR(DeriveAppContainerSidFromAppContainerName(package.Family, out var sid));
            try
            {
                if (exemptions.Remove(new SecurityIdentifier(sid).Value)) { families.Add(package.Family); }
            }
            finally { FreeSid(sid); }
        }
        return new(families, exemptions.Count);
    }

    private static HashSet<string> ReadSids()
    {
        var error = NetworkIsolationGetAppContainerConfig(out var count, out var entries);
        if (error != 0) { throw new Win32Exception(error); }
        try
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<SidAndAttributes>(entries + checked(i * Marshal.SizeOf<SidAndAttributes>()));
                result.Add(new SecurityIdentifier(entry.Sid).Value);
            }
            return result;
        }
        finally
        {
            // NetworkIsolationGetAppContainerConfig allocates both the SID array and
            // individual SIDs on the process heap; neither uses FreeSid/LocalFree.
            for (var i = 0; i < count; i++)
            {
                var sid = Marshal.ReadIntPtr(entries, checked(i * Marshal.SizeOf<SidAndAttributes>()));
                HeapFree(GetProcessHeap(), 0, sid);
            }
            HeapFree(GetProcessHeap(), 0, entries);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public nint Sid; public uint Attributes; }
    [DllImport("FirewallAPI.dll")]
    private static extern int NetworkIsolationGetAppContainerConfig(out int count, out nint entries);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);
    [DllImport("advapi32.dll")] private static extern nint FreeSid(nint sid);
    [DllImport("kernel32.dll")] private static extern nint GetProcessHeap();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HeapFree(nint heap, uint flags, nint memory);
}
