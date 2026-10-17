# Multiplayer runtime integration — design record

Status: Implementing (WS3.1 to WS3.3 of the live-multiplayer plan) · Date: 2026-10-16 · Owner: multiplayer

This records how TopiaForge's runtime drives the multiplayer provider: the internal SPI between them, the provider's
side of it, ModManager's composition root and its frame, and where later WS3 slices plug in. How the provider hosts and
joins live sessions is recorded in [MultiplayerLiveSessions.md](MultiplayerLiveSessions.md), and how the session engine
works in [MultiplayerSessionEngine.md](MultiplayerSessionEngine.md).

The slice lands in two steps:

1. **The SPI, the provider's implementation and the composition root** (sections 1 to 9).
2. **The realm and the factory seam** (sections 10 and 11), which use the replication model's `NetRealm` and
   `ModNetProfile`. Those sections record the design they follow until that step lands.

## 1. Shape

| Part | Where |
| --- | --- |
| The SPI | `src/TopiaForge.Mods.Multiplayer/Internal/`, namespace `TopiaForge.Mods.Internal` |
| The provider's control, content and join targets | `mods/TopiaForge.Multiplayer/Provider/` |
| The engine's system channels, readiness holds and world sources | `src/TopiaForge.Multiplayer.Engine/Core/SessionEngine.System.cs`, `SessionEngine.Runtime.cs`, `System/` |
| The composition root and the runtime host | `src/TopiaForge.ModManager/Multiplayer/` |

- **Visibility.** Every SPI type is internal. The SDK assembly grants its internals to the provider, ModManager, the
  test rig and the three test harnesses that use them. Nothing public changes, so the SDK API baselines do not either.
- **Ownership.** The registry reserves the multiplayer SDK assembly's contracts for the multiplayer provider, so only it
  can publish `IInternalMultiplayerControl`. ModManager is the control's only caller; the in-game interface (WS5)
  reaches it through the runtime.
- **Threads.** Every member runs on the main thread, which owns the session engine. The one exception is a voice
  playback stream's `Read`, which belongs to the audio thread.
- **Privacy.** No SPI member carries or exposes a network address. A join target is an opaque handle (section 3), and
  failures name a reason. An architecture test scans every SPI type's members and parameters for address-shaped names
  and network types (section 9).
- **Unity.** The composition root and the runtime host are Unity-free, so the ModManager and multiplayer harnesses
  compile and run them. The one Unity call, `Application.runInBackground`, sits behind `IMultiplayerPlatform`, which
  `CoreGameplayServices` implements.

## 2. The control

`IInternalMultiplayerControl` is the provider's control surface. Its members group as follows.

| Group | Members | Notes |
| --- | --- | --- |
| Frame | `AttachRuntime`, `DetachRuntime`, `Pump`, `Flush`, `Role` | Section 7. `Role` is `Solo`, `ListenHost` or `Client`, and is cheap enough to read every frame. |
| Hosting | `StartHosting`, `StopHosting` | Refused while joining (a join waiting for the identity included), while hosting, without an attached runtime, or with a standalone mod enabled, naming the mod. |
| Joining | `TryCreateJoinTarget`, `Join`, `SupplyPassword`, `Leave`, `Query` | A join starts once the identity it proves has loaded; an identity that cannot load ends the join, and the next join loads it again. Leaving is never refused; leaving a join that has not started ends it as cancelled. A query, like every member, answers only on the main thread. |
| LAN | `StartBrowsing`, `StopBrowsing`, `LanSessions` | Each listing carries a join target, never a path. |
| Moderation | `Kick`, `Ban`, `Unban`, `GetBans`, `SetServerMute`, `IsServerMuted` | Host only. |
| Session | `ReplaceSession`, `OpenSystemChannel`, `HoldReadiness`, `IsReadinessHeld`, `IsParticipantReady` | Sections 5 and 6. |
| Sources | `SetEntityIdentitySource`, `SetAvatarSource` | Shape only for now; section 8. |
| Voice and counters | `Voice`, `GetStatistics`, `GetStatus` | `Voice` is null until the voice service implements the SPI. |

- **Results.** Commands answer with `OperationResult<bool>`: `NotAuthoritative` for a host-only action on a joined
  player, `InvalidState` when the current role or phase forbids it, `Unavailable` while live sessions are switched off,
  without an attached runtime, or for a target only the TopiaForge service can resolve, `InvalidArgument` for bad
  input, and `NotFound` for an unknown player or ban.
- **Status.** `GetStatus` returns one snapshot: the role, the host's phase and failure, the join's phase, failure and
  detail, the host's message, the host's admission code, the content problems and whether the host wrote them, a
  changed server identity, the joined host's verified description, browsing, connected players and readiness. It
  allocates, so the interface reads it on change. `JoinDetail` is always this game's own words; what the host wrote is
  kept apart, as the next section sets out.
- **Bans.** A ban is listed by an opaque 32-character id, the first 128 bits of a labelled SHA-256 of the banned key,
  so the interface can lift a ban without ever holding the key.
- **Server mute.** A mute follows the player's identity until hosting stops. The host drops a muted player's chat at
  ingress (section 5), so nothing the host relays can carry it. Voice joins it when the voice engine lands.
- **Names.** The provider names this player after the runtime's display name whenever it hosts, in solo play too, and
  again whenever the name changes. A joining player sends the name in its join request.
- **Developer switch.** The `TOPIAFORGE_LIVE_MULTIPLAYER` switch still drives live sessions directly through the
  provider, as section 22 of the live-sessions record describes. It logs a join's phase and its failure's name, never
  the host's words. The in-game interface will use the SPI instead.

### Host-authored text

Some text in the status, the listings and the session comes from the host, and a host may write anything there, an
address included. It is:

- `InternalLiveStatus.HostMessage`: the host's kick, ban, refusal or end message;
- `InternalLiveStatus.ContentProblems` when `ContentProblemsFromHost` is set: the lines of the host's admission report;
- every text in `InternalServerInfo` and `InternalLanListing`: the server's name, build, runtime and protocol versions,
  gamemode and world, as the host's descriptor or announcement gives them;
- the participant list: each participant id, which the host assigns, and each display name, which a player chose and
  the host relays as it likes. These are the one exception to keeping host text away from mods: a mod sees them in
  `MultiplayerSessionSnapshot.Participants`, and as the sender of what it receives. They are bounded identities, at most
  128 characters without control characters, and every surface that shows one shows it as a name, never as an
  instruction or a link. The runtime redacts a participant id wherever it logs or keeps one, as it does a diagnostic's.

This game's own texts (`JoinDetail`, query details, and the content problems of its own check) never carry
host-authored text, except the host's package ids and versions, which the descriptor validates as identifiers. A
refused descriptor request is described from the host's status, never by repeating it: "The host refused the request.",
"The host is busy; try again shortly." or "The host is not serving a session.". The rules for the interface (WS5), the
launcher and anything else that shows a status:

- show host-authored text only as text, attributed to the host ("The host says: ...");
- never log it, put it in the diagnostics bundle, a crash report or an outcome file, or offer it through "Copy details"
  without running it through the runtime's redactor first;
- keep it off every surface a mod can see, but for the participant list above. The engine already does its part: a
  submission a session end cancels fails with the engine's own text, never the host's message, and the session snapshot
  carries only the end reason.

The provider and the runtime keep to the same rules for their own logs: the developer switch and the runtime log a join's
failure by name only, and an engine diagnostic about a replicated object names the type this game registered, never the
id the host chose for it.

## 3. Join targets

`TryCreateJoinTarget` reads what the player typed or followed, ignoring surrounding spaces.

| Input | Target |
| --- | --- |
| `topiaforge://join/<code>` or a bare invite code such as `ABCDE-12345` | An invite, which needs the TopiaForge service (WS7) |
| `topiaforge://join/server/<server id>` | A listed server, which needs the service too |
| `topiaforge://join/direct/<server id>/<host>/<port>` | A direct target pinned by the server id |
| `host`, `host:port`, `[IPv6]:port` or a bare IPv6 address | A direct target; the port defaults to 27250 |
| A LAN listing's target | The session heard on the local network |

- Ten characters that read as an invite code are one unless they hold a dot or a colon, so a dotted host name or an
  address is never mistaken for a code.
- The handle keeps the text inside the provider. It shows only its kind, a description (`direct session`,
  `LAN session`, `listed server` or `invite ABCDE-12345`) and the server id a link named. `ToString` returns the
  description, so logging a target cannot leak it.
- `Query` resolves a target and checks it as a join would, without connecting: the build, the protocol, the privacy
  choice and the content. It honours relay-only exactly as a join does.

## 4. The runtime host

`IInternalMultiplayerRuntimeHost` is what the provider needs from the runtime. ModManager's `MultiplayerRuntimeHost`
implements it, and the provider's `RuntimeContent` turns it into the live sessions' content. Without an attached
runtime nothing is announced and nobody is admitted.

| Member | Value |
| --- | --- |
| `GameBuild` | The game version's patch number, such as `2545`; `unknown` when the version is not known |
| `RuntimeVersion` | The loader's semantic version |
| `DisplayName` | The player's name, 1 to 24 printable characters, `Player` until the settings set one |
| `LocalContent` | One descriptor per loaded package, sorted by id; the first read can fail when a package cannot be read |
| `EvaluateAdmission` | The host's decision on a player's offer |
| `CheckHostContent` | A joining player's check of the host's packages |
| `SetRunInBackground` | On while online; off restores the game's own setting |
| `ReportDiagnostic` | Takes provider diagnostics, redacted before they go anywhere |

### Descriptors

Each loaded package is described from its manifest and the hash multiplayer compares it by.

- **Mode.** No `multiplayer` block is implicit sharing. A declared session mod carries its presence, protocol, peer
  range and synchronized files with their packed hashes. A session mod without a readable protocol, or a mode the
  runtime does not know, is described as standalone, so it can never take part in a session by accident.
- **Hash.** The SHA-256 of the archive the install receipt records. When the receipt cannot be read, a deterministic
  hash of the installed files stands in: each file's portable path, a zero byte, its length and its SHA-256, in ordinal
  order, under a label. The receipt is left out and links are not followed. A hand-copied development build therefore
  matches only the identical files elsewhere.
- **Packages that cannot be read.** Those files are hashed at the first read of `LocalContent`, on the main thread. A
  file that cannot be read then, as when another program holds it open, fails the read with an `IOException` that names
  the package, never its folder, and the next read tries again. The provider treats this game's content as unreadable,
  reports `content.read-failed`, and nothing connects:
  - hosting through the control returns `Io` with "This game's mods could not be read: a file of one of them is in use
    or cannot be opened.";
  - a live host started directly, as the developer switch does, fails as `ContentUnreadable` before anything binds;
  - a joining player's check ends the join as `ContentUnreadable`, "This game's mods could not be read.", with that line
    as its content problem;
  - a running host refuses every player as `not-ready`, with "The host could not read its own mods.".
- **The hash's cost.** On a Windows 11 x64 machine with the files cached, 512 MiB in 64 files hashed in 0.44 s on
  .NET 10 and in 3.9 s on Mono 6.13, whose SHA-256 is managed code, and 20,000 files of 4 KiB in 1.2 s and 3.2 s. A
  large hand-copied package can therefore hold the game's frame for seconds the first time multiplayer reads it. Installed
  packages carry a receipt and cost one small read each. Hashing off the main thread is later work (section 12).
- **The host's descriptor** lists implicit mods as `implicit` and session mods as `session-required` or
  `session-optional`. Client-local, server-only and standalone mods are never listed.

### The offer

A joining player presents its content in the join request, inside the engine's 64 KiB field. Version 1 is binary,
written with the engine's protocol primitives: the game build, the TopiaForge protocol version and range, then each
mod's id, version, archive hash (32 bytes), mode and, for a session mod, its presence, protocol, range and synchronized
files. Mods are sorted by id, each once. Client-local mods are never presented: they are nobody else's business.
Reading is strict and bounded, and anything else is a protocol refusal. 512 implicit mods fit; an offer that cannot fit
is reported to the player before anything connects.

### Admission on the host

The host runs `MultiplayerAdmissionPlanner` with itself as the server, the offer as the client, the `Compatible` policy
and the `ListenHost` role. The server profile is the real manifests of the loaded packages; the client profile is the
offer, rebuilt as manifests carrying exactly what it says. A refusal carries an admission report of code `content`,
whose problems are display lines naming mods and versions:

| Mismatch | Line |
| --- | --- |
| Game build | `The host runs Robotopia build 2545; this game runs 2478.` |
| TopiaForge protocol | `The host's multiplayer protocol cannot play with this game's.` |
| Missing on the player | `Lobby 1.0.0 is missing.` |
| Missing on the host | `The host does not run Party; disable it to join.` |
| Implicit package differs | `Sandbox: the host runs 0.2.0; this game has 0.3.0.` or `Sandbox 0.2.0 differs from the host's copy.` |
| Session protocol | `Lobby: the host's 1.0.0 cannot play with this game's 2.0.0.` |
| Synchronized files | `Lobby: its synchronized files differ from the host's.` |
| Server-only on the player | `Rules runs only on a host; disable it to join.` |
| Standalone | `Solo refuses multiplayer; disable it to join.` |
| Unreadable manifest | `This game's copy of Lobby cannot be used in multiplayer.` |

Lines come in the planner's order, each once, and each within the report's 256-character display bound. `RuntimeContent`
returns them as the live sessions' `ContentAdmission`, and the host's policy builds the admission report from it, so the
joining player sees them as `RejectionProblems` and the status's content problems. An offer that cannot be read is a
`protocol` refusal. A runtime whose check throws, a host that cannot read its own packages, or no attached runtime at
all, refuses the player with a report of its own code, `not-ready`, and the failure is reported. The player reads "The
host is not ready for players yet; try again shortly.", with the host's line saying why, never that its packages differ.

### The joining player's check

Before anything connects, the joining player checks the host's verified descriptor as far as the descriptor allows; the
host's planner decides the rest:

- every implicit package must be installed as the identical archive, because every player needs the identical package;
- every required session mod must be installed; its version, protocol and synchronized files are the host's planner's to
  judge, which admits another version whose protocol is compatible (WS1a and WS1b);
- optional session mods never block;
- an implicit or required session mod enabled here but not run by the host must be disabled, as the planner would also
  say;
- a server-only or standalone mod enabled here must be disabled;
- client-local mods are this player's own business.

So a player with Lobby 1.0.1 joins a host with Lobby 1.0.0 at the same protocol, while a player with another copy of an
implicit Sandbox is told before anything connects.

### Running in the background

The provider turns running in the background on when this process becomes a host or a client, and off when it returns
to solo play. Turning it off restores whatever the game had before, so single player behaves as it did; Robotopia
already runs in the background by default (`MultiplayerMonoSpike.md`, section 6). Detaching at shutdown restores it as
well.

### Diagnostics

The provider routes its engine diagnostics and live-session failures to the runtime once attached. The runtime redacts
every text before it writes it, the diagnostic's code and participant included. It splits the text into tokens at white
space, quotes, brackets of every kind, commas, semicolons and bars, and replaces a token whole with `[address]` when the
token, or what it percent-decodes to, holds:

- a URL, `scheme://authority`;
- an IPv4 address in four parts, even glued to a word or a dot; or in any shorter, hexadecimal or octal form the classic
  parsers accept (`10.20.7720`, `0x0A.0x14.0x1E.0x28`, `0x0A141E28`). Fewer than four dotted decimal parts that each fit
  a byte stay, since they read as a version;
- an IPv6 address the platform's parser accepts once a zone and a port are stripped, mapped and scoped forms included,
  even glued to a label (`endpoint:2001:db8::1`) or a word (`at2001:db8::1`);
- a `label:port` form whose label holds a letter (`myhost:27250`, `play.example.org:27250:`, `File.cs:42`);
- a host name in any part between `:`, `/`, `\`, `@`, `=`, `?`, `&` and `#` (`host:play.example.org`,
  `play.example.org/join/ABCDE`, a share's `\\fileserver.corp.local\share`): dotted labels ending in a top-level domain,
  which is any word of two letters or more written in one case, any two letters, a punycode label, or a common domain in
  any case (`MyServer.duckdns.org`, `Play.Example.Org`, `lab.example.edu`, `game.example.il`).

It errs towards removing too much. A four-part version goes, and so do file names and mod ids, which read like host
names: a stack frame keeps its method and loses its file. Times such as `12:30`, versions such as `1.0.0`, and code names
whose last part mixes cases, such as `System.Net.Sockets.SocketException`, stay; so do diagnostic codes, which end in a
hyphenated part (`content.admission-failed`). A failure's exception keeps its type; its message and, for errors, its
stack trace are redacted too, and an inner failure is never written. The newest 64 redacted lines stay for the
diagnostics bundle. A live join that ends is reported by its failure's name only, since a kick's message is the host's
own text.

The provider's own log takes a diagnostic when no runtime is attached, before ModRuntime attaches or after it detaches,
and when the attached runtime throws instead of taking one. It gets the line the runtime would have written: the
provider compiles the runtime's `MultiplayerDiagnosticRedactor` from its source, as the test harnesses do, and formats
with it. The exception never reaches that log as an object, since a mod log writes an exception's whole chain as it is,
and a log that throws loses the line rather than the frame.

## 5. System channels

The runtime's own traffic between peers travels as engine `SystemMessage` envelopes (type `0x20`), apart from any mod's
contract. `OpenSystemChannel` opens one of four channels, one owner each: `WorldReplication`, `Avatars`, `Chat` and
`SceneFollow`.

- **Wire.** `[epoch varint][join generation varint][kind byte][payload bytes]` on the session channel, reliable and
  ordered with the session's own traffic. `SystemCredit` (type `0x21`) carries `[epoch][join generation][kind][consumed
  messages][consumed bytes]`, cumulative for the stream. The host gives every accepted connection its join generation
  in `JoinAccepted`.
- **Targets.** A joined player sends to the host only. The host sends to everyone, one player, or everyone but one.
  Messages go to joined connections, players still synchronizing included, so a joining player receives the world
  before it is Ready. A joined player hears its host's messages from the host's player, or, from a host without one such
  as a dedicated server, from `InternalSystemMessage.HeadlessHostSender`, an id no player is ever given.
- **Streams.** A stream is one channel between this process and one peer, from that peer's join or the session's last
  replacement. Within a stream, every message the channel accepted arrives once and in order. A stream restarts when:
  - the host replaces the session: each side drops what its channels hold from the old session;
  - the player rejoins, even within the grace period that keeps its session and epoch: each side drops what its channels
    hold from the old connection, the player counts what its lost link never sent, and what was in flight is gone.

  A message stamped with another epoch or join generation is dropped and counted.
- **Generations.** `Generation` changes whenever a stream through the channel starts, restarts or ends, and
  `GetGeneration(peer)` gives the stream with one peer, zero when there is none. An owner that keeps state in step with a
  peer compares them each frame and starts again from a baseline when they change: world replication sends the
  returning player a new baseline, and the player discards what it had.
- **Transactions.** Sending inside a transaction is refused, because a transaction can still abort.
- **Windows.** A host may have 1,024 messages and 1 MiB outstanding to each player on each channel, and a player 64
  messages and 64 KiB to its host: sent, but not yet taken, dropped or left unclaimed by the receiving owner. The
  receiver reports what left its channel in credits at each flush, and the sender sends only within what is left, so a
  receiver holds at most one window per sender and an honest sender is never dropped, however long the receiver's game
  stalls. A full window holds back only its own stream:
  - a host's message to everyone, or everyone but one, goes to each selected player whose window has room. Each player
    whose window is full is skipped and named in `Send`'s `skipped` list; it misses that message, so its owner sends it a
    new baseline once `CanSend` to that player says there is room;
  - a message to one player, or a joined player's to its host, is refused whole with `RateLimited` when that window is
    full;
  - `CanSend` says whether a message would reach every selected peer.

  So one player can never crowd out another. A chunked baseline larger than the window is paced by its credits.
- **Stalls.** A Ready player whose window on a channel stays full for two minutes (`InternalSystemLimits.StallSeconds`),
  with the host waiting to send and the player crediting nothing, ends its connection as a protocol violation. Any credit
  starts the clock again, so a player whose game stalls for a while and then takes its messages recovers and keeps its
  connection. Before a player is Ready its stream can wait behind the snapshot on a slow link, so the stall counts from
  Ready, and the host's Ready bound (section 6) covers the time before.
- **Rate.** A joined player keeps to 256 messages a second over all channels, after a burst of 256, and is refused
  locally past that. Its host allows twice that, a burst of 1,024 and 512 a second, so bunched arrivals never trip an
  honest player. The rate counts only messages for the player's current streams: one stamped with an older epoch or
  join generation was sent before the player saw a replacement or its rejoin, and each replacement lets an honest player
  send a window on every channel again, so stale messages are never charged. They have an allowance of their own, the
  same burst and rate: within it they are dropped and counted, past it they are discarded uncounted, and neither ever
  ends their sender. A stale message costs only its header, never a copy of its payload.
- **Abuse.** A peer that sends past its window, credits what it was never sent, sends faster than its host allows, sends
  an oversized payload, or sends a system message before joining, ends its connection as a protocol violation, and so
  does a Ready player that stalls its window as above.
- **Unknown kinds**, which a newer peer may send, are left unclaimed and credited as taken at once, and so are messages
  for a channel nobody has open. A minor protocol version can therefore add a channel: the joining checks compare major
  versions only, and an older peer's credits keep a newer sender's window open.
- **Server mute.** The host's chat channel refuses a muted player's messages as they arrive.
- **Delivery** is by polling: the owner takes messages with `TryReceive` during its frame stage, which opens its sender's
  window again.

Unreliable delivery for poses (WS3.10) and the voice path (WS2g) are separate: the avatars slice adds an unreliable
lane over the link's realtime channel, and voice uses its own channel.

## 6. Readiness

`HoldReadiness(reason)` holds a joining player short of Ready while the runtime prepares, for example while it loads the
host's world and applies its baseline (WS3.6, WS3.8).

- With a hold open when the host's snapshot completes, the client stays `Synchronizing` and does not acknowledge, so the
  host neither delivers presentation events nor counts the player as ready. Releasing the last hold makes it Ready and
  acknowledges at the next flush. A hold released earlier never delays anything.
- A hold taken while Ready applies from the next snapshot, as when the host replaces the session for a new world.
- Holds are bounded, so a host that sends its snapshot and then withholds the baseline, or replaces the session again
  and again, cannot keep a player waiting. The rule, in `InternalReadinessLimits`:
  - **the snapshot.** The host has two minutes to send each snapshot, from link-up, or from the replacement that asks for
    a new one. A replacement before the player was Ready gets its own two minutes;
  - **the holds.** Once a snapshot has arrived, the holds may take longer, but the join ends as timed out after 60
    seconds without progress, and 5 minutes after the first hold, however many replacements follow it, until the player
    has been Ready;
  - **the allowance.** The player waits 7 minutes in all, the snapshot's two and the holds' five, from link-up, or from
    a replacement after it was Ready, until it has been Ready again;
  - **progress** is a snapshot, a system message from the host, or `IInternalReadinessHold.ReportProgress` while the
    runtime works on its own, for example while it loads a large scene;
  - **the host** waits 8 minutes for a player to acknowledge a snapshot, and then ends the connection as timed out. It
    counts from the join's acceptance, or from the replacement that took a Ready player out of the session and again
    from the player's report that this replacement reached it (`ReplacementReceived`, `0x42`): a replacement can wait
    behind minutes of earlier traffic when the player's windows are full, and the player counts its allowance from the
    replacement's arrival. A report counts once per replacement, so a player can move the host's start once and never
    again. Replacements while the player is still joining start nothing over, on either side, so a host that replaces
    the session more often than its bound never lets a player that stalls keep its slot. That is the player's whole
    allowance and a minute; the player counts from link-up, which comes before the acceptance, or from the replacement's
    arrival, which comes before its report, so an honest player always gives up first;
  - **reports that do not count.** A report from a player still joining, or naming an epoch the host has not reached
    or one older than the replacement it waits for, moves nothing, so a player that never acknowledges loses its slot
    at most twice the bound after a replacement. A report without its epoch is a protocol violation;
  - **peers without the report.** A host older than the report skips it by its length, as it skips any ignorable
    type, and counts from the replacement alone. A newer host does the same for a player that never reports, such as
    one older than the report: that player keeps only the minute's margin for the replacement to reach it.

  A session the host replaces while the player is in it is synchronized again within the same bounds, although the join
  stays `InSession`.
- **The baseline's budget.** The link carries the session at 32 KiB a second, so a snapshot must arrive within about
  3.75 MiB of traffic, and the holds' five minutes carry about 9.4 MiB more, whatever progress the runtime reports. World
  replication's baseline (WS3.6) must fit in that, together with everything else the link carries meanwhile.
- A host is always ready, so its holds change nothing.
- `IsParticipantReady` tells a host whether a player holds the session and acknowledged it; a joined player knows only
  itself.

## 7. The composition root and the frame

`MultiplayerRuntime` is created in `ModRuntime`'s constructor with its `MultiplayerRuntimeHost`. ModRuntime records each
package that loads in the host's inventory, and attaches at the end of `Load`, once the provider has published its
control. `UnloadAll` detaches first, before any mod stops, so the provider closes its sessions on its own as it unloads.

The frame:

| Step | Where | What runs | When |
| --- | --- | --- | --- |
| 1 | `DispatchUpdate`, before the mods | The provider's `Pump`: network receive and the session engine | Whenever a provider is attached |
| 2 | `DispatchUpdate`, before the mods | `WorldEvents`: world events and the session follower | Online |
| 3 | `DispatchUpdate` | The mods' Update | Always |
| 4 | `DispatchUpdate`, after the mods | `PresenceSend` (pose and voice), `HostSnapshots` (host only), then the provider's `Flush` | Online |
| Late | `DispatchLateUpdate`, before the mods | `ReplicaInterpolation` | Joined players |

- `BeginFrame` (time, input and the mods' scheduler) still runs first, as before.
- The late step runs from `CoreGameplayServices.BeginLateUpdate` through the runtime's LateUpdate dispatch, ahead of the
  mods, which keeps it testable with any gameplay host.
- While attached, the provider no longer pumps itself from its own update subscription; detached, it does again.
- **Offline cost.** Without a provider, each hook reads one field and allocates nothing. With a solo provider, step 1
  pumps the solo engine, which the provider did before from its own update, and every other hook reads one field. Both
  are measured at zero bytes allocated over 10,000 frames, through the real `ModRuntime` and through the real provider.
- **Failures** are isolated: a pump, flush or stage that throws is logged once, redacted, until it recovers, and the
  mods still update. Each stage keeps its own record, so a healthy stage cannot re-arm a failing one's report.

## 8. What later slices plug in where

| Slice | Plugs in |
| --- | --- |
| WS3.1 realm (step 2) | `MultiplayerRuntime` holds the `NetRealm` and checks the realm rules before the control's hosting and joining (section 10) |
| WS3.3 factory seam (step 2) | `ModNetProfile` through `CoreGameplayServices.Create`, the `Net*Service` decorators and `IRuntimeReplication` (section 11) |
| WS3.6 network world | `WorldEvents`, `HostSnapshots` and `ReplicaInterpolation`; the `WorldReplication` channel, paced by `CanSend` and re-baselined when `GetGeneration` changes; `SetEntityIdentitySource` |
| WS3.8 session follow | `WorldEvents`; the `SceneFollow` channel, re-baselined like world replication; `HoldReadiness` around loading the host's world, with `ReportProgress` while it loads; `ReplaceSession` for a new world |
| WS3.10 presence | `PresenceSend`; the `Avatars` channel and its unreliable lane; `SetAvatarSource` |
| WS5 interface | `GetStatus`, hosting, joining, targets, `Query`, LAN listings, moderation, and `PreferredDisplayName`, with the host-authored text rules of section 2 |
| WS5 chat | The `Chat` channel, relayed by the host; server mute already drops at ingress |
| WS5 voice | `IInternalVoiceService` from the provider's voice service, `SubmitCapture` from `PresenceSend`'s capture, and each speaker's `IVoicePlaybackStream` in an audio callback |

The entity identity and avatar sources are shape only: the session's `TryGetNetworkObjectId`, `TryGetEntity`,
`TryGetAvatar` and `TryGetPose` ask them once set, on the main thread, and answer no until then. A source that throws
answers no and is reported.

## 9. Tests

- **The SPI over real providers** (`InternalControlTests`): hosting, joining, leaving, kicking, banning and lifting a
  ban, server mute at the chat ingress, passwords, an identity that cannot load and the join after it, session
  replacement, role refusals, queries and their owner thread, readiness holds and their idle, total and
  resynchronization bounds, on the virtual network with real cryptography, transport and engine, each provider composed
  with the real runtime host and composition root.
- **Admission through the runtime host** (`RuntimeAdmissionTests`): the descriptor's packages by role, a stale player
  refused by the host's planner with its report reaching the player, the joining player's own check before connecting, a
  required session mod at a compatible version joining, the offer's format and its refusals, the content seam
  without a runtime, and a package whose only file another handle holds open: hosting through the control and through
  the live sessions refused before anything binds, a joining player stopped before anything connects, a running host
  refusing every player, every failure reported naming the package and never its folder, and no frame failing.
- **The engine** (`SystemChannelTests`): channels both ways and in order, stale epochs and generations, windows and
  ownership, hostile peers, readiness holds and their progress, the host's bound on players that never become Ready, the
  world sources and the end-of-frame flush.
- **Streams** (`SystemStreamTests`): a rejoin within the grace period that restarts every stream and says so, one sender
  unable to starve another, an honest chunked baseline through a stalled receiver with nothing dropped, the player's
  rate, and peers that break their windows, their credits or their rate losing their connection.
- **Fairness** (`SystemStreamFairnessTests`): a player that never credits holds back only its own stream, is named
  when a message to everyone skips it, and loses its connection at the stall bound; a player that stalls and recovers
  keeps it; a headless host is heard; replacements during an uplink stall never end an honest player, and a flood of
  stale messages stays within its allowance; a newer peer's channels are credited both ways.
- **Readiness bounds** (`ReadinessBoundsTests`): a replacement before the player is Ready gets its own snapshot
  deadline, a host that keeps replacing the session cannot keep the player waiting, and, with production timings and a
  1.6 MB snapshot on the link's 32 KiB a second, the host waits out an honest player's whole allowance. With production
  timings and every window full when the host replaces the session, the replacement reaches the player more than two
  minutes late and the host still waits out the player's allowance from there; on the engine, a late replacement's
  report moves the start of the host's wait once and never again, and a host that replaces the session every half
  second still ends a player that never acknowledges at the bound from its acceptance, also one that reports every
  replacement as it arrives.
- **The host's text** (`HostTextTests`): a refused descriptor request is described in this game's words, whatever
  status the host sends, and a host whose runtime is not ready refuses with a code of its own.
- **The composition root** (`RuntimeCompositionTests`, `MultiplayerRuntimeHostTests`, the ModRuntime harness): who
  pumps the provider, attach and detach, stages by role, failure isolation, the frame order around real mods, and the
  zero-allocation offline frame.
- **Privacy** (`RuntimePrivacyTests`): no SPI member, parameter or type is shaped like an address or names a network
  type; typed hosts stay inside the handle; what the runtime logs and keeps is redacted, every address form above
  included, and the provider's own log gets the same redacted lines with no runtime attached and behind one that throws;
  and the host's words reach neither a log, through the developer switch or the runtime, nor a mod.

## 10. The realm (step 2)

The runtime will hold the process's `NetRealm` (`Offline`, `ListenHost` or `Client`) and ask
`NetRealms.EvaluateTransition` before acting:

- opening to multiplayer mid-session promotes the running solo session in place (`Offline` to `ListenHost`);
- joining needs an idle local session (`Offline` to `Client` from Idle); a running one first asks "Leave current
  game?";
- leaving is never refused.

## 11. The factory seam (step 2)

`ModNetProfile` will be threaded through `CoreGameplayServices.Create`, `ModContext` and its scopes, with pass-through
`NetEntityService`, `NetAssetService` (keeping `IParentAssetScope`), `NetItemService` and `NetUiService` decorators that
change no behaviour yet, and the internal `IInternalReplicationContext` and `IRuntimeReplication` with stubs for the
later slices' replica factories, canonical registration and spawn requests, command routing, reference resolution, time
authority and the microphone arbiter.

## 12. Choices the plan leaves open

- **System channels in the engine.** The plan names `OpenSystemChannel` in the SPI and reserves `SystemMessage` for
  WS3; this slice implements both, reliable only, so every later slice has its transport.
- **Credit windows, not drops.** A receiver's channel holds at most a window per sender, and a message past it is never
  queued, so reliability never depends on the receiving owner's timing; the alternative, dropping at the receiver,
  lets one sender crowd out the rest and loses an honest baseline after a stall.
- **Skips, not refusals, for a message to many.** A message to everyone goes to every player with room and names the
  rest, so one stalled player never holds back the others; its owner re-baselines what that player missed, and the
  stall bound ends a player that never takes anything again.
- **Generations, not resends.** A rejoin restarts the streams and says so rather than replaying what was lost: the
  owners already resynchronize from a baseline, and a replay buffer would hold a window per peer for the whole grace
  period.
- **Readiness holds** delay the client's acknowledgement rather than a host's admission; a host is always ready. Their
  bounds are the joining player's to enforce, with the host's own, longer bound behind them, both from one rule in
  `InternalReadinessLimits`.
- **The provider pumps at step 1 and flushes at step 4.** The flush sends what the runtime queued during the frame
  without receiving again; in solo play neither step costs more than before.
- **The joining check is exact for implicit mods only**, and checks presence for required session mods, leaving the
  rest to the host's planner, as section 4 explains.
- **The offer is binary**, not canonical JSON: it never leaves C#, and 512 packages fit in the join request's field.
- **Packages without a receipt are hashed at first use, on the main thread**, so a player who never plays online pays
  nothing. Hand-copied builds are development work, and section 4 measures the cost; before the in-game interface offers
  hosting and joining to players, that hashing moves off the main thread, with content reported as not ready until it
  is done.
- **Bans are listed by id, not by key**, so the interface never holds a player's key.
- **Voice is null** until the voice lane adapts the provider's voice service to `IInternalVoiceService`.
