# Routing block editor

## Editing rules

The main routing rule editor uses blocks added with `+`. Domain, IP, Process and
Windows App are alternatives joined by OR. Port, Protocol, Inbound Tag and Network
are common AND constraints. Values within each block are alternatives:

`(Domain OR IP OR Process OR Windows App) AND Port AND Protocol AND Inbound Tag AND Network`

There are no operator switches. A rule may contain only common constraints.
Domain and IP lines starting with `#` are comments; commenting every line makes
that alternative match nothing. Empty blocks must be removed or filled in.

Process and Windows App blocks contain rows with enabled and Children checkboxes
and delete buttons. Process rows also have up/down controls. The enabled column is compact and has no
heading. Process offers Full path (file picker), Folder (folder picker), and
Executable (running network application picker) buttons. The value defines the
match: a full executable path, a folder ending in a separator, or a bare filename.
There is no separate matching-mode column. Existing explicit modes are converted
to their effective values when opening the editor, preserving flags and matching
behavior. The persisted mode is inferred from the edited value. Folder matching includes
subfolders and uses a trailing separator, so `C:/Apps/One/` does not match
`C:/Apps/OneOther/`. The value field can also be edited directly. Package `+` opens
the package checklist, including its read-only loopback-exemption import. Search
preserves hidden selections, and import merges checks with the current selection.
A package may occur in several rules; main-table order determines the result.
The common rule header supplies the remark and destination. Retained
package rows keep their enabled state and Children setting. Windows App rows show
localized package names when available and fall back to family identifiers.
Clicking the name heading toggles alphabetical order; package row order has no
routing significance. Family identifiers remain visible in tooltips and are the
only identities saved for matching. Name lookup runs off the UI thread.

Disabled rows remain in the saved rule. If every row is disabled, that block
matches nothing. Row order is for organization: enabled rows within one block
have the same destination. Main routing table order determines rule precedence.

The header places remark and enable switch on one row, followed by rule type,
outbound tag and profile picker. Titles and hints share a line. Inbound hints
list the configured local listeners and `app-routing`, the logical tag for
traffic intercepted by WinDivert.

## Destinations and capture

Both core and WinDivert routing use `proxy`, `direct`, `block`, or a selected
profile. Explicit SOCKS servers and outbound network-interface destinations
have been removed; their retired preview rules are discarded when loading the
configuration. The monitored-interface selection remains available: it controls
where capture applies, rather than choosing a destination adapter.

Enable Application routing in Settings → v2rayN settings to capture selected processes/packages with WinDivert.
Administrator privileges are required. Restart the routed application after
changing its selection. The main routing table is the sole source of rules;
the standalone window and its saved preview rules have been retired. Without
a matching rule, the normal main-profile default applies. Shared-table capture requires an active
profile supported by Xray; custom full configurations are excluded.

A rule containing only Port, optionally with Network, also selects matching
destination ports for capture across otherwise eligible processes on monitored
interfaces. Without Network it selects both TCP and UDP, for IPv4 and IPv6.
Additional blocks prevent this global port selection. **Full-range rules are
fallbacks:** `0-65535`, `1-65535`, or equivalent split/overlapping ranges remain
ordinary routing rules and never opt all applications into capture. This applies
even with a Network constraint. Narrower port rules and application selections
still route through the entire applicable main table in its original order.
Excluded interfaces, v2rayN/proxy-core processes and their descendants remain
outside selection. Application routing must be enabled for any interception.

The Children flag applies to WinDivert attribution. It includes descendants
running outside the matched folder or package, using observed parent generations
and retained exited ancestors. Native core process matching continues to match
only the process itself. Proxy-core processes and their descendants are excluded.

## Persistence and downgrade

`RulesItem.Blocks` stores the enabled state and filters. Application filters add
`Applications` rows containing Value, Enabled, Mode and IncludeChildren. Other
filters keep their existing Values list. No database schema migration is needed.
Legacy ordinary rules keep their original Domain OR IP OR Process semantics.

The legacy fields of a block rule remain disabled and carry the reserved
`v2rayN-block-rule-requires-new-editor` inbound tag. An older released client
cannot turn the rule into a broad match even by enabling that fallback. Old
clients do not execute these blocks and may discard their unknown fields when
rewriting a rule set; keep an export before editing in an older version.

## Core compilation

`RoutingBlockRules` expands the alternatives and appends common constraints.
Xray receives consecutive field rules; sing-box receives equivalent logical
rules. Application rows project only their enabled effective values. Package
families resolve once per configuration to installation-folder prefixes for
native core matching. No package executable scan or process/package intersection
is needed. Missing packages remove only their own alternative.
Process names preserve case for platforms with case-sensitive executable names;
Windows identity matching remains case-insensitive.

This native folder approximation identifies binaries installed in a package.
WinDivert instead uses the process's Windows package identity, including sparse
packages. Neither path can attribute work delegated to an unrelated Windows
service to its initiating app. The package picker does not change loopback
exemptions. Native package paths are refreshed on core reload.

For type ALL, the Domain alternative also contributes to DNS generation, as in
legacy rules. Common connection constraints do not apply to DNS. DNS-only rules
accept only a Domain block.

## Identity handoff to Xray

A relay socket belongs to v2rayN, so Xray cannot recover the originating process
from that socket. `RouteSharedRules` separates application predicates from native
network predicates. It preserves every rule's table position and common
constraints, giving each alternative a private marker during generation.

The observer evaluates application membership using process generations,
package identities and ancestry. `RouteSharedPolicy` caches a target per observed
membership combination, independent of PID. No helper process or
native query is run by packet lookup. These immutable targets belong to their
shared core, so reusing that core preserves connections and replacing it retires
every old target without comparing serialized rules.

`RoutePortCapture` compiles the union of eligible destination ports into two
65536-bit lookup tables, one per transport (16 KiB per policy). Applications
selected only through these ports carry this eligibility mask on their runtime
target. TCP table entries are filtered while building the snapshot; UDP and
socket-event entries are filtered on lookup, before merging owners. This keeps
unrelated destinations on a shared UDP bind unselected, while retaining ambiguity
checks for captured destinations. Process/package-selected targets keep their
original all-port selection. Capture eligibility is included
in branch metadata used for the shared-core reuse key, so adding another block
cannot accidentally reuse a previous global port policy.

`RouteSharedProfile` starts one additional supervised Xray core for the main table.
At the first connection for a membership combination, it adds the applicable
ordered native rules and an authenticated loopback SOCKS inbound through Xray's
HandlerService and RoutingService APIs. Rules are installed before the listener;
credentials are returned only after readiness succeeds. HTTP/TLS/QUIC sniffing is
routing-only. Xray still evaluates domains, IPs, ports, protocols and networks.
The existing SOCKS TCP and multi-destination UDP transports remain in use.

The packaged Xray executable supplies the API client (`api adrules`, `api adi`),
so no separately maintained protobuf dependency is introduced. Two short helper
processes run on first use; subsequent connections reuse the prepared endpoint.
There is no per-packet API call. Failed setup revokes any attempted scoped rules
and inbound.

Xray expands GeoSite/GeoIP expressions before sending the API request. If the
expanded rules exceed the server's gRPC receive limit, no rules are committed.
The resolver then loads the same ordered rules through a normal config file in
an additional owned core for that match combination. Subsequent new combinations
use this file path without retrying the oversized API request; existing endpoints
remain usable. This also handles a single oversized GeoSite list and negated GeoIP
expressions without splitting or changing their meaning. Each combination still
shares one cached endpoint across processes, but large configurations can consume
more memory and start more core processes. Every owned core is supervised; any
unexpected exit stops application routing through the existing failure path.

The cache is bounded to 256 observed combinations per configuration; exceeding
it rejects new preparations with a diagnostic. All listeners disappear with the
owned cores on stop or configuration replacement.

`RouteRuntime` prepares the shared core and commits the observer policy only
after it is ready. Old connections are retired when the effective configuration
changes. Runtime endpoint objects are not persisted. Core processes remain in
separate lifetime jobs and are excluded from capture. Saved routing edits request
an immediate normal reload while the routing list remains open. Every reload
reapplies enabled WinDivert routing, including recovery after an earlier failure;
turning it off in settings and saving stops interception.
An enabled configuration with no eligible selectors also releases the runtime,
while retaining the saved preference for the next routing edit.

References: [Xray router reload implementation](https://github.com/XTLS/Xray-core/blob/v26.9.9/app/router/router.go),
[add-inbound API client](https://github.com/XTLS/Xray-core/blob/v26.9.9/main/commands/all/api/inbounds_add.go),
[add-rule API client](https://github.com/XTLS/Xray-core/blob/v26.9.9/main/commands/all/api/rules_add.go).

## Validation

Managed tests cover OR truth tables, common constraints, disabled rows, row-mode
projection, package names/sorting/identities, comments, downgrade safety, folder boundaries,
children outside folders/packages, exited ancestors, PID reuse, protected cores,
main-table priority, port/network capture, full-range fallback preservation,
shared UDP ownership and fallback compilation. An isolated native Xray fixture
also exercises scoped-rule installation, listener reuse, TCP, domain precedence,
multiple UDP peers and IPv6. These checks do not load WinDivert or launch the GUI.
Real capture and desktop interaction are tested on a separate machine.
