using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class SharedProfileTests
{
    private sealed class Core : IRouteProfile
    {
        public TaskCompletionSource End { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RouteSocksEndpoint Endpoint { get; } = new(10001);
        public int ProcessId => 100;
        public Task Completion => End.Task;
        public bool Disposed;
        public ValueTask DisposeAsync() { Disposed = true; End.TrySetResult(); return ValueTask.CompletedTask; }
    }

    private sealed class Api(string failedCleanup) : IRouteCoreApi
    {
        public List<string> Commands { get; } = [];
        public Task Execute(string command, JsonNode? input, CancellationToken token, params string[] arguments)
        {
            token.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (command == "adi") { throw new IOException("inbound preparation failed"); }
            if (command == failedCleanup) { throw new IOException("rollback failed"); }
            return Task.CompletedTask;
        }
    }

    private static (RouteSharedRules Rules, RouteSharedTemplate Template, RouteProcessInfo Process) Configuration()
    {
        var rule = new RulesItem { OutboundTag = Global.ProxyTag, Blocks = new() { Filters = [new()
        {
            Selector = RoutingSelector.Process,
            Applications = [new() { Value = "app.exe", Mode = RoutingProcessMode.Name }]
        }] } };
        var rules = new RouteSharedRules(new RoutingItem { RuleSet = JsonUtils.Serialize(new[] { rule }) });
        var json = new JsonObject { ["routing"] = new JsonObject { ["rules"] = new JsonArray(
            new JsonObject { ["inboundTag"] = new JsonArray(rules.Branches[0].Marker), ["outboundTag"] = Global.ProxyTag },
            new JsonObject { ["outboundTag"] = Global.DirectTag }) } };
        return (rules, new(json.ToJsonString(), rules), new(new(10, 1), 0, "app.exe", null, PackageFamily: ""));
    }

    [Test]
    [Arguments("")]
    [Arguments("rmi")]
    [Arguments("rmrules")]
    public async Task FailedPreparationRevokesBothResourcesAndOnlyFailedRollbackInvalidatesTheCore(string failedCleanup)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var (rules, template, process) = Configuration();
        var core = new Core();
        var api = new Api(failedCleanup);
        await using var profile = new RouteSharedProfile(core, template, api, rules, (_, _) => throw new Exception("no file fallback"));
        var target = profile.SharedPolicy.Select([process])!;
        await Assert.ThrowsAsync<IOException>(() => target.ResolveEndpoint(default));
        await api.Commands.SequenceEqual(new[] { "adrules", "adi", "rmi", "rmrules" }).Should().BeTrue();
        await core.Disposed.Should().BeFalse(); // The supervisor stages its replacement first.
        var completion = profile.Completion;
        await completion.IsCompleted.Should().BeEqualTo(failedCleanup != "");
        if (failedCleanup != "")
        {
            await Assert.ThrowsAsync<IOException>(async () => await completion);
            var count = api.Commands.Count;
            await Assert.ThrowsAsync<IOException>(() => target.ResolveEndpoint(default));
            await api.Commands.Count.Should().BeEqualTo(count);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => target.ResolveEndpoint(default));
            await api.Commands.Count.Should().BeEqualTo(8);
        }
    }
}
