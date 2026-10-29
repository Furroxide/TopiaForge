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
same identity anchor as physics hits. Canonical player references bind the host-issued `MultiplayerParticipant.LifetimeId` to the current session id
with SHA-256 and resolve to the local player or current presence avatar, including the listen host. Persistence
keys are never used for reference identity. Accepted reconnects and session replacements retire old references;
a stalled connection alone preserves the lifetime. Providers without a lifetime token fail reference creation closed. `Chase`
translates guest targets to these player/entity references; host-local movement stays direct and allocation-free in
the routing wrapper. Native animation and navigation still require in-game qualification.

## Internal wire contract

World message **71** carries an authority target, consuming mod id, provider kind and bounded payload. The authenticated
channel supplies the sender. The host validates the sender's current proxy, target ownership, shared consuming package
and kind before running the provider under that consumer's forwarded intent. A guest cannot command another player's
or the server's robot. Generic runtime payloads are capped at 16 KiB; RobotKit's version-2 payloads are capped at 2 KiB.

Robot descriptions encode pose, robot type, brain/movement settings, tint, name, scale, talk policy, emote,
`IsMoving`, `HasReachedTarget` and the last settled movement-command token. Movement commands append a 16-byte
opaque token, echoed by the host; a routed handle awaits its own token before exposing completion, so an older
arrival cannot complete a new GoTo or Patrol leg. The echo settles fully decoded host processing, including
refusal or a native-operation failure; it does not turn the operation result into success. A raced Chase target
that disappears before execution still returns NotFound and preserves the previous authoritative intent. Echoing
that Chase's token restores observations of the existing intent, while neither an older success nor an older refusal
can settle a newer wait. Malformed commands are rejected before effects or token settlement.
Other commands encode their operation and bounded arguments. Decoding rejects unknown versions/options, non-finite or
out-of-range numbers, invalid UTF-8, markup, oversized text and trailing bytes before applying effects. Callbacks,
credentials, conversation history, prompts, audio and transcripts have no wire representation. A guest spawn or
interaction containing a custom callback is explicitly refused; host-local callbacks remain supported.

Commands issued before adoption wait automatically, so appearance edits cannot break the spawn's descriptor match.
There are at most 128 waiting commands per client; overflow returns `RateLimited`. They share transform pacing and
the host's transform budget (burst 60, sustained 30/s). Dead predictions and stopped consuming scopes discard pending
commands. World replacement, departure and stream restart clear them; commands are not replayed across generations.
Stream restart also clears passive acknowledgement waits on retained predictions, including pre-adoption bodies.
A shadow command that sends nothing starts no wait. Binding a prediction or late scripted shadow applies the
current authoritative description immediately, even when no later status change occurs.
Success on a guest means accepted for delivery, not an acknowledgement that the host has applied the command.

Movement status and configuration update a passive replica state; they never call replica `ConfigureMovement`
or enable navigation. The host samples provider descriptions at the existing 10 Hz world tick, publishing only
changed payloads, so arrival without a new command still reaches guests. Unchanged descriptions remain cached.

Appearance updates replace the ledger description without changing entity id, owner slot, shadow key or lifetime.
Existing replicas update in place; late join and stream recovery use the current world baseline. After clearing
interpolation, a changed description also places the body at the baseline pose, including resting bodies. Pose-only movement
does not re-encode descriptions. The existing world sender handles per-peer backpressure and baseline recovery.
Spawns share the runtime's automatic 64 pending/live objects per participant limit, pacing and departure-grace cleanup.
Disposing the consuming lifetime also disposes its local handles and removes the canonical participant objects.

## Brain and conversation boundary

The resolved autonomous brain-query facade refuses guests before checking local backend/token availability. The
host or solo process uses its own backend. Local conversations receive the existing local service directly; speech
capture keeps the existing microphone lease and local credentials. Replicas never enable native talk. This does not
add speech replication, remote callback execution or persistent ragdoll bodies.

## Compatibility

The participant lifetime field changes the engine protocol to **2.0.0**, with peer range `>=2.0.0 <3.0.0`.
Protocol-1 peers are refused at admission. Robot payload version 1 is rejected before effects; the public robot API
and existing participant constructors remain available. Authors continue to call Spawn, ConfigureMovement, MoveTo,
Chase and objectives normally; no token generation, synchronization or reconnect bookkeeping is required.

## Verification and limits

`--replica-state-review` covers movement/arrival, guest GoTo and Patrol, configuration, cross-peer host references,
same-player rejoin retirement, and changed-description recovery pose. `--replica-motion-rejections` queues a Chase,
despawns its canonical target before host execution, and checks refusal settlement, stale success/refusal protection,
follow-on movement and stream retirement. `--participant-lifetimes` in the Multiplayer
harness exercises real engine serialization, reconnect/replacement, malformed lifetime fields and protocol refusal.


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
