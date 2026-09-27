using System.Text;
using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class ProcessEventDecoderTests
{
    private const long Born = 134139471961234567;
    private const ulong Sequence = 0xf000000000000002, ParentSequence = 0xe000000000000001;
    private const string Image = @"\??\C:\Apps With Spaces\Рабочий.exe";
    private const string Package = "Example.NetworkApp_1.2.3.4_x64__123456789abcd";

    [Test]
    [Arguments(0, 1)]
    [Arguments(1, 1)]
    [Arguments(2, 1)]
    [Arguments(3, 0)]
    [Arguments(3, 1)]
    [Arguments(3, 3)]
    [Arguments(4, 1)]
    [Arguments(4, 15)]
    public async Task StartSchemasPreserveIdentityAcrossVariableLengthSecurityIdentifiers(int version, int subauthorities)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var process = RouteProcessEventDecoder.Decode(1, version, 100, StartPayload(version, subauthorities, Package));
        await process.Key.Should().BeEqualTo(new RouteProcessKey(20, Born));
        await process.ParentPid.Should().BeEqualTo(10);
        await process.StartedAt.Should().BeEqualTo(100L);
        await process.Exited.Should().BeNull();
        await process.Sequence.Should().BeEqualTo(version >= 3 ? Sequence : 0UL);
        await process.ParentSequence.Should().BeEqualTo(version >= 3 ? ParentSequence : 0UL);
        await process.Name.Should().BeEqualTo("Рабочий.exe");
        await process.Path.Should().BeEqualTo(@"C:\Apps With Spaces\Рабочий.exe");
        await process.PackageFamily.Should().BeEqualTo(version >= 2 ? "Example.NetworkApp_123456789abcd" : null);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task StopSchemasPreserveExactFileTimesAndOptionalSequence(int version)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(20u);
        if (version == 2) { writer.Write(Sequence); }
        writer.Write(Born);
        writer.Write(Born + 1);
        var process = RouteProcessEventDecoder.Decode(2, version, 200, bytes.ToArray());
        await process.Key.Should().BeEqualTo(new RouteProcessKey(20, Born));
        await process.Exited.Should().BeEqualTo(Born + 1);
        await process.ExitedAt.Should().BeEqualTo(200L);
        await process.Sequence.Should().BeEqualTo(version == 2 ? Sequence : 0UL);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task EmptyPackageMeansUnpackagedAndInvalidPackageRemainsUnknown(int version)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RouteProcessEventDecoder.Decode(1, version, 100, StartPayload(version, 1, "")).PackageFamily.Should().BeEqualTo("");
        await RouteProcessEventDecoder.Decode(1, version, 100, StartPayload(version, 1, "invalid")).PackageFamily.Should().BeNull();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task TruncatedRequiredStartFieldsFailWithoutReadingBeyondPayload(int version)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var bytes = StartPayload(version, 1, Package);
        for (var length = 0; length < bytes.Length; length++)
        {
            var truncated = bytes[..length];
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Task.Run(() =>
            {
                if (OperatingSystem.IsWindows()) { RouteProcessEventDecoder.Decode(1, version, 100, truncated); }
            }));
            await error.Message.Contains($"event 1, version {version}").Should().BeTrue();
        }
    }

    [Test]
    [Arguments(1, 5)]
    [Arguments(2, 3)]
    [Arguments(3, 0)]
    public async Task UnknownSchemasFailExplicitlyInsteadOfGuessingOffsets(int id, int version)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Task.Run(() =>
        {
            if (OperatingSystem.IsWindows()) { RouteProcessEventDecoder.Decode(id, version, 100, []); }
        }));
        await error.Message.Contains($"event {id}, version {version}").Should().BeTrue();
    }

    // Field order from the installed Microsoft-Windows-Kernel-Process manifest.
    // Stop after the last field we consume; relative app ID/security mitigations
    // and stop-event resource counters do not participate in attribution.
    private static byte[] StartPayload(int version, int subauthorities, string package)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(20u); // ProcessID (payload PID, not the emitting process)
        if (version >= 3) { writer.Write(Sequence); }
        writer.Write(Born);
        writer.Write(10u);
        if (version >= 3) { writer.Write(ParentSequence); }
        writer.Write(1u); // SessionID
        if (version >= 1) { writer.Write(0u); } // Flags
        if (version >= 3)
        {
            writer.Write(1u); // ProcessTokenElevationType
            writer.Write(0u); // ProcessTokenIsElevated
            writer.Write(new byte[] { 1, (byte)subauthorities, 0, 0, 0, 0, 0, 16 });
            for (var i = 0; i < subauthorities; i++) { writer.Write(8192u); }
        }
        writer.Write(Encoding.Unicode.GetBytes(Image + '\0'));
        if (version >= 2)
        {
            writer.Write(0u); // ImageChecksum
            writer.Write(0u); // TimeDateStamp
            writer.Write(Encoding.Unicode.GetBytes(package + '\0'));
        }
        return bytes.ToArray();
    }
}
