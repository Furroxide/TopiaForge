# Multiplayer session launches — design record

Status: Implementing (WS6.2 and WS6.3 of the live-multiplayer plan) · Date: 2026-10-15 · Owner: multiplayer

This records how the launcher starts a game to host or join a live session, what the game reports back, the channels
the two keep open while a session runs, and how the launcher finds the exact package versions a server runs. The
transport and the live sessions themselves are recorded in `MultiplayerLiveSessions.md`.

| Piece | Dart (launcher) | C# (game) |
| --- | --- | --- |
| Request V5 | `launch_resolution/profile_transport_v5.dart`, `session_request.dart`, `session_targets.dart` | `ProfileLaunchConfigurationV5.cs`, `LaunchSessionRequest.cs`, `LaunchTransportJson.Session.cs` |
| Progress and outcome V2 | `outcome_transport.dart`, `session_outputs.dart` | `LaunchProgress.cs`, `LaunchSessionOutputs.cs` |
| Channels and preferences | `session_channels.dart`, `session_preferences*.dart`, `session_journal.dart` | `LaunchSessionChannels.cs`, `MultiplayerPreferences.cs`, `LaunchTransportJson.Channels.cs` |
| Vocabulary and value rules | `session_values.dart` | `LaunchContractValues.cs`, `LaunchSessionValues.cs`, `LaunchSessionHosts.cs` |
| Staging | `launch_staging_store.dart`, `launch_staging_sessions.dart` | `LaunchStagingStore.Sessions.cs` |
| Exact-version resolution | `multiplayer/exact_package_resolution.dart`, `exact_package_catalog_loading.dart` | — |

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
host's plan, otherwise `main-menu`.

| `session` member | Value |
| --- | --- |
| `role` | `host` or `join` |
| `intentId` | The launcher's intent: one host or join attempt, kept across its restarts and re-plans |
| `displayName` | 2 to 24 UTF-16 code units of display text |
| `transport` | `privacy` (`relay-only` or `direct`), `directConsent`, `port` (0 to 65535, zero for any free port), `upnp`, `lan` |
| `serviceOrigin` | The control plane's `https://` origin: a lowercase DNS name and an optional port. Omitted when none is configured. An IP literal is refused |
| `target` | A join's server (section 3) |
| `host` | A host's options: `name` (1 to 48), `maxPlayers` (2 to 16), `visibility` (`public`, `invite`, `lan`), `voice`, `passwordRequired`, `servePackages` (developer mode only) |
| `devInstance` | `topiaforge dev --peers N` only: `index` and `count` (2 to 8) |

- **Passwords are never staged.** They are typed in game. `passwordRequired` only tells a host to ask before it opens,
  and a `password` member anywhere is refused.
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

- **The handoff never carries an address.** A server typed by address in game goes to the launcher as a
  `topiaforge://join/…` link. The launcher takes a handoff once and restarts into the server's profile through its
  owned-restart path.
- **A live join never carries an address.** Its target is an invite or a listing; a direct target needs a launch
  request. The game refuses a join whose package-set digest is not its own, and reports it under `joinId` as it would
  a launch request. A newer join replaces one the game has not taken.

## 7. Preferences and their change journal

`multiplayer-preferences-v1.json` projects the launcher's preferences for the game: `displayName`, `privacy` (`mode`
always `relay-only`, and the acknowledged `disclosure`), `voice` (`consent`, `enabled`), `network` (`lan`, `upnp`),
`hosting` defaults (`name`, `maxPlayers`, `visibility`, `port`, `voice`), the optional `serviceOrigin`, its `revision`
and `journalApplied`. Favorites and history are never projected.

- **The game never edits the projection.** It records what the player changed in game in
  `multiplayer-preferences-journal-v1.json`: at most 64 changes in strictly increasing `sequence` order, each setting
  exactly one of `displayName`, `disclosure`, `voice`, `network` or `hosting`. Privacy is not a change, since direct
  play is never stored.
- **One round** (`MultiplayerPreferencesSync`, and `syncPreferences` in staging): the launcher builds the projection
  from its multiplayer store and service configuration through `MultiplayerPreferencesSource`, applies the journal's
  changes after `journalApplied` on top, raises the revision when what the game reads changes, and keeps the game's
  changes in its store. An acknowledgement never lowers the disclosure version. The game then drops the changes the
  projection's `journalApplied` covers.

## 8. The game side today

- `TopiaForgeModManagerPlugin` consumes a V4 or V5 request (`LaunchStagingStore.ConsumeLaunchRequest`), logs its role
  but never its target, and runs the executable profile through the V4 path.
- Before mods load, a V5 session is offered to the multiplayer provider as an internal ticket
  (`TopiaForge.Mods.Internal.LaunchSessionTickets`), unless the command was rejected or recovery runs. The provider's
  narrow entry point is `MultiplayerProvider.TakeLaunchSession`, which takes the ticket once and maps it onto live
  sessions (`LaunchSessionPlan`): host settings, or a join target pinned by the verified server and its settings.
  Invites and Internet listings wait for the control plane.
- `RuntimeLaunchPublisher` reports a session launch's runtime records as version 2: progress with the session still
  `pending`, and a failure before the session started as the session launch's own outcome. A running target or the
  main menu is not yet a session's success, and a gamemode session's end is not the live session's, so both are left
  for the provider to report. A rejected V5 request is correlated by its intent and reported the same way.

What later slices wire: starting the session from the plan when live sessions are on, the provider's progress and
outcomes V2 from its live flows, writing handoffs, polling for live joins, and reading the projection in game.

## 9. Exact-version resolution

A join must install the exact `id@version@sha256` a server runs, which is often not a source's latest version.

- **History manifests.** `topiaforge registry index` inlines the full manifest and the archive size (`manifest`,
  `packageBytes` on each `history` row) for each mod's newest 20 older versions (`--history-manifests n`, 0 to 1000).
  Both members are additive; launchers that ignore history are unaffected.
- **Candidates, never tiles.** The launcher reads a history row only when its manifest names the entry's mod at the
  row's version and its URL and hash pass the source's checks; anything else is skipped without failing the source.
  Such rows fill the exact-version candidate map that dependency installs use, after every current entry, and never
  become catalog tiles.
- **Classification.** `ExactPackageCatalog` classifies each descriptor package by its exact SHA-256: installed,
  available (with its source), in a disabled source, host-offered (developer mode, when the host serves packages, after
  every registry option), hash mismatch, version unavailable, or not in sources. Only the first two carry a manifest,
  and both come from the player's own receipts and sources. `JoinPlanner.planFromCatalog` plans from it.
- **Loading.** `LocalLauncherRepository.loadExactPackageCatalog` lists every valid installed archive with its receipt's
  manifest, and every row of every source, history included. A disabled source is read only when it is local, so the
  launcher never contacts a remote source the player switched off.

## 10. Fixtures and tests

`tests/fixtures/multiplayer/{launch-v5,progress-v2,channels}/` are generated by
`tests/fixtures/multiplayer/generate_launch.py`, independently of both runtimes. Both replay every case: valid documents
read the same in any member order and write back to the exact bytes, and every invalid one is refused. The launcher
also replays how a journal applies. The generator checks that no progress, outcome or channel fixture names a direct
target's host or port.
