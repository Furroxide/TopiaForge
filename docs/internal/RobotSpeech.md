# Private robot speech

RobotKit's `agent.SayAsync(text, cancellationToken)` requires the exact consuming context that owns the live
host NPC. Existing native `AgentHead.Say` calls on registered RobotKit heads enter that same route. Solo
standalone bodies use exact local ownership. Guest calls fail before native work; guest-owned canonical
bodies cannot borrow a server context. Replicas retain dormant brains and disabled native conversation.
The author chooses the reply and when to speak. The runtime handles ownership, references, earshot,
delivery, synthesis and cancellation. No author networking, serialization or token distribution is needed.

## Audience and disclosure

Only currently synchronized listeners in the same running world, with fresh host-validated positions
within a spherical 20-metre radius, receive text. The source must be a live, visible body with a current owning speech association.
RobotKit's persistent parent belongs to Unity's DontDestroyOnLoad scene; that scene is not the owning
world. Association lifetime, active hierarchy and enabled renderers determine speech visibility.
The host uses the same earshot rule for its own synthesis. Receivers recheck their local position,
world, replica lifetime and native visibility before synthesis and while playback remains active.
Moving the host out of earshot stops only its local playback; other eligible listeners continue.

Packets contain one effective line and voice fingerprint, plus bounded routing/authentication metadata.
There are no audience lists, conversation histories, transcripts, callbacks, credentials, addresses or audio
samples in this contract. Speech is not logged or added to diagnostics, spawn descriptions or baselines.
NPC synthesis does not acquire a microphone, change player voice consent, or call the robot backend.
Brain queries continue to run on the host with its own local credential.

## Native voice binding

The voice fingerprint is SHA-256 over the actual bound `AgentHead.ActiveVoice`: the model/configuration
asset contents, native FMOD event GUID and eleven native synthesis timing/speed settings. No catalog id
is invented. Asset hashes are cached weakly; tuning is read again. Receivers resolve the fingerprint only
against locally loaded native `OvertoneVoiceAsset` instances, capped at 128 candidates. Missing assets,
settings, hooks or matching fingerprints fail closed with `Unavailable`. No assets are fetched.

Playback uses a scoped native `OverrideSpeech`, empty `HintGroup.FromAssets`, and a named `TaskGraph`.
Native calls are serialized per body so cancelled speech cannot restore its override over a newer line.
Only the admitted native playback entry bypasses the routing hooks. Native subtitle-only and pre-recorded
clip overrides are refused on registered heads; neither audio nor arbitrary native arguments are serialized.
Unsupported game bindings require compatible bindings and a restart. Late-loading voice assets permit
retrying a fresh line. Missing receiver assets never cause historical speech to play after recovery.

## Private wire and compatibility

The existing world channel's version 1 permits additive message types. Private speech schema 2 uses
72 (readiness), 73 (line), 74 (cancel), 75 (withdrawal) and 76 (finished). Speech schema 1 peers cannot
exchange readiness with schema 2 peers, so mixed versions receive no speech rather than claiming
unacknowledged completion. Existing spawn/command payloads and public interfaces keep their signatures. The new extension
API is marked `RequiresTopiaForge("0.2.0")`; the RobotKit assembly identity remains `0.1.0.0`.

Every frame starts with the world-channel version/type and speech schema, nonzero world instance,
32-character random recipient challenge, 64-bit nonce and canonical entity id. Readiness uses its nonce
field for the recipient's monotonic timestamp, has entity id zero, and appends its current world key.
Lines append that timestamp, consuming mod, provider kind, 64-character lowercase voice digest and text.
Cancellation identifies the same entity/line without text; withdrawal has zero nonce/entity fields.
Finished identifies the same entity/line and appends exactly one byte: 1 for native task completion,
0 for failure. A receiver emits it on the frame thread only after completion or cancellation.
Decoding requires minimal integers, strict UTF-8, bounded fields and complete payload consumption before effects.

The authenticated transport supplies the sender. Only the host can send lines/cancellation. The receiver
requires its own fresh challenge, current world instance and a live host-owned entity of the named consumer
and provider. A monotonic high-water nonce rejects duplicates and older lines. Challenges are replaced on
stream, world or scene changes; readiness withdrawal removes the previous host gate. Readiness heartbeats
run at most once a second. Readiness and withdrawal share a per-peer burst-four, sustained-two-per-second
budget that withdrawal cannot reset; accepted readiness updates are at least 200 milliseconds apart. The echoed
recipient timestamp expires after two seconds, bounding transport backlog age without synchronized clocks.

## Bounds and lifetime

- Text: 1–512 plain-text UTF-16 characters, at most 2,048 UTF-8 bytes; no markup or controls.
- Frames: at most 2,560 bytes; mod/provider/world keys at most 128 bytes each.
- Host admission: burst four, sustained two lines per second; one new line per robot per second.
- Receiver admission: burst eight, sustained four per second; at most 32 active operations per process.
- Playback: at most 15 seconds, linked to caller and context cancellation; one current line per entity.
- Readiness: at most 256 peers; departed/restarted streams lose their entries.

There is no outbound speech retry queue or late-join replay. A full window drops that recipient's event.
Immediate spawn-and-speak announces the descriptor first. A receiver without that descriptor drops the
line rather than retaining secret text for later. Replacement, despawn, context/provider disposal,
world/scene changes and stream loss cancel outstanding work. Cancellation is best effort over the wire; the receiver also enforces its own deadline.
The native serializer allows only the newest uncancelled line to wait for the prior native call to unwind.

## Completion contract

`Success(true)` means every admitted listener's native playback task completed successfully. The host
waits for its local task (if in earshot) and a finished report from each remote recipient to which the
line was sent. The same rule applies regardless of the host's position. Native task completion is not
proof of physical audible output; a remote report is a statement by that authenticated participant,
not acoustic attestation. No estimated speech duration is used to claim playback completion.

Successful calls also wait until the existing one-second per-robot pace expires. When no listener is
admitted (including no one in earshot, incompatible peers, or no successful sends), `Success(false)`
waits that same second without synthesis. There is no fictitious playback or retained line for a later
listener. Thus an ordinary sequential `await SayAsync(...)` can start the next line without cutting off
the first or hitting its pace, even for an empty audience. Concurrent traffic still shares the unchanged
global budget and active cap; callers must handle admission failures.

A finished report is accepted once only from the admitted authenticated remote sender, for the exact
world instance, connection generation, readiness challenge, entity and line nonce. Current ownership,
readiness and earshot must still hold. Wrong senders, stale streams/worlds, duplicates, malformed
statuses and surplus bytes are rejected. Reports contain no text. The host retains only bounded
per-operation recipient metadata; acknowledgements do not add a queue or late-join history.

Listener failure, loss of eligibility or disconnection records failure while other admitted listeners
finish. Local earshot loss cancels only local playback. Caller cancellation, replacement or source
ownership/provider/world loss cancels the whole operation. A missing report or stalled native task
expires at the existing 15-second deadline; timeouts never count as success. Settlement and all world,
Unity and transport access occur on the frame thread. Reset settles pending callers and rejects
reentrant speech admission until teardown completes. A full acknowledgement window is not retried;
the host times out. Successful operations retire before releasing a sequential caller.

## Verification and remaining native checks

`TopiaForge.ModManager.Tests --robot-speech` exercises the production runtime/parser/provider with controlled
native adapters. `--first-party-replication` includes those cases and the inherited ownership/brain regressions.
Cached native reference builds and GameCompat metadata checks verify signatures, not physical audio.

At both 2560x1440 and 1920x1080, check direct native and safe API calls on Robot and Copbot, audible output,
voice matching, immediate spawn/speak, cold synthesis, earshot crossings, hidden/out-of-world bodies,
replacement, disposal, reconnect and late join. Verify one native synthesis per admitted line, no backend
synthesis requests, no guest brain work and unchanged microphone consent. Two-machine and eight-seat
acceptance remain required. Native arrival tolerance and full-cast availability remain separate open checks.
