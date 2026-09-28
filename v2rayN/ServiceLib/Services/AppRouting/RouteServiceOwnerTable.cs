using System.Buffers.Binary;

namespace ServiceLib.Services.AppRouting;

/// <summary>Owner-module tables are read only when service rules exist. Module names are hints,
/// not service identities until matched to a service running in the owning process.</summary>
[SupportedOSPlatform("windows")]
internal static class RouteServiceOwnerTable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp4
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid;
        public long Created;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public ulong[] Module;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScope, LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress;
        public uint RemoteScope, RemotePort, State, Pid;
        public long Created;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public ulong[] Module;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Udp4
    {
        public uint LocalAddress, LocalPort, Pid;
        public long Created;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public ulong[] Module;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Udp6
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScope, LocalPort, Pid;
        public long Created;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public ulong[] Module;
    }

    public static List<RouteOwnerTable.Row> Read(byte protocol, AddressFamily family, RouteServiceSnapshot services)
    {
        var tcp = protocol == 6;
        var ipv6 = family == AddressFamily.InterNetworkV6;
        uint size = 0;
        uint Query(IntPtr buffer) => tcp
            ? GetExtendedTcpTable(buffer, ref size, false, (uint)family, 8, 0)
            : GetExtendedUdpTable(buffer, ref size, false, (uint)family, 2, 0);
        var error = Query(IntPtr.Zero);
        if (error is not (0 or 122)) { throw new Win32Exception((int)error); }
        var rowSize = tcp ? ipv6 ? Marshal.SizeOf<Tcp6>() : Marshal.SizeOf<Tcp4>()
            : ipv6 ? Marshal.SizeOf<Udp6>() : Marshal.SizeOf<Udp4>();
        // Owner-module rows contain 64-bit fields; the native table pads its count to 8 bytes.
        const int firstRow = 8;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var capacity = checked((int)size);
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                error = Query(buffer);
                if (error == 122) { continue; }
                if (error != 0) { throw new Win32Exception((int)error); }
                var count = Marshal.ReadInt32(buffer);
                if (count < 0 || count > (checked((int)size) - firstRow) / rowSize)
                { throw new IOException("Invalid IP owner-module table."); }
                var result = new List<RouteOwnerTable.Row>(count);
                for (var i = 0; i < count; i++)
                {
                    var ptr = IntPtr.Add(buffer, firstRow + i * rowSize);
                    if (tcp && ipv6)
                    {
                        var row = Marshal.PtrToStructure<Tcp6>(ptr);
                        result.Add(new(new IPAddress(row.LocalAddress, row.LocalScope), Port(row.LocalPort),
                            new IPAddress(row.RemoteAddress, row.RemoteScope), Port(row.RemotePort), checked((int)row.Pid),
                            services.NeedsModule((int)row.Pid) ? ModuleName(ptr, true, true) : null));
                    }
                    else if (tcp)
                    {
                        var row = Marshal.PtrToStructure<Tcp4>(ptr);
                        result.Add(new(new IPAddress(BitConverter.GetBytes(row.LocalAddress)), Port(row.LocalPort),
                            new IPAddress(BitConverter.GetBytes(row.RemoteAddress)), Port(row.RemotePort), checked((int)row.Pid),
                            services.NeedsModule((int)row.Pid) ? ModuleName(ptr, true, false) : null));
                    }
                    else if (ipv6)
                    {
                        var row = Marshal.PtrToStructure<Udp6>(ptr);
                        result.Add(new(new IPAddress(row.LocalAddress, row.LocalScope), Port(row.LocalPort), null, 0,
                            checked((int)row.Pid), services.NeedsModule((int)row.Pid) ? ModuleName(ptr, false, true) : null));
                    }
                    else
                    {
                        var row = Marshal.PtrToStructure<Udp4>(ptr);
                        result.Add(new(new IPAddress(BitConverter.GetBytes(row.LocalAddress)), Port(row.LocalPort), null, 0,
                            checked((int)row.Pid), services.NeedsModule((int)row.Pid) ? ModuleName(ptr, false, false) : null));
                    }
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("IP owner-module table changed repeatedly.");
    }

    private static ushort Port(uint native) => BinaryPrimitives.ReverseEndianness((ushort)native);

    private static string? ModuleName(IntPtr row, bool tcp, bool ipv6)
    {
        uint size = 0;
        uint Query(IntPtr buffer) => tcp
            ? ipv6 ? GetOwnerModuleFromTcp6Entry(row, 0, buffer, ref size) : GetOwnerModuleFromTcpEntry(row, 0, buffer, ref size)
            : ipv6 ? GetOwnerModuleFromUdp6Entry(row, 0, buffer, ref size) : GetOwnerModuleFromUdpEntry(row, 0, buffer, ref size);
        if (Query(IntPtr.Zero) != 122 || size == 0 || size > 65536) { return null; }
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (Query(buffer) != 0) { return null; }
            var name = Marshal.ReadIntPtr(buffer);
            return name == IntPtr.Zero ? null : Marshal.PtrToStringUni(name);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, bool order, uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, bool order, uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetOwnerModuleFromTcpEntry(IntPtr row, uint infoClass, IntPtr buffer, ref uint size);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetOwnerModuleFromTcp6Entry(IntPtr row, uint infoClass, IntPtr buffer, ref uint size);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetOwnerModuleFromUdpEntry(IntPtr row, uint infoClass, IntPtr buffer, ref uint size);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetOwnerModuleFromUdp6Entry(IntPtr row, uint infoClass, IntPtr buffer, ref uint size);
}
