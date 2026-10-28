# RobotKit replication

RobotKit uses the consuming context's replication seam for `Spawn` and agent commands. Its provider registers the
`robotkit.agent` kind once, using an internal shared-provider marker; other shared mods may consume the kind without
registering factories or sending packets. The runtime records the consumer, its execution profile and its lifetime.
The host requires both the consuming package and the registered provider to be shared before accepting a guest spawn.
A guest's client-local consumer is refused before construction, with a message explaining the shared-mod requirement.

## Authority and body ownership

Solo and host spawns are canonical. Guest participant spawns use the existing prediction/adoption path and retain
their handle. Scripted shared contexts use shadows. Gamemode server contexts remain host-only. Predictions and
replicas always start dormant, with native talk and locomotion disabled and rigidbodies kinematic. Their animator
reads speed from replicated movement. NPC colliders remain targetable; avatar cosmetic bodies retain disabled
colliders and use presence's capsule instead. The existing avatar factory and microphone arbiter remain in use.

Standalone and client-local consumers have no shared spawn ledger entry. Outside the client realm, their commands
run directly through the registered provider only for the exact entity and kind spawned by that consuming context.
Lifetime admission and command bounds still apply; dead bodies are forgotten and scope cleanup clears local ownership.
These local bodies never enter a shared baseline. Shared consumers always retain the ledger path, including promotion
of their existing solo bodies when hosting starts; a missing shared record never enables a local fallback.

The runtime accepts native roots from the internal provider entity seam, so adoption and entity references use the
same identity anchor as physics hits. Canonical player references use the Players provider's lifetime key and resolve
to the local player or current presence avatar. Departed and replaced player lifetimes do not resolve. `Chase`
translates guest targets to these player/entity references; host-local movement stays direct and allocation-free in
the routing wrapper. Native animation and navigation still require in-game qualification.

## Internal wire contract

World message **71** carries an authority target, consuming mod id, provider kind and bounded payload. The authenticated
channel supplies the sender. The host validates the sender's current proxy, target ownership, shared consuming package
and kind before running the provider under that consumer's forwarded intent. A guest cannot command another player's
or the server's robot. Generic runtime payloads are capped at 16 KiB; RobotKit's version-1 payloads are capped at 2 KiB.

Robot descriptions encode pose, robot type, brain/movement settings, tint, name, scale, talk policy and emote. Commands
encode only the operation and its bounded arguments. Decoding rejects unknown versions/options, non-finite or
out-of-range numbers, invalid UTF-8, markup, oversized text and trailing bytes before applying effects. Callbacks,
credentials, conversation history, prompts, audio and transcripts have no wire representation. A guest spawn or
interaction containing a custom callback is explicitly refused; host-local callbacks remain supported.

Commands issued before adoption wait automatically, so appearance edits cannot break the spawn's descriptor match.
There are at most 128 waiting commands per client; overflow returns `RateLimited`. They share transform pacing and
the host's transform budget (burst 60, sustained 30/s). Dead predictions and stopped consuming scopes discard pending
commands. World replacement, departure and stream restart clear them; commands are not replayed across generations.
Success on a guest means accepted for delivery, not an acknowledgement that the host has applied the command.

Appearance updates replace the ledger description without changing entity id, owner slot, shadow key or lifetime.
Existing replicas update in place; late join and stream recovery use the current world baseline. Pose-only movement
does not re-encode descriptions. The existing world sender handles per-peer backpressure and baseline recovery.
Spawns share the runtime's automatic 64 pending/live objects per participant limit, pacing and departure-grace cleanup.
Disposing the consuming lifetime also disposes its local handles and removes the canonical participant objects.

## Brain and conversation boundary

The resolved autonomous brain-query facade refuses guests before checking local backend/token availability. The
host or solo process uses its own backend. Local conversations receive the existing local service directly; speech
capture keeps the existing microphone lease and local credentials. Replicas never enable native talk. This does not
add speech replication, remote callback execution or persistent ragdoll bodies.

## Verification and limits

`TopiaForge.ModManager.Tests --first-party-replication` exercises the production routing and provider parser with
controlled native adapters: server/guest spawning, immediate and later appearance updates, late join, canonical
Chase, forged ownership, malformed payloads, flooding, quotas, scope/departure cleanup, bounded command queues,
player lifetime references and brain-query authority. The default harness includes these regressions.

`--first-party-local-robots` focuses on standalone and client-local offline mutators, exact-context ownership,
bounded commands, explicit despawn, handle disposal and consuming-lifetime cleanup. These cases also run with
`--first-party-replication`, including the exclusion of client-local bodies from a later shared baseline.

Qualify the real Robot, Copbot and MuniciPal bodies, imported-map pathfinding, native brain suppression, avatar
colliders, replicated speed animation and zero steady-state allocation in the game. No physical-device or two-machine
result follows from controlled adapters. Per-player optional-mod availability and canonical input tick constraints
remain unchanged. CreatorContent, Sandbox, Worlds and Zombies integration is separate from this RobotKit contract.
