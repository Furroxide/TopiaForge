# Native robot speech ownership

RobotKit remains a stable first-party provider without `unsafe-native` or a reference to
`TopiaForge.Mods.Interop.Unity`. Its public author contracts and speech wire format are unchanged.

The existing internal entity-speech seam owns host authority, consuming-context attribution,
private earshot delivery, voiceset identity, quotas, nonces and world/session cancellation.
It does not expose native objects or patch installation. Native hosting uses a separate internal,
in-process seam, compiled into Abstractions and visible only to existing trusted friend assemblies.

`CoreGameplayServices` supplies the native speech host only for RobotKit's provider identity.
The root `ModContext` carries it; child scopes deliberately do not. This does not change the
manifest capability gate for general Unity interop. The host accepts a speech router and returns
an installation lease, never a patch delegate, `MethodInfo`, Harmony object or general native API.

An installation cannot play a native object. It issues a playback capability only after the provider
confirms the exact head and current canonical association. That capability captures its head and
association; each playback rechecks both before native entry and cannot select another head.
RobotReplication creates an opaque generation for every canonical state, including receiver replicas.
Attaching a consuming replication context replaces that generation. The provider checks the exact
state, agent, consuming-context reference and generation, so foreign contexts, withdrawn states and
retired generations cannot borrow playback authority. Public authors receive none of these tokens.

Host associations also call the runtime's existing speech-owner predicate: the consuming context
must still run here and own the exact live ledger record (or local target) of the same kind. A stopped
consumer or removed/replaced ledger record invalidates the capability even while its native body and
provider state remain alive. The installation reads the bound stopping token from the validated router;
caller-supplied cancellation cannot replace it. BeginStop cancels active native work immediately.
Receiver replicas use their canonical receiving state and, when attached, their consuming context's
stopping token; authenticated inbound delivery still supplies receiver authority. A host body gains no
playback capability before a live consuming owner is attached.

RobotAgent owns and retires the capability on rebinding/release, cancelling active native work.
Queued speech captures its capability and rechecks it after serial admission, before resolving or
overriding a native voice. Per-head removal compares the agent reference: cleanup of an old agent
cannot remove a newer association on the same native head. Normal inbound replica playback still
uses authenticated canonical-state delivery and does not require a host-origin speech route.

The runtime resolves the two supported `AgentHead.Say` overloads, applies its own prefixes with
`OwnerHarmonyLease`, and maps routed results to native results. RobotKit retains head-to-agent
associations and voice bindings. A registered head always follows its consuming context's route;
an unsupported form or a synchronous routing failure is rejected without native fallback.
Unregistered native heads keep normal game behavior.

Playback bypass is consumed by the exact initial named call (head, text reference and token).
It is restored in `finally`; reentrant calls cannot inherit it. Playback awaits native completion
and links caller cancellation, provider stopping and installation disposal. BeginStop prevents
new playback before resource drain. The provider still serializes voice overrides per robot.

Installation reserves a single provider and rolls back partial patch application. Failed unpatch
keeps that reservation and owned-head rejection until disposal succeeds; replacement cannot reuse
old prefixes. Successful teardown clears callback, associations and voices. Repeated old-lease
disposal cannot clear a newer installation. Per-agent release retains the existing destroyed-Unity-
object handling, cancellation and corpse-preserving prune behavior.

The host notifies the provider after successful unpatch, even when lifetime cleanup retries a
previous failure or cancellation callbacks throw. A persistent final unpatch failure remains
unavailable and requires restart; it is never reported as a clean installation or successful speech.

Missing hosts or incompatible bindings report unavailable rather than pretending playback succeeded.
No author opt-in, networking setup or manual interception installation is required. No native objects,
callbacks, audio, credentials, addresses or transcript history enter the wire or diagnostics.
Microphone consent and relay-only privacy behavior are unchanged.

Focused tests compile the shipped host/provider source against native boundary doubles and exercise
ownership, failed installation/unpatch, reentry, awaiting, cancellation, stopping and replacement.
The arbitrary-head/false-router regression rejects registration before native entry. Further cases
cover valid playback, foreign context/entity, release, state withdrawal, reused heads, retired providers
and queued retirement with zero native voice resolutions, overrides or speech entries.
The existing native corpse/lifetime and transport tests remain in the focused speech entrypoint.
Actual Harmony execution, native audio, arrival, the full robot catalogue and UI at 2560x1440 and
1920x1080 still need in-game verification; source tests do not prove those outcomes.
