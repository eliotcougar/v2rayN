namespace ServiceLib.Services.AppRouting;

/// <summary>One supervised core for the main routing table. Each observed application-match
/// combination receives a private authenticated inbound, keeping identity across the SOCKS relay.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RouteSharedProfile : IRouteProfile
{
    private readonly IRouteProfile _core;
    private readonly RouteSharedTemplate _template;
    private readonly RouteCoreApi _api;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RouteSocksEndpoint> _endpoints = [];
    public RouteSocksEndpoint Endpoint => _core.Endpoint;
    public int ProcessId => _core.ProcessId;
    public Task Completion => _core.Completion;
    public RouteSharedPolicy SharedPolicy { get; }

    private RouteSharedProfile(IRouteProfile core, RouteSharedTemplate template, RouteCoreApi api, RouteSharedRules rules)
    {
        _core = core; _template = template; _api = api;
        SharedPolicy = new(rules, PrepareEndpoint);
    }

    public static async Task<IRouteProfile> StartAsync(string json, RouteSharedRules rules, IReadOnlyList<AppRouteRule> fallbacks,
        string core, Dictionary<string, string>? environment, CancellationToken token)
    {
        var template = new RouteSharedTemplate(json, rules, fallbacks);
        var apiPort = ReservePort();
        template.Root["api"] = new JsonObject { ["tag"] = "app-routing-api", ["listen"] = $"127.0.0.1:{apiPort}",
            ["services"] = new JsonArray("HandlerService", "RoutingService") };
        var instance = await RouteProfileInstance.StartAsync(template.Root.ToJsonString(), core, environment, token);
        return new RouteSharedProfile(instance, template, new(core, environment, apiPort), rules);
    }

    internal static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task<RouteSocksEndpoint> PrepareEndpoint(IReadOnlyList<string> markers, AppRouteRule? fallback, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        token = cancellation.Token;
        var key = string.Join(',', markers) + ":" + fallback?.Id;
        await _gate.WaitAsync(token);
        try
        {
            if (_endpoints.TryGetValue(key, out var existing)) { return existing; }
            // A listener is shared by all processes with the same rule membership, not by PID.
            // Bound resource use for unusually large sets of ancestry combinations.
            if (_endpoints.Count >= 256) { throw new IOException("Too many distinct application-routing match combinations (256). Simplify overlapping child-process rules."); }
            if (Completion.IsCompleted) { throw new IOException("The shared application-routing core has stopped."); }
            var tag = "app-match-" + Guid.NewGuid().ToString("N");
            var endpoint = new RouteSocksEndpoint(ReservePort(), "app-route", Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
            var nativeRules = _template.RulesFor(markers, fallback, tag);
            var inbound = RouteSharedTemplate.Inbound(tag, endpoint);
            try
            {
                // No traffic can enter before the entire ordered rule set is installed.
                await _api.Execute("adrules", new JsonObject { ["routing"] = new JsonObject { ["rules"] = nativeRules } }, token, "-append");
                await _api.Execute("adi", new JsonObject { ["inbounds"] = new JsonArray(inbound) }, token);
                using var ready = await RouteConnector.ConnectProxy(endpoint, token);
                _endpoints.Add(key, endpoint);
                return endpoint;
            }
            catch
            {
                // The API may have committed before its client was cancelled. Revoke both
                // independently, using a cleanup token, before allowing another attempt.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await _api.Execute("rmi", null, cleanup.Token, tag); }
                catch (Exception ex) { Logging.SaveLog("AppRouting inbound cleanup", ex); }
                try { await _api.Execute("rmrules", null, cleanup.Token, nativeRules.Select(r => r!["ruleTag"]!.GetValue<string>()).ToArray()); }
                catch (Exception ex) { Logging.SaveLog("AppRouting rule cleanup", ex); }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _gate.WaitAsync();
        try { await _core.DisposeAsync(); }
        finally { _gate.Release(); _stop.Dispose(); }
    }
}

/// <summary>Pure JSON projection, also used by tests without starting a driver or core.</summary>
internal sealed class RouteSharedTemplate
{
    public JsonObject Root { get; }
    private readonly Dictionary<string, JsonNode> _branches = [];
    private readonly JsonNode _final;
    private readonly Dictionary<string, string> _fallbackTags = [];

    public RouteSharedTemplate(string json, RouteSharedRules rules, IReadOnlyList<AppRouteRule> fallbacks)
    {
        Root = JsonNode.Parse(json)!.AsObject();
        var native = Root["routing"]!["rules"]!.AsArray();
        _final = native.Last()!.DeepClone();
        native.RemoveAt(native.Count - 1);
        foreach (var branch in rules.Branches)
        {
            var compiled = native.Single(r => r?["inboundTag"] is JsonArray tags && tags.Any(t => t?.GetValue<string>() == branch.Marker))!;
            _branches.Add(branch.Marker, compiled.DeepClone());
            native.Remove(compiled);
        }
        // Keep internal DNS routes; the readiness listener cannot proxy arbitrary traffic.
        native.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("app-routing-ready"), ["outboundTag"] = Global.BlockTag });
        Root["inbounds"] = new JsonArray(Inbound("app-routing-ready", new(10808, "app-route", "template")));
        foreach (var fallback in fallbacks.Where(r => r.Kind != AppRouteKind.ActiveProfile))
        {
            if (fallback.Kind is AppRouteKind.Direct or AppRouteKind.Block)
            {
                _fallbackTags.Add(fallback.Id, fallback.Kind == AppRouteKind.Direct ? Global.DirectTag : Global.BlockTag);
                continue;
            }
            var tag = "app-fallback-" + _fallbackTags.Count;
            _fallbackTags.Add(fallback.Id, tag);
            Root["outbounds"]!.AsArray().Add(FallbackOutbound(fallback, tag));
        }
    }

    internal JsonArray RulesFor(IReadOnlyList<string> markers, AppRouteRule? fallback, string inbound)
    {
        var result = new JsonArray();
        foreach (var marker in markers) { result.Add(_branches[marker].DeepClone()); }
        var final = _final.DeepClone();
        if (fallback != null && _fallbackTags.TryGetValue(fallback.Id, out var tag))
        { final["outboundTag"] = tag; final.AsObject().Remove("balancerTag"); }
        result.Add(final);
        for (var index = 0; index < result.Count; index++)
        {
            result[index]!["inboundTag"] = new JsonArray(inbound);
            result[index]!["ruleTag"] = $"{inbound}-{index}";
        }
        return result;
    }

    internal static JsonObject Inbound(string tag, RouteSocksEndpoint endpoint) => new()
    {
        ["tag"] = tag, ["listen"] = "127.0.0.1", ["port"] = endpoint.Port, ["protocol"] = "socks",
        ["settings"] = new JsonObject { ["auth"] = "password", ["udp"] = true, ["ip"] = "127.0.0.1",
            ["accounts"] = new JsonArray(new JsonObject { ["user"] = endpoint.Username, ["pass"] = endpoint.Password }) },
        ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls", "quic"), ["routeOnly"] = true },
    };

    private static JsonObject FallbackOutbound(AppRouteRule rule, string tag)
    {
        var endpoint = rule.ProxyEndpoint ?? throw new InvalidOperationException("Fallback profiles must be prepared before the shared routing core.");
        var server = new JsonObject { ["address"] = Global.Loopback, ["port"] = endpoint.Port };
        if (endpoint.Username.Length > 0)
        { server["users"] = new JsonArray(new JsonObject { ["user"] = endpoint.Username, ["pass"] = endpoint.Password }); }
        return new() { ["tag"] = tag, ["protocol"] = "socks", ["settings"] = new JsonObject { ["servers"] = new JsonArray(server) } };
    }
}
