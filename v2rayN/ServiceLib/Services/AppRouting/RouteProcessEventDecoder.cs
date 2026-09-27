using System.Buffers.Binary;

namespace ServiceLib.Services.AppRouting;

/// <summary>Decodes Microsoft-Windows-Kernel-Process start/stop payloads, independent of process bitness.</summary>
[SupportedOSPlatform("windows")]
internal static class RouteProcessEventDecoder
{
    public static RouteProcessInfo Decode(int id, int version, long timestamp, ReadOnlySpan<byte> payload)
    {
        // TraceEvent's dynamic parser stops at win:SID in start v3/v4, omitting
        // ImageName and package identity. Read these two versioned schemas directly.
        // Reject new layouts explicitly instead of attributing traffic using wrong offsets.
        if (version < 0 || !(id == 1 && version <= 4 || id == 2 && version <= 2))
        { throw new NotSupportedException($"Unsupported process event {id}, version {version}."); }
        try
        {
            var reader = new PayloadReader(payload);
            var pid = checked((int)reader.UInt32());
            var sequence = (id == 1 ? version >= 3 : version >= 2) ? reader.UInt64() : 0;
            var key = new RouteProcessKey(pid, reader.Int64()); // Native FILETIME, same as GetProcessTimes.
            if (id == 2)
            {
                return new(key, 0, "", null, reader.Int64(), sequence, ExitedAt: timestamp);
            }
            var parent = checked((int)reader.UInt32());
            var parentSequence = version >= 3 ? reader.UInt64() : 0;
            reader.Take(4); // SessionID
            if (version >= 1) { reader.Take(4); } // Flags
            if (version >= 3)
            {
                reader.Take(8); // TokenElevationType and TokenIsElevated
                var sid = reader.Take(8); // SID revision, subauthority count and identifier authority
                reader.Take(sid[1] * 4);
            }
            var image = reader.UnicodeString();
            string? package = null;
            if (version >= 2)
            {
                reader.Take(8); // ImageChecksum and TimeDateStamp
                package = RoutePackageIdentity.FromFullName(reader.UnicodeString());
            }
            return new(key, parent, Path.GetFileName(image), RouteProcessPath.Normalize(image), Sequence: sequence,
                ParentSequence: parentSequence, StartedAt: timestamp, PackageFamily: package);
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        { throw new InvalidDataException($"Invalid process event {id}, version {version}: {ex.Message}", ex); }
    }

    private ref struct PayloadReader(ReadOnlySpan<byte> remaining)
    {
        private ReadOnlySpan<byte> _remaining = remaining;

        public ReadOnlySpan<byte> Take(int length)
        {
            if (_remaining.Length < length) { throw new InvalidDataException("Truncated process event payload."); }
            var value = _remaining[..length];
            _remaining = _remaining[length..];
            return value;
        }

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public string UnicodeString()
        {
            for (var length = 0; length + 1 < _remaining.Length; length += 2)
            {
                if (_remaining[length] != 0 || _remaining[length + 1] != 0) { continue; }
                var value = Encoding.Unicode.GetString(Take(length));
                Take(2);
                return value;
            }
            throw new InvalidDataException("Unterminated process event string.");
        }
    }
}
