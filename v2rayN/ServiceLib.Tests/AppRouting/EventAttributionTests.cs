using System.ComponentModel;
using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class EventAttributionTests
{
    private static AppRouteRule Rule(string name) => new() { ExecutablePath = name, MatchByName = true, IncludeChildProcesses = true };
    private static RouteProcessInfo Process(int pid, long born, int parent, string name, long at) => new(new(pid, born), parent, name, null, StartedAt: at);
    private static RouteSocketEvent Socket(byte kind, long at, ulong endpoint = 1, int pid = 20, byte protocol = 17) =>
        new(kind, at, endpoint, pid, PacketTests.Flow(false) with { Protocol = protocol });

    [Test]
    public async Task DelayedShortLivedLauncherJoinsItsSurvivingWorker()
    {
        var rule = Rule("App.exe");
        var tree = new RouteProcessTree(new([rule]), []);
        tree.Update([Process(10, 100, 0, "App.exe", 1000), Process(30, 112, 20, "Worker.exe", 1200)]);
        await tree.Decide(new(30, 112), 100).Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
        // Stop may be delivered before start; the later start fills identity without undoing the exit.
        tree.Update([new(new(20, 110), 0, "", null, Exited: 115, ExitedAt: 1250)]);
        tree.Update([Process(20, 110, 10, "Launcher.exe", 1100)]);
        await tree.Decide(new(30, 112), 100).Rule.Should().BeEqualTo(rule);
        await tree.Processes.Single(p => p.Key == new RouteProcessKey(20, 110)).Exited.Should().BeEqualTo(115L);
    }

    [Test]
    public async Task LateSnapshotCannotResurrectExitedProcessOrDiscardParentSequence()
    {
        var tree = new RouteProcessTree(new([Rule("App.exe")]), []);
        var start = Process(20, 110, 10, "Worker.exe", 1100) with { Sequence = 2, ParentSequence = 1 };
        tree.Update([start, start with { Exited = 120, ExitedAt = 1200 }]);
        tree.Update([start with { StartedAt = 0, Sequence = 0, ParentSequence = 0 }]);
        var info = tree.Processes.Single();
        await info.Exited.Should().BeEqualTo(120L);
        await info.Sequence.Should().BeEqualTo(2UL);
        await info.ParentSequence.Should().BeEqualTo(1UL);
        await new RouteProcessDecisions(tree, 100).Current(20).Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
    }

    [Test]
    public async Task ParentSequenceRejectsDifferentGenerationButAllowsSeededParent()
    {
        var rule = Rule("App.exe");
        var tree = new RouteProcessTree(new([rule]), []);
        tree.Update([Process(10, 90, 0, "App.exe", 0), Process(20, 110, 10, "Worker.exe", 1100) with { ParentSequence = 2 }]);
        await tree.Decide(new(20, 110), 100).Rule.Should().BeEqualTo(rule);
        tree.Update([Process(10, 90, 0, "App.exe", 900) with { Sequence = 1 }]);
        await tree.Decide(new(20, 110), 100).Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
    }

    [Test]
    public async Task SocketTimestampSelectsExitedGenerationWithoutFollowingReusedPid()
    {
        var selected = Rule("App.exe");
        var tree = new RouteProcessTree(new([selected]), []);
        tree.Update([Process(20, 110, 0, "App.exe", 1000) with { Exited = 120, ExitedAt = 2000 },
            Process(20, 130, 0, "Other.exe", 3000)]);
        var index = new RouteProcessDecisions(tree, 100);
        await index.At(20, 1500).Rule.Should().BeEqualTo(selected);
        await index.At(20, 2500).Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
        await index.At(20, 3500).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
        await index.Current(20).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
    }

    [Test]
    public async Task UnreceivedStartCannotBorrowOlderPidIdentity()
    {
        var tree = new RouteProcessTree(new([Rule("App.exe")]), []);
        tree.Update([Process(20, 90, 0, "App.exe", 0), Process(20, 110, 0, "Other.exe", 0)]);
        var index = new RouteProcessDecisions(tree, 100);
        await index.At(20, 1500).Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
        await index.Current(20).Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
    }

    [Test]
    public async Task ClosedSocketAttributesCapturedDatagramButNeverAReplyOrReusedPort()
    {
        var history = new RouteSocketHistory();
        history.Update([Socket(3, 100), Socket(7, 200), Socket(3, 300, 2, 30)], 400);
        var first = new RouteDecision(RouteDecisionKind.Selected, new(20, 1), Rule("App.exe"));
        var index = history.Snapshot((pid, _) => pid == 20 ? first : new(RouteDecisionKind.Unselected, new(30, 2)));
        var flow = Socket(3, 100).Flow;
        await index.Find(flow, 150)!.Process.Should().BeEqualTo(first.Process);
        await index.Find(flow, 250)!.Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
        await index.Find(flow, 350)!.Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
        await index.Find(flow, 0)!.Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
    }

    [Test]
    public async Task CloseBeforeBindDeliveryDoesNotResurrectEndpoint()
    {
        var history = new RouteSocketHistory();
        history.Update([Socket(7, 200)], 300);
        history.Update([Socket(3, 100)], 300);
        var index = history.Snapshot((_, _) => new(RouteDecisionKind.Selected, new(20, 1), Rule("App.exe")));
        await index.Find(Socket(3, 100).Flow, 150)!.Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await index.Find(Socket(3, 100).Flow, 0)!.Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
    }

    [Test]
    public async Task SharedEndpointAndSharedTableRemainAmbiguous()
    {
        var selected = new RouteDecision(RouteDecisionKind.Selected, new(20, 1), Rule("App.exe"));
        var history = new RouteSocketHistory();
        history.Update([Socket(3, 100), Socket(3, 100, 2, 30)], 200);
        var sockets = history.Snapshot((pid, _) => pid == 20 ? selected : new(RouteDecisionKind.Unselected, new(30, 1)));
        var flow = Socket(3, 100).Flow;
        await sockets.Find(flow, 150)!.Kind.Should().BeEqualTo(RouteDecisionKind.Ambiguous);
        var one = new RouteSocketHistory();
        one.Update([Socket(3, 100)], 200);
        var snapshot = new RouteAttributionSnapshot([], [new(flow.LocalAddress, flow.LocalPort, null, 0, 30)],
            _ => new(RouteDecisionKind.Unselected, new(30, 1)), 200, one.Snapshot((_, _) => selected));
        await snapshot.Find(flow, 150).Kind.Should().BeEqualTo(RouteDecisionKind.Ambiguous);
    }

    [Test]
    public async Task HistoricalPacketIsNotReassignedToCurrentTableOwner()
    {
        var history = new RouteSocketHistory();
        history.Update([Socket(3, 100), Socket(7, 200)], 300);
        var decision = new RouteDecision(RouteDecisionKind.Selected, new(20, 1), Rule("App.exe"));
        var flow = Socket(3, 100).Flow;
        var snapshot = new RouteAttributionSnapshot([], [new(flow.LocalAddress, flow.LocalPort, null, 0, 30)],
            _ => new(RouteDecisionKind.Unselected, new(30, 1)), 300, history.Snapshot((_, _) => decision));
        await snapshot.Find(flow, 150).Process.Should().BeEqualTo(decision.Process);
        await snapshot.Find(flow).Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
    }

    [Test]
    public async Task ProcessExitClosesSocketHistoryAndOldHistoryExpires()
    {
        var history = new RouteSocketHistory();
        history.Update([Socket(3, 100)], 300, (_, _) => 200);
        RouteDecision Decide(int _, long __) => RouteDecision.Unselected;
        await history.Snapshot(Decide).Find(Socket(3, 100).Flow, 150)!.Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
        await history.Snapshot(Decide).Find(Socket(3, 100).Flow, 0)!.Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
        history.Update([], 11 * System.Diagnostics.Stopwatch.Frequency + 300);
        await history.Snapshot(Decide).Find(Socket(3, 100).Flow, 150).Should().BeNull();
    }

    [Test]
    public async Task NativeSocketMetadataLayoutAndHostOrderAddressesArePreserved()
    {
        await Marshal.SizeOf<DivertAddress>().Should().BeEqualTo(80);
        await Marshal.OffsetOf<DivertAddress>(nameof(DivertAddress.ProcessId)).ToInt32().Should().BeEqualTo(32);
        await Marshal.OffsetOf<DivertAddress>(nameof(DivertAddress.Protocol)).ToInt32().Should().BeEqualTo(72);
        var native = new DivertAddress
        {
            Flags = 3 | (4 << 8), EndpointId = 123, ProcessId = 20, Timestamp = 1000,
            LocalAddress = new() { Word0 = 0x7f000001, Word1 = 0xffff },
            RemoteAddress = new() { Word3 = 0x20010db8, Word0 = 1 },
            LocalPort = 12345, RemotePort = 443, Protocol = 6
        };
        var socket = RouteSocketEvent.FromAddress(native);
        await socket.Event.Should().BeEqualTo((byte)4);
        await socket.Flow.LocalAddress.Should().BeEqualTo(IPAddress.Loopback);
        await socket.Flow.RemoteAddress.Should().BeEqualTo(IPAddress.Parse("2001:db8::1"));
        await socket.Flow.RemotePort.Should().BeEqualTo((ushort)443);
    }

    [Test]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(7)]
    public async Task SocketEventDecodingIgnoresEveryCombinationOfAddressFlags(int kind)
    {
        // Sniffed, Outbound, Loopback, Impostor, IPv6 and checksum bits follow
        // the eight-bit Event field in the WinDivert ABI. Live sniffed events
        // always exercise upper bits that the original metadata fixture omitted.
        for (uint flags = 0; flags < 256; flags++)
        {
            var address = new DivertAddress { Flags = 3u | ((uint)kind << 8) | (flags << 16),
                ProcessId = 20, Protocol = 17, LocalPort = 12345 };
            var socket = RouteSocketEvent.FromAddress(address);
            await socket.Event.Should().BeEqualTo((byte)kind);
            await address.Outbound.Should().BeEqualTo((flags & 2) != 0);
        }
    }

    [Test]
    public async Task IngressOverflowCannotSilentlyPublishIncompleteHistory()
    {
        var queue = new RouteEventQueue<int>("fixture");
        for (var i = 0; i <= RouteEventQueue<int>.Capacity; i++) { queue.Add(i); }
        await Assert.ThrowsAsync<IOException>(() => Task.Run(() => queue.Drain()));
    }

    [Test]
    public async Task ObserverFailureKeepsItsSourceAndOriginalCauseForDiagnostics()
    {
        var queue = new RouteEventQueue<int>("socket");
        var original = new Win32Exception(6); // ERROR_INVALID_HANDLE
        queue.Fail(original);
        queue.Fail(new IOException("Later failure must not replace the first cause."));
        var error = await Assert.ThrowsAsync<IOException>(() => Task.Run(() => queue.Drain()));
        await error.Message.Contains("socket observation failed").Should().BeTrue();
        await error.Message.Contains(original.Message).Should().BeTrue();
        await error.InnerException.Should().BeEqualTo(original);
    }

    [Test]
    public async Task EndpointIdentifierReuseAndLateClosePreserveBothLifetimes()
    {
        var history = new RouteSocketHistory();
        history.Update([Socket(3, 100), Socket(7, 200), Socket(3, 300, pid: 30)], 400);
        history.Update([Socket(7, 200)], 400);
        var index = history.Snapshot((pid, _) => new(RouteDecisionKind.Selected, new(pid, pid), Rule("App.exe")));
        await index.Find(Socket(3, 100).Flow, 150)!.Process.Should().BeEqualTo(new RouteProcessKey(20, 20));
        await index.Find(Socket(3, 100).Flow, 350)!.Process.Should().BeEqualTo(new RouteProcessKey(30, 30));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TcpConnectMetadataMatchesTheCompleteTupleBeforeItsSyn(bool ipv6)
    {
        var flow = PacketTests.Flow(ipv6) with { Protocol = 6 };
        var history = new RouteSocketHistory();
        history.Update([new(4, 100, 1, 20, flow)], 200);
        var sockets = history.Snapshot((_, _) => new(RouteDecisionKind.Selected, new(20, 10), Rule("App.exe")));
        await sockets.Find(flow, 150)!.Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await sockets.Find(flow with { RemotePort = 8443 }, 150).Should().BeNull();
        await sockets.Find(flow, 50)!.Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
    }

    [Test]
    public async Task FailedReaderIsReportedBeforeQueryingItsClosedSession()
    {
        var queue = new RouteEventQueue<int>("process");
        var original = new InvalidDataException("Process event decoding failed.");
        queue.Fail(original);
        var queried = false;
        var error = await Assert.ThrowsAsync<IOException>(() => Task.Run(() => queue.Drain(() =>
        {
            queried = true;
            throw new COMException("Session no longer exists.", unchecked((int)0x80071069));
        })));
        await queried.Should().BeFalse();
        await error.InnerException.Should().BeEqualTo(original);
    }

    [Test]
    public async Task ReaderFailureDuringHealthCheckWinsOverSessionCleanupError()
    {
        var queue = new RouteEventQueue<int>("process");
        var original = new InvalidDataException("Process event decoding failed.");
        var error = await Assert.ThrowsAsync<IOException>(() => Task.Run(() => queue.Drain(() =>
        {
            queue.Fail(original);
            throw new COMException("Session no longer exists.", unchecked((int)0x80071069));
        })));
        await error.InnerException.Should().BeEqualTo(original);
        await error.Message.Contains("Process event decoding failed.").Should().BeTrue();
    }

    [Test]
    public async Task HealthCheckFailureCannotPublishAnIncompleteBatch()
    {
        var queue = new RouteEventQueue<int>("process");
        queue.Add(1);
        var lost = new IOException("Process events were lost.");
        var error = await Assert.ThrowsAsync<IOException>(() => Task.Run(() => queue.Drain(() => throw lost)));
        await error.InnerException.Should().BeEqualTo(lost);
        var next = await Assert.ThrowsAsync<IOException>(() => Task.Run(() => queue.Drain()));
        await next.InnerException.Should().BeEqualTo(lost);
    }

    [Test]
    public async Task FlushCanDeliverEventsWithoutHoldingTheIngressLock()
    {
        var queue = new RouteEventQueue<int>("process");
        var batch = queue.Drain(() =>
        {
            var callback = Task.Run(() => queue.Add(1));
            if (!callback.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("Flush blocked event ingress."); }
        });
        await batch.Single().Should().BeEqualTo(1);
        await queue.Drain().Count.Should().BeEqualTo(0);
    }

    [Test]
    public async Task DelayedExitRefinesTheObservedSocketLifetime()
    {
        var history = new RouteSocketHistory();
        history.Update([Socket(3, 100)], 400, (_, _) => 300);
        history.Update([], 400, (_, _) => 200);
        var sockets = history.Snapshot((_, _) => new(RouteDecisionKind.Selected, new(20, 1), Rule("App.exe")));
        await sockets.Find(Socket(3, 100).Flow, 150)!.Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await sockets.Find(Socket(3, 100).Flow, 250)!.Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
    }

    [Test]
    public async Task ReusedEndpointDoesNotRetainExpiredHistory()
    {
        var now = 12 * System.Diagnostics.Stopwatch.Frequency;
        var history = new RouteSocketHistory();
        history.Update([Socket(3, 100), Socket(7, 200), Socket(3, now, pid: 30)], now);
        var sockets = history.Snapshot((pid, _) => new(RouteDecisionKind.Selected, new(pid, 1), Rule("App.exe")));
        await sockets.Find(Socket(3, 100).Flow, 150)!.Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
        await sockets.Find(Socket(3, 100).Flow, now)!.Process.Should().BeEqualTo(new RouteProcessKey(30, 1));
    }

    [Test]
    public async Task NativeImagePathsNormalizeWithoutOpeningTheExitedProcess()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RouteProcessPath.Normalize(@"\??\C:\Apps With Spaces\App.exe").Should().BeEqualTo(@"C:\Apps With Spaces\App.exe");
        await RouteProcessPath.Normalize(@"\??\UNC\server\share\App.exe").Should().BeEqualTo(@"\\server\share\App.exe");
        await RouteProcessPath.Normalize(@"\Device\Mup\server\share\App.exe").Should().BeEqualTo(@"\\server\share\App.exe");
        await RouteProcessPath.Normalize(@"\SystemRoot\System32\App.exe").Should().BeEqualTo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\App.exe"));
        await RouteProcessPath.Normalize("App.exe").Should().BeNull();
    }

    [Test]
    public async Task PreparedRuleSignatureRetainsOnlyTheSameDestination()
    {
        var rule = Rule("App.exe");
        var signature = JsonUtils.Serialize(rule);
        var policy = new RoutePolicy([rule], []);
        await policy.Retains(rule.Id, signature).Should().BeTrue();
        rule.ProxyEndpoint = new(12345);
        await new RoutePolicy([rule], []).Retains(rule.Id, signature).Should().BeFalse();
        await new RoutePolicy([], []).Retains(rule.Id, signature).Should().BeFalse();
    }
}
