# Multiplayer runtime integration — design record

Status: Implementing (WS3.1 to WS3.4, WS3.6, WS3.9 and part of WS3.7 of the live-multiplayer plan) · Date: 2026-10-22 · Owner: multiplayer

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

All three have landed, and so have the voice service behind the SPI (section 2, "Voice"), intent tracking (WS3.4,
section 14), the participant realm (WS3.9, section 15) and the first part of authority routing (WS3.7, section 16):
spawn adoption, shadows, and validated transform changes and destroys. Section 8 says what each later WS3 slice plugs in
where, and section 13 says exactly where motion ownership (WS3.7's second part) and session follow (WS3.8) attach to the
network world.

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
| Voice and counters | `Voice`, `GetStatistics`, `GetStatus` | `Voice` is the provider's voice service, or null without one; see "Voice" below. |
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
- **Server mute.** A mute is the engine's voice moderation, the one set the voice relay and the chat ingress both read.
  It follows the player's identity, through rejoins, until hosting stops: closing every connection forgets it. The
  host drops a muted player's voice at the relay and their chat at ingress (section 5), so nothing the host relays can
  carry either. `IsServerMuted` counts the automatic mutes the relay makes for abusing voice chat
  ([MultiplayerVoice.md](MultiplayerVoice.md), section 2.4), and answers on a joined player too, from the host's voice
  roster. Live sessions always run with voice; a provider without it, as some tests compose, answers `SetServerMute`
  with `Unavailable`.
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

### Voice

`Voice` is the provider's voice service ([MultiplayerVoice.md](MultiplayerVoice.md), part 3) behind
`ProviderVoiceService`, or null when the provider has none: it runs voice only with live multiplayer switched on, and
hands out none once it has stopped.

| Members | What they do |
| --- | --- |
| `Enabled` | Off until the player turns it on, which the interface does only after consent. Until then nothing is captured, coded or sent |
| `SubmitCapture`, `SetTransmitting` | Capture at the device's rate. `SetTransmitting(true)` while push-to-talk is held or voice activation listens, and `false` at the release, which ends the talk spurt |
| `Mode`, `ActivationThresholdDb` | Push-to-talk or voice activation, and its threshold. An undefined mode, or a threshold outside -96 to 0 dBFS, is refused |
| `InputLevelDb`, `TransmitState`, `IsSpeaking` | The meter, the transmit indicator and the talk glyph |
| `GetPlaybackStream`, `TryGetPlaybackStream` | Each speaker's stream, read on the audio thread |
| `SetListenerMutes`, `GetListenerMutes` | Whom the player does not want to hear, and the set as the engine kept it |
| `GetStatistics` | The counters, mapped below; the control's `GetStatistics` carries them as well |

- **Threads.** The voice service belongs to the main thread, the engine's owner, so every member runs there and refuses
  any other with `InvalidOperationException`. The one exception is a stream's `Read`, which is wait-free on the audio
  thread: it takes no lock, never blocks and allocates nothing, so it runs even while the main thread holds the
  service's gate. Capture taken on an audio callback is handed to the main thread first. Coding runs on the provider's
  voice worker.
- **Mutes.** Names belong to the host that listed them, and a wish to hear nobody holds across joins. A participant id
  names a player only on the host that gave it, so names stay with that host, through an in-place replacement and a
  reconnect that finds this player's record kept. Leaving for solo play forgets them, whether a joined player leaves or
  a host stops hosting, and so does a join that finds another host or one without the record; names given after such a
  join, before that host lists its players, are dropped. A host's names go when it stops hosting, because they named
  players whose records closing every connection forgot, and a host never gives one id to two players. The wish to hear
  nobody names nobody, so it follows the player to every host, and through hosting again, until they change it. The
  interface shows whom the player muted from `GetListenerMutes`, never from a list of its own.
- **Streams.** The SPI hands out the service's own `VoicePlaybackStream`, never a wrapper, so a stream plays the
  speaker the session listed under its id, and nobody else. It lasts while the session lists them, through an in-place
  replacement and a rejoin within the grace, and ends for good, playing silence, when the session stops listing them,
  when the bound of 16 streams leaves no room, and when a join lets go of the list that named them: the engine's
  `ParticipantListGeneration` moves at a join that finds another host or one without this player's record, and at a
  return to solo play, and from then on the same id may name someone else. Follow a player across a join by player key,
  never by id alone.
- **A session without voice.** While this game hosts, or has joined, a session that offers no voice chat, nothing is
  coded, sent or played whatever `Enabled` says, and `TransmitState` reads `NotOffered`, ahead of every state but
  `Failed`. A host's setting is fixed for its hosting run, and a join holds to what its descriptor or LAN listing showed
  before it began, whatever the host says later ([MultiplayerVoice.md](MultiplayerVoice.md), section 2.12).
- **Asking again.** `TryGetPlaybackStream` gives the speaker's stream, or makes one for a speaker the session listed at
  the last pump while there is room, and otherwise returns false, making and allocating nothing, so it may run every
  frame. `GetPlaybackStream` always gives a stream: for a speaker the session does not list it makes one, a 64 KiB ring
  and a decoder kept for 2 s, and when the bound leaves no room it makes a new 64 KiB ring at every call. Asking it every
  frame for an ended stream costs that much each time, so ask once per speaker and ask again with
  `TryGetPlaybackStream`.

The statistics are mapped by what each counter means:

| `InternalVoiceStatistics` | From `VoiceServiceStatistics` | Meaning |
| --- | --- | --- |
| `Mode`, `State` | `Mode`, `State` | The same values, mapped by name |
| `InputLevelDb`, `ExpectedLossPercent` | The same | The meter, and the loss the encoder plans for |
| `SubmittedSamples` | `SubmittedSamples` | Capture handed in while voice was on |
| `FramesEncoded` | `FramesEncoded` | Frames coded for sending |
| `FramesSent` | `Sent` and `Held` | Frames the session took to send. The engine holds every other frame of a steady talker to share a datagram with the next, so `Sent` alone counts about half of them |
| `FramesReceived` | `FramesReceived` | Frames the engine delivered for playback |
| `FramesRefused` | `RateLimited`, `ServerMuted`, `NotConnected` and `NotOffered` | Frames the session refused to send when it was handed them. `Invalid` frames are a fault, not a refusal, and stay out |
| Each speaker's `Decoded`, `Recovered`, `Late`, `TargetDelayMicros`, `IsSpeaking` | The same, for that speaker | — |
| Each speaker's `Concealed` | `Concealed` and `Faded` | Frames concealed because nothing usable arrived in time. After three, the jitter buffer fades two more to silence, and those are concealment too. A frame it adds on purpose to keep its delay is not counted |
| Each speaker's `Underruns` | The stream's `Underruns` | Reads that found too little audio |

Each coded frame counts once, by the session's first answer, so `FramesSent` is what left only while every held frame
leaves with its pair. A frame still held when the link goes counts as sent though it never leaves, and a pair the
talker's own rate limit refused would count its first frame as sent and its second as refused. The service keeps only
the newest part of a stall's capture, so a talker never reaches that limit: through a 2.5-second stall, every frame
counted as sent reached the host (`InternalVoiceStreamTests.StatisticsAreMappedByMeaning`). A link that goes can
therefore leave one frame counted as sent that never left.

The service's other counters, such as stale capture, the worker's steps and the engine's own voice statistics, stay
in the provider.

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
| `LocalContent` | One descriptor per loaded package, sorted by id; it answers once the packages are identified, off the main thread |
| `ContentReadiness` | Whether the packages are identified yet: pending with progress, ready, or unreadable; it never blocks |
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
- **Identification, off the main thread.** The first look at `ContentReadiness` or `LocalContent` reads each package's
  install receipt, one small file, on the thread that asks, so a game whose packages all carry receipts is ready at
  once, and a player who never opens multiplayer pays nothing. Hashing the files of packages without a usable receipt
  runs on a background thread at below-normal priority (`MultiplayerContentIdentification`), never on the thread that
  asks. Until it finishes, `ContentReadiness` is pending, with progress in packages and in bytes hashed, and
  `LocalContent` throws `InternalContentNotReadyException`. Nothing failed, so nothing is reported, and the provider
  waits:
  - hosting through the control returns `Unavailable` with "This game is still identifying its mods for multiplayer; try
    again in a moment.";
  - a join through the control waits before it starts, as it waits for the identity, and so does the developer switch;
  - nothing is announced, offered or admitted meanwhile; a host is refused as `not-ready`, "The host is not ready to
    admit players yet.".

  The HOST and BROWSE views show the progress, and hosting and joining stay disabled until the mods are identified. A
  run whose packages change before it finishes is dropped, and the next look starts a new one.
- **Packages that cannot be read.** A file that identification cannot read, as when another program holds it open,
  makes the content unreadable. `ContentReadiness` says so until `LocalContent` is read again; that read throws an
  `IOException` that names the package, never its folder, and starts identifying again. The interface's retry does the
  same. The provider treats this game's content as unreadable, reports `content.read-failed`, and nothing connects:
  - hosting through the control returns `Io` with "This game's mods could not be read: a file of one of them is in use
    or cannot be opened.";
  - a live host started directly, as the developer switch does, fails as `ContentUnreadable` before anything binds;
  - a joining player's check ends the join as `ContentUnreadable`, "This game's mods could not be read.", with that line
    as its content problem;
  - a running host refuses every player as `not-ready`, with "The host could not read its own mods.".
- **The hash's cost.** On a Windows 11 x64 machine with the files cached, 512 MiB in 64 files hashed in 0.44 s on
  .NET 10 and in 3.9 s on Mono 6.13, whose SHA-256 is managed code, and 20,000 files of 4 KiB in 1.2 s and 3.2 s. That
  time is spent on the background thread; the frame only reads the progress. Installed packages carry a receipt and cost
  one small read each, at the first look.
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
own text. The first time a game socket opens over IPv4 alone, because the system has no IPv6, the provider says so once,
as the information `live.network-ipv4-only`, without an address (`MultiplayerLiveSessions.md`, section 13).

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
- **Server mute.** The host's chat channel refuses a muted player's messages as they arrive, by the engine's voice
  moderation: a mute by hand, or one the voice relay made for abuse.
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
| WS3.4 intent tracking | Landed (section 14): `IntentScope`, fed by input-active frames, the UI kit's controls and the SDK UI service, commands and interaction handlers, and read through `IRuntimeReplication.CurrentIntent` and `RouteCurrentIntent()` and the decorators' `RuntimeReplication.VisibleIntent` |
| WS3.5 core replication model | Landed in Core (`MultiplayerReplicationModel.md`); the slices below drive it |
| WS3.6 network world | Landed (section 13): `PromotionCensus`, `WorldEvents`, `HostSnapshots` and `ReplicaInterpolation`, an observer of the realm, the spawn ledger's observer, the `WorldReplication` channel, `SetEntityIdentitySource`, `MultiplayerReplication.References` and `LocalContent`, and replicas through `TryGetReplicaFactory` |
| WS3.7 authority routing | First part landed (section 16): `NetAssetService` and `NetEntityService` route a joined player's spawns, transform changes and destroys by `ExecutionRouting.Route(realm, profile, intent)`, and the SPI's `RequestSpawn` and `RegisterCanonical` do so for providers: spawn adoption under a host's per-player `ProxyParticipant` scope, shadows bound by `NetSpawnOrigin`, and requests the host validates through `PermissionPolicy` and `TokenBucket`, over message types 64 to 66 and 96 and 97. Still to come: `NetEntityMotion` behind `AcquireMotion` and `HeldItemOwnershipTracker` behind `NetItemService`, whose hooks into the network world are listed in section 13, and forwarded commands naming their sender in `InternalCommand.Sender` |
| WS3.8 session follow | `WorldEvents`; the `SceneFollow` channel, re-baselined like world replication; `HoldReadiness` around loading the host's world, with `ReportProgress` while it loads; `ReplaceSession` for a new world; the follower's world-owner scopes through `CreateChildScopeAsync`'s execution context; `Leave` from a joined player's main menu. The network world's scene hooks, hold and progress it works beside are listed in section 13 |
| WS3.9 participant realm | Landed (section 15): `IGamemodeParticipantFactory`, the participant's scope through `CreateChildScopeAsync(..., ModExecutionContext.Participant)`, and the orchestrator's start by realm, which session follow (WS3.8) calls on a joined player |
| WS3.10 presence | `PresenceSend`; the `Avatars` channel and its unreliable lane; `SetAvatarSource` |
| WS3.11 Players API | Player references (`InternalNetRefKind.Player`) through `MultiplayerReplication.References`, beside the network world's entity references |
| WS3.12 presentation routing | `NetUiService`, routing each call by `PresentationRoutingPolicy.Route(realm, profile, kind)` |
| WS3.13 pause and time | `TimeAuthority` and `NativePauseFreezesWorld`; the bridge that publishes the host's time scale and applies it on joined players adds to the SPI |
| WS4 first-party mods | Each provider reaches its context's `IRuntimeReplication`: RobotKit's replica factory, spawn requests, routed mutators, references for `Chase` targets and its dialogue's microphone lease; CreatorContent's `creator.content` factory and routed commands; the Worlds arena registered as world content by key; Chronos's time authority |
| WS5 interface | `OpenToMultiplayer`, `Join`, `Leave` and `EvaluateRealmChange` on the runtime, never the control's own hosting and joining, so "Leave current game?" follows `RequiresLeave`; `GetStatus`, targets, `Query`, LAN listings, moderation, `PreferredDisplayName` and `ContentReadiness`, with the host-authored text rules of section 2; see [MultiplayerInGameSessions.md](MultiplayerInGameSessions.md) |
| WS5 chat | The `Chat` channel, relayed by the host; server mute already drops at ingress |
| WS5 voice | The control's `Voice` (section 2, "Voice"): `Enabled` once the player consents, `SubmitCapture` and `SetTransmitting` from `PresenceSend`'s capture, each speaker's `IVoicePlaybackStream` read in an audio callback and asked for again with `TryGetPlaybackStream` once it ends, and mutes shown from `GetListenerMutes`; the microphone arbiter's `NativeRecording` from the game's recording state, and a `VoiceChat` lease that capture stops under while it is preempted. The host's `InternalHostSettings.Voice` is enforced: a session that offers no voice chat carries none, and `TransmitState` reads `NotOffered` for the transmit indicator to show ([MultiplayerVoice.md](MultiplayerVoice.md), section 2.12) |

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
- **Voice through the SPI** (`InternalVoiceTests`, `InternalVoiceStreamTests`), over the same peers with the real Opus
  codec, each voice worker stepped on the network's clock: no voice without voice options; voice off until it is turned
  on, and off again at once; push-to-talk and voice activation following the SPI's binding, mode and threshold; listener
  mutes kept through an in-place replacement and a reconnect, left behind at a move to another host whose p1 is someone
  else, and a wish to hear nobody held across the move back; a host's server mute silencing the relay, read on joined
  players from the roster, and forgotten when hosting stops; an automatic mute for malformed voice sent past the
  client's engine, counted by every control and dropping chat at ingress until it ends, and not after; a host that
  stops and hosts again keeping its bans and the wish to hear nobody, forgetting its mutes for everyone, by hand and
  automatic, and the names its player muted, and taking a kicked player back at once; the service's own streams, kept
  across a rejoin within the grace and ended once the record goes; asking again for an unlisted or refused speaker
  allocating nothing while `GetPlaybackStream` makes a ring each time; every statistic against the service's own, and
  every frame counted as sent reaching the host through a 2.5-second stall; every member refused off the main thread
  while a stream reads past the service's gate; and a stream for one host process's p1 ending at the reconnect to its
  restarted process, never playing the new p1. `InternalVoiceOfferTests` adds sessions that offer no voice chat: the
  host's player and two joined players with voice on coding, sending and hearing nothing, three hosting runs each with
  its own setting, a session the launcher planned without voice, and a join shown without voice that stays without it
  after reconnecting to its host restarted with voice.
- **Admission through the runtime host** (`RuntimeAdmissionTests`): the descriptor's packages by role, a stale player
  refused by the host's planner with its report reaching the player, the joining player's own check before connecting, a
  required session mod at a compatible version joining, the offer's format and its refusals, the content seam
  without a runtime, and a package whose only file another handle holds open: hosting through the control and through
  the live sessions refused before anything binds, a joining player stopped before anything connects, a running host
  refusing every player, every failure reported naming the package and never its folder, and no frame failing.
- **Identification** (`MultiplayerContentIdentificationTests`, `RuntimeContentReadinessTests`): nothing hashes on the
  calling thread; progress counts packages and bytes; an unreadable package stays unreadable until read or retried, and a
  run whose packages change is dropped; the production thread identifies a package; settled readiness allocates nothing;
  and while identification runs, hosting is refused as not ready, a join and the developer switch wait, and nothing is
  announced, admitted or opened.
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
  and the host's words reach neither a log, through the developer switch or the runtime, nor a mod. For voice
  (`InternalVoiceStreamTests.TheVoiceSpiCarriesNoAddress`): the voice SPI with every SDK type it names, transitively,
  and the provider's types behind it, scanned the same way, and every name it answers in a live session a listed
  participant's id.
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
- **Intent tracking** (section 14 lists its suites).

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
  `EvaluateRealmChange(target)` asks the rule alone, so the interface can ask "Leave current game?" before it acts. It
  allocates nothing, as the interface asks it every frame its views bind a choice
  ([MultiplayerInGameSessions.md](MultiplayerInGameSessions.md), section 6).
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
  what it is, its transform when it was made, its description, the entity, the seam that recorded it, its shadow key,
  and the slot of the player whose object it is, zero for the host's world (section 16). Only what a listen host would
  baseline is recorded: a client-local mod's spawns, which are never networked, and a standalone mod's, which never go
  online, are not, and cost nothing. Nor is anything a joined player spawns or registers: its
  spawns are predictions and shadows of the host's, never canonical (section 16). Registering a recorded entity again
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
  | A session's scope for its gamemode's package, also when that package provides the world, where the server runs | `GamemodeServer` |
  | A session's scope for any other package it selects (the world's, and a launch target's owner), and for the gamemode's package on a joined player | `WorldOwner` |
  | A content or discovery scope | Its caller's |
  | A participant scope (WS3.9, section 15) | `Participant` |

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
  | `Route(intent)` | `ExecutionRouting.Route` for the realm, the profile and the given intent | — |
  | `CurrentIntent`, `RouteCurrentIntent()` | The intent the calling code acts on, as the context's mod sees it, and the route for it (section 14) | WS3.7 routes spawns, transform changes and destroys by it |
  | `RegisterReplicaFactory` | Kept, one per kind in the process; a joined player builds the host's entities of that kind through it, and releases them through `ReleaseReplica` | — |
  | `RegisterCanonical` | Offline and on a host, records at once, and a host announces it, a scripted SDK spawn with its mod's next shadow key. On a joined player records nothing: world content waits to bind by key to the host's, a scripted SDK spawn is a shadow, and one for the player is a prediction its kind's spawn handler makes again on the host (section 16) | — |
  | `RequestSpawn` | Offline and on a host, runs the spawn here and records it. On a joined player runs it here and records nothing: scripted, a shadow; for the player, a prediction the host adopts or refuses, refused here when the kind has no spawn handler (section 16) | — |
  | `RegisterSpawnHandler` | Kept, one per kind in the process: how the host makes the mod's kind for a player, inside that player's forwarded intent, and how it ends one | — |
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
- **Packages without a receipt are hashed at first use, off the main thread**, so a player who never plays online pays
  nothing and a frame never waits for it. Hand-copied builds are development work, and section 4 measures the cost;
  content reports as pending, with its progress, until the work is done.
- **Bans are listed by id, not by key**, so the interface never holds a player's key.
- **The voice service's own streams, not wrappers.** A wrapper that looked a speaker up by id at each read would carry
  on into whoever has that id after a join; the service's stream ends there, and stays ended.
- **`TryGetPlaybackStream` beside `GetPlaybackStream`.** The plan's emitter asks for one stream per avatar; once one
  ends, asking again must cost nothing until there is something to give, and the service's own `GetPlaybackStream`
  makes a stream for anybody it is asked about. The new member gives only what exists or what the session lists.
- **`FramesSent` counts held frames, and `Concealed` faded ones.** The SPI's counters say what happened to the frames;
  the service splits both further, and a field taken by name alone would undercount.
- **One set of server mutes, the engine's.** The control kept its own set for chat, which the voice relay never saw.
  Now the engine's moderation drives both, automatic mutes included, and closing every connection forgets it, as the
  control's set ended with hosting. It forgets the names the host's own player muted too, since they named only the
  players whose records went; bans are kept by identity, in the host's data.
- **Chat follows automatic mutes.** A player muted automatically for abusing voice chat cannot chat until the mute
  ends, 30 seconds later. The roster carries one muted flag, so a joined player learns only that the host muted it, and
  chat that went through while its indicator said so would make the flag wrong. An honest client is never muted
  automatically: it checks its own frames and keeps to the host's limits before anything leaves.
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
- **Intent tracking's choices** are recorded with it, at the end of section 14.

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
  its origin, `{owning mod, shadow key}` for a spawn a joined player's own run of the same script shadows (section 16)
  and none otherwise, and its owner slot, and marked through `Adopt`. Then the loaded scenes' objects are indexed
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
  only while that player's window would also take a whole 32 KiB message. The player reports each chunk it reads
  (`BaselineProgress`, at most once a frame), and the host keeps at most four chunks, 80 KiB, in flight beyond the
  last one reported. The link, not the window, sets the pace: 80 KiB keeps a 256 kbit/s link busy across seconds of
  round trip, and the broadcasts behind a baseline keep all but 80 KiB of the player's 1 MiB window, however large the
  world. Paced by the window alone, a baseline larger than the window filled it to its last 12 KiB, so an announcement
  of a few robots, up to 24 KiB, skipped the player and started its baseline again: over the real provider, seven
  players joining a 1.2 MB world while the host spawned three robots every three seconds never became Ready in seven
  minutes, 231 baselines begun and none completed; paced by what they read, each was Ready after 49.5 s, in one
  baseline. A report for another baseline, or past what was sent, moves nothing further. A chunk the window refuses is
  written again next frame from where it stopped; an entity that ended before its entry was written is left out,
  since the player hears of its end. A new baseline for the same player supersedes the one under way.
- **Its world.** Each promotion draws a random, non-zero world instance, and every `BaselineBegin` names it, so a
  player can tell a rejoin to the same world from one to another world of the same host (below).

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
  restored exactly as far as it went. A scene object the host removed is hidden through the same suppressor. When a
  scene unloads, or the next one loads, whichever the engine reports first, the settings of objects gone with it are
  forgotten, so an unloaded scene stays reachable through nothing until the player leaves.
- **Applying.** `BaselineBegin` marks every mirrored entity stale, entries and announcements make them current, and
  `BaselineEnd` removes whatever is still stale. A known entity whose description is unchanged keeps its replica, and
  a changed one is built again. A baseline's entry is newer than any sample the player buffered before it, so the
  entity drops those samples and stands at the entry's pose until the next.
- **Building.** A scene object binds by its key. World content binds by its content key to the entity this player's
  own world provider registered (`RegisterCanonical` on a joined player). An asset spawn is built through the owning
  mod's package asset service from its bundle path and asset name: each bundle and prefab is loaded once and shared,
  polled each frame and never awaited, and a load that takes 30 seconds counts as unbuildable. A session loads at most
  256 bundles and 4,096 prefabs for replicas, so a host that makes paths up gets no more file lookups or kept loads
  than that; past the bound a replica counts as unbuildable and the refusal is counted. A provider's spawn is
  built by its kind's replica factory. Replicas are made kinematic, marked and placed. What cannot be built stays
  mirrored, is counted, and is tried again only when its description changes; nothing throws. A player mirrors at most
  16,384 entities.
- **Before its scene.** An entity whose scene object is not loaded binds when the scene loads, and the keys the host
  removed and the poses it placed wait for their scene too, up to 65,536 keys. The baseline may therefore arrive before
  session follow (WS3.8) has loaded the world.
- **Readiness.** The hold is released once a baseline is applied and no replica is still loading; each finished replica
  reports progress. A stream that restarts takes the hold again, until its new baseline is in place. The engine counts
  every message of the host's world as progress while the player is held, so a baseline that keeps arriving never
  meets the hold's 60-second idle limit; holds last 300 seconds in all, about 9 MiB of baseline at 32 KiB/s.
- **Another world.** A host's process can restart under the same identity, and a player whose link was down finds it
  again within the grace, as the engine allows: the new process serves another world, numbered from one again and
  with whatever the old one destroyed. Its first baseline names another world instance, and the player starts that
  world from nothing: every replica is released and every scene object unbound, the entity table forgotten, the
  objects the old world hid shown again (only those: native authority stays off), and the loaded scenes indexed
  afresh by the keys they took as they loaded. A rejoin to the same process names the same instance and rebuilds
  nothing.
- **Snapshots.** Each sample goes into its entity's interpolation buffer at the host's time and this player's arrival
  time. In LateUpdate, before the mods, the driver writes every replica the buffer's pose for now. A replica still
  being built keeps its samples and moves once it exists.
- **Leaving.** Every replica is released: asset replicas are disposed, and factory replicas go back through
  `ReleaseReplica`. Scene objects are unmarked, native settings restored, bundle and prefab handles let go of, and the
  hold released. A load still running is cancelled, and the engine's loader lets go of its result on the game thread as
  it lands: the loader runs what waits for a load on another thread, where no handle may be let go of, and a bundle
  left loaded would make the engine refuse it to the next join. Each step runs whatever the one before it did, and a
  restore that failed is logged once, with its counts.

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

Every message starts `[version 1][type]`, and varints are minimal LEB128. A malformed message is counted, and reading
stops at its first bad field: items before it in a list still apply, as the host could have sent them alone.

| Type | From | Body |
| --- | --- | --- |
| 1 `BaselineBegin` | Host | baseline number, expected entries, host time, world instance (8 bytes, never zero) |
| 2 `BaselineEntries` | Host | baseline number, count, entries |
| 3 `BaselineEnd` | Host | baseline number |
| 4 `Spawn` | Host | count, entries: new entities, and new descriptions |
| 5 `Despawn` | Host | count, network ids |
| 6 `Snapshot` | Host | host time, count, then id and transform record each |
| 7 `SceneRemoved` | Host | count, scene keys |
| 32 `BaselineRequest` | Player | reason: 0 when a stream starts, 1 to resynchronize |
| 33 `BaselineProgress` | Player | baseline number, chunks read |
| 64 `SpawnRequest` | Player | authority routing (section 16): provisional id, mod, kind, intent, input frame, scale, transform, description |
| 65 `TransformRequest` | Player | target (network id, or the player's provisional id), scale, transform |
| 66 `DestroyRequest` | Player | target |
| 96 `SpawnAdopted` | Host | provisional id, network id |
| 97 `SpawnRefused` | Host | provisional id, reason |
| 67 to 95, 98 to 127 | Either | Kept for authority routing's second part (WS3.7) |

An entry starts with its source: a scene object (id, key, pose), an SDK spawn (id, kind, owning mod, origin sequence,
owner slot, scale, pose, description), world content (id, kind, content key, owning mod, owner slot, scale, pose,
description), a removed scene object (key), or an unbound scene object's pose (key, pose). A joined player checks every
field as the runtime checks its own: kinds and mod ids by their token rules, an origin sequence of 0, for none, or a
shadow key from 1 (section 16), an owner slot up to 255, finite non-zero scales up to 10,000, content keys as world
content's, descriptions up to 16 KiB, and transform records exact in their flags, length and spare bits. A message is
at most 24 KiB and lists at most 4,096 items.

### Privacy and cost

- **Privacy.** Players are named by participant id inside the process and never logged. The world logs fixed texts and
  counts only, and its counters hold counts. Nothing it sends, keeps or answers holds an address, and the architecture
  test of section 9 covers its types with the runtime's.
- **Offline**, every frame hook is one field read; the offline frame still allocates zero bytes over 10,000 frames,
  through the composition root and the real `ModRuntime`, with a keyed scene loaded. A scene load keys that scene inside
  its event, and an unload forgets its keys.
- **The census at a load.** The engine lists the loaded rigidbodies (`FindObjectsByType`, inactive ones included),
  and the marks too, but only while some mark carries an id: each mark counts itself in `NetworkMarks` while it does,
  and offline none does, so a load in single player skips the engine's search through every behaviour. It looks above
  each rigidbody for another (`GetComponentsInParent`), so nothing here walks a hierarchy but a candidate's own path,
  from the object up to the nearest ancestor already hashed. Each object on such a path is named
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
  4.7 ms is the engine's first touch of each object the census visits. The table was measured with the search for
  marks at every load. Without it, as every offline load now runs, ten loads of City Streets over two runs, alternating,
  took 2.2 to 3.5 ms (2.8 ms on average; the first load of each run, which pays the engine's first touch, 3.1 and
  2.9 ms), and with it 3.9 to 4.4 ms (4.1 ms); the search alone, measured warm just after, cost 0.9 to 1.8 ms. Every
  one of the ten gave the descriptor walk's 1,031 keys, none of them the local player's, whom the census found at each
  load.
- **Online**, measured over 600 frames: a host and two players with a world at rest allocate nothing per frame; with
  one body moving, 14 bytes a frame over the loopback channel. Over the real provider the world's stages allocate
  nothing at rest and 21 bytes a frame with one body moving, its snapshots' share of the channel, beside the provider's
  own 393 bytes a frame for the two peers. In the game, promoted in place in City Streets with a loopback player, the
  host's stages driven 2,000 ticks at rest allocated nothing and sent nothing; over 600 frames the receive stage
  allocated nothing, and the send stage one 4 KiB heap block while bodies the run had pushed still settled and 60
  snapshots went out, each of which the loopback test channel copies.

### Tests

- **ModManager harness** (`--network-world`): `SceneObjectIndexTests` (the model's keys, duplicates against the model's
  census over 200 seeded hierarchies, first-motion binding, later scenes, keying a scene twice, unloads);
  `SceneKeyCaptureTests` (the hasher against the model's rule over 3,000 seeded paths with every escaped and surrogate
  character; a sibling destroyed before the game opened, which the joining player hides while the boxes after it keep
  their bindings; an object reparented and one renamed, which keep their keys; duplicates; additive scenes loaded
  before and while hosting; objects made after the load; one capture per load, forgotten on unload, and a failed one
  keyed late; an offline frame with 500 keyed objects, which allocates nothing; and the marks' count, which lets a
  load skip their search); `NativeAuthoritySuppressorTests` (exact restore, changes the game made meanwhile, partial and
  failing suppression, switches that lie, an unloaded scene's settings forgotten in either order of the engine's
  reports and its objects unreachable, one kind restored alone); `NetworkWorldWireTests` (round trips, hostile and
  truncated bytes, a host that names made-up assets without end, and made-up prefabs of a bundle the player has, keys
  of scenes the player never loaded past their bound, a baseline that names no world); `NetworkWorldCensusTests` (the
  census over ledger records, later records, ended scopes); `WorldBaselineSyncTests` (a late joiner gets the whole
  world, the world before its scene, what happens during a baseline, a generation change mid-baseline and in sync, a
  replacement, a restart that rebuilds nothing, progress into the hold and the overlay, a load that never lands giving
  up, a player a broadcast skipped, a host whose process restarted serving another world);
  `WorldBaselinePacingTests` (chunk bounds, a stalled window resumed from where it stopped without a skipped
  broadcast, bounded requests, players that never hold each other back, leaving players forgotten, a world larger than
  the window with a large announcement behind its baseline, progress reports that move only their own baseline);
  `ReplicaTransformDriverTests`
  (interpolation timing); `NetworkWorldLifecycleTests` (leaving and detaching restore everything, leaving part way
  through a baseline, loads that land after the player left let go of on the game thread through the runtime's own
  native asset operation, so the next join loads them again, a channel that will not open saying what to do, lookups
  through the registry, allocation, failures that stay where they happen); `NetworkWorldPrivacyTests` (what a host
  writes, and players whose ids look like addresses, reach no log or report, and the counters are counts).
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
  destroyed, and past that check, the pushed box binds to the box before it. The bounds fail as plainly: without the
  cap on loads a hostile host has 2,000 bundles looked up; paced by the window alone the large world's announcement
  skips the player; with progress past what was sent taken whole, or for any baseline, the reports' test fails;
  without the new world's reset, or with the old world's hidden objects kept, the restarted host's box stays hidden;
  and without forgetting at the unload, or at the next load, the unloaded scene's settings stay held. Without the
  prefab cap, a host that makes prefab names up has 4,597 loaded from one bundle; a player that stops sending its
  progress, or counting the chunks it read, never completes a large baseline; without cancelling a load still running
  when the player leaves, or without passing the load its token, the late bundle or prefab stays loaded; without the
  bounds on keys of scenes not loaded, a hostile host has 78,000 removed keys or 76,000 poses kept; and without the
  timeout, a load that never lands keeps the player loading.

### Where WS3.7 and WS3.8 attach

Authority routing (WS3.7). Its first part has landed (section 16): its messages are types 64 to 66 and 96 and 97 on
`NetworkWorld.Channel`; a host's adopted spawn is a ledger record announced with its player's owner slot; a joined
player's `NetworkWorldClient.Build` binds a prediction or a shadow in place of a new replica before `Attach` marks it;
and `SetTransform` and `Destroy` on a joined player are requests whose results reach the player as announcements,
despawns and snapshots. The second part attaches here:

- **Messages.** Types 67 to 95 and 98 to 127 on `NetworkWorld.Channel`, through `NetworkWorldChannel.Register`, beside
  world replication's own on the same ordered stream. `TrySendTo`, `TrySendToHost` and `Broadcast` keep this section's
  window and skip rules.
- **Motion.** A body a player holds is simulated there: `ReplicaTransformDriver.Drive` must skip it, its kinematic
  switch must be handed back to the game for the hold and suppressed again after, and the host must bind an unbound
  scene object at once when a player acquires it, since today binding waits for first motion (`NetworkWorldHost.Bind`).
  A hand-back carries `{position, linear velocity, angular velocity}`, and snapshots carry no angular velocity, so the
  hand-back is one of authority routing's own messages. A player's own adopted object is already simulated nowhere
  but the host; a grab of one goes through the same ownership protocol as any other body.

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
- **One snapshot schedule for every player.** Relevancy (`RelevancySet`) needs each player's position, which arrives
  with presence (WS3.10), so every player gets the same snapshots, 800 bytes a tick at most. Measured over the real
  provider with eight players in a City Streets of 1,031 keyed objects: up to about 50 bodies moving at once, each
  reaches every player at the full 10 Hz; 100 at 5.3 Hz each; 300 at 1.7 Hz each, below what interpolation hides. No
  player ever missed a broadcast, and snapshots cost each link about 8 KB a second. Per-player budgets and relevancy
  come with presence; bodies a player holds will stream at 30 Hz of their own (WS3.7).

### Choices

- **Baselines are pulled.** A player asks on each stream start, so the host acts on requests and generations, never on
  the participant list, which may be unreliable during joins.
- **Lists are fixed when a baseline begins**, so a stalled player costs nothing per skipped broadcast.
- **Pacing by what the player read.** The channel answers `CanSend` for one message, at most 32 KiB, so no reserve
  larger than a message can be probed for, and the window is a megabyte. A baseline therefore probes for a whole message
  and sends at most 20 KiB, and keeps no more than four chunks in flight beyond what the player reported reading.
- **Joined players record nothing** rather than clearing the ledger on joining and leaving: clearing would drop what
  this process made before it joined, which a later promotion should baseline.
- **Restores yield to the game.** A setting the game changed while the player was joined is left as the game set it.
- **Poses, not entities, for what moved before a promotion.** Binding every object indexed at the promotion would
  make each an entity in every snapshot; a pose in the baseline costs 27 bytes once.
- **Keys at the load, not at the promotion.** Keys taken at the promotion name objects by the hierarchy the solo session
  left, so a sibling destroyed or reparented before it rebinds every object after it to the wrong copy on each joining
  player, and a destroyed object is unknown to the host. Taken at the load, the keys cost the load, about 3 ms for City
  Streets in single player, and 72 bytes an object while the scene stays loaded, and frames nothing; the promotion
  indexes them and reads poses.
- **A world instance in every baseline**, rather than a signal from the engine, so a player tells another world of the
  same host by what world replication itself sends.

## 14. Intent tracking

WS3.4 records, for the code that runs now, whether it acts on a player's intent and whose: the local player's, or a
participant's that a listen host acts for. Code that acts without intent, as scripted code does, has none. Authority
routing (WS3.7) reads it to decide whether a client's spawn, transform change or destroy is forwarded to the host or is
a shadow of the host's own (the execution model's context table). Authors never annotate anything: the runtime opens
the scope around the mod code that runs for a player's own action, and the rules below decide what that code's
continuations, timers and callbacks see.

| Part | Where | What it does |
| --- | --- | --- |
| `IntentRecord`, `IntentScope` | `Core/Multiplayer/IntentScope.cs` | The record, and the scope that carries it with the execution context |
| `IntentLogicalStore` | `Core/Multiplayer/IntentLogicalStore.cs` | The logical call context, where a scope keeps its record on the game's Mono |
| `IntentEntryRules`, `IntentFlowRules` | `Core/Multiplayer/IntentTables.cs` | The two rule tables, and the decisions that apply them |
| `MultiplayerIntents` | `MM/Multiplayer/Intent/` | The runtime's tracking: the realm epoch, the entry points, the motion lease and forwarded-request seams |
| `NetInputService`, `NetScheduler`, `NetInteractionService` | `MM/Multiplayer/Intent/` | The decorators that feed and carry it, beside section 11's |
| `ModContext.RaiseUpdate`, `RaiseLateUpdate` | `MM/ModContext.Intent.cs` | Input-active frames |
| `OwnerCommandService`, `ModServiceRegistry` | `MM/OwnerCoreServices.cs`, `MM/ModServiceRegistry.cs` | Commands, and the player's own command for the chat overlay (`ModRuntime.TryExecutePlayerCommand`) |
| `TopiaForgeIntent`, `TopiaForgeCallbacks.Player` | `src/TopiaForge.Mods.UnityUi/Runtime/` and the widgets | The kit's half: a player's input on a control |
| `MultiplayerIntentKitHook` | `MM/Multiplayer/Intent/Kit/` | The runtime's sink for the kit, which ModRuntime installs and removes |
| `IRuntimeReplication.CurrentIntent`, `RouteCurrentIntent`, `InternalIntentRecord` | `src/TopiaForge.Mods.Abstractions/Internal/` | The SPI first-party providers and WS3.7 ask |

### The record and the scope

- **A record** names what caused the intent (`IntentSource`), the mod whose code acts on it, whose intent it is (null
  for this process's own player, or the participant id the host gave a player), the realm epoch it was recorded in,
  and whether an input-active frame made it (`IsInputFrame`, below). It never holds an address. The runtime keeps one
  record per mod and source for an epoch, so the steady state makes none.
- **The scope** carries the record with the execution context, as an `AsyncLocal` would. Opening one runs the mod code
  with the record flowing to it and to every continuation that code starts, then restores what flowed before, on
  success and on failure alike. It is observed only on the thread that made it, the game's main thread; every other
  thread reads none.
- **How it carries the record** (`IntentScopeMode`) is the cheapest way the runtime has that keeps every rule, picked
  once per process by a probe that checks the runtime's behaviour first:
  - **.NET** runs a captured execution context more than once, so the scope keeps, on each record, the context that is
    the caller's plus the record, and runs that again (`ExecutionContext.Run`).
  - **The game's Mono**, like the .NET Framework, runs a captured context once only. There the scope keeps the record
    in the logical call context (`IntentLogicalStore`): set in place on the main thread, copied only when something
    captures the context, as an await, `Task.Run` or a timer does, and removed, never set to null, when the scope ends,
    so a capture at rest copies nothing of it. It uses the public static members of corlib's
    `System.Runtime.Remoting.Messaging.CallContext` (`LogicalGetData`, `LogicalSetData`, `FreeNamedDataSlot`), bound
    by name because the netstandard2.1 surface does not name the type, and only after the probe has seen that a
    capture keeps the value it held, a later capture does not, and a value set while a captured context runs stays in
    it. Nothing private in corlib is touched.
  - **Elsewhere** it sets an `AsyncLocal` and restores it, which copies the runtime's map of async-locals on each
    change. That is the fallback when neither way above exists, and the probe falls back to it on any failure.

  The manager log names the way once, when the runtime first attaches to the provider, as information, and as a
  warning when it is the fallback, so a corlib that stops offering the logical call context shows there rather than
  only as allocations in a profiler.
- **Online only.** Offline every route is canonical whatever the intent, so no scope opens, and each entry point costs
  one field read. The realm reports each change to the tracking at the pump that shows it (`FollowRealm`), and each
  change starts a new epoch.
- **Who observes a record** (`IntentFlowRules.Observe`): the mod the record names, on the main thread, in the record's
  epoch. Mod ids compare as the runtime's do, ignoring case.

### Where intent comes from

`IntentEntryRules` classifies every place the runtime runs mod code. Only the player's own actions carry intent:

| Entry point | Source | Opened by | Whose |
| --- | --- | --- | --- |
| A mod's Update in a frame where one of its own SDK input actions, in any of its contexts, was pressed or released at that frame's sample, or is held | `InputActiveFrame` | `ModContext.RaiseUpdate`, around that mod's Update only, after `CoreGameplayServices.BeginFrame` sampled input | The local player |
| The same mod's LateUpdate in that frame | `InputActiveFrame` | `ModContext.RaiseLateUpdate`, by the same test | The local player |
| The player's own input on a TopiaForgeUi control: a click or confirm, a value chosen, typed, dragged or submitted, a selection, a close by the close button or Escape, a hotkey, a key pressed into a keybind field | `UiCallback` | The kit, where the input arrives (`TopiaForgeCallbacks.Player`, `TopiaForgeIntent.PlayerAction`); the SDK UI service renders through the kit, so its callbacks run inside it | The local player, as the control's owner: `UiHost.OwnerId`, the mod's id for `Context.Ui` and for `TopiaForgeUi.For(context)` |
| A command the player runs, or that code acting on a player's intent runs | `OwnerCommand` | `ModServiceRegistry`, around the command's handler, as the command's own mod; `ModRuntime.TryExecutePlayerCommand` for the chat overlay (WS5) | The player whose intent ran it |
| An interaction handler | `InteractionHandler` | `NetInteractionService`, around each call the game makes when the local player interacts | The local player |
| Code driving a motion lease the player holds | `MotionLease` | `MultiplayerIntents.EnterMotionLease`, the seam WS3.7 calls; nothing calls it yet | The local player |
| On a listen host, the code that carries out a participant's forwarded request | The request's own | `MultiplayerIntents.EnterForwarded`, the seam WS3.7 calls; a joined player never acts for another, so there it opens nothing | That participant, by id |

**Input-active frames are the coarse source.** A mod's whole Update and LateUpdate carry the player's intent while its
input is active, scripted work in them included, so a record says whether an input-active frame made it
(`IsInputFrame`), and what such a frame starts stays coarse: a command it runs, and a control's event its code fires,
whichever mod owns the control, since the player's own input never reaches a control from inside mod code. A click, a
command the player runs, an interaction and a motion lease are direct actions.

Everything else is scripted and carries none: a mod's Update and LateUpdate in a frame where none of its own input is
active, FixedUpdate (it runs before the frame's input sample), a timer that scripted code scheduled, a command that
scripted code runs, what only happens to a control (hover, focus, row binding, a theme or accessibility change, and
anything code does to a control itself: a value set without notifying, a programmatic close, show or selection, a
value set through an input field's `Input.text` while it has no focus), load and unload, scene and session events,
world events, and every other runtime callback.

### How intent flows

`IntentFlowRules` states what each way out of the intent's code does with it. These are the exact rules:

| Flow | What it does |
| --- | --- |
| An await's continuation | Keeps it, whenever and wherever it resumes on the main thread, also frames later: the record flows with the execution context. `DelayAsync` is an await |
| Code on a worker thread (Task.Run, the thread pool, a timer's thread, a thread of the mod's own) | Observes none. An await that comes back to the main thread observes it again |
| Work a worker hands back through the SDK scheduler, as the SDK tells background work to | Captures it, when the intent's own code started the worker: the worker never observes the record, but the execution context carries it there |
| `NextFrame`, `After`, `Every` | Capture what the scheduling code observes, its own mod's intent, and run with it each time, `Every` on every run, while the realm lasts. Scripted code's schedules capture none and are handed to the host's scheduler unchanged. A coroutine stepped through `NextFrame` keeps what its first step captured |
| A callback the kit defers, such as a window's or modal's close animation finishing | Captures what flowed where it began, and its completion runs with it |
| A native Unity coroutine's steps after its first | None: Unity runs them from its own loop. Its first step runs inside the code that starts it |
| An event subscription made inside a scope | Carries nothing: the subscriber is a frame or scene callback, decided by the table above |
| Later frames | Never: a frame's scope ends with that mod's callback, and the next frame, its timers included, starts with none |
| Another mod's code run from inside the scope: its callbacks, the services it published, and their continuations | None: only the mod the record names observes it. A first-party provider bound to the calling mod's context acts as that mod, so it does |
| A change of realm: hosting, joining, leaving, stopping | Ends it: nothing recorded before is observed after, also mid-scope |
| A command run from inside the scope | Re-attributed: the handler acts for the same player, as an owner command of the command's own mod, and stays coarse when an input-active frame ran it. A command scripted code runs clears what else flows, so it never borrows another mod's intent through the mod that ran it |
| A control's event that code fires while an input-active frame's intent flows | Coarse: the control's owner acts on the player's UI intent as that frame's (`IsInputFrame`), never as a click |
| A scope opened inside another | The inner one flows for its duration, and the outer one again after it. The same record nested changes nothing |

### What it costs

- **Offline and online at rest** a frame allocates nothing: measured over 10,000 frames through the real `ModRuntime`
  and through real contexts, the B3 tests included. Online at rest each mod's frame reads its tracked actions.
- **An input-active frame** allocates nothing in the steady state on .NET, measured the same way, by reusing its
  context. The async-local fallback costs 160 bytes a scope there.
- **On the game's Mono** an input-active frame's scope allocates nothing in the steady state either. Measured with the
  built Core assembly on the game's own 64-bit runtime (`mono-2.0-bdwgc.dll`, the Boehm collector) and its own corlib,
  embedded the way the Unity player embeds Mono, with an allocation profiler counting what the main thread allocates:
  0 bytes a scope over 1,000,000 scopes, with no collection, at about 0.10 microseconds a scope. The
  async-local way, which this slice first shipped there, measured 456 bytes and 0.5 microseconds a scope on the same
  runtime. The same runs checked every rule there, under the logical call context and the fallback alike: awaits keep
  the record, also when the awaited work completes on a worker or while another scope is open; a capture is a snapshot;
  `Task.Run`'s thread observes none while its context carries the record for what it hands back; nested scopes and
  failures restore; a scope opened inside an async method or a continuation ends there; other threads open nothing.
  A second run on the same runtime added that reads inside a scope allocate nothing, a thread left after running a
  captured context carries none, an await resumed at once inside another scope keeps its own record and the other
  returns after it, and 2,000 frames of awaits leave nothing flowing between frames while every continuation keeps its
  own record. It also showed that the startup probe, run while execution-context flow is suppressed, refuses the
  logical call context, which is why the way records run is logged.
- **An await inside a scope** copies the record's slot along with the context: on the game's Mono a capture there costs
  384 bytes rather than 72. Every await already allocates its state machine and continuation, and at rest a capture
  copies nothing of the scope.
- **UI activations, commands and interactions** allocate a little each time a player's action opens one: a command a
  record, an interaction the delivery of its arguments. A control's activation reuses one record per owner, whether
  the player's click opened it or an input frame's code fired it.
- **Time.** The ModRuntime harness reports a whole frame's time in each case and never asserts it, since it depends on
  the machine. On a Windows 11 x64 machine on 2026-10-18, the fewest of five 10,000-frame runs took 0.15 microseconds a
  frame offline, 0.23 online at rest and 0.35 online with input held, for a mod with one input action.

### The SPI, and what WS3.7 can ask

- **"Does this call carry intent, and whose?"** `IRuntimeReplication.CurrentIntent` answers for the context's own mod:
  its source, `IsPlayerIntent`, `IsLocalPlayer`, the participant id when a host acts for a participant, and whether it
  is one of the player's direct actions (`IsDirectAction`) or an input-active frame's coarse intent (`IsInputFrame`). It
  allocates nothing. `RouteCurrentIntent()` is `Route` for that source, so on a joined player a call on intent is
  `Forwarded` and a scripted one a `Shadow`; a gamemode server there is `NotRun`, and a client-local mod's `LocalOnly`.
  Inside the runtime the network decorators read the same through `RuntimeReplication.VisibleIntent`.
- **Forwarded requests.** On the host, WS3.7 runs the code that carries out a participant's request inside
  `EnterForwarded(mod, source, participantId, fromInputFrame)`, so a spawn made there knows whose it is, for its
  `ProxyParticipant` scope, and a command it runs keeps that participant. The request carries whether the participant's
  process made it from an input-active frame, so the host weighs it as that process did. An interaction forwarded that
  way runs the mod's own handler, `InteractionIntent.Handler`, rather than the local player's relay.
- **Motion leases.** WS3.7 opens `EnterMotionLease(mod)` around the code that drives a lease the local player holds,
  such as GravityGun's FixedUpdate, which moves the body the player holds.
- **Providers that act for another mod.** A provider's own seam answers for the provider's mod, so it sees none of a
  consumer's intent. CreatorContent's and RobotKit's facades are bound by owner id alone (`IOwnerBoundExtensionFactory`),
  and their spawns run in another mod's code: RobotKit's own for a robot, and the content source's factory for creator
  content. Binding those facades to the consumer's context (`IOwnerContextBoundExtensionFactory`, as Worlds already
  is) lets them ask the consumer's seam (`IInternalReplicationContext.Replication.CurrentIntent`), but that answer still
  has to reach the seam that routes the spawn. WS3.7 decides how: re-attributing the consumer's current intent to the
  mod whose code spawns, for the call's duration, as a command does, or handing the route down with the request.
- **Input-active frames are coarse.** A mod's whole Update and LateUpdate carry `InputActiveFrame` while its own input
  is active, scripted work included. On a joined player, a scripted spawn made there reads as the player's, although the
  host's own run of the same script makes it too. `CurrentIntent.IsDirectAction` is false for it and for what it starts,
  so WS3.7 can weigh it apart from a click, a command or an interaction when it decides between forwarding a spawn and
  shadowing it.

### Privacy

Records name players by participant id alone; nothing in the tracking is shaped like an address or names a network
type, and an architecture test scans every type it adds. A record's text names its source and whether the local player
or a participant acts, never the participant's id. The tracking logs one line, the way it runs records, and names no
player in it.

### What an author needs to know

The four sources cover the common cases by themselves: input read in Update or LateUpdate, UI callbacks, commands,
interactions, and whatever their code awaits or schedules. Code that does any of the following is treated as scripted
on purpose, and the fix keeps it the player's:

| Code | Why it is scripted | Fix |
| --- | --- | --- |
| Input read in FixedUpdate, or in a timer that load scheduled | Only a mod's Update and LateUpdate in an input-active frame carry the frame's input | Read input in Update or LateUpdate; the analyzer warns about an edge read in FixedUpdate (TF1009). A body the player drives with physics gets a motion lease (WS3.7) |
| A completion polled in a later Update, as `PendingOperation<T>` polls | The later frame is quiet | Await the call's task in the code that started it, rather than polling; its continuation keeps the intent |
| Work another mod's published service does when this mod's code calls it | Intent stays with the mod whose code acted on it | Run that work as the other mod's command (`Context.Commands.TryExecute`), which acts for the same player |
| An Update subscription made from a click, meant to act for the player each frame | Subscribers are frame callbacks | Schedule the work instead (`Every`, `NextFrame`), which keeps the click's intent |
| Native Unity input or coroutines (`unsafe-native`) | The runtime sees neither | Use `Context.Input`, `Context.Scheduler` and `await` |
| A kit control's event fired from code, such as `Button.onClick.Invoke()` | It looks like the player's input | Call the handler directly |

An input-active frame is coarse: the mod's whole Update and LateUpdate carry the player's intent while its input is
active, scripted work in them included, so scripted spawning belongs in a timer rather than in input handling code.

### Tests

- **Core** (`MultiplayerIntentRulesTests`, `MultiplayerIntentScopeTests`): both tables, every entry point and flow;
  observation by mod, thread and epoch; captures and their resumption; commands, which stay coarse when an input frame
  ran them; a control's event fired inside any mod's input frame; records' bounds and text; and the scope with each of
  its ways to run a record, the logical call context emulated since .NET has none: a scope ends with its callback,
  awaits keep the intent after the scope ended and after an await completed from scripted code, Task.Run's thread and
  the work an await hands it observe none while the context carries the record for what it hands back, nested scopes
  replace and restore, failures restore, other threads open nothing, the logical call context is left empty at rest,
  the probe trusts only a logical call context whose captures keep their values, and refuses one whose captures share
  its slots, one that empties a slot in place under the captures taken before, one that empties it only at the next
  change of context, one whose thread reads an emptied slot until it next changes context, one that hands an emptied
  slot's value back after a change of context, one where a value set in a captured run escapes, and one that refuses
  access, the store binds
  `CallContext`'s three public members by name, checked against a stand-in with the same names and shapes, and never
  members that are not public, the mode follows the runtime, and the reusing and logical ways allocate nothing. The
  game's Mono is outside the harnesses; the costs above record the same rules checked on it.
- **The runtime** (`MultiplayerIntentRuntimeTests`, `MultiplayerIntentServicesTests`): the epoch follows the realm and
  ends an intent mid-scope; attaching names the way records run once, the fallback as a warning; the kit's hook opens,
  nests and carries deferred completions, and survives a sink that throws; commands act for the player who ran them, as
  their own mod, a participant's included, and scripted runs clear what else flows; the seam answers and routes by the
  current intent in every realm; the motion lease and forwarded seams; what an input frame starts stays coarse while a
  click, the player's command, an interaction, a lease and a forwarded click are direct
  (`MultiplayerIntentCoarsenessTests`); the privacy scan. Over real contexts: input edges
  and held input make a mod's Update and LateUpdate input-active while FixedUpdate, quiet frames and another mod's
  Update never are; a session scope's action counts for its mod; the input decorator passes everything through, ends
  actions with their lifetime, and changes nothing when the game's action refuses a dispose off the main thread, so the
  retry its error asks for works, the mod's unload still ends an action whose dispose was refused, and a second
  dispose changes nothing; scheduled callbacks, `Every`, a coroutine and a worker's hand-back capture where
  they were scheduled, another mod's and scripted ones none, and a realm change ends them; interactions; and frames
  that allocate nothing offline, at rest and with input held.
- **The analyzer** (the analyzer harness): TF1009 reports an edge read in a FixedUpdate lambda, method in another file,
  local function, handler kept in a local, lambda the handler runs at once, or local function or delegate in a local
  the handler calls, even when it also hands it on, naming a conditional read with its receiver; and not one in Update
  or LateUpdate, in what a FixedUpdate handler subscribes, the SDK's scene subscriptions and their extensions included,
  or schedules for later, written in place, kept in a local or as a local function, held or analog input in
  FixedUpdate, a mod's own member named like an edge, or a mod's own edge being set. TF1008 offers awaiting before
  polling, and every rule's help link redirects to its entry.
- **The kit** (`UiKitIntentConventionTests`): every callback the kit raises is listed, every callback a player's input
  raises opens the owner's action, every one raised inside an action or by what only happens to a control opens nothing
  of its own, tweens carry what they captured, every listener is accounted for, and the runtime connects and
  disconnects the kit.
- **The real runtime** (the ModRuntime harness): a mod's input handler, a callback its input scheduled, its UI
  callback and that callback's continuation, its command and its interaction each act on the player's intent; its
  scripted timer, its FixedUpdate, and another mod's Update and published callback in the same frames see none;
  another mod's command it runs acts for the same player; offline nothing records; the UI kit is connected while the
  runtime runs; and frames allocate nothing offline, at rest or with input held.

### Choices

- **Online only.** Offline every route is canonical whatever the intent, so recording there would cost without
  changing anything, and an intent recorded before a realm change is never observed after it anyway.
- **Ownership by mod.** A record belongs to the mod whose code received it, so another mod's callbacks never see it
  however they are reached; commands are the one explicit way to act for the same player as another mod.
- **Epochs.** Every realm change starts a new epoch, so a continuation of a click made while hosting never acts for the
  player after joining another host.
- **Capture by the scheduler, not by subscription.** A timer a click schedules is the click's continuation; a frame or
  scene subscriber is not, and stays scripted.
- **Worker hand-back.** The SDK tells background work to continue through `Context.Scheduler`, so what a worker the
  intent's code started hands back keeps the intent, while the worker itself never observes it.
- **Decorators, not host changes.** Input activity, scheduler capture and interactions live in Unity-free decorators
  every gameplay host passes through, as section 11's do, so the harnesses run the same code the game does; the kit's
  half is the one Unity-side part, checked in its sources.
- **Three ways to run a record**, picked by a probe: the cheapest the runtime has that keeps every rule, with the same
  rules each way and the async-local as the fallback.
- **The logical call context on Mono, not corlib's internals.** The plan asks that an input-active frame's scope
  allocate nothing in the steady state. On the game's Mono an async-local cannot: each change copies its map. Re-arming
  a cached execution context through corlib's internal `isNewCapture` reached zero too, but needs private corlib
  access twice over. The logical call context reaches zero through public members of a public corlib type, which Mono
  and the .NET Framework both document, and the startup probe falls back rather than trust one that behaves otherwise.
  It is bound by name, so a corlib without it, as .NET is, simply never uses it.
- **Coarse stays coarse.** An input-active frame cannot tell the player's input handling from scripted work in the same
  Update, so what it starts never passes for a direct action: WS3.7 sees which is which instead of a guess.

## 15. The participant realm

WS3.9 gives every player a controller of their own. Worlds gains `IGamemodeParticipantFactory`, an additive public
interface that extends `IGamemodeFactory` with `StartParticipantAsync`; the gamemode's declared type implements it. By
the realm a session starts in, the orchestrator decides what of the gamemode runs in this process
(`GamemodeSessionOrchestrator.Participant.cs`):

| Realm the session starts in | The gamemode package's session scope | Server controller | Participant controller |
| --- | --- | --- | --- |
| `Offline` (single player) | `GamemodeServer` | Starts first | Starts second, in a scope of its own |
| `ListenHost` | `GamemodeServer` | Starts first | Starts second, for the host's own player |
| `Client` | `WorldOwner` | Never | Starts alone |

The host never runs a participant for another player: each player's participant runs in that player's own game.

- **The realm.** The realm of the gamemode package's context, from its replication services, read once when the session
  is admitted (`SessionRecord.Realm`). The realm rules keep it from changing while a session starts: a join needs Idle,
  and every request is `Busy` from Preparing to StartingMode. A promotion in place, from `Offline` to `ListenHost`, needs
  nothing: the server controller and the host's participant already run.
- **The participant's scope.** A child of the gamemode package's own context, `{session id}:participant`, made with
  `ModExecutionContext.Participant` right before `StartParticipantAsync`, and only for a factory that implements the
  interface, so a gamemode without it keeps exactly the scopes and phases it had. It keeps the package's identity,
  capabilities and dependency visibility, and has a lifetime of its own. It borrows no native transition: its slot never
  holds a grant, since scene changes belong to the session's world owner. Its replication seam reports `Participant`:
  canonical in single player and on the host, forwarded on a joined player, and never in control of time online.
- **Start.** In StartingMode: the factory is made, the server controller starts where the server runs, then the
  participant's scope is made and `StartParticipantAsync` runs; the startup reservation closes and the session commits
  Running only after that. A joined player never constructs a gamemode without a participant factory: the binding's
  `ImplementationType` says what the declared type implements before anything runs, so none of its code runs there.
- **Failure.** A participant that returns a failure ends the session with its code and "The gamemode's participant
  controller failed to start: " followed by its message; a `Cancelled` result keeps its own message. One that throws
  ends the session as `External` with the same sentence. A reason left empty reads "The session operation failed.", and
  a code the SDK does not define fails as `External`, saying so, as every failed start does. Either way the session ends
  as a failed server start does: the launch fails with that reason, the server controller is disposed at cleanup, and
  every scope closes. A controller a participant returns after the session began stopping is kept, and disposed.
- **Stop.** Cleanup disposes the participant controller before the server controller, attempting each whatever the
  other did, so a participant that throws never keeps the server controller from stopping; the failure is reported in
  the session's terminal outcome. Scopes close in reverse, the participant's before the server's.
- **UI surfaces.** What a participant makes through `participant.Context.Ui` is tracked on its scope's lifetime, so it
  ends with the session at the latest. Until the UI surface lifetimes slice (#252) merges, an SDK HUD draws on the
  scene's canvas, so a native scene change that keeps the session's controllers can end a participant's HUD while the
  controller runs; with #252, a scope's surfaces outlive scenes and end with the scope.

### What session follow (WS3.8) calls

On a joined player the follower builds the session's record with the host's session id and the realm `Client`, makes
its scopes through `ScopeContextFor(package, gamemodePackage, NetRealm.Client)`, which gives every package, the
gamemode's included, `WorldOwner`, loads the host's world through the same provider, synchronizes its baseline, and then
calls `StartGamemodeAsync(record, readiness)`. On a client, that call makes the participant's scope and starts only the
participant controller in it, so follow makes no participant scope in Preparing. The call commits no phase of its own:
follow commits its Participant state before it and Running once it returns. Cleanup is the same as a host's,
participant first.

Follow also decides what this slice leaves alone on a joined player: a local launch there still runs the host's startup
steps in the `Client` realm (the scene authority policy refuses its native world changes), and the session's bound
`StopAsync` and `RestartAsync` act locally until follow answers `NotAuthoritative`.

### Scopes that resolve the session (DevEx audit B1)

A scope's context builds its own extension service, so code in a gamemode's or a participant's scope that resolves the
multiplayer session gets a second facade of the same mod, bound to the scope's lifetime. Closing a facade used to retire
the whole mod (`SessionEngine.RetireOwner`): a scope that stopped despawned the mod's own drone, one object before and
none after, and cancelled the mod's own pending submissions on a client. Now:

- **Each facade retires what it made** (`SessionEngine.CloseFacade`): on a host, the objects it spawned leave
  canonically, so every player sees them go; on a client, the commands it submitted that still wait complete as
  cancelled, with "The part of the mod that submitted this stopped.". Objects record the facade that spawned them, and
  commands the facade that submitted them.
- **The mod's last facade retires the mod**, as before: a host's remaining objects of the mod, a client's copies of
  them, and the mod's replicated-object inputs, which go through the object rather than a facade. The engine counts
  each mod's open facades, and decides at the settle point the close is handled at.

Retiring per facade is the meaning a scope's lifetime has everywhere else in the SDK: what a scope made ends with the
scope. Counting facades alone, the audit's other fix, would have kept a stopped gamemode's round objects in every
player's game until the mod unloads.

### Tests

- **ModManager harness.** `GamemodeParticipantRealmTests`: in every realm, the order of the start and stop calls, the
  scopes' execution contexts, and their closing order; a gamemode without the factory unchanged in every realm; a
  participant that fails or throws ending the session, on a host, in single player and on a joined player, with its
  reason, the server controller disposed and every scope released; a failed server start that never starts the
  participant; a participant that throws while stopping, followed by the server controller's stop; cancellation while
  the participant starts, with the late controller disposed once, first, and a clean successor; the participant's own
  scope, identity, route and time authority; and B1 at the runtime, through real contexts and scopes over the test rig:
  the session's end retires the server's and the participant's objects while the mod's drone stays, until the mod
  stops. `ModContextNetProfileTests`: the scope context by realm. `TestingKitTests`: the participant session fake and
  `GamemodeSessionRunner` in every role, with its failures.
- **Conformance.** The gamemode-v6 fixtures gain a `lifecycle` channel that only C# runs, since only C# has the
  orchestrator. Each of its 17 `participant-lifecycle` cases runs on the orchestrator, in its realm, and on the testing
  kit's `GamemodeSessionRunner`, which must give the same result, its code and words included, so the kit cannot drift
  from the game. Five of them state a failure the game words for itself: a reason left empty, or a code the SDK does not
  define, from the server and from the participant, and a participant that throws without a reason.
- **ModRuntime harness.** The production-binding case `participant`: a real installed package whose declared gamemode
  type implements the factory is bound and launched by production code; the participant starts after the server, its
  services report `Participant`, and it is disposed before the server controller.
- **Multiplayer harness.** `ScopedFacadeRetirementTests`: the audit's reproduction through the real provider and the
  drone sample; a host's scope facade despawning only its own objects, seen by a joined player; a client's scope facade
  cancelling only its own commands, while the mod's command and its object's input go through; each side's last facade
  retiring the mod; a close decided at the settle point that handles it, so a facade the mod opens before that point
  keeps the mod in the session, closes from another thread settle on the owning thread, two of them in one settle point
  retire only what each made, and a second dispose closes nothing more; and a mod's facades at rest allocating nothing.

### Choices

- **The participant factory extends `IGamemodeFactory`.** A type that implements it is always a bindable gamemode, so
  the participant can never sit on a type the runtime does not bind, and one declaration covers both controllers.
- **Its own session and controller types.** `IGamemodeParticipantSession` and `IGamemodeParticipantController` carry the
  same members as the server's, but a participant can never be handed the server's scope, or the reverse.
- **The participant's scope is made only when it starts**, so a gamemode without one gains no scope, no allocation and
  no new phase.
- **The realm comes from the contexts' replication services**, the same composition root every scope's seam reports,
  so the orchestrator and the participant's services can never disagree about where the session runs.
- **Retire per facade, with the mod's last facade retiring the mod**, for the reason above.

## 16. Authority routing

WS3.7's first part: spawn adoption, shadows, and validated `SetTransform` and `Destroy`. Scripts run on the host and a
player's own actions run where that player is (the execution model), so a joined player's call goes one of three
ways. What the player does is made at once where the player is, asked of the host, and kept or ended by the host's
answer; what scripts do is the host's to do, and the player's own run of the same script only stands in for it; and
nothing a player makes is ever recorded as canonical there. Offline and on a listen host every call is canonical, as
before. Motion ownership (`NetEntityMotion`, `HeldItemOwnershipTracker`) is the second part.

| Part | Where | What it does |
| --- | --- | --- |
| `SpawnAuthorityRules`, `ShadowKeyNumbering`, `SpawnRefusal` | `Core/Multiplayer/SpawnAuthority.cs` | The rules, Unity-free: relations, refusals and their texts, which spawns are shadowed, an honest player's pace, safe bundle paths, the prediction and shadow state machines, and each mod's shadow numbering |
| `RuntimeReplication.Authority` | `MM/Multiplayer/` | Where a context's spawns and registrations go, and the shadow key a canonical spawn takes |
| `NetAssetService`, `NetEntityService` | `MM/Multiplayer/` | Route a joined player's spawns, transform changes and destroys, and list the mod's own loads |
| `NetworkWorldClient` `.Predictions`, `.Shadows`, `.Requests`; `LocalSpawn`, `ClientPrediction`, `ClientShadow` | `MM/Multiplayer/World/` | A joined player's side |
| `NetworkWorldProxies` (with `.Builds` and `.Requests`), `ProxyParticipant`, `ProxySpawn` | `MM/Multiplayer/World/` | The listen host's side: validation, the per-player scopes, and the builds |
| `AuthorityWire`, `AuthorityMessages` | `MM/Multiplayer/World/AuthorityWire.cs` | The messages, and their strict reading |
| `ModAssetLoads` | `MM/Multiplayer/World/ModAssetLoads.cs` | Each mod's own bundle and prefab loads, which builds use first |
| `IRuntimeReplication.RegisterSpawnHandler`, `IInternalSpawnHandler` | `src/TopiaForge.Mods.Abstractions/Internal/` | How a provider's kind is made, and ended, for a player |

### Routing

On a joined player, by `ExecutionRouting.Route(Client, profile, intent)` for the calling context:

| Route | Spawn | `SetTransform`, `Destroy` |
| --- | --- | --- |
| `Forwarded`: the player's intent, or any call of a participant context, such as a gamemode's participant controller (section 15), which acts for its player | A prediction (below) | On the player's own object, a request; on anything else the network world holds, refused here with `NotAuthoritative`, as the host's default rules would |
| `Shadow`: scripted code in a process or world-owner context of a shared mod | A shadow (below) | On the player's own object, a request; on a shadow still waiting, here, since it is the player's alone until it binds; on anything else, a no-op that succeeds, since the host's own run of the same script makes the change there |
| `LocalOnly`: a client-local mod | Here, as offline | Here, as offline |
| `NotRun`: a gamemode server | Refused, as before | Refused, as before |

An entity the network world does not hold, such as a scene object that has never moved, is the game's, as offline. A
change to an object of the player's own is a request whatever code makes it, since the host's own run of the mod never
held it.

### Spawn adoption

1. **A prediction.** The spawn runs here through the host's asset service, or the provider's own spawn, and the
   mod gets its handle at once. The instance is frozen as a replica is (`PrepareReplica`), kinematic and unmarked, and
   takes the player's next provisional id, from 1, never one still in use. Its request carries the mod, the kind, the
   intent and whether an input frame made it, the scale and transform, and a description: an asset's bundle path and
   name, as world entries carry them, or the provider's payload. A prediction is refused here, before the spawn runs,
   when the player already has as many objects and waiting predictions as the host's quota (64), and an asset whose
   prefab does not say which bundle it comes from is refused as unshareable.
2. **The host validates.** Every request spends from the player's spawn bucket first (`PlayerRequestLimiter` over
   `PermissionPolicy.Default`: a burst of 8, then 4 a second), so a flood is throttled whatever it asks for; then the
   rules, then the quota of 64 live objects. Then what it asks for: an asset of a mod the host shares, named by a plain
   path inside the package (`IsSafeBundlePath`: relative, no `.` or `..` segment, no drive, scheme or control
   character, at most 512 characters, which the asset service checks against the package's folder again), or a
   provider's kind, made by the spawn handler the same mod registered. The runtime's own `asset.` kinds are never made
   for a player. Nothing is loaded and no mod code runs before all of that passes.
3. **The host makes it canonically,** as an object of the player's `ProxyParticipant` scope: an asset through the mod's
   own handles first, a prefab its code loaded, else a prefab loaded from a bundle it holds, waiting while its own load
   of the bundle runs, since the game refuses a second load of a bundle that is loaded already; only a bundle the mod
   does not hold is loaded through its package, and let go of after 2 seconds unused, so the mod can load it itself. A
   provider's kind is made by its handler inside the player's forwarded intent (`EnterForwarded`), so the provider reads
   whose object it makes from `CurrentIntent`. A build that takes over 30 seconds is refused as unbuildable. The object
   is recorded like any other spawn, with the player's slot as its owner and no shadow key, and announced to everyone.
4. **The verdict.** The host owes the player its verdict, sent as the player's window allows. Adopted, the instance
   stands in place of a replica when the entry arrives, under the same handle and `IEntity.Id`, and a lookup by its
   network id finds the mod's own handle; if the entry came first, the replica built for it gives way. Refused, the
   instance ends, through the provider's handler for a provider's kind, so the handle is dead, and the player sees a
   warning toast through the context's own UI service, which renders through the kit's toasts. Refusals closer together
   than 3.5 seconds are counted, not shown. A spawn its mod removed before the verdict is not shown as refused.

A request asked again for a provisional id the host already tracks, as after a restarted stream, is answered with the
verdict it has and never made twice. A prediction whose verdict has not come 60 seconds after its request left ends
with a toast, and the host is asked to end what it may make of it late. A restarted stream sends every request still
waiting again, oldest first. Leaving ends every prediction with the host's world.

### Shadows

Each mod's shadowable spawns are numbered 1, 2, 3 and so on (`ShadowKeyNumbering`): scripted spawns of a process or
world-owner context of a shared mod, the spawns a joined player's own run of the same code makes too. A listen host
stamps each such spawn with its number as the entry's origin sequence, 0 meaning none; a player's adopted spawns, a
gamemode's and a participant's take none. A joined player numbers its own run alike, and its spawn becomes a shadow: made
here, frozen, never sent and never recorded, waiting for the host's entity with the same `(mod, number)`.

- **The host's spawn arrives,** first or after: the shadow stands for it in place of a replica, under the mod's own
  handle, and follows the host's snapshots; the host's despawn ends it.
- **It comes for another asset or kind,** or another copy already stands for it: the shadow ends at once and the host's
  replica is built. A provider's kind matches by kind alone, since its description may change between runs.
- **It never comes:** the shadow ends 10 seconds after it was made, counted only while the player's world is
  synchronized, so a baseline still arriving is never mistaken for an absence.

The numbering starts over on a host when its world session starts (the orchestrator's `Preparing`), and on a player when
it joins and when its host's world is replaced. A number a live entity from an earlier session still holds is never
announced twice: the newer spawn goes without one (`ShadowKeysReused`).

### Requests

A transform change or destroy names its target by network id, or the player's own spawn by provisional id, before or
after the host adopted it. The host takes the sender from the channel, never from the request, spends from the player's
bucket for the kind first, and decides by how the player relates to the target: its own, the world's (owner slot zero:
the host's spawns, scene objects and world content), or another player's. By default a player moves and destroys only
its own objects. A move of a spawn still being made changes where it is made; a destroy of one cancels it. A player the
host made nothing for owns nothing, and gets no scope by asking. On a joined player, moves to one object coalesce, the
newest winning, a destroy outdates the moves waiting before it, and each kind keeps to an honest pace
(`ClientPace`): half the host's burst and its full rate, so requests that arrive bunched never meet the host's limit. A
change before the verdict applies here at once and travels with, or after, the request; a destroy before it ends the
instance here at once, and one still queued behind the pace was never asked for at all. When the mod disposes an object
of its own, the host is asked to destroy it.

### The proxy scope

The host keeps one `ProxyParticipant` per player it made anything for, in the player's participant slot (1 to 255),
created at its first spawn request. It is the runtime's own scope: no mod code runs in it, and it opens no session of
its own, so retiring it retires only the records it owns (`MultiplayerSpawnLedger.ReleaseOwner(proxy)`), never a mod's
contract objects or its lifetime. When the player's stream ends, the scope keeps everything for 30 seconds, the time the
session engine keeps a departed player's place; a player back in that window finds its objects. Past it, every object
the scope made or was making ends, its records with it, and the slot's budgets are forgotten (`PlayerRequestLimiter
.Forget`) before anyone else may take the slot. When hosting stops, every scope is cleaned up at once.

Section 15's retirement per facade concerns the session engine's contract objects and commands; the world's objects a
mod's scope made end with that scope's lifetime as before. On the host, a scope's records end with its seam and its
spawns with its asset service. On a joined player, a scope that ends disposes the handles of its predictions, and each
disposal asks the host to destroy what it made of it, so a participant controller's objects end with it on every peer.

### The wire

| Type | From | Body |
| --- | --- | --- |
| 64 `SpawnRequest` | Player | provisional id (from 1), mod, kind, intent (0 to 5), input frame (0 or 1), scale (3 x f32), transform, description |
| 65 `TransformRequest` | Player | target, scale (3 x f32), transform |
| 66 `DestroyRequest` | Player | target |
| 96 `SpawnAdopted` | Host | provisional id, network id |
| 97 `SpawnRefused` | Host | provisional id, reason (1 to 6; another reads as unbuildable) |

A target is `[0][network id]` or `[1][provisional id]`, from 1. Every field is checked as world entries' are: mod ids by
the manifest rule, kinds by the token rule, scales finite, non-zero and at most 10,000, transform records exact, and
descriptions up to 16 KiB, with nothing after the last field. A request names no owner and no origin, and a player's
message in the host's voice, a verdict or an announcement, is discarded on the host.

### Bounds

| What | Bound |
| --- | --- |
| A player's predictions waiting, with its adopted objects | 64, the host's quota |
| A player's shadows waiting | 4,096 |
| Verdicts a host owes one player | 128; past it they are dropped and counted (`VerdictsDropped`) |
| Spawns a host tracks for one player, live and being made | 128 |
| Bundles and prefabs a host loads itself at once | 256 and 4,096, as a joined player's replicas |
| A host's build; a player's wait for a verdict; a shadow's wait | 30 seconds; 60 seconds; 10 seconds while synchronized |

### Privacy and cost

Nothing in a request or a verdict is shaped like an address, and the host never reads a sender from a request. A
refusal's toast names no mod, player or object, so it carries nothing another player wrote, and the counters
(`NetworkWorldCounters`) are numbers only. At rest nothing is sent and nothing allocates: the zero-allocation tests below
run a host keeping two players' scopes, and players holding adopted objects and bound shadows, through hundreds of
frames, and a frame of all three then takes about 10 microseconds in the harness. A player dragging its own object every
frame sends 30 requests a second, its pace, of 30 bytes each, about 0.9 KB a second, and the host applies each with one
pose write; the host and that player together allocate about 270 bytes a frame in the harness, the loopback channel's
copy of each message and the SDK's result for each call included.

### Tests

- **Rules** (`MultiplayerAuthorityRulesTests`): relations by owner slot, limiter answers as refusals, verdicts read from
  the wire, which profiles and intents are shadowed, an honest pace against the host's limit under jitter of up to
  0.9 seconds in order, safe and unsafe bundle paths, refusal texts, both state machines for every state and event,
  and the numbering, which allocates nothing once a mod has a number.
- **Wire** (`AuthorityWireTests`): every message round trips, sits in its range, and is refused whole when a field is
  bent, a byte follows its last field, or it is cut anywhere.
- **Adoption** (`SpawnAdoptionTests`): adoption in place under the same handle and id, a lookup by network id finding
  the mod's handle, the other player's replica; the host's builds from the mod's prefab, from its bundle while its own
  load runs, and from a load of its own let go of when idle; a refusal killing the handle with one coalesced warning
  toast; a verdict lost with its stream asked for again and never made twice, and an entry before its verdict swapping
  the replica out; a verdict that never comes; a provider's kind made by its handler inside the player's intent; a
  joined player recording nothing; and a joined player's replicas built from its mod's own handles.
- **Requests** (`SpawnChangeTests`): moving and destroying the player's own object through the host, by its action,
  by script and by disposing its handle; changes before the verdict; another player's, the host's and a scene object
  refused here with nothing sent, and left to the host's script when scripted; an entity outside the world passing
  through; and twenty spawns, a hundred moves of each and twenty destroys in one go never refused.
- **Shadows** (`SpawnShadowTests`): binding when the host's spawn is here and when it comes later, ending for another
  asset either way, ending when it never comes and only while synchronized, the numbering skipping the host player's,
  a gamemode's and a player's spawns, starting over on joining and at a world session, a provider's spawns and
  registrations, and scripted changes left to the host.
- **Abuse** (`SpawnAbuseTests`): a raw flood throttled to the burst and the rate and bounded by the quota without
  touching another player; what a flood leaves bounded when the player stops reading; assets of an unshared mod, from
  outside the package or a rooted or drive path, undescribed assets, the runtime's kinds, unknown kinds and another
  mod's kind refused before anything loads or runs; a provisional id reused or another player's, origins appended and
  messages in the host's voice; and every change of another player's, the host's, a scene object, a destroyed and an
  unknown entity refused, by a player with a scope or without one.
- **The scope** (`ProxyParticipantTests`): a leaving player's objects ending after its grace and nothing else, a mod's
  spawn, record, lifetime and another player's object untouched; coming back within the grace, and a link that drops
  and returns keeping the handles bound; hosting stopping; and a slot given again starting with whole budgets.
- **Cost** (`AuthorityCostTests`): the decorators add nothing offline, nothing allocates online at rest, and the cost
  of a player dragging its object every frame is measured and printed.
- **The real runtime** (the ModRuntime harness, `TestAuthorityRoutingAcrossRuntimes`): three runtimes over the loopback
  network load one mod; its scene script's crate is shadowed on both players, so each sees one; a player's dropped crate
  is adopted under the same handle, moved and removed for everyone by its commands; a crate from outside the package is
  refused with the mod's own toast; a leaving player's crates end after its grace and nothing else; and frames allocate
  nothing offline or online at rest.

### Limits, stated plainly

- **A scale set after a spawn is not replicated**: snapshots carry poses, so a transform request moves and turns its
  target, and the scale stays as the spawn made it.
- **A scene object that has never moved is no entity**, so a joined player's change to it applies to its own copy only,
  as offline, until the host moves it. Binding one at once when a player acts on it comes with motion ownership.
- **Leaving ends a player's copies of the host's world**, its predictions and shadows with its replicas, so a mod's
  handles to them die. A player back within its grace finds its objects on the host as the host's, still counted
  against its quota until it leaves for good, since its handles to them ended when it left. A link that drops and comes
  back without leaving keeps everything.
- **Transform changes and destroys in flight when a stream restarts are lost**, as world replication's are; the player
  asks again only for spawns.
- **Shadows bind by order.** A script that spawns differently on two machines, by chance or by time, numbers its spawns
  differently, so its shadows end after their wait and the player sees the host's.
- **A participant context's preview spawn is forwarded** like any other of its calls; a local-only preview needs a
  client-local mod until a context can say so.
- **A gamemode package that also provides its world** runs as the gamemode server on the host and as a world owner on a
  joined player (section 15), so its world's scripted SDK spawns take no shadow key on the host: a joined player's own
  run of them ends after its wait, and the host's are built as replicas. World content registered by its key binds as
  before.
- **A bundle the network world loaded for a player's replicas is held for the session**, and the game refuses the
  player's mod a second load of it. Replicas and the host's builds use the mod's own handles when it holds the bundle
  first; when the replicas load it first, the mod's own later load of it fails in the game until the player leaves.
- **Commands are not forwarded yet**: `SubmitCommand` still runs the mod's handler here in every realm.

### Choices

- **Predict everything the player does, refuse little here.** The player sees its action at once, and the host is the
  only judge; the player refuses here only what the host would refuse for certain (its quota, or another's object),
  which saves a round trip and a toast for something it knows.
- **Shadows by number, not by position or name.** A position or a name can repeat; a mod's run of a script makes the same
  spawns in the same order on every peer that runs it, which is what the context table says shadowable code is.
- **The scope is the runtime's.** A proxy that opened a mod session for each player would run mod code for players the
  mod never saw, and retiring it would reach the mod's own contract objects. The runtime's own scope owns records only.
- **A grace before cleanup**, matching the session engine's 30 seconds, so a reconnect does not cost a player its
  objects, and only a player gone for good does.
- **The mod's own handles first**, on the host and for replicas: the game loads a bundle once per process, so sharing
  the mod's handle is the only way both can use it.
