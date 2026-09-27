# Application routing for Windows

This feature adds an **Application routing** switch and **Monitored interfaces**
button to **Settings → v2rayN settings**, immediately after **Double-clicking
configuration makes it active**, in both WPF and Avalonia. All rules live in the
ordinary routing table's [block editor](routing-block-editor.md). Process and
Windows App blocks select applications; standalone Port rules, optionally with
Network, select destination ports across applications. Full port ranges remain
fallback rules and never opt every application into capture.

The implementation handles TCP and UDP over IPv4 and IPv6. QUIC is carried as
UDP; there is no TLS interception. Destinations are proxy, direct, block, or a
saved profile, evaluated in the normal routing-table order.

**Status:** experimental implementation. Compilation, packet tests and local
SOCKS5 relay tests are available. Privileged WinDivert interception, desktop
interaction and end-to-end routing on real adapters still require validation
in a dedicated Windows test environment. Do not treat a passing build as proof
of working process interception or absence of traffic leaks.

## Use

1. Run the test copy as Administrator, with TUN disabled.
2. Open **Settings → v2rayN settings**. The Application routing switch follows
   the double-click activation option. Save the settings dialog to apply its
   enabled state. Cancel leaves that preference unchanged. Without administrator
   rights, the switch is unclickable and continues to display the saved state.
3. Use **Monitored interfaces** to select adapters and the default for newly seen
   adapters. Confirming this separate dialog saves and applies its choices
   immediately, without committing an unfinished enable-switch change.
4. Edit rules in **Routing settings**. Add Process or Windows App blocks, or a
   standalone Port block with optional Network. Process offers Full path, Folder,
   and Executable picker buttons. Windows App offers a package checklist. Enable
   and Children flags are editable in those blocks. Rule precedence is table order.
5. Saving a routing set, changing its selection or changing routing options now
   requests a core reload immediately, even while the routing list remains open.
   WinDivert applies the current saved policy at the start of that reload, before
   the main core is replaced and before the availability-test delay. Rapid reload
   requests use the application's existing reload coordinator.
6. Fully exit and restart affected applications to establish new connections.
   Existing TCP connections cannot be moved to another route. Disabling routing
   and saving stops interception. Closing settings does not stop it. Normal app
   exit preserves the enabled preference; startup and subsequent reloads retry
   enabled routing even if an earlier start failed. Errors use normal notifications.

The standalone Application routing window and its rule storage are retired.
Old preview `AppRouting.Rules` fields are ignored when loading; only enablement
and interface preferences remain. Existing preview rules must be expressed in
ordinary routing settings. Proxy-core processes and their descendants remain
excluded to prevent loops.

### Windows App rules

Add a **Windows App** block in the ordinary rule editor and click its `+` button.
Check any number of installed packages. Search matches package name, family and
publisher; Select visible and Clear visible affect only the filtered rows.
Confirming updates the block's draft; saving the routing set applies it.
Packages may occur in multiple ordinary rules, with normal table precedence.
Rows show localized display names where available, retain stable family IDs for
matching, and sort by clicking the name heading. Framework/resource packages and
bundles are excluded. The list belongs to the Windows account running v2rayN.

**Import loopback exemption rules** checks installed packages matching the current
Windows loopback exemptions. Import adds to the selection without clearing existing
checks. It reports unmatched exemptions;
an exemption for a removed package or a non-package AppContainer cannot be silently
converted into a package rule. Import does not create, remove or reset any Windows
loopback permission, and the existing EnableLoopback utility remains available.

Matching uses stable **package family names**, independent of version, architecture
and installation directory. All applications/processes in each selected package
share the rule, including package-owned background processes launched through
brokers. It does not select every instance of a shared Windows host executable.
The ordinary routing table decides precedence between process, package and inherited matches. **Include
child processes** also includes unpackaged descendants. Missing packages remain
selected when editing a saved rule, so an uninstall/reinstall does not erase its
membership.

Package rules apply to **direct network connections**. Traffic an app already
sends to the local system proxy continues through that proxy's routing. Importing
or removing a loopback exemption does not turn that traffic into direct connections.
WinUI and AppContainer are different properties: packaged desktop applications
may run with full trust, while sandboxed apps can still need their existing loopback
permissions. Test direct TCP/UDP and existing proxy use separately; this feature
does not bypass AppContainer network permissions.

### Monitored interfaces

Click **Monitored interfaces** beside the application-routing switch to choose
the adapters on which application rules apply. Uncheck a VPN tunnel or a second
local adapter to leave its application traffic on the normal Windows route.
This selects traffic to intercept. Selecting an outbound network adapter is no
longer offered as a rule destination.

The dialog has one row per current non-loopback adapter, including disconnected
and VPN adapters, with its description and status. Windows filter modules such
as Npcap and QoS are part of their adapter's stack, not separate IP routing
choices, and do not get extra rows. Missing adapters keep their saved choice
and reappear if they return; historical rows are hidden. The compact checkbox
list uses a single pixel-scrolling viewport and fixed-height rows; full text is
available in tooltips. **Confirm** saves; **Cancel** leaves
the choices unchanged. Interface settings can be edited without administrator
rights or enabling routing. Restart routed apps after changing the selection.

**Monitor new interfaces** defaults to on, preserving the behavior of older
configurations. Each newly discovered adapter inherits this switch once; changing
the switch does not change any existing adapter's selection. Discovery runs while
v2rayN is open, including when application routing is off. Choices are saved by
the Windows adapter ID, so renaming an adapter or changing its interface index
does not reset them. An adapter recreated with a different ID is a new interface.
An adapter created and removed entirely between discovery reads cannot be remembered.

Selection uses the outbound interface reported by WinDivert, before any relay
connection is created. It is a routing scope, not a firewall or a guarantee of
compatibility with every VPN. The persistent WinDivert handle still captures
outbound packets; traffic on excluded adapters is reinjected unchanged without
process attribution or relaying. TCP reflection replies must still be translated,
including late replies after an interface is unchecked. Potentially reflected
TCP fragments still need bounded reassembly; other excluded fragments bypass it.
Adapter discovery follows address-change notifications, with a five-second
fallback scan. Until discovery resolves a new index, the new-interface default
applies. As with process attribution, this is not an atomic security boundary.

### Child processes

**Include child processes** is off by default and saved per rule. A process
matching that rule and its descendants use the same destination. A child's own
enabled rule takes priority; otherwise the closest ancestor with child routing
enabled supplies its route. v2rayN/proxy cores and their descendants remain
excluded, even when an ancestor matches, to prevent routing loops.

While routing is running, a process-only Windows ETW observer records starts and
exits, including intermediate launchers that live entirely between snapshots.
A passive WinDivert SOCKET observer records socket owners and lifetimes. Together
these let captured traffic use a short-lived process's identity and inherited
rule even after that process exits. No additional setting is required.

An initial read-only process snapshot seeds already-running applications; further
process snapshots reconcile once per second. Socket tables and queued events
refresh on a background worker normally after 100 ms, or sooner for unresolved
traffic, with at least 25 ms between completed reads. Pending traffic requests an
ETW buffer flush rather than waiting for the normal trace delivery interval.
Packet processing uses immutable indexes; it does not query ETW or open processes.
The observer enables only process start/stop events, requests a 4 MiB trace buffer
pool, and writes no trace file. It stops when application routing stops.

Creation times distinguish reused PIDs; newer Windows event versions also supply
process and parent sequence numbers. Closed socket history is retained briefly
for packets already captured, while replies require current ownership. Queued
UDP sends can finish after the sender exits; their replies are discarded once
ownership is lost. Rules and interface selection still govern those sends.

A launcher that exited before observation started, inaccessible identity, or a
launch delegated to a system broker may still require an explicit child rule.
Restart the target app after enabling routing or editing rules; observed ancestry
is retained when rules are reapplied. This follows Windows parent-process
relationships, not application/package membership. ETW and socket delivery are
asynchronous: unresolved traffic still has the existing 250 ms bounded wait.
Observer failure, reported ETW event loss or managed queue overflow stops routing
with an error instead of silently continuing with incomplete history. Observer
errors identify the process/socket observer and include the underlying cause;
the normal application log also records the full exception chain. This is
not a firewall: after routing stops, normal Windows routing resumes.

### Destinations

| Outbound tag | Behavior |
| --- | --- |
| proxy | Uses the active profile's generated outbound/chain. |
| direct | Uses Xray's normal direct outbound. |
| block | Rejects matching traffic. |
| Saved profile | Uses that profile's generated outbound/chain. Custom full configurations are excluded. |

Every captured flow goes through the applicable rules from the active main
routing table, preserving their order. There is no separate Apply blocking rules
option: put block rules before later routes as in ordinary routing. A supervised
shared Xray core retains originating process/package identity across the relay.
HTTP/TLS/QUIC sniffing is routing-only and preserves the original destination IP.
Encrypted or unavailable names cannot participate in hostname rules.

Changed effective configurations prepare a new shared core before applying the
new policy. Unchanged configurations reuse their core; failed preparation retains
the previous live policy and reports the failure. The capture engine stays open
through successful updates; changed routes are retired. Ordinary inbound-tag
conditions use the logical `app-routing` tag for this traffic.

The shared core's console output appears unchanged in the main log panel; access
records identify the route with `app-match-... -> outbound` tags. It follows the
normal core logging settings; enabling core log files sends access/error records
to those files instead.
Intercepted traffic can show a destination IP rather than a hostname, including
Windows Time requests to UDP port 123. Application-routing failures appear in the
main log panel instead of popup notifications; `guiLogs` retains diagnostics.

## Scope and limitations

- This is outbound TCP/UDP application routing, not a firewall or kill switch.
  Stopping it, exiting v2rayN, or losing the driver returns applications to the
  normal Windows route. Attributed traffic has no direct fallback while its
  configured relay is active and failing.
- Loopback traffic is excluded. Windows DNS requests made by a shared system
  service cannot be attributed to the calling executable and retain their
  normal route. Applications' own non-loopback DNS sockets follow their rules.
- Process attribution uses Windows TCP/UDP owner tables. Shared UDP ports with
  multiple owners are ambiguous and blocked when an owner has a selected rule.
  A new TCP connection refreshes ownership before choosing its route. Initial
  sequence numbers distinguish a reconnect from a retransmitted SYN while the
  previous relay is still closing.
  UDP sessions check the latest ownership index before sending or delivering
  replies and are discarded after an observed ownership change. Failed associations can be retried on the next datagram.
  Missing or stale ownership is held for up to 250 ms on retries, within a
  512-packet/4 MiB budget, then dropped with a throttled notice. It is not treated
  as a proven unselected application; under load or inaccessible ownership, this
  can also drop otherwise unselected traffic.
  Owner-table sampling still has a race with process/socket teardown; this is
  not a security boundary. Native port-reuse stress testing remains necessary.
- SOCKS5 UDP fragments (`FRAG != 0`) are unsupported; IP fragments are handled
  separately. Known unselected traffic bypasses reassembly after its
  first fragment is classified; later parts use a bounded bypass index. Selected
  or unresolved traffic, fragmented SYNs and reflected TCP replies still need
  assembly. Overlapping, incomplete, expired or excessive assemblies are dropped.
  Reassembly is bounded to 256 assemblies, 16 MiB and 15 seconds per assembly.
  Out-of-order fragments without a classifiable first fragment can still wait
  or hit those limits. Passed-through traffic keeps its original fragments.
- A UDP process/local endpoint/rule shares one socket or SOCKS association across
  remote peers, preserving its outbound source port for that session. Replies use
  their actual source address/port. Wildcard sockets using different local addresses
  or IP families can still have multiple sessions; this is not a kernel socket ID.
- Relay connections are bounded to 2048 TCP and 2048 UDP sessions. UDP queues
  hold at most 64 datagrams and 64 KiB of payload per session, and drop excess
  traffic without blocking packet capture; idle UDP sessions expire
  after 60 seconds. These limits protect memory and do not promise zero loss.
  A datagram that exceeds the outbound socket's size limit is dropped and reported
  without closing its UDP association. SOCKS framing reduces the available payload
  size; application datagrams are not split into SOCKS5 fragments.
  Connection resets during TCP accept are recoverable; a fatal capture/listener
  or maintenance-worker failure, or an unexpected isolated Xray exit, stops and
  cleans up the runtime and reports the error. There is no automatic retry loop;
  the saved enabled preference remains intact for the next launch.
- ICMP, raw IP protocols, inbound servers, multicast/broadcast discovery and
  shared-service traffic are outside the supported application-routing scope.
- Both x64 and x86 Windows builds include application routing. The x86 build can
  run on 32-bit Windows or under WOW64 on x64 Windows. ARM64 interception is not
  provided. Linux/macOS retain their existing behavior and hide the menu item.
- A machine-wide capture lease prevents two copies of this feature from owning
  interception simultaneously. A competing start fails without signalling or
  stopping the existing owner. It does not coordinate third-party filters.
- Interactions with other WinDivert/WFP filters, VPNs and security products must
  be tested separately. Do not use the existing production v2rayN installation
  as a native integration-test environment.

## Build and package

Use the .NET 10.0.1xx SDK required by the project and initialize submodules:

```powershell
git submodule update --init --recursive
powershell -File scripts/Get-WinDivert.ps1 -CurlPath C:\Path\To\curl.exe
dotnet build v2rayN/v2rayN.slnx -c Release -p:EnableWindowsTargeting=true --disable-build-servers
dotnet publish v2rayN/v2rayN/v2rayN.csproj -c Release -r win-x64 -p:SelfContained=true -o artifacts/windows-win-x64 --disable-build-servers
dotnet publish v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj -c Release -r win-x64 -p:SelfContained=true -o artifacts/windows-win-x64-desktop --disable-build-servers
dotnet test --project v2rayN/ServiceLib.Tests -c Release -- --report-trx
```

For 32-bit packages, use `-r win-x86` and separate output folders for both UIs.
The Windows CI test matrix runs self-contained x64 and x86 test hosts, including
native process/owner-table tests. Under WOW64, the child-process fixture also
checks that a 32-bit host can identify and match a 64-bit child.

Run these commands sequentially. Building and running the managed tests does not
load WinDivert or require Windows administrator privileges. Activating application
routing loads the driver and requires running v2rayN as Administrator.

`Get-WinDivert.ps1` downloads the pinned official WinDivert **2.2.2-A** archive,
verifies SHA-256
`63CB41763BB4B20F600B6DE04E991A9C2BE73279E317D4D82F237B150C5F3F15`, and verifies
the driver signatures. It copies dependencies into ignored `v2rayN/WinDivert`.
`Directory.Build.targets` includes the correct native files in Windows publishes,
outside the single-file executable. The x64 package requires the x64
`WinDivert.dll`, `WinDivert64.sys` and `WinDivert-LICENSE.txt` beside `v2rayN.exe`.
The x86 package includes the x86 DLL and both `WinDivert32.sys` and
`WinDivert64.sys`; driver selection follows the OS, not the app's bitness.
The shared x64 and dedicated x86 release workflows prepare these dependencies
before publishing either UI variant. Local builds can run the same download script;
`-CurlPath` accepts an alternative HTTPS-capable curl installation when needed.

The process observer uses the pinned `Microsoft.Diagnostics.Tracing.TraceEvent`
NuGet package. Normal restore/publish includes it; no separate ETW service,
scheduled task, or runtime installer is needed.
Its binary decoder handles process-start schemas v0-v4 and process-stop schemas
v0-v2, including the variable-length security identifier in recent Windows
versions. Unknown schema versions stop observation with an explicit diagnostic;
they are never interpreted using guessed field offsets.

The download script does not load/install a running driver. Keep the upstream
WinDivert license with distributed binaries and retain the corresponding source
and licensing references. Source builds also need separately packaged Xray/core
assets for saved-profile routing, just like ordinary v2rayN release packaging.
Explicit SOCKS5 and outbound-interface rules from earlier previews are no longer supported and are removed on configuration load.

## Implementation and validation

For a source-by-source explanation of configuration, process matching, TCP/UDP
traffic flow, resource ownership, and test coverage, see the
[code walkthrough for reviewers](application-routing-code-review.md).

`AppRoutingManager` supervises a staged `RouteRuntime`, which owns one persistent
engine, its exclusive capture lease, and isolated profile instances. Each profile
instance owns its process, lifetime job and temporary configuration. `AppRouteEngine` reflects
selected TCP connections into local listeners, using a complete five-tuple and
independent translated port for each connection. UDP sessions retain their
SOCKS5 control channel, relay datagrams and inject replies into the original
application flow. The shared-table identity bridge is described in
[routing-block-editor.md](routing-block-editor.md).

Automated tests cover the 80-byte WinDivert address ABI, packet bounds and
rewriting, IPv4/IPv6 fragments, TCP tuple collisions/reconnections, owner matching,
mixed packet batches, maximum packet sizes, partial-batch flushing and UDP buffer ownership,
SOCKS5 authentication and split replies (including domain bind addresses),
cancellation during each handshake stage, shutdown overlapping a final connection,
UDP multi-peer framing/association/cleanup, shared-table identity handoff, staged
replacement/rollback, core/engine supervision, exclusive ownership, delayed
process events, exited launchers, PID/socket reuse and timestamped attribution.
Socket fixtures are loopback-only and never load
WinDivert. Existing core/config tests remain in the full test suite.

Before declaring native support verified, use a Windows VM or dedicated test
host with two adapters and an IPv6-capable test destination. For each route type,
check TCP, UDP/QUIC and both IP versions with packet captures at the destination
and host. Include simultaneous selected/unselected executables, identical source
ports, short-lived launcher/helper chains, one-shot UDP senders, rapid PID/socket
reuse, ETW startup/stop and event-loss reporting, adapter loss, proxy failure,
fragmentation, start/stop/exit and both UI variants. Confirm that unselected
traffic and a separate v2rayN installation remain unaffected. No such privileged
test has been run on the developer's production machine.

References:

- [WireShift architecture reference](https://github.com/Farerudesu/WireShift)
- [WinDivert documentation and limitations](https://reqrypt.org/windivert-doc.html)
- [WinDivert source and license](https://github.com/basil00/WinDivert/tree/v2.2.2)
- [Microsoft TraceEvent library](https://github.com/microsoft/perfview/tree/main/src/TraceEvent)
- [SOCKS5, RFC 1928](https://www.rfc-editor.org/rfc/rfc1928)
- [Windows IPv4 socket options](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options)
- [Windows IPv6 socket options](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ipv6-socket-options)
