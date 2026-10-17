# Multiplayer runtime integration — design record

Status: Implementing (WS3.1 to WS3.3 and WS3.6 of the live-multiplayer plan) · Date: 2026-10-17 · Owner: multiplayer

This records how TopiaForge's runtime drives the multiplayer provider: the internal SPI between them, the provider's
side of it, ModManager's composition root and its frame, and where later WS3 slices plug in. How the provider hosts and
joins live sessions is recorded in [MultiplayerLiveSessions.md](MultiplayerLiveSessions.md), and how the session engine
works in [MultiplayerSessionEngine.md](MultiplayerSessionEngine.md).

The work lands in three steps:

1. **The SPI, the provider's implementation and the composition root** (sections 1 to 9).
2. **The realm and the factory seam** (sections 10 and 11), which use the replication model's `NetRealm` and
   `ModNetProfile`.
3. **The network world** (WS3.6, section 13): scene objects, spawns and world content that exist, move and baseline on
   every player.

All three have landed. Section 8 says what each later WS3 slice plugs in where, and section 13 says exactly where
authority routing (WS3.7) and session follow (WS3.8) attach to the network world.

## 1. Shape

| Part | Where |
| --- | --- |
| The SPI | `src/TopiaForge.Mods.Multiplayer/Internal/`, namespace `TopiaForge.Mods.Internal` |
| The provider's control, content and join targets | `mods/TopiaForge.Multiplayer/Provider/` |
| The engine's system channels, readiness holds and world sources | `src/TopiaForge.Multiplayer.Engine/Core/SessionEngine.System.cs`, `SessionEngine.Runtime.cs`, `System/` |
| The composition root and the runtime host | `src/TopiaForge.ModManager/Multiplayer/` |
| The replication SPI (section 11) | `src/TopiaForge.Mods.Abstractions/Internal/`, namespace `TopiaForge.Mods.Internal` |
| The realm, the spawn ledger, the replication seam and the network decorators | `src/TopiaForge.ModManager/Multiplayer/` |
| The network world (section 13) | `src/TopiaForge.ModManager/Multiplayer/World/`, Unity-free; its engine side in `World/Unity/` |

- **Visibility.** Every SPI type is internal. The multiplayer SDK assembly grants its internals to the provider,
  ModManager, the test rig and the three test harnesses that use them. The replication SPI lives in the core SDK
  assembly, which already grants its internals to ModManager and the first-party providers that use it. Nothing public
  changes, so the SDK API baselines do not either.
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
| Internet | `CreateInvite`, `RevokeInvite` | Host only, once the session is registered with the TopiaForge service: `MultiplayerInternetSessions.md`. |

- **Results.** Commands answer with `OperationResult<bool>`: `NotAuthoritative` for a host-only action on a joined
  player, `InvalidState` when the current role or phase forbids it, `Unavailable` while live sessions are switched off,
  without an attached runtime, or, with no TopiaForge service configured, for Public or Invite hosting and for a
  target only the service can resolve, `InvalidArgument` for bad input, and `NotFound` for an unknown player or ban.
- **Status.** `GetStatus` returns one snapshot: the role, the host's phase and failure, the join's phase, failure and
  detail, the host's message, the host's admission code, the content problems and whether the host wrote them, a
  changed server identity, the joined host's verified description, browsing, connected players, readiness, and the
  session's standing with the TopiaForge service (`Internet`). It allocates, so the interface reads it on change.
  `JoinDetail` is always this game's own words; what the host wrote is kept apart, as the next section sets out.
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
  description, except that an invite prints as `invite` without its code, so logging a target leaks neither an address
  nor a code.
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

The realm (section 10) and the factory seam (section 11) have landed. The rest of WS3, and the slices that build on it,
plug in here:

| Slice | Plugs in |
| --- | --- |
| WS3.4 intent tracking | `IntentScope`, fed after the input sample, from UI renderer callbacks, from `OwnerCommandService` and from interaction handlers. `IRuntimeReplication.Route` takes the intent from its caller today; the intent scope makes the current one available to the network decorators and the SPI |
| WS3.5 core replication model | Landed in Core (`MultiplayerReplicationModel.md`); the slices below drive it |
| WS3.6 network world | Landed (section 13): `PromotionCensus`, `WorldEvents`, `HostSnapshots` and `ReplicaInterpolation`, an observer of the realm, the spawn ledger's observer, the `WorldReplication` channel, `SetEntityIdentitySource`, `MultiplayerReplication.References` and `LocalContent`, and replicas through `TryGetReplicaFactory` |
| WS3.7 authority routing | The `Net*Service` decorators and the SPI's spawn and command stubs, each call routed by `ExecutionRouting.Route(realm, profile, intent)`: spawn adoption, and shadows bound by `NetSpawnOrigin`; `NetEntityMotion` behind `AcquireMotion`; `HeldItemOwnershipTracker` behind `NetItemService`; forwarded commands naming their sender in `InternalCommand.Sender`. Its traffic is message types 64 to 127 on the network world's channel, and its hooks into the network world are listed in section 13 |
| WS3.8 session follow | `WorldEvents`; the `SceneFollow` channel, re-baselined like world replication; `HoldReadiness` around loading the host's world, with `ReportProgress` while it loads; `ReplaceSession` for a new world; the follower's world-owner scopes through `CreateChildScopeAsync`'s execution context; `Leave` from a joined player's main menu. The network world's scene hooks, hold and progress it works beside are listed in section 13 |
| WS3.9 participant realm | Participant scopes through `CreateChildScopeAsync(..., ModExecutionContext.Participant)` |
| WS3.10 presence | `PresenceSend`; the `Avatars` channel and its unreliable lane; `SetAvatarSource` |
| WS3.11 Players API | Player references (`InternalNetRefKind.Player`) through `MultiplayerReplication.References`, beside the network world's entity references |
| WS3.12 presentation routing | `NetUiService`, routing each call by `PresentationRoutingPolicy.Route(realm, profile, kind)` |
| WS3.13 pause and time | `TimeAuthority` and `NativePauseFreezesWorld`; the bridge that publishes the host's time scale and applies it on joined players adds to the SPI |
| WS4 first-party mods | Each provider reaches its context's `IRuntimeReplication`: RobotKit's replica factory, spawn requests, routed mutators, references for `Chase` targets and its dialogue's microphone lease; CreatorContent's `creator.content` factory and routed commands; the Worlds arena registered as world content by key; Chronos's time authority |
| WS5 interface | `OpenToMultiplayer`, `Join`, `Leave` and `EvaluateRealmChange` on the runtime, never the control's own hosting and joining, so "Leave current game?" follows `RequiresLeave`; `GetStatus`, targets, `Query`, LAN listings, moderation, and `PreferredDisplayName`, with the host-authored text rules of section 2 |
| WS5 chat | The `Chat` channel, relayed by the host; server mute already drops at ingress |
| WS5 voice | `IInternalVoiceService` from the provider's voice service, `SubmitCapture` from `PresenceSend`'s capture, and each speaker's `IVoicePlaybackStream` in an audio callback; the microphone arbiter's `NativeRecording` from the game's recording state, and a `VoiceChat` lease that capture stops under while it is preempted |

Two rules hold for everything that plugs in:

- **Broadcasts.** A message to everyone goes out through `IInternalSystemChannel.Send`, whose `skipped` list names the
  players whose window was full; their owner sends each of them a new baseline once `CanSend` to that player says there
  is room. A broadcast is never gated on `CanSend` for everyone: that is false while any one player's window is full,
  and waiting on it starves every other player (section 5).
- **Generations.** A change of a stream's generation, from a rejoin or a replacement, means "start again from a
  baseline", whatever the owner was in the middle of.

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
- **The realm at the runtime** (`MultiplayerRealmRuntimeTests`, and `RuntimeRealmTests` over real providers): the realm
  follows the provider's role at each pump; opening to multiplayer while a world runs promotes the solo session in
  place, keeping its session id, and the census baselines the live spawns recorded offline, in order, once per
  promotion; from the menu the game starts as a host. Every realm, phase and standalone combination for hosting and
  joining is refused with its verdict and text before the provider hears of it, or reaches the provider; leaving is
  never refused, from any realm or phase, stops hosting on its way and keeps an earlier hosting failure on record; a
  missing provider or live sessions switched off is said first; a phase that cannot be read counts as busy; and the
  census, leaving and phase failures are logged once, redacted. Over a real orchestrator (`MultiplayerRealmPhaseTests`,
  and the ModRuntime harness), an idle session that is still changing is busy: while the main menu loads after the
  player left the game, a join and hosting wait, as a launch does, and go through once it has loaded; so do a native
  scene change, the orchestrator's own operation alone, and shutdown.
- **The spawn ledger** (`MultiplayerSpawnLedgerTests`): order, liveness, what a host would baseline, describing an entity
  again in place, descriptions that are refused, the sweep's bound under churn, and offline frames with a thousand
  records, a solo frame, and a client-local spawn, each allocating nothing.
- **The decorators** (`NetGameplayServicesTests`): every member of the four passes exactly its arguments to the host's
  service and returns exactly its result, its exceptions included by reference; parent scopes pass through the
  decorator; a spawn is recorded only when it succeeds, and a failure to record never reaches the mod.
- **The profile** (`ModContextNetProfileTests`, the ModRuntime harness): every class of manifest reaches the package
  context, its gameplay host and its replication seam; scopes take their execution context, and content and discovery
  scopes inherit theirs; a real launch's scopes are its gamemode server and world owner; a real mod's spawn at load is
  recorded; and a join is refused while a real session runs, and goes through once it stops.
- **The replication SPI** (`RuntimeReplicationTests`, `RuntimeReplicationServiceTests`): its enums mirror the runtime's;
  routes and time authority match the tables in every realm; factories, canonical registration, spawn requests,
  commands, references and the microphone, with their bounds, refusals and lifetimes; and a joined player's
  predictions, shadows and asset spawns recorded as nothing.
- **The network world** (section 13 lists its suites).
- **Its privacy** (`ReplicationSpiPrivacyTests`): no member, parameter or type of the replication SPI (its interfaces, its
  limits and every internal type they name, transitively), or of the runtime types behind it, is shaped like an address
  or names a network type, and nothing the realm and the seam log or answer holds one, however many addresses the
  failures they report carry. Other internal contracts in the namespace, such as the launch contract's session ticket,
  are held to their own rules.

## 10. The realm

`MultiplayerRuntime` holds the process's `NetRealm` (`Offline`, `ListenHost` or `Client`) and applies the replication
model's realm rules, `NetRealms.EvaluateTransition`, to every request to go online or to leave
(`MultiplayerRuntime.Realm.cs`).

- **The realm follows the provider.** `Realm` maps the role the last pump read: a solo session is `Offline`. It changes
  only at a pump, or when the runtime detaches, and each change is logged once, by name. A request never sets it:
  hosting and a join start at later pumps, and a join that fails before its link is up never leaves `Offline`.
- **Requests.** The interface (WS5) asks the runtime, never the control's own hosting and joining:

  | Request | Realm rule | Then, when the rule allows it |
  | --- | --- | --- |
  | `OpenToMultiplayer(settings)` | to `ListenHost` | The control's `StartHosting` |
  | `Join(target, settings)` | to `Client` | The control's `Join` |
  | `Leave()` | to `Offline`, never refused | `StopHosting` while hosting, or while hosting starts, then `Leave`, which also stops a join on its way |

  Each answers a `MultiplayerRealmChange`: the rule's verdict and effect, and the control's own result, or the refusal.
  `EvaluateRealmChange(target)` asks the rule alone, so the interface can ask "Leave current game?" before it acts.
- **What the rule reads.** The realm, the local session's phase, and whether a standalone mod is loaded. ModRuntime
  points `SessionPhaseSource` at its gamemode session orchestrator's `RealmPhase`; until the session runtime starts,
  the session is idle. A phase that cannot be read counts as busy, so nothing goes online on a guess, and its failure is
  logged once.
- **Idle means settled.** `RealmPhase` counts an idle session that is still changing as stopping, so the rule says
  `Busy`: a return to the main menu that has ended the session but still loads the menu, any other operation or native
  scene change that holds it, and shutdown. The orchestrator refuses to start a session then, and a join replaces the
  local session, so it waits too. After "Leave current game?", the interface asks again once the menu has loaded.
- **Refusals**, in this order: a missing provider, or live sessions switched off, first, since no leaving would change
  them; then the rule's verdict:

  | Refusal | Code | Text |
  | --- | --- | --- |
  | No provider, or live sessions switched off, whatever the verdict | `Unavailable` | "Multiplayer is unavailable: the multiplayer provider is not loaded." or "Live multiplayer is switched off in this build." |
  | `RequiresLeave`, joining while a game runs | `InvalidState` | "Leave the current game before joining another session." |
  | `RequiresLeave`, between online realms | `InvalidState` | "Stop hosting before joining another session." or "Leave the joined session before hosting." |
  | `Busy` | `Conflict` | "A game is starting or stopping; try again once it has settled." |
  | `StandaloneRefused` | `InvalidState` | "Solo refuses multiplayer; disable it to host." (or "to join"), naming three mods at most |
  | `Unchanged` | `InvalidState` | "This game is already open to other players." or "This game is already in a joined session; leave it before joining another." |

  The provider never hears of a refusal. A refusal of the provider's own, such as a join already in progress, comes back
  as it is, under the rule's `Allowed`.
- **Joining needs Idle.** Joining replaces the local session, so while a game runs the runtime refuses with
  `RequiresLeave`. The interface asks "Leave current game?", stops the local session, and asks again from the menu.
- **Leaving is never refused**, whatever the realm, the phase or the loaded mods. A host stops hosting and plays on alone
  in the same session. Stopping hosting only when it runs or starts keeps an earlier hosting failure on record for the
  interface. A provider that throws while leaving is reported once, redacted, as a failure of its own, never as a
  refusal, and every step is still tried.
- **Promotion in place.** Opening to multiplayer while a world session runs promotes it: the provider turns the running
  solo session into a listen host, keeping its session id, its objects and the mods' state (live sessions, section 18).
  From the menu the game starts as a host instead.
- **The census seam.** `PromotionCensus` (`IMultiplayerPromotionCensus`) is the network world's (WS3.6). It is taken
  once each time this process moves from `Offline` to `ListenHost`, after the pump that shows it hosting and before that
  frame's world events, with a `MultiplayerPromotion`: `PromoteInPlace` when a world session runs at that moment and
  `StartAsHost` otherwise, the phase, and the live spawn records in the order they happened. Until a slice fills it, a
  promotion takes no census and the records stay. A census that throws is logged once until it recovers, and the host's
  frame goes on.
- **Spawn-time recording.** `Spawns` (`MultiplayerSpawnLedger`) records SDK spawns and world content when they happen, in
  every realm and above all offline:

  | Recorded from | Kind | Description |
  | --- | --- | --- |
  | `Assets.Spawn` that succeeds, through `NetAssetService` | `asset.prefab` | The prefab's bundle path and asset name (`MultiplayerAssetDescriptor`); a prefab that cannot say them is described as unknown, and stays local |
  | `RegisterCanonical` and `RequestSpawn`, from first-party providers | The provider's own | Its `InternalSpawnDescriptor`, world content with its content key |

  Each record keeps a sequence that is never reused, the owning mod, the network profile of the context that made it,
  what it is, its transform when it was made, its description, the entity, and the seam that recorded it. Only what a
  listen host would baseline is recorded: a client-local mod's spawns, which are never networked, and a standalone
  mod's, which never go online, are not, and cost nothing. Nor is anything a joined player spawns or registers: its
  spawns are predictions and shadows of the host's, never canonical (section 13). Registering a recorded entity again
  describes it in place, keeping its place in the order. A record ends when its entity does: dead records are swept as
  recording goes on, whenever the ledger reaches twice what was alive at the last sweep, or 64, so it never holds more
  than that. It also ends, at once, when the context whose seam recorded it ends: the seam tracks one release in its
  context's lifetime, and an ended record lets go of its entity, so no record keeps an ended session scope reachable.
  While an observer is set, the network world on a listen host, each new record and each new description is reported
  to it, so spawns after the census are announced. Recording happens only when something spawns, so an offline frame
  costs nothing: 10,000 frames with a thousand records allocate zero bytes, through the composition root with and
  without a solo provider, and through `ModRuntime` with a loaded mod's recorded spawn.
- **Network classes.** Each loaded package's `ModNetworkClass` comes from its effective manifest
  (`ModNetworkClasses.Classify`): no multiplayer block and `session` are `Shared`, `client-local` is `ClientLocal`,
  `server-only` is `ServerOnly`, and `standalone`, a mode that cannot be read and an unsupported schema are
  `Standalone`. The runtime gives it to each package context's profile (`NetworkClassOf`), answers it for a loaded mod
  (`TryGetNetworkClass`), and names the loaded standalone mods (`StandaloneMods`) for the rule.
- **The developer switch** still drives the provider directly (section 2), so its hosting and joining skip the realm
  rules. It is off by default, and goes when the interface does.

## 11. The factory seam

- **Profiles.** Every mod context carries a `ModNetProfile`: its mod's network class and its execution context.

  | Context | Execution context |
  | --- | --- |
  | A package context, made by `ModRuntime.Load` | `Process` |
  | A session's scope for its gamemode's package, also when that package provides the world | `GamemodeServer` |
  | A session's scope for any other package it selects: the world's, and a launch target's owner | `WorldOwner` |
  | A content or discovery scope | Its caller's |
  | A participant scope (WS3.9) | `Participant` |

  A scope keeps its mod's network class. `CreateChildScopeAsync` takes the execution context, refuses an undefined one,
  and without one inherits its parent's.
- **The factory.** `IGameplayContextFactory.Create` receives the profile, so every service a gameplay host makes can
  know where its context runs; the Unity-side parts later slices make for a context (WS3.6, WS3.7) take it from there.
  `ModContext` then wraps the host's entity, asset, item and UI services in the network decorators, bound to the
  context's replication seam, so every gameplay host gets the same seam. The entity service stays inside the lifetime
  guard, so a stopping context is refused before anything routes.
- **The decorators.** `NetEntityService`, `NetAssetService`, `NetItemService` and `NetUiService` change no behaviour
  yet: each member passes exactly its arguments to the host's service and returns exactly its result, its exceptions
  included. `NetAssetService` keeps `IParentAssetScope`: a scope's decorator hands its host service the parent's own host
  service, not the parent's decorator, so the host's check of the relationship sees what it saw before, and a host
  service without parent scopes is left as it was. It also records each spawn that succeeds (section 10). A failure to
  record is reported once and never reaches the mod, whose spawn succeeded.
- **The replication SPI.** `IInternalReplicationContext`, which every `ModContext` implements, gives trusted first-party
  providers their context's `IRuntimeReplication`. A context that does not implement it, such as a test double, is
  single player. Every member runs on the main thread; a stopping context is refused with `Cancelled`, and a profile
  that does not run in the current realm, such as a gamemode server on a joined player, with `NotAuthoritative`.

  | Members | Now | From |
  | --- | --- | --- |
  | `Realm`, `NetworkClass`, `ExecutionContext` | The runtime's realm and the context's profile | — |
  | `Route(intent)` | `ExecutionRouting.Route` for the realm, the profile and the given intent | WS3.4 supplies the current intent |
  | `RegisterReplicaFactory` | Kept, one per kind in the process; a joined player builds the host's entities of that kind through it, and releases them through `ReleaseReplica` | — |
  | `RegisterCanonical` | Offline and on a host, records at once, and a host announces it. On a joined player records nothing: world content waits to bind by key to the host's | WS3.7 binds a joined player's other registrations as shadows |
  | `RequestSpawn` | Runs the spawn here in every realm (stub), and records it offline and on a host | WS3.7 forwards from a joined player, predicts, then adopts or rolls back |
  | `RegisterCommand`, `SubmitCommand` | Runs the mod's own handler here at once, in every realm (stub) | WS3.7 forwards from a joined player; the host checks the sender |
  | `TryCreateRef`, `TryResolveRef` | The network world names its entities, `world-` and the network id | WS3.11 names players |
  | `TimeAuthority`, `NativePauseFreezesWorld` | `ExecutionRouting.TimeControl` and `NativePause` | WS3.13 publishes and applies the host's scale |
  | `AcquireMicrophone` | The arbiter: the game's recording, then robot dialogue, then voice chat | WS5 feeds the game's recording and holds voice chat's leases |

- **Bounds.** Replica and command kinds are lowercase tokens of at most 64 characters, the same rule as the entity
  table's, and kinds that start with `asset.` are the runtime's own. Descriptions and command payloads are at most
  16 KiB and copied in. A world content key is 1 to 256 printable characters, a reference's id 1 to 128, and a
  microphone reason 1 to 128. A mod's command kinds are its own: two mods may each have a `spawn`, and neither runs
  the other's. Registrations and microphone leases end with the context's lifetime, or when their lease is disposed.
- **Microphone.** The game's own recording comes first and is observed, never taken; robot dialogue comes next, and voice
  chat last. A use is refused with `Conflict` while one that comes before it holds the microphone, naming the holder,
  and a lease is preempted, and must not capture, while one that comes before it takes the microphone later. Leases of
  one use coexist.
- **Privacy.** No member of the replication SPI carries an address: entities and players are named by runtime ids, and
  every text the realm and the seam write or answer is this game's own, with failures redacted. The architecture test of
  section 9 holds the SPI and the runtime types behind it to that.

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
- **The realm follows the provider's role** rather than the requests: a request only asks, and the realm moves when the
  pump shows the provider hosting or joined. A join that fails early therefore never leaves `Offline`, and nothing that
  reads the realm sees a session that did not happen.
- **One census seam for both ways of hosting.** The census runs whenever this process becomes a host, with the effect
  read at that moment rather than at the request: a world may start or stop between the request and the pump that
  hosts, and the census should describe the session that is actually opened.
- **Records follow their entities.** The ledger holds a live entity and forgets it once it ends, rather than taking a
  lease the provider must dispose: spawns end with their mods' lifetimes already, and a lease per spawn would grow the
  lifetime's list for as long as the mod runs.
- **The decorators wrap in `ModContext`**, not inside `CoreGameplayServices.Create`, so the Unity host and every test
  host get the same seam, and the harnesses run it. `Create` still receives the profile.
- **The microphone arbiter is real**, not a stub: the plan fixes its priority, and an arbiter that always granted would
  have to be unlearned by the providers that use it.
- **Kinds that start with `asset.` are the runtime's own**, so no provider can describe what the asset service spawns in
  a format the runtime's own replicas would not read.
- **The core SDK assembly's internals were already granted** to ModManager and the first-party providers, so the
  replication SPI adds no visibility to it. The Core assembly grants its internals to the ModRuntime and multiplayer
  harnesses, which compile and implement the seam's realm and profile types.
- **The network world's choices** are recorded with it, at the end of section 13.

## 13. The network world

WS3.6 makes a hosted session's objects exist, move and baseline on every player
(`src/TopiaForge.ModManager/Multiplayer/World/`). The folder is Unity-free, so the ModManager and multiplayer harnesses
compile all of it. The engine sits behind `INetworkWorldBackend`, which `UnityNetworkWorldBackend` (`World/Unity/`)
implements over the loaded scenes and the runtime's entity registry, and the tests' `FakeWorldScene` over a fake engine.

| Part | What it does |
| --- | --- |
| `NetworkWorld` | Wires the rest to the composition root: the three frame stages, the census seam, the realm's observers, the session's identity source, and the replication seam's references and world content. It owns the `WorldReplication` channel while a provider is attached |
| `NetworkWorldHost` | The listen host: registering recorded spawns, indexing scenes, binding on first motion, announcements, despawns and snapshots |
| `NetworkWorldHost.Baselines` | The host's players and their chunked baselines |
| `NetworkWorldClient`, `.Replicas`, `.Assets` | A joined player: its readiness hold, census and suppression, applying what the host sends, and building and releasing replicas |
| `SceneKeyRegistry` | Every loaded scene's object keys, taken inside the scene's load event in every realm and kept until it unloads |
| `SceneKeyHasher` | The replication model's `SceneObjectKey`, hashed a path segment at a time, so the objects under one parent share its part |
| `SceneObjectIndex` | A session's keyed scene objects, with duplicates excluded, objects gone since their load removed, and binding on first motion |
| `NativeAuthoritySuppressor` | What a joined player turns off, and the exact restore |
| `ReplicaTransformDriver` | Host samples into the replication model's `SnapshotInterpolationBuffer`, and replicas moved to them |
| `NetworkWorldChannel`, `NetworkWorldWire`, `NetworkWorldEntries` | The channel's end, its wire format and its entries |
| `TopiaForgeNetworkIdentity` | The component that marks a network root with its id. It is not an `IRuntimeEntityIdentityAnchor`, so a `GetComponent` for the anchor never finds two |

### The lifecycle

- **Install.** ModRuntime creates the network world after the composition root, over the gameplay host's backend
  (`INetworkWorldBackendSource`; a host without one replicates no scene object and adopts no spawn), a monotonic clock
  in microseconds, and the owning mod's package asset service for replicas. `WorldEvents` runs the host's and the
  joined player's receive, `HostSnapshots` the host's send, and `ReplicaInterpolation` the joined player's driver.
- **Attached.** The world opens `WorldReplication` and sets itself as the session's entity identity source. A channel
  that cannot open is a warning, once; the world then does nothing online.
- **The realm** (`IMultiplayerRuntimeObserver.RealmChanged`, told after the pump that shows the change, before that
  frame's world events and before a new host's census): leaving `ListenHost` ends the host's side, which takes every
  mark off; leaving `Client` ends the joined player's side (below); entering `Client` starts it.
- **The census** (`PromotionCensus`): becoming a host starts the host's side with the promotion's records.
- **Detaching** ends both sides, closes the channel and removes the identity source.
- **Scenes.** ModRuntime tells the world of every scene load, inside the load's own event and before the mods hear of
  it, and of every unload, in every realm. The world keys the scene's objects as it loads (below) and forgets them as it
  unloads. Frames pay nothing for either.

### The host

- **Promotion.** The promotion's live records are registered in the order they were made, each a network entity with
  its origin, `{owning mod, record sequence}`, and marked through `Adopt`. Then the loaded scenes' objects are indexed
  by the keys each took as its scene loaded, and the world becomes the spawn ledger's observer, so every later record
  and description is announced.
- **Scene keys.** As each scene loads, in single player as much as online, its candidates are keyed: the topmost
  objects that carry a rigidbody, never the local player or a marked root, each by its scene, its path from the root as
  the scene loaded and `UnityEngine.Rigidbody`. The keys stay with the scene until it unloads. A key carries sibling
  indices, so a key taken later would shift with every sibling destroyed or reparented before it; taken at the load, an
  object keeps its key whatever the hierarchy goes through since. One moved to another parent, or renamed, is still
  named by its path as the scene loaded, and one destroyed moves no other object's key. A joining player, which loads
  the scene fresh and keys it at its own load, names every object alike.
- **Scene objects.** At the promotion, and for a scene that loads while hosting, the index takes each scene's keyed
  objects. One its scene had at the load and that is gone since is indexed as removed, so every baseline names it and a
  joining player hides its copy. One registered as a spawn or world content since is that entity already, and binds
  nothing. An object made after its scene loaded has no key, and is no scene object. Keys that repeat are excluded,
  every copy. A scene is indexed once per load. A load that could not be keyed is keyed when a session first needs the
  scene, from the hierarchy as it is then, and counted (`ScenesKeyedLate`).
- **First motion.** Each downstream tick checks up to 512 unbound objects in turn against the pose the census read, on
  the quantization grid. One that moved becomes an entity of kind `asset.scene`, is marked and announced; one that is
  gone is removed, and every player hears its key. Every object is checked before a baseline begins.
- **Poses before the promotion.** The poses the index reads at the promotion are ones the solo session may have moved,
  so they are not the scene's own. A baseline carries the pose of every object indexed then and still unbound, and a
  joining player places its copy there. A scene loaded while hosting is indexed at its load pose and needs no such entry.
- **Downstream.** Every frame, the host broadcasts what ended (despawns and removed keys) and what appeared or changed
  (announcements). Every 100 ms it broadcasts a snapshot of the poses the replication scheduler picks under its
  800-byte budget, resting entities after their three confirmations dropping out. Every broadcast goes through `Send`,
  and each player its `skipped` list names gets a new baseline once its window has room.
- **Players.** The host keeps a player's state only once that player asks for a baseline on its stream, never from the
  participant list, so nothing acts on a list before the player is Ready. The first request on a stream is always
  answered. A further one is a resynchronization, which costs 10 tokens of a bucket of 20 that refills one a second, so
  a player gets two at once and then one every ten seconds. When a stream's generation changes, a player whose stream
  restarted starts again on the new one, and its baseline under way is abandoned; one whose stream ended is forgotten.
  At most 256 players keep state.
- **Baselines.** A baseline's list is fixed when it begins, not when it is asked for, so a player whose window stays
  full costs nothing but a check: the live entities in order, the removed scene keys, and the poses above. It goes as
  `BaselineBegin`, chunks of at most 20 KiB, and `BaselineEnd`, at most eight messages per player per frame, and each
  only while that player's window would also take a whole 32 KiB message. A baseline therefore leaves at least 12 KiB
  of the window to the broadcasts behind it, and never makes one skip its player. A chunk the window refuses is written
  again next frame from where it stopped; an entity that ended before its entry was written is left out, since the
  player hears of its end. A new baseline for the same player supersedes the one under way.

### A joined player

- **Joining.** The world holds readiness ("Loading the host's world"), indexes the loaded scenes by the keys they took
  as they loaded, turns native authority off, and asks for a baseline, every 250 ms until the channel takes the
  request, as it will once the stream starts.
- **Native authority.** Scene rigidbodies become kinematic. Robot brains (`LLMAgent.llmDisabled`, and Behavior
  Designer's `BehaviorTree`), locomotion (`LocomotionController`), path followers (`NavMeshAgent`) and spawners
  (`PooledSpawner`) are turned off. The local player's hierarchy is never touched. The suppressor reads each setting,
  records it, then writes the suppressed value, and keeps each setting once. On leaving it restores the newest first,
  and only where the value is still the one it wrote, so a change the game made meanwhile stands. It counts what it
  restored, what had changed, what is gone, what failed and what was never written, so a join that fails part way is
  restored exactly as far as it went. A scene object the host removed is hidden through the same suppressor.
- **Applying.** `BaselineBegin` marks every mirrored entity stale, entries and announcements make them current, and
  `BaselineEnd` removes whatever is still stale. A known entity whose description is unchanged keeps its replica, and
  a changed one is built again. A baseline's entry is newer than any sample the player buffered before it, so the
  entity drops those samples and stands at the entry's pose until the next.
- **Building.** A scene object binds by its key. World content binds by its content key to the entity this player's
  own world provider registered (`RegisterCanonical` on a joined player). An asset spawn is built through the owning
  mod's package asset service from its bundle path and asset name: each bundle and prefab is loaded once and shared,
  polled each frame and never awaited, and a load that takes 30 seconds counts as unbuildable. A provider's spawn is
  built by its kind's replica factory. Replicas are made kinematic, marked and placed. What cannot be built stays
  mirrored, is counted, and is tried again only when its description changes; nothing throws. A player mirrors at most
  16,384 entities.
- **Before its scene.** An entity whose scene object is not loaded binds when the scene loads, and the keys the host
  removed and the poses it placed wait for their scene too, up to 65,536 keys. The baseline may therefore arrive before
  session follow (WS3.8) has loaded the world.
- **Readiness.** The hold is released once a baseline is applied and no replica is still loading; each finished replica
  reports progress. A stream that restarts takes the hold again, until its new baseline is in place.
- **Snapshots.** Each sample goes into its entity's interpolation buffer at the host's time and this player's arrival
  time. In LateUpdate, before the mods, the driver writes every replica the buffer's pose for now. A replica still
  being built keeps its samples and moves once it exists.
- **Leaving.** Every replica is released: asset replicas are disposed, and factory replicas go back through
  `ReleaseReplica`. Scene objects are unmarked, native settings restored, bundle and prefab handles let go of (a load
  still running lets go of its result as it lands), and the hold released. Each step runs whatever the one before it
  did, and a restore that failed is logged once, with its counts.

### Lookups, references and the ledger

- **Ids.** Entities are named `world-` and their network id in decimal, which never collides with the engine's
  `net-object-` objects. `TryGetNetworkObjectId` and `TryGetEntity`, the session's identity source, and the replication
  seam's `TryCreateRef` and `TryResolveRef` go through the backend: `UnityEntityRegistry.TryGetGameObject`, then the
  nearest marked ancestor, so a part of an object names its network root. Only entities this session replicates are
  named, and an id must be in exactly its canonical form. Player references are left to the players directory (WS3.11).
- **Later spawns.** The spawn ledger reports each record made after the census, and each new description, to the host.
- **Ended scopes.** A record keeps the seam that made it, and ends with that seam's context, letting go of its entity. A
  description holds no handle of the scope: an asset spawn is described by its prefab's bundle path and asset name,
  which `UnityPrefabHandle` reports (`IPrefabAssetProvenance`), not by its request.
- **A joined player records nothing as canonical.** This is the choice between the two the plan allowed. Its
  predictions and shadows never enter the ledger, so nothing needs clearing on joining or leaving, and a promotion
  after leaving baselines only what this process made while it was its own host or offline.

### The wire

Every message starts `[version 1][type]`, varints are minimal LEB128, and a malformed message is discarded whole and
counted.

| Type | From | Body |
| --- | --- | --- |
| 1 `BaselineBegin` | Host | baseline number, expected entries, host time |
| 2 `BaselineEntries` | Host | baseline number, count, entries |
| 3 `BaselineEnd` | Host | baseline number |
| 4 `Spawn` | Host | count, entries: new entities, and new descriptions |
| 5 `Despawn` | Host | count, network ids |
| 6 `Snapshot` | Host | host time, count, then id and transform record each |
| 7 `SceneRemoved` | Host | count, scene keys |
| 32 `BaselineRequest` | Player | reason: 0 when a stream starts, 1 to resynchronize |
| 64 to 127 | Either | Authority routing's (WS3.7) |

An entry starts with its source: a scene object (id, key, pose), an SDK spawn (id, kind, owning mod, origin sequence,
owner slot, scale, pose, description), world content (id, kind, content key, owning mod, owner slot, scale, pose,
description), a removed scene object (key), or an unbound scene object's pose (key, pose). A joined player checks every
field as the runtime checks its own: kinds and mod ids by their token rules, a sequence of at least 1, an owner slot up
to 255, finite non-zero scales up to 10,000, content keys as world content's, descriptions up to 16 KiB, and transform
records exact in their flags, length and spare bits. A message is at most 24 KiB and lists at most 4,096 items.

### Privacy and cost

- **Privacy.** Players are named by participant id inside the process and never logged. The world logs fixed texts and
  counts only, and its counters hold counts. Nothing it sends, keeps or answers holds an address, and the architecture
  test of section 9 covers its types with the runtime's.
- **Offline**, every frame hook is one field read; the offline frame still allocates zero bytes over 10,000 frames,
  through the composition root and the real `ModRuntime`, with a keyed scene loaded. A scene load keys that scene inside
  its event, and an unload forgets its keys.
- **The census at a load.** The engine lists the loaded rigidbodies and marks (`FindObjectsByType`, inactive ones
  included) and looks above each rigidbody for another (`GetComponentsInParent`), so nothing here walks a hierarchy but
  a candidate's own path, from the object up to the nearest ancestor already hashed. Each object on such a path is named
  and hashed once, and a wide parent searched often has every child's index read in one pass. The working sets go with
  the census; what stays is the scene's array of keyed objects, a 24-byte slot and a 48-byte wrapper each, 72 bytes a
  key by their layout (the game's heap grew 58 bytes a key over 100,000 of them). The keys also keep the engine's own
  managed objects of each keyed object and its rigidbody alive, where nothing else does. The runtime has the census's
  steps compiled as it starts; the first scene load still compiles the rest, which is most of the menu's first capture.
- **Scene keys in the game**, measured in Robotopia 2545 on a Windows 11 x64 machine: the capture inside each load's
  event, the first time each scene loads and after a full collection, and what the keys keep while the scene stays
  loaded. Each scene gave exactly
  the keys the descriptor walk the census replaced gives, through the replication model's own rule. That walk named
  every object of a scene, and cost City Streets 16.6 ms and IntroSewer 10.1 ms at their loads.

  | Scene | In normal play | Transforms | Rigidbodies | Keyed | Capture | Kept |
  | --- | --- | ---: | ---: | ---: | ---: | ---: |
  | `TestCityStartMenu` | The main menu | 520 | 0 | 0 | 3.2 ms first, 0.1 ms after | under 100 bytes |
  | `UgcPlay` | An arena's host scene, before its arena is built | 113 | 1 | 0 | 0.5 ms | under 100 bytes |
  | `IntroSewer` | A free-play world | 10,152 | 320 | 69 | 1.5 ms | 4.9 KiB |
  | `Collector Prototype` | A free-play world | 14,058 | 413 | 250 | 1.6 ms | 17.6 KiB |
  | `AcidChamber` | A free-play world | 5,211 | 112 | 69 | 0.6 ms | 4.9 KiB |
  | `City Streets` | A free-play world | 19,570 | 1,500 | 1,031 | 4.7 ms | 72.5 KiB |
  | `Robotopia Entrance` | A free-play world | 10,517 | 157 | 82 | 0.8 ms | 5.8 KiB |

  City Streets is the largest. A second census of it, right after the first, costs 1.7 ms; the rest of the first one's
  4.7 ms is the engine's first touch of each object the census visits.
- **Online**, measured over 600 frames: a host and two players with a world at rest allocate nothing per frame; with
  one body moving, 14 bytes a frame over the loopback channel. Over the real provider the world's stages allocate
  nothing at rest and 21 bytes a frame with one body moving, its snapshots' share of the channel, beside the provider's
  own 393 bytes a frame for the two peers.

### Tests

- **ModManager harness** (`--network-world`): `SceneObjectIndexTests` (the model's keys, duplicates against the model's
  census over 200 seeded hierarchies, first-motion binding, later scenes, keying a scene twice, unloads);
  `SceneKeyCaptureTests` (the hasher against the model's rule over 3,000 seeded paths with every escaped and surrogate
  character; a sibling destroyed before the game opened, which the joining player hides while the boxes after it keep
  their bindings; an object reparented and one renamed, which keep their keys; duplicates; additive scenes loaded
  before and while hosting; objects made after the load; one capture per load, forgotten on unload, and a failed one
  keyed late; and an offline frame with 500 keyed objects, which allocates nothing);
  `NativeAuthoritySuppressorTests` (exact restore, changes the game made meanwhile, partial and failing suppression,
  switches that lie); `NetworkWorldWireTests` (round trips, and hostile and truncated bytes); `NetworkWorldCensusTests`
  (the census over ledger records, later records, ended scopes); `WorldBaselineSyncTests` (a late joiner gets the whole
  world, the world before its scene, what happens during a baseline, a generation change mid-baseline and in sync, a
  replacement, a restart that rebuilds nothing, progress into the hold and the overlay, a player a broadcast skipped);
  `WorldBaselinePacingTests` (chunk bounds, a stalled window resumed from where it stopped without a skipped broadcast,
  bounded requests, players that never hold each other back, leaving players forgotten); `ReplicaTransformDriverTests`
  (interpolation timing); `NetworkWorldLifecycleTests` (leaving and detaching restore everything, lookups through the
  registry, allocation, failures that stay where they happen); `NetworkWorldPrivacyTests` (what a host writes, and
  players whose ids look like addresses, reach no log or report, and the counters are counts).
- **ModRuntime harness** (`TestNetworkWorldAcrossRuntimes`): three real runtimes over the loopback network, each loading
  the same mod. The host's scene objects and its mod's spawns, made before and after it opened, appear on a joined
  player, which builds the crates through its own copy of the mod; a box moves there under interpolation, smoothly and
  a little behind; a late joiner gets everything in its baseline before it has loaded the scene; and after leaving, and
  after shutting down while joined, every native setting is back. `TestSceneKeysCostOnlyTheLoad`: the real runtime
  keys a scene as it loads, offline, and its frames allocate nothing over 10,000 with no provider and with a solo one.
- **Multiplayer harness** (`NetworkWorldEngineTests`): real providers on the virtual network. The baseline crosses the
  real channel while the player's hold keeps it short of Ready, motion reaches the player, a link that drops and comes
  back within the grace starts again from a new baseline that rebuilds nothing the player has, and the world's share of
  a frame is measured.
- **Mutation checks**, each applied, run and reverted. The suppressor's suites fail when a restore overwrites a setting
  the game changed meanwhile, when it writes back the suppressed value instead of the original, and when a change is
  recorded only once its write returned. The baseline suites fail when a refused chunk is not rewound, when the host
  goes on with a restarted player's old baseline, when a joined player does not ask again after its stream restarts,
  and when the reader keeps a field an earlier entry read. With the host's keys taken at the promotion again, from the
  hierarchy as it is then, the destroyed sibling's test fails: the joining player keeps its copy of the box the host
  destroyed, and past that check, the pushed box binds to the box before it.

### Where WS3.7 and WS3.8 attach

Authority routing (WS3.7):

- **Messages.** Types 64 to 127 on `NetworkWorld.Channel`, through `NetworkWorldChannel.Register`, beside world
  replication's own on the same ordered stream. `TrySendTo`, `TrySendToHost` and `Broadcast` keep this section's window
  and skip rules.
- **Spawn adoption.** A host's adopted spawn is a ledger record like any other, so it is announced with its origin. The
  entries already carry an owner slot, zero today, for the participant whose proxy scope owns it. A joined player's
  `NetworkWorldClient.Build` is where a predicted instance binds in place of a new replica, by whatever provisional id
  the prediction and the host's announcement share, before `Attach` marks it.
- **Shadows.** `RegisterCanonical` on a joined player records nothing today; authority routing binds its shadows by
  `NetSpawnOrigin` at the same point.
- **Motion.** A body a player holds is simulated there: `ReplicaTransformDriver.Drive` must skip it, its kinematic
  switch must be handed back to the game for the hold and suppressed again after, and the host must bind an unbound
  scene object at once when a player acquires it, since today binding waits for first motion (`NetworkWorldHost.Bind`).
  A hand-back carries `{position, linear velocity, angular velocity}`, and snapshots carry no angular velocity, so the
  hand-back is one of authority routing's own messages.
- **Requests.** `SetTransform` and `Destroy` on a joined player become requests; the host's results reach the player as
  announcements, despawns and snapshots, as now.

Session follow (WS3.8):

- **Its own channel.** `SceneFollow`, re-baselined on its generation changes, as world replication is.
- **Scenes.** The network world already hears every scene load and unload from ModRuntime's dispatch, in every realm,
  and keys each scene as it loads. A follower loads the host's world after joining, so it keys those scenes as the host
  did, and what the world's baseline names waits for them.
- **Readiness.** The network world's hold covers its baseline and replicas only; follow holds readiness around
  LoadingWorld with its own hold and `ReportProgress`, and the player is Ready once both are released.
- **Progress.** `NetworkWorld.Progress` (`NetworkWorldProgress`: phase, entries received and expected, replicas loading,
  and a fraction) feeds the connecting overlay beside follow's own.
- **A new world.** `ReplaceSession` restarts every stream, so every player starts again from a baseline; the host's
  scene unload ends its bound objects and the new scene is keyed at its load.

### Limits, stated plainly

- **Native spawns** are not replicated: only SDK spawns, world content and scene objects. A joined player's spawners
  are off, so it makes none of its own.
- **Keys are the load's.** A scene is keyed as its load event sees it, after the scene's own `Awake` and `OnEnable`. A
  scene whose start-up makes, destroys or orders objects differently on two machines keys those objects differently,
  so they match nothing on the other side and stay where each player's scene put them.
- **A load that could not be keyed** is keyed when a session first needs the scene, from the hierarchy as it is then,
  so for that scene a sibling destroyed or reparented before then moves the keys after it. `ScenesKeyedLate` counts it.
- **Objects made after their scene loaded** are no scene objects: an SDK spawn or world content is replicated as such,
  and anything else is a native spawn (above). A generated arena, which Worlds builds after `UgcPlay` loads, is
  therefore not keyed; the arena is to be registered as world content by its key (WS4).
- **Keys stay while their scene does**: 72 bytes an object, 72.5 KiB for City Streets, offline as much as online.
- **Objects that never move** are never entities. A joined player keeps them where its scene put them, or where the
  baseline placed them.
- **A scene a joined player reloads on its own**, which follow's authority policy prevents, keeps its unbound objects at
  the scene's poses.

### Choices

- **Baselines are pulled.** A player asks on each stream start, so the host acts on requests and generations, never on
  the participant list, which may be unreliable during joins.
- **Lists are fixed when a baseline begins**, so a stalled player costs nothing per skipped broadcast.
- **Pacing by probe.** The channel answers `CanSend` for one message, at most 32 KiB, so a baseline probes for a whole
  message and sends at most 20 KiB; no reserve larger than a message could be asked for.
- **Joined players record nothing** rather than clearing the ledger on joining and leaving: clearing would drop what
  this process made before it joined, which a later promotion should baseline.
- **Restores yield to the game.** A setting the game changed while the player was joined is left as the game set it.
- **Poses, not entities, for what moved before a promotion.** Binding every object indexed at the promotion would
  make each an entity in every snapshot; a pose in the baseline costs 27 bytes once.
- **Keys at the load, not at the promotion.** Keys taken at the promotion name objects by the hierarchy the solo session
  left, so a sibling destroyed or reparented before it rebinds every object after it to the wrong copy on each joining
  player, and a destroyed object is unknown to the host. Taken at the load, the keys cost the load, at most 4.7 ms
  (City Streets), and 72 bytes an object while the scene stays loaded, and frames nothing; the promotion indexes them
  and reads poses.
