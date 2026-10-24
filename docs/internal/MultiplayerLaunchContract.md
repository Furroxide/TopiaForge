# Multiplayer session launches — design record

Status: Implementing (WS6.2, WS6.3 and the exact installs of WS6.5 of the live-multiplayer plan) · Date: 2026-10-18 ·
Owner: multiplayer

This records how the launcher starts a game to host or join a live session, what the game reports back, the channels
the two keep open while a session runs, how the launcher finds the exact package versions a server runs, and how a
join installs them. The transport and the live sessions themselves are recorded in `MultiplayerLiveSessions.md`.

| Piece | Dart (launcher) | C# (game) |
| --- | --- | --- |
| Request V5 | `launch_resolution/profile_transport_v5.dart`, `session_request.dart`, `session_targets.dart` | `ProfileLaunchConfigurationV5.cs`, `LaunchSessionRequest.cs`, `LaunchTransportJson.Session.cs` |
| Progress and outcome V2 | `outcome_transport.dart`, `session_outputs.dart` | `LaunchProgress.cs`, `LaunchSessionOutputs.cs` |
| Channels and preferences | `session_channels.dart`, `session_preferences*.dart`, `session_journal.dart` | `LaunchSessionChannels.cs`, `MultiplayerPreferences.cs`, `LaunchTransportJson.Channels.cs` |
| Vocabulary and value rules | `session_values.dart` | `LaunchContractValues.cs`, `LaunchSessionValues.cs`, `LaunchSessionHosts.cs` |
| Staging | `launch_staging_store.dart`, `launch_staging_sessions.dart` | `LaunchStagingStore.Sessions.cs` |
| Exact-version resolution | `multiplayer/exact_package_resolution.dart`, `exact_package_catalog_loading.dart` | — |
| Exact installs and server content | `MultiplayerContentHost` in `repositories.dart`; `multiplayer/server_content*.dart`, `server_profiles.dart`; `join_content_*.dart`, `join_install_journal.dart`, `join_server_profile.dart`, `server_content_*.dart`, `multiplayer/server_content_store.dart`, `secure_http_download.dart` | — |
| Host-served packages (developer mode) | `join_host_packages.dart` (domain); `join_host_downloads.dart`; `multiplayer/host_package_client.dart`, `package_response.dart`, `join_host_packages.dart`, `lan_discovery_holds.dart` | `DescriptorEndpoint.cs` |

## 1. One launch, one version

- **Only host-session and join-session launches write request V5.** `StagedLaunchRequest.forLaunch` is the one place
  that chooses: given no session it stages `ProfileLaunchConfigurationV4` exactly as the V4 writer writes it, so every
  other launch, acceptance, creator-source play and the CLI keep their bytes. A golden test pins those bytes as the
  writer produced them before V5 existed.
- **Every new document is sorted JSON:** each object's keys in ordinal order, no white space, and only the escapes of
  the quotation mark, the backslash and U+0000 to U+001F. The game, the launcher and the fixture generator therefore
  write the same bytes. Readers accept any member order and spacing but refuse unknown or repeated members, explicit
  nulls and wrong types, as the V4 readers do. Optional members are omitted, never null.
- **Bounds.** Requests, progress and outcomes keep the launch transport's 4 MiB; channels and preferences are at most
  64 KiB.

## 2. Request V5

V5 is `launch-profile-<request key>.json` in manager staging, the file V4 uses, and `TOPIAFORGE_LAUNCH_PROFILE` names it.

| Member | Value |
| --- | --- |
| `schemaVersion` | `5` |
| `command` | `host-session` or `join-session`, agreeing with `session.role` |
| `profileId`, `profileRevision`, `requestId`, `packages`, `digest`, `enabledMods`, `selectedVersions`, `inheritManagerModState` | As in V4, with the same rules |
| `plan` | A host's launch target, as in V4. A join carries none: the host's session decides what loads |
| `session` | The session object below |

There is no `safeMode`: safe mode never hosts or joins. A join never inherits the manager's mod state, since it runs
exactly the packages it planned. The runtime executes the request's V4 view (`Executable`): `launch-target` with the
host's plan, otherwise `main-menu`. `LocalLauncherRepository.launchSession` refuses Safe Mode, a join from a profile that
inherits the manager's mod state, and acceptance runs before it does anything, so a restart never stops the running
game for a launch V5 cannot carry.

| `session` member | Value |
| --- | --- |
| `role` | `host` or `join` |
| `intentId` | The launcher's intent: one host or join attempt, kept across its restarts and re-plans |
| `displayName` | 2 to 24 UTF-16 code units of display text |
| `developerMode` | Whether the launcher runs in developer mode. Always present |
| `transport` | `privacy` (`relay-only` or `direct`), `directConsent`, `port` (0 to 65535, zero for any free port), `upnp`, `lan` |
| `serviceOrigin` | The control plane's origin, omitted when none is configured: see below |
| `target` | A join's server (section 3) |
| `host` | A host's options: `name` (1 to 48), `maxPlayers` (2 to 16), `visibility` (`public`, `invite`, `lan`), `voice`, `passwordRequired`, `servePackages` |
| `devInstance` | `topiaforge dev --peers N` only: `index` and `count` (2 to 8) |

- **Passwords are never staged.** They are typed in game. `passwordRequired` only tells a host to ask before it opens,
  and a `password` member anywhere is refused.
- **Served packages need developer mode.** Both readers refuse `servePackages` without `developerMode`, and the
  provider's plan serves nothing without it either.
- **Display text** has no surrounding white space, no control characters, no unpaired surrogates, and neither U+FFFE
  nor U+FFFF, which the game's JSON reader refuses: a document holding one could never be read back.
- **The service origin** is written one way: `https://`, a lowercase DNS name, and a port of 1 to 65535 in plain
  decimal, never the default 443. An address is refused, so none can pass as the service: an IP literal, and a name
  whose last label is a number, decimal or `0x` hexadecimal, which the URL Standard and the system resolver read as an
  IPv4 address (`0x7f000001` is 127.0.0.1). The launcher's service configuration reads its origin through
  `LaunchSessionRequest.isServiceOrigin`, so one rule decides both: a build can name only an origin a launch carries.
- **Consent to direct play.** `privacy: direct` must carry `directConsent`: the `intentId` it was given for, which must
  be this session's, and the address disclosure version the player acknowledged, 1 or later. Relay-only never carries
  one. The launcher can only build a direct transport from `MultiplayerSettings.privacyForSession(allowDirect: true)`,
  which refuses until the disclosure is acknowledged.
- **Relay-only and direct targets.** Under relay-only a direct target may only name a host on the local network: a
  private, link-local, unique-local or loopback IP literal, or a `localhost`, `*.localhost` or `*.local` name. Any other
  host lacks the consent to direct play, and both readers refuse the request. The game's resolver applies the same rule
  again before it contacts anyone.
- **What needs what.** Invites, Internet listings, and public or invite-only hosting need a `serviceOrigin`. LAN
  listings, and LAN or public hosting, need `lan`. A join never maps a port (`upnp` is false).

## 3. Join targets

| `kind` | Members besides the pins | Found through |
| --- | --- | --- |
| `invite` | `inviteCode`, canonical | The control plane |
| `listing` | `source`: `internet` or `lan` | The control plane, or LAN discovery |
| `direct` | `host`, canonical, and `port` | The address itself |

- **Pins.** Every target carries `serverId`, `descriptorSha256` and `packageDigest`: the server the launcher verified,
  the SHA-256 of the exact descriptor it planned from, and that descriptor's package digest
  (`LaunchSessionTarget.forPlan`). The game joins only that server, and a descriptor that hashes differently fails as
  `admission-changed`, which the launcher plans once more.
- **No second trust on first use.** A typed address learns its server from its first descriptor in the launcher. The
  game then gets that server id pinned on the direct target, so its resolver checks the descriptor against it and never
  consults its own known-hosts record for that join.
- **The address stays in the request.** A direct target's host and port exist only in the session's own launch
  request, which the game deletes when it consumes it. No progress, outcome, channel, projection, log, description or
  refusal names them.

## 4. Progress V2

Progress V2 is `launch-progress-<request key>.json`, as version 1 is. It carries a `session` block with `role`,
`intentId` and `state`, and optionally `serverId`, `players` with `maxPlayers`, and a host's `inviteCode`. It names no
address and no port.

- **Phases.** The six of version 1, plus `connecting` and `synchronizing`, which only a join reports.
- **States.** A host's are `pending`, `starting`, `hosting`, `stopping` and `stopped`. A join's are `pending`,
  `resolving`, `checking`, `awaiting-password`, `preparing-proof`, `connecting`, `synchronizing`, `in-session`,
  `reconnecting` and `ended`. `pending` means the game has not started the session yet.

## 5. Outcome V2

Outcome V2 keeps version 1's two records, `launch-outcome-` and `session-outcome-`, and adds a `multiplayer` block:
`role`, `intentId`, and optionally `serverId`, `failure`, `detail` (display text up to 1,024), `admission` and
`endReason`.

- **A launch outcome** names the role's command. It succeeds once hosting is open or the join is in session, at
  Running with a gamemode session or at Idle without one, and carries no failure. It fails with a block, an error or a
  `failure`; only a failure may carry the host's `admission` report (its `code` and up to 64 `problems`).
- **A terminal outcome** is Idle, names no command and says why the session ended in `endReason`: `left`,
  `host-ended`, `kicked`, `banned`, `timed-out`, `replaced`, `admission-rejected`, `protocol-mismatch` or
  `transport-failed`, the game's `MultiplayerSessionEndReason`. Only `admission-rejected` carries a report. Its status
  says whether the runtime tore down cleanly.
- **Failures.** A join's are the launcher's `JoinFailureCode`s of the same name that the connecting and synchronizing
  stages accept, so every one moves the join reducer: `timeout`, `nat-traversal-failed`, `relay-unavailable`,
  `server-full`, `password-rejected`, `kicked`, `protocol-mismatch`, `admission-changed`, `disconnected`,
  `direct-not-allowed` and `snapshot-failed`. The game folds its own causes into them; `detail` and the admission
  report explain the rest. A host's are `port-in-use`, `identity-unavailable`, `network-stopped` and
  `service-unavailable`.
- **The launcher's monitor** reads both versions. A session launch owes a terminal record once it has started, so a
  process that exits without one leaves the activity unconfirmed.

## 6. Channels

| File in manager staging | Direction | Holds |
| --- | --- | --- |
| `session-handoff-<request key>.json` | Game to launcher | A join the game cannot make as it is: `requestId` (the process's launch request), `handoffId`, and a `target` that is an invite, a listing, or `current` (this process's own join, planned once more) |
| `session-join-<request key>.json` | Launcher to game | A live join for a game whose packages already match: `requestId`, its own `joinId`, the running `packageSetDigest`, and a join `session` |
| `launcher-quit-<request key>.json` | Launcher to game | A request to quit, before an owned restart or a join's install: `requestId` (the process's launch request) and its own `quitId` |

- **The handoff never carries an address.** A server typed by address in game goes to the launcher as a
  `topiaforge://join/…` link. The launcher takes a handoff once and restarts into the server's profile through its
  owned-restart path.
- **A live join never carries an address.** Its target is an invite or a listing; a direct target needs a launch
  request. The game refuses a join whose package-set digest is not its own, and reports it under `joinId` as it would
  a launch request. A newer join replaces one the game has not taken.
- **A quit request reaches only the game the launcher started.** The launcher writes one only for a process it owns
  and has just verified, named by that process's launch request, which no other game knows. The manager looks for its
  own request twice a second, takes it once and quits as the game's **QUIT** button does, so mods unload and the clean
  exit is recorded. The launcher waits up to 10 seconds for the request to be taken and 30 in all for the process to
  exit, then withdraws an untaken request (`withdrawLauncherQuit`) and stops the process. A launcher that closes while
  it waits withdraws the request and stops nothing. Section 8 has why this is TopiaForge's own channel.
- **The launcher reports what the next start will be.** Once the game has ended, however it ended, the launcher reads
  the state the manager left in its startup journal (`startup-journal.json`), as the manager's next start will:
  only a recorded clean exit, or no journal, starts normally. A quit whose mods took longer than the manager's 10
  seconds to unload records none, so `LaunchResult.replacedGame` then says the start opens in safe mode, as it does
  after a stop.
- **A take claims before it reads.** The taker renames the file to a private `.taking-<milliseconds>-<nonce>` name,
  the claim time in Unix milliseconds and 32 random hexadecimal digits, then reads, checks and removes that, so a newer
  file the other side writes meanwhile lands at the channel's name again and is never removed unread. A malformed or
  foreign file is removed without being acted on. `withdrawSessionJoin` says whether it withdrew the join, since the
  game may have taken it.
- **Stale claims are swept.** A take that crashes leaves its claim behind, so each side sweeps its own: the launcher its
  handoff claims whenever it takes a handoff (`sweepStaleClaims`), the game its join and quit claims at startup and
  whenever it takes either (`SweepStaleClaims`). A sweep removes only its own side's claim pattern, and only a claim whose recorded
  claim time is more than an hour past. The time comes from the name, because a rename keeps a file's own timestamps. A
  live take holds its claim for milliseconds. Neither sweep removes a claim another process holds open: the game opens a
  claim sharing nothing but deletion before it deletes it, and Windows refuses the launcher's deletion while another
  handle holds the file. On other systems the age alone protects a claim. Claims hold no address.
- **Writes retry briefly.** Windows refuses to rename over a file while another process reads it, however that reader
  shares it, so both sides retry a refused replace a few times, waiting up to about 310 ms in all. The game's readers
  share deletion, so the launcher can still claim or remove a file the game is reading. A retry sleeps the thread that
  writes, so the game writes channels off its main thread.

## 7. Preferences and their change journal

`multiplayer-preferences-v1.json` projects the launcher's preferences for the game: `displayName`, `privacy` (`mode`
always `relay-only`, and the acknowledged `disclosure`), `voice` (`consent`, `enabled`), `network` (`lan`, `upnp`),
`hosting` defaults (`name`, `maxPlayers`, `visibility`, `port`, `voice`), the optional `serviceOrigin`, its `revision`,
and the launcher's position in the journal: `journalEpoch`, present once the launcher has taken in a journal, and
`journalApplied`, which counts in that journal only and is 0 without one. Favorites and history are never projected.

- **The game never edits the projection.** It records what the player changed in game in
  `multiplayer-preferences-journal-v1.json`: an `epoch` and at most 64 changes in strictly increasing `sequence` order,
  each setting exactly one of `displayName`, `disclosure`, `voice`, `network` or `hosting`. Privacy is not a change,
  since direct play is never stored.
- **Each journal has an epoch.** It is 32 lowercase hexadecimal digits, a random id the game picks when it starts a
  journal: when there is no journal file, as in a fresh or lost staging, or the one there is not a journal the launcher
  could apply (`LaunchStagingStore.RecordPreferenceChange`). A journal keeps its epoch as it grows. A journal file the
  game cannot open is retried briefly and then refused, never replaced, so its changes are not lost to a new journal.
  Both readers refuse a journal without a valid epoch, and a projection whose `journalApplied` names no epoch.
- **A long session never fills the journal.** The launcher takes the journal in only while no game runs from the
  install: at its own start or when another install is chosen, once a game it found running has closed, and after a
  change in Settings. Writing the projection before a session launch comes with the join flow. Every edit of a session
  therefore lands in one journal. Each change sets its whole preference, so when the game appends one
  (`MultiplayerPreferencesJournal.Append`) it drops every earlier change of the same preference; an earlier disclosure
  goes only when the new one acknowledges at least as much. The launcher's result is the same either way.
- **Read-only while the game runs.** Nothing records whether an edit in the launcher came before or after one in game,
  so the two never overlap. While a game runs from the install, or the launcher cannot tell, Settings → Multiplayer
  shows what the game can change (display name, privacy, voice, hosting defaults and the network switches) as
  read-only and says it can be changed once the game is closed. Before it saves such a change, the launcher checks
  again that the game is closed, and refuses the change otherwise. Link handling is the launcher's own and stays
  editable.
- **The launcher's store is the record.** It keeps the projection's `revision` and its journal position, the pair
  `journalEpoch` and `journalApplied`, with the player's settings, and gives them to the round through
  `MultiplayerPreferencesSource`. That is one value more than a store that keeps only `journalApplied`.
- **One round** (`MultiplayerPreferencesSync`, and `syncPreferences` in staging): the launcher builds the projection
  from its store and service configuration and applies the journal's new changes on top. When the store or the
  projection counts the journal's epoch, the new changes are those after the later of their positions in it; a journal
  under any other epoch started over, and applies from its first change. Every write takes the next revision after the
  later of theirs, and records the journal's epoch and last applied sequence. When the projection must change, the
  store is saved first and the projection written after. A projection that is missing or damaged is therefore
  rewritten from the store: no journal change applies twice, a journal started over after staging was lost is never
  skipped, and the revision never goes down. An acknowledgement never lowers the disclosure version. The game then
  drops the changes a projection counting its journal covers.
- **One journal at a time.** The launcher remembers the latest journal only, so a journal restored from before the
  latest one, say from a backup of staging, counts as started over and applies again.
- **Hosting defaults.** The launcher's store holds hosting defaults from the first round that writes the projection, or
  from the player setting a port in Settings. Until then the source gives none, and the projection keeps its own, a
  change made in game included. The first projection starts from `MultiplayerHostingDefaults.forPlayer`.

## 8. The game side

- `TopiaForgeModManagerPlugin` consumes a V4 or V5 request (`LaunchStagingStore.ConsumeLaunchRequest`), logs its role
  but never its target, and runs the executable profile through the V4 path.
- **Offered, then planned.** Before mods load, a V5 session becomes an `InternalLaunchSession` (`LaunchSessionOffer`),
  unless the command was rejected or recovery runs. Direct play is allowed only when the request carries the player's
  consent for this very intent, under the disclosure version this game shows (`MultiplayerPrivacyText`); any other
  launch is relay-only, whatever this game's own settings say. Once mods have loaded, the runtime hands the session to
  the reserved provider's internal control (`IInternalMultiplayerControl.PlanLaunchSession`) once, and lets go of it
  either way. A session no provider took is withdrawn, and its launch fails at once as `unavailable` ("The multiplayer
  provider is not loaded."), so a direct target's address never stays in memory for the whole process.
- **The one inbound address.** The launch session is the only SPI type that carries an address, a direct target's host
  and port, and a host's own port: it only goes in. Nothing in the SPI returns or keeps one, which
  `RuntimePrivacyTests.TheSpiCarriesNoAddress` proves from the compiled types, and the plan the provider returns names
  none. The session is internal to the multiplayer SDK, and the manager's package validator keeps it so: a package may
  not bundle any assembly a TopiaForge assembly names in `InternalsVisibleTo`, unless it is the first-party package that
  ships it. Test and tool assemblies never ship.
- **The plan.** The provider maps the session onto live sessions (`LaunchSessionPlan`): host settings on exactly the
  launch's port, with no fallback to another one, under the launch's privacy, with router forwarding only for direct
  play; or a join target pinned by the server id and the descriptor SHA-256 the launcher verified. A server that answers
  with another descriptor is refused before anything connects (`admission-changed`). Invites and Internet listings go
  through the control plane (`MultiplayerInternetSessions.md`), and the request's `serviceOrigin` comes before the
  manager's own.
- **Started through the realm.** The in-game screens start the plan as their own flows start sessions
  (`MultiplayerInGameSessions.md`, section 9): a host once the launch's own game runs, opened in place, or at the
  menu, after its password is typed in game; a join through the connecting overlay, with "Leave current game?" while a
  game runs. A LAN session is listened for first, for up to 15 seconds. Neither asks this game's disclosure again, and a
  launched join never offers the in-game direct prompt: the launcher plans any change to its privacy.
- **Records.** `RuntimeLaunchPublisher` is the request's one writer of progress and outcomes V2 (sections 4 and 5). The
  runtime's command and the provider's session share one sequence, the session block follows the provider's status
  (`SessionLaunchRecords`), the phase is the join's own while it connects or synchronizes, and a success comes only from
  the provider: hosting open, or the join in session. Only a status read after the session began settles it, since the
  provider reports an earlier join's end until the next join starts. The session's end is written when it ends, even
  once a newer launch has begun, or as the process exits with it still running (`host-ended` or `left`, failed with an
  `external` error when the runtime did not stop cleanly). A failed command settles the launch with the runtime's
  outcome, on whichever frame it fails, and a rejected V5 request is correlated by its intent and reported the same way.
- **Handoffs.** When the launcher owns this game, INSTALL & JOIN IN LAUNCHER and the failure view's Open launcher write
  a `session-handoff` naming the invite, the listing or the current session, on the background queue; a write that
  fails opens the link instead. A game no launcher started, and a server typed by address, open the
  `topiaforge://join/` link as before. A handoff never carries an address.
- **Live joins.** A game the launcher started looks for its `session-join` file once a second, on a pool thread
  (`LiveJoinIntake`); the store's take refuses a join for another request and one it cannot read. A join whose
  package-set digest is not this game's fails as `admission-changed`, one through another service is refused as
  `unavailable`, and a join id seen before is ignored. The rest are planned and started as a launched join is, and report
  under their join id; a success stands at `idle`. A live join whose session still runs as the game closes ends in its
  records as `left`.
- **Preferences.** The game reads the projection and its journal once at start, applying the journal's changes the
  projection does not count yet, as the launcher applies them (section 7). The display name and the hosting defaults
  are the launcher's: a change in game takes effect at once and is recorded with `RecordPreferenceChange`, one write at
  a time, the newest of each preference only. The game's own settings file keeps only what is the game's: the
  disclosure acknowledgement, Hide my IP, the game last hosted, favorites and history.
- **Off the main thread.** Every channel read and write runs on the background queue or the live-join poll, so a refused
  write's retries, about 310 ms in all, never hold a frame. `MultiplayerUiArchitectureTests` reads the compiled code to
  keep it so.
- In crash recovery, the session is not offered, and its launch fails at once (`unavailable`, with the reason), so the
  launcher never waits on a session nothing will report.
- At startup the manager sweeps the join and quit claims an earlier, crashed process left behind (section 6).
- **The launcher's quit request** (`LauncherQuitWatcher`). A process the launcher started, rejected command and safe
  mode included, looks for its `launcher-quit` request twice a second from the plugin's `Update`, and quits with
  `Application.Quit` once it has taken it. The plugin's quit handler then lets the game's own quit steps run and holds
  the quit while mods unload, as for the **QUIT** button. A game started any other way has no request id, so nothing can
  ask it to quit.
  - **Why TopiaForge's own channel.** Robotopia itself quits when a `quit-request` file appears in its persistent-data
    folder. That folder belongs to every copy of the game on the machine, so a request there would reach a game the
    launcher did not start, and it is an undocumented game behaviour, a binding no surface check can prove. The staging
    channel names one launch request, lives beside the install it concerns, and is TopiaForge's to keep. Its only cost is
    that it works while the manager runs, which every launch the launcher owns has; a game whose manager does not answer
    is stopped once the answer window passes.

## 9. Exact-version resolution

A join must install the exact `id@version@sha256` a server runs, which is often not a source's latest version.

- **History manifests.** `topiaforge registry index` inlines the full manifest and the archive size (`manifest`,
  `packageBytes` on each `history` row) for each mod's newest 20 older versions (`--history-manifests n`, 0 to 1000).
  Both members are additive; launchers that ignore history are unaffected. The builder refuses to write an index
  larger than the 16 MiB a launcher reads (`ModRegistryFormat.maxIndexBytes`).
- **Candidates, never tiles.** The launcher reads a history row only when its manifest names the entry's mod at the
  row's version and its URL and hash pass the source's checks; anything else is skipped without failing the source.
  Such rows fill the exact-version candidate map that dependency installs use, after every current entry, and never
  become catalog tiles.
- **Classification.** `ExactPackageCatalog` classifies each descriptor package by its exact SHA-256: installed,
  installed as another archive (or, when that archive is another server's hidden content, installed for another
  server), available (with its source), in a disabled source, host-offered (developer mode, when the host serves
  packages, after every registry option), hash mismatch, version unavailable, or not in sources. Only installed and
  available carry a manifest, and both come from the player's own receipts and sources. `JoinPlanner.planFromCatalog`
  plans from it. An archive installed as another one blocks the join, since both need the same folder and a join never
  replaces one: the player uninstalls their copy, or removes the other server's content.
- **Loading.** `LocalLauncherRepository.loadExactPackageCatalog` lists every valid installed archive with its receipt's
  manifest, and every row of every source, history included. A disabled source is read only when it is local, so the
  launcher never contacts a remote source the player switched off.

## 10. Fixtures and tests

`tests/fixtures/multiplayer/{launch-v5,progress-v2,channels}/` are generated by
`tests/fixtures/multiplayer/generate_launch.py`, independently of both runtimes. Both replay every case: valid documents
read the same in any member order and write back to the exact bytes, and every invalid one is refused. The launcher
also replays how a journal applies. The generator checks that no progress, outcome or channel fixture names a direct
target's host or port.

`tests/fixtures/multiplayer/server-content/cases.json` holds the record of server content (section 11), generated by
`tests/fixtures/multiplayer/generate_server_content.py` with no randomness. The manager's tests and the launcher's
domain tests replay it: each valid record reads in any spelling, the launcher writes it back to the exact bytes, and both
withhold the same versions; each newer one is refused as newer, every other one as damaged; and the generated cases, the
largest record among them, are rebuilt from their recipes and checked by length and SHA-256.

The generator takes its server pins (`serverId`, `descriptorSha256` and `packageDigest`) from `descriptor/cases.json`,
and both runtimes check that every pinned target, and every handoff listing, names a server those descriptor fixtures
describe. A change to the descriptor fixtures therefore fails until `generate_launch.py` runs again after
`generate.py`.

## 11. Exact installs, server profiles and server content

After consent, a join installs exactly the archives its plan names and saves the server's profile; the player matches
nothing by hand, and nothing the player's own profiles load changes. `MultiplayerContentHost`, an optional capability
of a launcher repository that `LocalLauncherRepository` has, answers the three stages, and the multiplayer facade's
`answerJoin` hands them to it, so every event the reducer takes still comes from one place.

| Stage | What happens | What it can report |
| --- | --- | --- |
| Downloading | Each `download` package, from the source the plan names, then each `downloadFromHost` package, from the host (developer mode) | `JoinDownloadProgressed`, `downloadFailed`, `integrityFailed`, `manifestMismatch`, `hostDownloadUnsupported`, `directNotAllowed`, `unreachable`; a cancel stops it at once |
| Installing | Every downloaded package, as one change | `JoinInstallProgressed`, `installFailed` with `rollbackComplete`, `gameRunning`, `JoinInstallUndone` |
| Preparing the profile | The server profile, saved | `profileSaveFailed` |

Every failure detail says, in plain words, what happened and what to do next. What went wrong underneath goes to the
launcher log, and neither ever names a URL or an address.

- **Downloads.** An HTTPS source goes through `downloadHttpsToFile`, which follows redirects only to HTTPS, as
  `fetchHttpsBytes` does; a local source is copied under the same rules. The body streams to a temporary file beside
  the package cache, with its SHA-256 computed as it arrives. It is bounded by `ModRegistryFormat.maxPackageBytes` and
  by the size the source states: an announced length past either is refused before the body is read, a body that
  passes its bound as soon as it does, and one that ends short once it ends. The SHA-256 must be the pinned one before
  anything is kept. Then the archive's `topiaforge.mod.json` must equal the trusted manifest the plan used, compared as
  package installs compare it. Verified archives stay in the cache by SHA-256, so a join tried again fetches nothing
  twice.
- **Packages only the host serves (developer mode).** A package none of the player's sources offers, from a host whose
  descriptor says `hostServesPackages`, is planned as `downloadFromHost` in developer mode, and the consent must
  acknowledge it (`JoinSessionState.hostServedAcknowledged`). The downloading stage fetches it last, once every source
  download is in the cache, from the host's own endpoint with `TFD1 package <sha256>` (`HostPackageClient`), through
  a `JoinHostPackageSource` the multiplayer facade hands the content host.
  - **Gates the data layer checks itself.** Developer mode, as the launcher's settings say at that moment, and the
    acknowledgement, before anything is fetched, sources included, again before each of the host's packages, so that
    turning developer mode off asks the host for nothing more, and again before the install; settings that cannot be
    read count as off. Without either the join ends as `hostDownloadUnsupported`.
  - **Where the host is.** Only as the descriptor fetch reached it: a LAN listing through its beacon's entry, looked
    up again for each package, so wherever the server's beacons come from at that moment, as for its descriptor; and a
    direct link or typed address only through the address that served the descriptor the join verified, which the
    facade keeps privately for that join. A join the service resolved (an invite, a server link or an Internet
    listing) never contacts its host, whose address the launcher does not know, whatever the session's privacy: its
    plan never takes a package from the host (`JoinSessionState.mayContactHost`), so the consent never offers one. A
    package only that host has blocks the join while checking (`contentUnavailable`), in words that name it and the
    ways that work: joining the host on the local network or by an address it gives, or the host publishing it to a
    source. The data layer refuses such a join's host as well (`hostDownloadUnsupported`). The session's privacy is
    applied to the host's address again before every package, so under relay-only nothing beyond the local network is
    contacted (`directNotAllowed`), and a LAN host no longer heard is `unreachable`.
  - **The LAN entry.** A LAN host is reached only through the entry its beacons keep alive, so the downloading stage
    holds LAN browsing (`LanDiscoveryClient.hold`): a stop asked for meanwhile, as when the player leaves the browser,
    waits until the stage ends, however it ends, a cancel included. Turning LAN discovery off still stops it at once.
    Browsing that had stopped before the stage starts again when the player's settings allow it, waits about six
    seconds for the host's beacon, and stops again after.
  - **Reading.** The status line comes within 64 bytes and holds the game's 64-bit length. A length past
    `ModRegistryFormat.maxPackageBytes` is refused before the body is read. The body streams to a temporary file beside
    the package cache, with its SHA-256 computed as it arrives, up to the announced length and no further, as the
    game's own client reads; one that ends short is refused once it ends (`Tfd1PackageReader`). Nothing a host sends
    makes the reader throw, and however a download ends early, its temporary file is deleted.
  - **Deadlines.** 5 seconds to connect, as for a descriptor; 15 seconds of silence from the host, its status line
    included, ends the transfer as stalled, long enough to ride out a brief network hiccup and short enough that a
    host that went away is given up on quickly; and the whole archive must arrive within the two minutes the game's
    endpoint gives one transfer before it closes it, so waiting longer could never succeed.
  - **Checks.** The SHA-256 must be the one the host's signed descriptor pins (`integrityFailed`). No source the
    player chose vouches for the manifest, so the archive's own must name exactly the mod and version the descriptor
    claims (`manifestMismatch`). Verified archives join the cache by SHA-256 and install as registry archives do, the
    receipt naming the verified cache as their source; nothing records where the host was.
  - **Failures.** `unreachable` for a host gone, unreachable or stalled; `downloadFailed` for a refusal, a busy host,
    an answer the launcher cannot read, a body cut short, an archive larger than a mod may be, a transfer past its two
    minutes, or a full disk. Each names the mod and says what to do next, and neither they nor the launcher log name
    an address.
- **The player's state is untouched.** A join never writes the manager record of a mod that already has one. A mod
  new to the player, with neither a record nor an installed version, gets a disabled record before its folder
  appears, so neither the game nor an inheriting profile turns it on by default. If that record is lost, as when a
  game that read its manager state before the join quits, or the player deletes the manager state, the mod still
  starts disabled, in the launcher and the game's loader alike (`addedRecords`).
- **What the player's own profiles see.** A version a join installed for a mod the player already had (installed,
  recorded, or named by one of their profiles) is hidden from every profile that is not a server profile, from the
  Mods list, and from a game started without the launcher. So an unpinned profile, or the game's own unpinned
  selection, never moves to a server's newer version. Server profiles pin every package and see every version. A mod
  new to the player stays visible, but disabled; if the player turns it on, it is theirs to use. A version counts as
  server content only while its install receipt is still the one the join wrote, so a version the player installs
  again is theirs.
- **One change, with a journal.** The install stages every package with the package installs' stage, commit and
  rollback path (`_stagePackageInstall`), in one folder, `launcher-join-*` in manager staging. A journal there says how
  far it got. In this order it then records the mods new to the player, adds its archives to the record of server
  content, and moves each package into place. A failure after that order begins rolls back everything this join
  changed, and only that: packages an earlier join installed stay. The rollback first notes in the journal that it is
  undoing the install, then moves each folder out of the packages folder whole, in one move, before deleting it, since a
  recursive delete in place would take the install receipt first and leave the rest as a broken version no record
  names. A folder that cannot move, as when another program holds one of its files, stays whole, receipt and all, and
  keeps its entry in the record of server content and its added record, so it stays hidden until a later start moves
  it out. Until then a join is refused with "try again" rather than count that folder as installed, since the start
  that settles it would take it from under the new server profile. For the same reason no plan counts it as
  installed: the catalog a join plans from (`loadExactPackageCatalog`) leaves out what an unsettled journal names, so
  the join downloads that version and reaches the install, which refuses while the folder stays and installs it afresh
  once a sweep took it out. A plan that downloads nothing never reaches the install, so without that it would save a
  server profile on the folder. The plan is made before the install's lease and its sweep, so the install also checks
  again that every version its plan found installed is still there, as the same archive, and otherwise ends with
  "join again". A cancel before the order begins removes what was staged and reports `JoinInstallUndone`. Once it
  begins the install finishes, and the reducer then ends the join as cancelled.
- **Crash safety.** A launcher stopped before the order begins left only staged folders, which nothing loads. One
  stopped part way through it left disabled records and hidden versions, which no profile of the player's own loads.
  Either way the next start, the next install or the next cleanup takes the staging lease and settles the journal: it
  undoes a partial install, or one whose rollback began, moving each folder out whole as the rollback does; it keeps an
  install whose every package is in place and whose rollback never began; and it removes the folder. A journal that a
  save stopped between its two moves left aside as a backup is put back first, as every file the launcher saves is.
  While a journal cannot be read just now, as while another program holds that backup, its folder waits for a later
  start with nothing undone or kept, and what that join left in place is unknown: a join is refused, and the catalog a
  join plans from (`loadExactPackageCatalog`) throws, each with the launcher's message for a file it cannot read, which
  names no path and says what to do. The catalog reads without the lease, so a journal that a save in another launcher
  has just moved aside counts as one that cannot be read, never as none. A join's own journal never refuses it. A
  journal that is there but not as written is never acted on, and its folder goes.
- **A running game.** It writes its manager state when it quits, over anything written while it ran, which would drop
  the disabled records. So nothing installs or cleans up beside one; the package inbox likewise holds what arrives
  until the next start. An install closes a game this launcher started only when the player's consent covered it
  (`JoinConsentGiven.closeOwnedGameAcknowledged`), and only once every package is staged and the last cancel was
  looked for, so a join that is refused or cancelled leaves the game running. It then decides again what to install,
  since the game wrote its manager state as it quit, and ends the join if that changed. Without that consent, a game
  this launcher started ends the join as `gameRunning`, owned, asking the player to save and quit; a game it did not
  start, or one that will not close, ends it as `gameRunning` with its ownership. Downloads and the server profile do
  not touch the game, so they go ahead while it runs.
  - **How the game is closed, and what the join does with how it ended.** The install asks the game to quit through
    the quit request (section 6), as an owned restart does, and stops it only when the request is not taken within 10
    seconds or the game has not exited within 30. Whether the join goes on is read from the startup journal the game
    left, as the manager's next start reads it, never inferred from how the game ended: a game that recorded no clean
    exit, because it was stopped, or quit before its mods finished unloading, or had crashed, opens its next start in
    the one-shot safe mode, in which a session launch is refused (section 8). A game this launcher started that ended
    on its own before the install came to close it, while the join downloaded or staged, is read the same way. Such a
    join ends before anything is installed, as `installFailed` with its rollback complete, and its detail says how the
    game ended, that a game in safe mode cannot join a session, and what to do: start the game from the launcher once
    and quit it normally, then join again; what was downloaded is kept. A game whose process cannot be verified is
    never asked, and one that neither quits nor can be stopped ends the join as `gameRunning`, owned; a game found
    running once the launcher's own has ended is another copy, and ends it as `gameRunning`, foreign. A launcher that
    closes while it waits withdraws an untaken request, stops nothing and installs nothing. When this launcher started
    no game, the install reads no journal; the join's launch, once it is built, has to. The consent sheet can
    therefore acknowledge closing after saying that joining quits the game as its **QUIT** button does.
- **The staging lease.** Installs and cleanups take `acquireLaunchLease`, so no launch stages while they change the
  game's files. A lease another launch or install holds ends the install at once with a plain "try again".
- **The server profile.** `mp-<sha16(serverId)>`, named `Server · <name>`, with `inheritManagerModState: false` and
  every package pinned (`ServerProfiles`). A join owns the id and name, the inheritance, the enabled packages and their
  pins, and Safe Mode, which is always off since Safe Mode never joins. A re-join replaces those and keeps everything
  else the player set: extra launch arguments, the launch environment, the remembered launch selection, and the config
  and backup metadata. The profiles file is replaced in one step, so a failed save leaves the earlier profile as it
  was. A profile of the player's own under that id is never taken over. Which profile is selected never changes.
- **The record of server content.** `BepInEx/TopiaForge/server-content.json`, beside the manager state. The launcher
  writes it, and both the launcher and the game's loader read it, so it is a contract between the two languages,
  pinned by the fixtures in section 10. It lives with the install so it moves with the packages, and the staging lease
  serializes its writes.
  - **Shape.** Exactly the members `schemaVersion` (1), `versions`, `withheld` and `addedRecords`. Each `versions`
    entry is an archive a join installed: `id`, `version`, `sha256`, `receipt` (the SHA-256 of its install receipt),
    `hidden`, and `installedAt` (Unix seconds, 0 to 2^53 - 1). Each `withheld` entry is a version kept from the
    player's own play without being known server content (`id`, `version`, `receipt`), which only a repair writes.
    `addedRecords` lists, lowercase, the mods whose manager record a join added. Ids are mod ids, versions SemVer of at
    most 64 characters, digests 64 lowercase hexadecimal digits.
  - **Bounds.** At most 1 MiB of UTF-8 with no byte order mark, at most 1,024 entries in each list, no member twice
    in any object, and no version twice in one list. A join that would pass a bound is refused, never trimmed, since a
    dropped entry would expose a hidden version.
  - **Spelling.** The launcher writes canonical JSON: members in ordinal order, no white space, each list sorted by
    lowercase id, then version. Readers accept any spelling.
  - **Reading.** A record is read whole or not at all. Anything that is not exactly the contract is damaged, a
    `schemaVersion` above 1 is newer, and a file that cannot be read just now is unreadable. None of the three says
    what is withheld, so none is ever taken to mean that nothing is.
  - **What it withholds.** A `hidden` entry or a `withheld` one keeps its version, matched by id in any case and by
    exact version, out of the player's own play while the version's install receipt has the recorded SHA-256. A
    receipt that cannot be read withholds every version the record names, failing closed. Unpinned selection never
    takes such a version. An exact pin of a profile of the player's own still takes a `withheld` one, as the game's
    loader takes every exact pin, so a profile that pins a version of the player's own keeps loading it after a
    repair; a `hidden` version is for server profiles alone.
  - **Writes.** Atomic, one after another under the staging lease, never over a newer record or one that cannot be
    read. A write a stopped launcher left between its two moves (`server-content.json.<token>.bak`) is read where it
    stands, by the launcher as by the game's loader, which withholds what any such backup withholds. Only the next
    write, which holds the lease, puts it back: the desktop launcher and the command line can both have the install
    open, and a reader that moved a backup back could land it on the record a write in the other had just moved in.
  - **Repair.** A damaged record is repaired under the staging lease, at the launcher's next start and before an
    install or a cleanup. A copy is kept aside (`server-content.damaged-<UTC time>.json`). The repaired record keeps
    every entry that still reads, and withholds each other installed version the manager state does not name. A pin of
    a profile of the player's own is no evidence, since a copy of a server profile pins the server's versions; exact
    pins still load what a repair withheld. So no version a join hid becomes the player's own, and what the player
    loaded before stays theirs. A repair waits while a receipt it would read cannot be read. A link, a folder or an
    oversized file in the record's place is moved aside under that name before the repaired record is written, so a
    write that fails there, as on a full disk, or a launcher stopped there, leaves the damaged copy with no record
    beside it. Both languages read that as damaged, never as missing, and the next start repairs it again.
  - **While it cannot be used.** A launch of a profile that is not a server profile is blocked, and so are installs and
    cleanups, each with what to do next: update the launcher for a newer record, restart it for a damaged one, close
    the program holding the file for an unreadable one. The launcher's own view of the player's mods, the Mods list
    and what a new or edited profile pins from it, freezes as the game's loader does: each mod shows only the version
    its manager record names, and a mod with no manager record shows nothing.
- **The game's loader.** `ModRegistry` reads the record (`ServerContentSelection`) on every scan, through
  `ServerContentRecord`, the same contract in C#.
  - **Unpinned selection.** This is a game started without the launcher, the player's own play. It never takes a
    version the record withholds, and a mod whose every installed version is withheld offers nothing to it. A mod the
    record lists in `addedRecords`, found with no manager record, starts disabled.
  - **A record that cannot be used** (damaged, newer or unreadable, or missing beside a damaged copy a repair set
    aside) freezes unpinned selection. Each mod loads only the version the manager state names, which is what loaded
    before, and a mod with no manager record does not load. Its message says why and to start the game from the
    launcher once, which repairs the record; the manager log says the same once.
  - **Exact pins**, which every launch the launcher makes writes, see every version, so server profiles are
    unaffected.
- **Cleanup.** `planServerContentCleanup` lists each version of server content with its size and the server profiles
  that pin it, and whether it can go: not while a profile of the player's own loads or pins it, or the manager state
  has it on (what a game started without the launcher loads), and not while a server profile the cleanup keeps pins
  it. A version whose install receipt cannot be read just now is neither listed nor dropped from the record, so a file
  another program holds never exposes a server's version; the next cleanup judges it. The player's own installs never
  appear. A plan for every server, or for the ones it names, removes those server profiles too.
  `applyServerContentCleanup` applies exactly that plan, checked by its fingerprint, and refuses one gone stale, a
  running game, and a held lease. It moves every version out of the packages folder first, undoing the moves if one
  fails, then removes the manager records of mods left with no version that a join added or whose uninstall is
  pending, as an uninstall does, then the profiles, then the downloaded archives no installed version names. An added
  record goes once its mod has neither a manager record nor an installed version, as an uninstall leaves it; while a
  version is installed it stays, since it is what starts that mod disabled again. Versions no server profile pins any
  more, such as the ones a re-join replaced, are removed after each profile save on their own, so the player only ever
  decides about whole servers.
