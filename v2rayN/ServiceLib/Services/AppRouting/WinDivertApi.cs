namespace ServiceLib.Services.AppRouting;

// WinDivert 2.2 ABI: 16-byte header followed by a 64-byte union.
// In particular, NETWORK's 8 bytes do not determine the size of the union.
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal struct DivertAddress
{
    [FieldOffset(0)] public long Timestamp;
    [FieldOffset(8)] public uint Flags;
    [FieldOffset(16)] public uint InterfaceIndex;
    [FieldOffset(20)] public uint SubInterfaceIndex;
    [FieldOffset(16)] public ulong EndpointId;
    [FieldOffset(24)] public ulong ParentEndpointId;
    [FieldOffset(32)] public uint ProcessId;
    [FieldOffset(36)] public DivertIp LocalAddress;
    [FieldOffset(52)] public DivertIp RemoteAddress;
    [FieldOffset(68)] public ushort LocalPort;
    [FieldOffset(70)] public ushort RemotePort;
    [FieldOffset(72)] public byte Protocol;
    // Event occupies bits 8..15; upper bits contain Sniffed/Outbound/etc.
    // Mask before narrowing because this project checks integer overflow.
    public readonly byte Event => (byte)((Flags >> 8) & 0xff);
    public bool Outbound
    {
        readonly get => (Flags & (1u << 17)) != 0; set => Flags = value ? Flags | (1u << 17) : Flags & ~(1u << 17);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct DivertIp
{
    public uint Word0, Word1, Word2, Word3;
    public readonly IPAddress ToAddress()
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, Word3);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes[4..], Word2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes[8..], Word1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes[12..], Word0);
        var address = new IPAddress(bytes);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}

internal static class WinDivertApi
{
    private const string Library = "WinDivert.dll";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    internal static extern IntPtr WinDivertOpen([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertRecv(IntPtr handle, IntPtr packet, uint length, out uint received, out DivertAddress address);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertSetParam(IntPtr handle, uint parameter, ulong value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertRecvEx(IntPtr handle, [Out] byte[] packet, uint length, out uint received,
        ulong flags, [Out] DivertAddress[] addresses, ref uint addressLength, IntPtr overlapped);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertSend(IntPtr handle, ref byte packet, uint length, out uint sent, ref DivertAddress address);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertSendEx(IntPtr handle, ref byte packet, uint length, out uint sent,
        ulong flags, ref DivertAddress addresses, uint addressLength, IntPtr overlapped);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertHelperCalcChecksums(ref byte packet, uint length, ref DivertAddress address, ulong flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertShutdown(IntPtr handle, uint how);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertClose(IntPtr handle);
}
