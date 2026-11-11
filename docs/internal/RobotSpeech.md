# Private robot speech

RobotKit's `agent.SayAsync(text, cancellationToken)` requires the exact consuming context that owns the live
host NPC. Existing native `AgentHead.Say` calls on registered RobotKit heads enter that same route. Solo
standalone bodies use exact local ownership. Guest calls fail before native work; guest-owned canonical
bodies cannot borrow a server context. Replicas retain dormant brains and disabled native conversation.
The author chooses the reply and when to speak. The runtime handles ownership, references, earshot,
delivery, synthesis and cancellation. No author networking, serialization or token distribution is needed.

## Audience and disclosure

Only currently synchronized listeners in the same running world, with fresh host-validated positions
within a spherical 20-metre radius, receive text. The host checks each listener's world-view leases
before sending the line. The source must have a current owning speech association, an active hierarchy
and an enabled renderer. For a renderer under a view hide, the source check uses its value before the
hide. The owning association also covers bodies under Unity's persistent DontDestroyOnLoad root.
The host applies the same earshot and view rules for its own synthesis. Receivers recheck their local
position, world, replica lifetime and native visibility before synthesis and while playback is active.
A listener who moves out of earshot or has the robot hidden from their view loses their own playback.
Other eligible listeners continue. Restoring a view permits fresh lines.

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

The existing world channel's version 1 permits additive message types. Private speech schema 3 uses
72 (readiness), 73 (line), 74 (cancel), 75 (withdrawal) and 76 (finished). Schema 3 adds a terminal decline
status that older hosts cannot interpret. All five message types require an exact schema match, including
readiness: schema 1 or 2 peers cannot exchange readiness with schema 3 peers. Mixed versions can still
join the session, but exchange no robot speech; they are never admitted to each other's speech operations.
Existing spawn/command payloads and public interfaces keep their signatures. The extension
API is marked `RequiresTopiaForge("0.2.0")`; the RobotKit assembly identity remains `0.1.0.0`.

Every frame starts with the world-channel version/type and speech schema, nonzero world instance,
32-character random recipient challenge, 64-bit nonce and canonical entity id. Readiness uses its nonce
field for the recipient's monotonic timestamp, has entity id zero, and appends its current world key.
Lines append that timestamp, consuming mod, provider kind, 64-character lowercase voice digest and text.
Cancellation identifies the same entity/line without text; withdrawal has zero nonce/entity fields.
Finished identifies the same entity/line and appends exactly one status byte: 0 for playback failure,
1 for native task completion, or 2 for decline before playback. No other values or trailing fields are
accepted. A decline carries no reason, text or visibility flag. A receiver emits a finished report on
the frame thread after playback completion/cancellation, or immediately when it declines a valid line.
Decoding requires minimal integers, strict UTF-8, bounded fields and complete payload consumption before effects.

The authenticated transport supplies the sender. Only the host can send lines/cancellation. The receiver
requires its own fresh challenge and current world instance before considering a line. Playback also
requires a live host-owned entity of the named consumer and provider. A monotonic high-water nonce rejects
duplicates and older lines without a reply. Malformed frames, future echoed timestamps, wrong schemas and
unauthenticated senders also receive no reply. A well-formed current-host line with a fresh nonce may be
declined for an expired timestamp, receiver budget/cap, missing replica, local earshot or visibility refusal.
The nonce is consumed even when the line is declined; retrying it cannot solicit another response.
Challenges are replaced on stream, world or scene changes; readiness withdrawal removes the previous host
gate. Readiness heartbeats
run at most once a second. Readiness and withdrawal share a per-peer burst-four, sustained-two-per-second
budget that withdrawal cannot reset; accepted readiness updates are at least 200 milliseconds apart. The echoed
recipient timestamp expires after two seconds, bounding transport backlog age without synchronized clocks.

## Bounds and lifetime

- Text: 1–512 plain-text UTF-16 characters, at most 2,048 UTF-8 bytes; no markup or controls.
- Frames: at most 2,560 bytes; mod/provider/world keys at most 128 bytes each.
- Host admission: burst four, sustained two lines per second; one new line per robot per second.
- Receiver admission: burst eight, sustained four per second; at most 32 active operations per process.
- Decline replies: a separate burst-eight, sustained-four-per-second budget, not reset by world/stream changes.
  A full admission budget can therefore close a line; fresh-nonce floods still cannot create unbounded replies.
- Playback: at most 15 seconds, linked to caller and context cancellation; one current line per entity.
- Readiness: at most 256 peers; departed/restarted streams lose their entries.

There is no outbound speech retry queue or late-join replay. A full window drops that recipient's event.
Immediate spawn-and-speak announces the descriptor first. A receiver without that descriptor declines the
line without retaining secret text for later. Replacement, despawn, context/provider disposal,
world/scene changes and stream loss cancel outstanding work. Cancellation is best effort over the wire; the receiver also enforces its own deadline.
The native serializer allows only the newest uncancelled line to wait for the prior native call to unwind.

## Completion contract

`Success(true)` means at least one listener completed native playback and every listener still accepting
the line completed successfully. The host waits for its local task (if in earshot) and a finished report
from each remote recipient to which the line was sent. When the host accepts a decline before playback,
it removes that recipient immediately without failing the call or counting it as successful playback.
A later departure leaves that decline or an accepted completion intact. Other listeners must still
finish. The same rule applies regardless of the host's position. Native task completion is not
proof of physical audible output; a remote report is a statement by that authenticated participant,
not acoustic attestation. No estimated speech duration is used to claim playback completion.

Successful calls also wait until the existing one-second per-robot pace expires. When no listener accepts
(including no one in earshot, incompatible peers, no successful sends, or accepted declines from every recipient),
`Success(false)` waits that same second without synthesis. There is no fictitious playback or retained line
for a later listener. Thus an ordinary sequential `await SayAsync(...)` can start the next line without cutting off
the first or hitting its pace, even for an empty audience. Concurrent traffic still shares the unchanged
global budget and active cap; callers must handle admission failures.

A finished report is accepted once only from the admitted authenticated remote sender, for the exact
world instance, connection generation, readiness challenge, entity and line nonce. At receipt, the
host checks current ownership, readiness, earshot and the recipient's view eligibility for all finished
statuses, including decline. Wrong senders, stale streams/worlds, duplicates, malformed
statuses and surplus bytes are rejected. Reports contain no text. The host retains only bounded
per-operation recipient metadata; acknowledgements do not add a queue or late-join history.

Loss of a local or remote listener whose playback is pending records `Unavailable` while the other
admitted listeners finish. For example, leaving earshot or losing a view during playback stops that
listener's copy. A failed remote playback report or a local provider cancellation outside whole-line
cancellation also records `Unavailable`. If the host still awaits a native task or a report at
the 15-second deadline, it stops the remaining copies and returns `TimedOut`. Caller cancellation,
replacement, or invalidation of the source, owner, provider or world returns `Cancelled`. Cancellation
already observed when the host checks the deadline takes precedence. A receiver enforces its own
15-second deadline and reports failure through the existing finished status.

Frame maintenance observes caller and owner cancellation and the host or receiver deadline. Native
playback receives its own token, which the runtime cancels on the frame thread during maintenance or
explicit teardown. The host records listener loss and selects its deadline result before stopping the
affected native copy. Settlement and all world, Unity and transport access occur on the frame thread.
Reset settles pending callers and rejects reentrant speech admission until teardown completes. A full
acknowledgement window or exhausted decline reply budget leaves the host waiting until its deadline.
Successful operations retire before releasing a sequential caller.

## Verification and remaining native checks

`TopiaForge.ModManager.Tests --robot-speech` exercises the production runtime/parser/provider with controlled
native adapters. `--first-party-replication` includes those cases and the inherited ownership/brain regressions.
Cached native reference builds and GameCompat metadata checks verify signatures, not physical audio.

At both 2560x1440 and 1920x1080, check direct native and safe API calls on Robot and Copbot, audible output,
voice matching, immediate spawn/speak, cold synthesis, earshot crossings, hidden/out-of-world bodies,
replacement, disposal, reconnect and late join. Verify one native synthesis per admitted line, no backend
synthesis requests, no guest brain work and unchanged microphone consent. Two-machine and eight-seat
acceptance remain required. Native arrival tolerance and full-cast availability remain separate open checks.
