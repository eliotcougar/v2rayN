namespace ServiceLib.Services.AppRouting;

/// <summary>Read-only Windows interface metadata, including NDIS filter modules.</summary>
internal static class RouteInterfaceNative
{
    internal readonly record struct InterfaceInfo(Guid Id, uint Index, bool IsFilter);

    // MIB_IF_ROW2 from netioapi.h: fixed-width fields, WCHAR[257] strings,
    // and 64-bit counters. The complete native row is 1352 bytes on x86 and x64.
    // Keep the LUID field so the table's first row has the native 8-byte alignment.
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    internal struct InterfaceRow
    {
        [FieldOffset(0)] public ulong Luid;
        [FieldOffset(8)] public uint Index;
        [FieldOffset(12)] public Guid Id;
        [FieldOffset(1152)] public byte Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InterfaceTable
    {
        public uint Count;
        public InterfaceRow First;
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<InterfaceInfo> Read()
    {
        var error = GetIfTable2(out var table);
        if (error != 0) { throw new Win32Exception((int)error); }
        try
        {
            var count = Marshal.ReadInt32(table);
            var first = IntPtr.Add(table, Marshal.OffsetOf<InterfaceTable>(nameof(InterfaceTable.First)).ToInt32());
            var rows = new List<InterfaceInfo>(count);
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<InterfaceRow>(IntPtr.Add(first, i * Marshal.SizeOf<InterfaceRow>()));
                rows.Add(new(row.Id, row.Index, (row.Flags & 2) != 0)); // FilterInterface bit.
            }
            return rows;
        }
        finally { FreeMibTable(table); }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern void FreeMibTable(IntPtr table);
}
