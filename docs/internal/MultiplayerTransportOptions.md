# TopiaForge Multiplayer Transport Options — Decision Record

Status: Accepted (pre-gate) · Date: 2026-08-22 · Owner: docs/platform · Repo: repository root

---

## Why this document is internal

It carries monetary figures. `docs/internal/` is excluded from release zips (see `topiaforge release build-package`
and `docs/internal/README.md`), so cost modelling stays out of anything a player receives. **Keep dollar and euro
amounts out of every player-facing document,** including `docs/Multiplayer.md`, `docs/PrivacyAndCapabilities.md`, and
`docs/MultiplayerHostingFeasibility.md`.

## Scope and standing constraint

The [multiplayer hosting feasibility gate](../MultiplayerHostingFeasibility.md) is **open**. Items 1-5 are unproven.
This document records vendor, budget, topology, and abuse decisions so they are not relitigated later; it does not
authorise transport work, does not assume a headless Robotopia server exists, and does not imply a ship date. Nothing
here may be used to advertise live multiplayer.

TopiaForge is a third-party mod framework for Robotopia. We do not own the game and hold no recorded permission from
its owner. That fact is the primary input to the vendor decision below — ahead of price.

---

## 1. Vendor analysis

### Primary criterion: game-identity neutrality

The deciding question is not "what does a relayed byte cost". It is **"does using this service require us to assert a
relationship with Robotopia that we do not have?"** A cheaper relay we are not entitled to register for is not
cheaper; it is unavailable.

| Option | Requires a game-identity relationship? | Verdict |
| --- | --- | --- |
| Epic Online Services (EOS) Relay | **Yes.** Requires an EOS product registration. The registrant is asserting product ownership. | Disqualified |
| Unity Relay (Unity Gaming Services) | **Yes.** UGS binds to a Unity project and organisation. The project is Robotopia's, not ours. | Disqualified |
| Steam Datagram Relay (SDR) | **Yes.** Keyed to a Steam AppID; needs the AppID owner's cooperation. | Disqualified without publisher cooperation |
| Cloudflare Calls TURN | **No.** No product registration, no AppID, no publisher relationship. A generic TURN service. | **Selected** |
| Self-hosted coturn | No. | Deferred — see section 6 |

The original review flagged the ownership problem for EOS only. **It applies identically to Unity Relay.** Unity
Gaming Services is provisioned against a Unity project; a third-party mod framework cannot bind to a project it does
not own without misrepresenting who it is. Recording this here so the "but Unity Relay is right there and the game is
Unity" argument does not resurface: the Unity engine being present in Robotopia grants us nothing on Unity's backend.

SDR is technically the best fit for our exact threat model (see section 5 on IP exposure — SDR exists precisely to
hide peer addresses). It is unavailable to us for the same class of reason: it is the publisher's, not ours.

**Cloudflare TURN is the only option that is game-identity-neutral. That is the decisive argument, not price.** It is
a general-purpose TURN service; using it asserts nothing about Robotopia, its publisher, or our relationship to
either. If the publisher relationship ever changes, SDR should be re-evaluated on the merits.

### Verified pricing facts

Recorded as facts. **Do not re-derive these; correct them only against a fresh vendor quote.**

- **$0.05 per GB.**
- **1,000 GB per month free.**
- **Metering is egress-only** — data sent *from* the Cloudflare edge *to* the TURN client. Ingress is free.
- **There is no spend cap feature.** This is a design input, not a footnote; see section 4.

### Cloudflare Workers have no UDP

Workers cannot open or accept UDP. A TURN data path is UDP. Therefore the control plane (credential minting, lobby
metadata, reachability signalling, kill switch) and the data plane (relayed game traffic) **cannot** live in the same
runtime.

**The control-plane / data-plane split is forced by the platform, not a stylistic preference.** Recorded so it is not
reopened as "could we just do it all in a Worker".

---

## 2. Bandwidth envelope (design constraint, not a measurement)

The wire contract in `TopiaForge.Mods.Multiplayer` is frozen. The bandwidth budget is therefore **decided now** rather
than discovered after a transport exists and the numbers are expensive to change.

### The budget

| Parameter | Value | Notes |
| --- | --- | --- |
| Canonical server tick | **20 Hz** | Simulation cadence. |
| Downstream snapshot/delta send rate | **10 Hz** per client | Deliberately decoupled from tick; the server coalesces. |
| Per-connection sustained ceiling | **256 kbit/s** each direction | 32 KiB/s. |
| Per-connection burst ceiling | **512 kbit/s** for up to 2 s | Join, reconnect, and session-replacement snapshots only. |
| Client-to-server sub-budget | **32 KiB/s** (256 kbit/s) | The half the generator can check — see below. |

Derived, at the sustained ceiling: 32,768 B/s × 3,600 s ≈ **0.12 GB per connection-hour per direction**. Budget
conservatively at **0.24 GB per relayed connection-hour**, assuming both directions of a relayed flow bill as egress
toward a TURN client. If only one peer allocates a relay, the real figure halves; do not plan on the halving.

### What `[NetworkBound]` can and cannot express

`[NetworkBound(maximum)]` sets a per-member character/element bound. The generator already folds those bounds into a
worst-case encoded size per DTO (`CodecModel.MaximumBytes`) and compares it against each declaration's
`MaximumPayloadBytes` (TFMP008) and against the hard 1 MiB transport limit (TFMP011).

**Expressible.** `[MultiplayerCommand]` and `[ReplicatedObject]` each declare `MaximumPerSecond` *and*
`MaximumPayloadBytes`. Their product is a declared worst-case byte rate, and summing it across a contract yields a
client-to-server ceiling that is fully derivable from the frozen contract.

**Implemented.** `TFMP014` sums `Sum(MaximumPerSecond × MaximumPayloadBytes)` over a contract's commands and
replicated objects and reports when it exceeds 32 KiB/s. It is a **warning**, not an error, for two reasons: the
budget is a pre-gate design constraint rather than a shipped wire invariant, and the attribute *defaults*
(`MaximumPerSecond = 30`, `MaximumPayloadBytes = 16 KiB` → 3.9 Mbit/s) exceed it by an order of magnitude, so an error
would break every contract that has not yet thought about the question. Changing those defaults would alter the schema
digest and therefore every checked-in contract lock, which is out of scope. Both first-party samples pass:
CounterSample declares 20/s × 512 B (82 kbit/s) and DroneSample 30/s × 512 B (123 kbit/s).

The budget constant is never encoded, never hashed into a schema digest, and never enters a contract lock. A generator
test asserts it does not appear in generated output, so the check cannot silently become a wire fact.

**Recorded gap — the metered direction is the direction we cannot check.**

- `[ReplicatedState]` declares **no rate**. Snapshot and delta cadence is a provider decision invisible to the
  generator.
- `[PresentationEvent]` declares **neither a rate nor a payload limit** — only the 1 MiB hard transport bound.
- Nothing in the contract expresses **fan-out**. A server-to-client byte is multiplied by lobby size; the generator
  has no notion of how many participants a session has.

So the generator can bound the direction that costs nothing (ingress at the relay is free) and cannot bound the
direction that is billed and multiplied. Closing this gap requires either provider-side accounting (a per-connection
outbound shaper the transport enforces at runtime) or new contract surface for downstream rate — and new contract
surface means a wire-format revision bump, which is out of scope while the gate is open.

**Provider-side enforcement of the 256 kbit/s downstream ceiling is a transport requirement, tracked here. It is not
solvable in the generator as the contract stands.**

---

## 3. Relay demand is host-clustered, not per-connection

### The model

In a player-hosted topology, reachability is a property of **the host**, not of each connection. One unreachable host
relays *every* peer in that lobby. Relay demand is therefore:

    relayed connections per lobby = P(host unreachable) × (L - 1)

for a lobby of size `L`, where the `P` draw happens **once per lobby**, not once per peer.

### Why the distinction is not pedantic

Let `B ~ Bernoulli(P)` be "this lobby's host is unreachable", and `L - 1` the peer count.

| Model | Mean | Variance |
| --- | --- | --- |
| Independent per-connection draws (wrong) | `P(L-1)` | `(L-1) · P(1-P)` |
| Host-clustered (correct) | `P(L-1)` | `(L-1)² · P(1-P)` |

The means are identical — which is exactly the trap. The variance is **`(L-1)` times larger**, so the standard
deviation is `sqrt(L-1)` times larger; at `L = 8` that is ×2.65. The per-lobby distribution is bimodal: a lobby
relays *nothing* or it relays *everything*.

**Consequence: budgeting on the mean under-provisions.** For a service with no spend cap (section 1), the number that
matters is peak concurrent relay load, and peak is driven by the clustered variance, not the mean.

### Worked envelope

Using 0.24 GB per relayed connection-hour from section 2, and 1,000 GB/month free:

| Lobby size `L` | `P(host unreachable)` | GB per lobby-hour | Lobby-hours inside the free tier |
| --- | --- | --- | --- |
| 4 | 0.15 | 0.108 | ~9,300 |
| 4 | 0.35 | 0.252 | ~4,000 |
| 8 | 0.35 | 0.588 | ~1,700 |

The spread across plausible `P` values is larger than any plausible per-byte optimisation. **`P` is the dominant term
in the entire cost model, and we have never measured it.** That is what the launcher reachability probe exists to fix
— see [`LauncherReachabilityProbe.md`](LauncherReachabilityProbe.md).

### The highest-leverage lever is host selection, not bytes

If lobby formation probes reachability across `k` willing candidates and prefers a directly-reachable host, the
probability that the *lobby* needs a relay falls from `P` to approximately `P^k`. At `P = 0.35` and `k = 3`, that is
0.043 — roughly an **8× reduction in relayed traffic** from a control-plane change that moves no bytes.

No compression, delta-encoding, or tick-rate reduction available to us comes close. Recorded as **control-plane
requirements**:

- **CP-R1 — Probe reachability at lobby formation.** Each candidate host reports a reachability classification before
  a lobby commits to it.
- **CP-R2 — Prefer a directly-reachable host.** Host election ranks on reachability first, then on the ordinary
  criteria (latency, willingness, capacity).
- **CP-R3 — Support host migration.** A lobby whose host becomes unreachable mid-session must be able to re-elect
  rather than relay every peer for the session's remaining lifetime.

These are requirements on a control plane that does not exist yet. They are recorded so whoever builds it does not
discover the clustering property after the first billing cycle.

---

## 4. Abuse and spend control

### Why this is required before, not after

Two facts combine badly:

1. **Cloudflare TURN has no spend cap** (section 1). Unbounded egress is a billing incident, not a graceful
   degradation.
2. **Anyone who can run the launcher can mint credentials.** The launcher is publicly distributed. Whatever it can
   reach to obtain a TURN credential, an attacker can reach the same way.

**Short-lived credentials bound the window, not the rate.** A 60-second TTL limits how long a leaked credential keeps
working; it does nothing about how many bytes flow while it is valid. Credential TTL is a revocation-latency control
and must not be mistaken for a spend control.

### Required controls

| Control | Specification | What it bounds |
| --- | --- | --- |
| **Per-ticket GB ceiling** | Every minted credential carries a hard byte ceiling. The counter refuses further allocation once the ticket is spent; the ticket is not renewable in place. | Worst case per credential |
| **Per-IP mint rate limit** | The control plane limits mints per source IP per window, with a separate, tighter burst limit. | Credentials per source |
| **Durable-Object-side counters** | Counters live in a Durable Object keyed by mint scope. A DO is single-threaded and strongly consistent, which is the only place in the Workers model where a counter is authoritative — a Worker-local or KV counter races and undercounts exactly when it matters most. | Correctness of the two controls above |
| **Kill switch** | One control-plane flag halts all minting globally. Its effective latency is the credential TTL, because already-issued credentials keep working until they expire. **That is the real reason to keep TTL short.** | Blast radius over time |

Scope note: these counters are *spend-control state*, not the lobby Durable Object. A DO lobby implementation is out
of scope and deliberately so — it is the cheapest and most replaceable piece, and building it early is what tempts a
project past its own gate.

### The open problem, named honestly

**There is no identity model.** TopiaForge has no accounts. Steam app-ticket validation would give us a real per-user
identity, but it requires a publisher key we do not have and are not entitled to (section 1, same root cause).

Every control above is therefore scoped to a **ticket** or an **IP**. Both are cheap for an attacker to rotate. The
honest statement of what these controls achieve:

- They **do** bound the cost of any single abuser and make casual abuse uneconomic.
- They **do not** bound the *number* of abusers, and they do not survive a distributed source of requests.
- The per-IP limit is also a false-positive source. CGNAT and shared campus or household egress put many legitimate
  players behind one address — and those are disproportionately the players *most* likely to need a relay in the first
  place, because the same carrier NAT that shares the address also breaks direct connectivity.

That last point is a genuine tension with no clean resolution absent identity. It is recorded, not solved.

### Evidence status against the gate

This section is **partial evidence toward gate item 5 (platform operations — abuse handling)**. What exists: a
documented control design with named mechanisms and named limits. What does not exist: executable acceptance tests, an
identity model, and any deployed enforcement. **Gate item 5 remains unproven.**

---

## 5. Cross-references

- **IP exposure** is a player-safety decision, not a transport detail, and is recorded in
  [`docs/PrivacyAndCapabilities.md`](../PrivacyAndCapabilities.md). The short version: the cost model prefers direct
  connections because relayed bytes cost money, and direct connections are exactly what expose player addresses. The
  cheap axis and the safe axis are the same axis, pointing opposite ways.
- **Reachability measurement** — the launcher probe that would measure `P` — is described in
  [`LauncherReachabilityProbe.md`](LauncherReachabilityProbe.md).
- **The gate itself** remains open: [`docs/MultiplayerHostingFeasibility.md`](../MultiplayerHostingFeasibility.md).

---

## 6. Deferred: self-hosting

**The argument against self-hosting early is anycast DDoS absorption. It is not machine cost.** State that plainly,
because the cost arithmetic points the other way and will be rediscovered by the next person who looks.

At roughly 3 TB/month, three Hetzner-class nodes run about **EUR 15/month**. The same volume on Cloudflare TURN is
about **$112/month** (1,000 GB free, then $0.05/GB, so ~3.2 TB total). **That volume is approximately the crossover.
Do not claim managed relay is cheaper.** Below it the free tier wins; above it self-hosting wins on machine cost, and
the gap widens with volume.

We defer self-hosting anyway, because:

- **A public coturn with a wide UDP port range is a DDoS target.** It is a small number of known unicast addresses
  with no absorption capacity, and taking them down takes multiplayer down for everyone at once.
- **It is a reflection and amplification vector.** An open relay that will forward UDP to an arbitrary permissioned
  peer can be pointed at third parties. Worse for us specifically, it can be pointed at **player-hosted sessions** —
  which means our own infrastructure becomes the weapon used against our own users' home connections. Those users did
  not sign up to be a target and cannot defend themselves.
- Cloudflare's anycast edge absorbs volumetric attacks as a property of the network. **That absorption is the product
  being bought — not bandwidth.** The premium is not transit; it is not being a small, fixed, attackable target.

**Revisit self-hosting when**, and only when, measured volume is well past the crossover *and* the operational answer
to volumetric attack and reflection abuse is written down and testable.
