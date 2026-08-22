# Launcher Reachability Probe — Design and Approval Dependency

Status: Implemented behind the developer flag, reporting blocked · Date: 2026-08-22 · Owner: docs/platform

---

## What this measures and why it is worth measuring

`docs/internal/MultiplayerTransportOptions.md` section 3 shows that relay demand is
`P(host unreachable) x (lobby size - 1)`, with the probability drawn **once per lobby** because reachability is a
property of the host. Across plausible values of `P` the monthly relay volume spans roughly a factor of five, which
is larger than any per-byte optimisation available to us.

**`P` is the dominant term in the entire multiplayer cost model, and nobody has measured it.** Published NAT-type
distributions are drawn from populations that do not resemble ours — different regions, different carrier mixes,
different eras of CGNAT deployment. Guessing here is guessing at the answer, not at a rounding error.

The launcher can measure it without any of the things the feasibility gate is blocking:

- It does not touch Robotopia, load the game, or run a session.
- It does not need a headless server, a transport, or a closed gate.
- It measures the launcher's own host — **which is the host a session would run on anyway**.
- The launcher is already installed on real players' machines and already makes network calls for update checks and
  the package registry, so the capability surface is not new.

## What it does not do

Non-goals, held firmly:

- **No ICE agent.** No candidate gathering, no pairing, no connectivity checks, no nomination.
- **No TURN client.** No allocation, no permissions, no relayed data.
- **No WebRTC stack.**
- **No always-on collection.** It runs when a person presses a button, and not otherwise.

The implementation is a Binding Request and a Binding Success Response — a handful of small UDP datagrams — and the
RFC 5780 behaviour-discovery sequence built on them. `packages/launcher_data/lib/src/reachability/stun_message.dart`
says the same thing at the top of the file, so the constraint travels with the code.

## Classification

`packages/launcher_domain/lib/src/reachability_probe.dart` derives three values:

- **Mapping behaviour** (RFC 4787): endpoint-independent, address-dependent, or address-and-port-dependent.
  Address- or port-dependent mapping is "symmetric" NAT and is the case a relay cannot be avoided for.
- **Filtering behaviour** (RFC 4787): endpoint-independent (full cone), address-dependent (restricted cone), or
  address-and-port-dependent (port-restricted cone).
- **`HostReachability`** — the single derived answer the cost model consumes: `direct`, `holePunchable`,
  `relayRequired`, `udpBlocked`, or `unknown`.

`HostReachability.requiresRelay` treats `unknown` and `udpBlocked` as needing a relay. **Missing evidence is not
evidence of a good host,** and host election (`CP-R2` in the transport options record) must not read it as a pass.
The classifier likewise refuses to infer mapping behaviour from fewer than three completed transactions rather than
guessing optimistically.

## Privacy design

The privacy claim is **structural, not procedural**. Rather than promising that nobody will log an address, the type
system is arranged so that nobody can.

| Layer | Sees addresses? | Notes |
| --- | --- | --- |
| `launcher_data` STUN codec and runner | Yes, transiently | Compares reflexive endpoints in memory and discards them. Never persists or logs one. |
| `NatObservation` (`launcher_domain`) | **No** | Six booleans and one counter. No field can hold an address, port, hostname, or timestamp. |
| `NatClassification` | **No** | Three enum values. |
| `ReachabilityReport` | **No** | A schema version and three enum names. That is the entire payload that could ever be sent. |

A test in `packages/launcher_domain/test/reachability_probe_test.dart` asserts the report's key set and that no value
looks like an address, so adding an endpoint field to the payload fails the suite rather than shipping quietly.

Only literal `address:port` entries are accepted as probe servers. Hostname resolution is deliberately not
implemented: DNS is a separate failure mode and a separate privacy surface, and the probe does not need it.

## Gating: three separate switches

| Gate | Default | Where |
| --- | --- | --- |
| Developer mode | Off | Existing launcher setting; the Dev tab is hidden without it. |
| Probe enabled | Off | `ReachabilityProbeSettings.enabled`, persisted to `reachability-probe.json`. |
| Sharing consent | Off | `ReachabilityProbeSettings.shareAggregateResults`. |
| **Privacy notice approved** | **`false`, and not a setting** | `ReachabilityProbePolicy.reportingApproved`. |

The settings file fails closed: a missing, unreadable, or malformed document decodes to off, and any non-`true` value
decodes to `false`. Turning the probe off also withdraws sharing consent, so re-enabling it later never silently
restores an agreement the player may have forgotten making.

A **local run** requires developer mode plus the probe being enabled. It is not collection — nothing leaves the
machine — so it does not require the privacy notice.

## The approval dependency, stated plainly

`docs/PrivacyAndCapabilities.md` makes an approved privacy notice a **release blocker** for every TopiaForge data
collection. That applies to this probe exactly as it applies to remote AI and microphone capture.

**Therefore: no result is reported anywhere, and no code exists that could report one.**
`ReachabilityProbeGateway` — the only interface the launcher UI can reach — has three methods: load the opt-in, save
the opt-in, run once locally. There is no report method. Adding one is a visible, reviewable change rather than a
wiring detail, and it would still be refused at runtime while `ReachabilityProbePolicy.reportingApproved` is `false`.

That constant is the release blocker expressed in code. Flipping it requires, first, the privacy/legal owner to
supply approved text covering what is collected, how long it is retained, and how a player withdraws.

**What this costs us:** with reporting blocked, the probe cannot yet produce the population distribution it exists to
produce. What it *does* give us today is a working, tested measurement that a maintainer can run, and a design that
is ready the moment approval arrives. Shipping the collection first and seeking approval afterwards is precisely the
mistake this project has already written down that it will not make.

## Running it

There is **no built-in server list**, so a default install contacts nothing. A maintainer supplies measurement
servers through the environment:

```bash
TOPIAFORGE_REACHABILITY_SERVERS=203.0.113.10:3478,203.0.113.11:3478
```

Behaviour discovery needs a server that advertises an alternate address (RFC 5780 `OTHER-ADDRESS`) and honours
`CHANGE-REQUEST`. Against a server that does not, the probe reports mapping as `unknown` rather than guessing.

Every entry has to name the same address family. One run binds one unconnected socket and decides mapping by
comparing reflexive endpoints across a server address and port, and endpoints in two families are not comparable, so
a mixed list is refused rather than quietly reduced to one of them.

Then: enable developer mode, open the **Dev** tab, switch on **Run the reachability probe**, and press **Run probe**.
The classification is shown in the pane and goes nowhere else.

## Where the code is

| Concern | File |
| --- | --- |
| Classification, observation, `HostReachability` | `packages/launcher_domain/lib/src/reachability_probe.dart` |
| Settings, policy gates, report shape, gateway | `packages/launcher_domain/lib/src/reachability_probe_policy.dart` |
| STUN binding codec | `packages/launcher_data/lib/src/reachability/stun_message.dart` |
| UDP transport | `packages/launcher_data/lib/src/reachability/stun_transport.dart` |
| Probe sequence and server parsing | `packages/launcher_data/lib/src/reachability/reachability_probe_runner.dart` |
| Service: persistence, gating, one run | `packages/launcher_data/lib/src/reachability_probe_service.dart` |
| Bloc handlers | `apps/topiaforge_launcher_flutter/lib/src/launcher_reachability_actions.dart` |
| Developer pane | `apps/topiaforge_launcher_flutter/lib/src/screens/developer_reachability_pane.dart` |

Tests: `packages/launcher_domain/test/reachability_probe_test.dart`,
`packages/launcher_data/test/reachability_probe_test.dart`,
`apps/topiaforge_launcher_flutter/test/widget_reachability_test_cases.dart`.

## Open questions

- **Sample bias.** Only players who enable developer mode *and* opt in will run this, and that population is not the
  player base. Any distribution derived from it needs that caveat attached, loudly. Whether a player-facing opt-in
  can ever be justified is a question for the privacy owner, not for engineering.
- **Longitudinal drift.** NAT behaviour changes with carrier policy and router firmware. One measurement dates.
- **IPv6.** A dual-stack host may be directly reachable over IPv6 and relay-bound over IPv4. The classifier reports
  one answer per run, over the family the configured servers name; measuring both means running the probe twice
  against two server lists and reading the results side by side. Reporting one number per host is future work, not a
  gap that changes the current conclusion.
