# In-game multiplayer sessions — design record

Status: Implementing (WS5, first part) · Date: 2026-10-17 · Owner: multiplayer

This records how the game's own interface hosts, browses, connects to and leaves live sessions: the manager overlay's
MULTIPLAYER tab, the connecting overlay and the approved privacy prompts. The interface drives the internal SPI that
[MultiplayerRuntimeIntegration.md](MultiplayerRuntimeIntegration.md) records, and live sessions stay behind the
developer switch until the release enables them: with them off, the tab says so and nothing reaches the network.

## 1. Shape

| Part | Where |
| --- | --- |
| Screens: the views' contracts, which name no SPI type, and the approved privacy wording | `src/TopiaForge.ModManager/Multiplayer/Screens/` |
| Presenters: state, validation, failure wording, the realm and privacy gates, all Unity-free | `src/TopiaForge.ModManager/Multiplayer/Sessions/` |
| Views: the tab, its sub-tabs, the connecting overlay and the prompts, through the TopiaForgeUi kit only | `src/TopiaForge.ModManager/Overlay/Multiplayer/` |
| The overlay's tab, the game-side environment and the menu button | `src/TopiaForge.ModManager/Overlay/`, `MenuButtonInjector.cs` |
| The UI Gallery's SESSIONS page: the same views over fake screens | `mods/TopiaForge.UiGallery/Pages/SessionsPage*.cs` |

- **Kit only.** The views use the kit and no Unity type, so the gallery compiles them from the manager's own source and
  shows every state with made-up data (F8, SESSIONS). An architecture test keeps them that way.
- **Testable.** The presenters and screens compile into the ModManager harness, which drives them over a recording
  control and a scripted environment.
- **Frame cost.** The presenter ticks every frame, overlay open or not, so a join is followed while the player plays.
  The control's status allocates, so it is read only when a cheap signal moves (the role, live availability, the
  readiness hold or the session snapshot), when an action asks, and at most five times a second while hosting starts or
  a join runs. A settled frame, and every value a view binds each frame, allocates nothing.
- **Entry points.** The main menu shows MULTIPLAYER above GAMEMODES and TOPIAFORGE whenever the multiplayer provider is
  loaded; it opens the overlay on the MULTIPLAYER tab. The overlay finds its tabs by name, so adding MULTIPLAYER after
  GAMEMODES changed no other tab.

## 2. The sub-tabs

| Sub-tab | Contents |
| --- | --- |
| BROWSE | LAN, Internet, Favorites and History, in a sortable, virtualized table: lock, name, gamemode, world, players, ping and mods (Ready, Needs N, Incompatible). JOIN, INSTALL & JOIN IN LAUNCHER, and favorites |
| HOST | The game to host (chosen at the menu, the running one otherwise), name (1 to 48 characters), players (2 to 16, 8 by default), password (optional, never kept), visibility (Public, Invite or LAN), and the host policy |
| CONNECT | An invite code, which needs the TopiaForge service, or an address and password the player types |
| VOICE | A slot the voice slice fills (`IMultiplayerVoiceSlot`) |
| SESSION | The invite, the players with kick, ban and mute for the host, and "End session for everyone" or "Leave server" |

- **Searching** the local network starts only when the player presses SEARCH LAN, and stops when they leave BROWSE or
  close the overlay. While it runs, each session heard is checked once, over the local network only, at most two at a
  time, for whether this game's mods fit.
- **The Internet list** says the TopiaForge service is not configured; a later slice fills it.
- **Host policy.** The session engine supports offering voice chat today. Guests building, prop protection and per-player
  quotas are named seams the HOST view shows as not available yet.
- **Favorites and history** keep a server's id and name, never an address. A typed address is typed again; a server the
  local network hears can be joined from either list.
- **Results** are toasts; long content scrolls; every action that removes a player, ends a session or leaves a game
  confirms through the kit's destructive modal.

## 3. Mods that are not identified yet

Installed packages are identified by their receipts at once; packages without one are hashed off the main thread
([MultiplayerRuntimeIntegration.md](MultiplayerRuntimeIntegration.md), section 4). While that runs, HOST, BROWSE and
CONNECT show "Identifying this game's mods for multiplayer" with its progress, and hosting and joining stay disabled
until it is done. Mods that could not be read are worded in the same line, with RETRY. The
interface reads readiness only once the player opened the tab, since the first read starts the work.

## 4. Privacy

- **The approved wording.** Every privacy string lives in `MultiplayerPrivacyText`, word for word from
  [MultiplayerPrivacyText.md](MultiplayerPrivacyText.md). A test parses that document and fails when any string
  differs, and another fails when any other source of the interface spells the wording again.
- **The disclosure** ("Your address and multiplayer") shows before the first host or join, and again whenever its
  version rises. The acknowledged version is kept in manager data. Closing it any other way than its button answers no,
  and nothing happens.
- **Hide my IP** is on by default and is a setting in the tab's header. Turning it off shows the disclosure again and
  takes effect only once the player acknowledges it; turning it on needs nothing.
- **The per-session prompt** ("Allow a direct connection?") shows whenever a session can only be reached directly while
  Hide my IP is on: a Public or Invite session to host, since only the local network finds a session without the
  service, and a typed host beyond the local network. For a typed host, CONNECT first checks the server under Hide my
  IP's rule; the provider then contacts nothing beyond the local network, a name server included, and says the session
  needs a direct connection. Only then does the prompt ask. Cancel, the safe choice, is its primary button; cancelling
  leaves Hide my IP on and contacts nothing. Allowing it applies to that session only.
- **Addresses.** No label, toast, log, copied detail or diagnostic holds an address. The only exceptions are the address
  the player types in CONNECT and the direct link handed to the launcher for it. Text another player wrote (a server's
  name, gamemode and world, a kick message, the host's admission lines, player names) is run through the runtime's
  redactor and shown as plain text, attributed to the host where it is the host's own words.
- **Copy details** holds the failure's name and step, this game's words, the host's admission code and lines, what the
  host said, and the game and TopiaForge versions. Every line is redacted; the target and anything typed are not part of
  it.
- **INSTALL & JOIN IN LAUNCHER** hands a `topiaforge://join/…` link to the system with `Application.OpenURL`. A LAN or
  listed server's link names its server id; an invite's names its code; a direct link carries only the host and port the
  player typed, pinned by the server id the check found. The launcher's consent flow takes it from there.

## 5. The connecting overlay

The provider resolves the host and checks this game's mods against its signed description before it connects; it asks
for a password, if the session needs one, before it connects; then the host admits the player; then the world
synchronizes. The stepper follows that order on the steps the plan names:

| Join phase | Step |
| --- | --- |
| Resolving | Resolving ("Finding the host") |
| Awaiting the password, preparing it, connecting, reconnecting | Connecting ("Password needed", "LAN", "Direct" or "Relay") |
| Synchronizing, before the host accepts | Authenticating ("Waiting for the host") |
| Synchronizing, with a readiness hold | Loading world |
| Synchronizing | Synchronizing ("Receiving the world", or n/m once the network world reports it) |
| In session | every step done; Spawning completes until avatars land (WS3.10) |

Verifying mods is done once the host admits the player. A failure stops at its own step or at the step the join reached,
whichever is later, and steps it never ran show skipped: mods that do not match this game's check fail at Verifying
mods, with Connecting and Authenticating skipped. Every `InternalLiveFailure`, and every admission code of a refusal,
has its own words. The failure view offers Retry where trying again can help, Open launcher where the launcher can
take the join over, Copy details, and Trust for a typed host that now serves a different server. Cancel, the close
button and ESC stop a running join at any step, and leaving is never refused.

## 6. The realm rules

Until the runtime holds the realm (WS3 step 2), `SessionRealmGate` applies `NetRealms.EvaluateTransition` to the
control's role and the local game's phase:

- **Joining** from a running game asks "Leave current game?" through the destructive modal; confirming returns to the
  menu, and the join starts once the game is idle. A host or a joined player ends that session first.
- **Hosting** a running solo game opens it in place. At the main menu the chosen game starts first, and hosting opens
  it once it runs.
- **Leaving** is never refused.
- **What step 2 changes.** The runtime will hold the `NetRealm` and check these rules before the control acts. The gate
  then asks the runtime for the realm instead of deriving it, and starting a game to host becomes one runtime transition
  rather than a launch followed by hosting.

## 7. Players and moderation

- **Ready only.** During a join the engine's snapshot can still show the previous host's players, mislabelled, until
  the new host's first update. SESSION lists players, and offers kick, ban and mute, only once the session is Ready:
  this game hosts it, or joined it and is in session, and the snapshot says Ready.
- **The player's key.** Participant ids are reused across hosts and restarts, and a row can be a frame old. A selection
  keeps the player's key with the id, and every kick, ban or mute checks that key against the current snapshot before it
  fires, after its confirmation. When another player holds the id now, or nobody does, nothing happens and the player is
  told the list changed. A player without a key cannot be moderated.

## 8. Work off the main thread

- **Settings** live in `data/multiplayer-settings.json`: the acknowledged disclosure version, Hide my IP, the display
  name (2 to 24 characters), the last hosting settings without the password, and favorites and history by server id.
  They load and save on `MultiplayerBackgroundQueue`, one write at a time, coalesced; the tab notes a pending write and
  stays responsive. Settings that cannot be read are kept for the session only, so the file is never overwritten with
  defaults.
- **Launcher channels.** Launch request V5's C# channels, `LaunchStagingStore.WriteSessionHandoff` and
  `RecordPreferenceChange`, can wait for a shared file for about 310 ms while they retry, so the interface never calls
  them on the main thread. Nothing in the game calls them yet, and the launcher neither takes a handoff nor applies the
  journal yet. Once both sides are wired, a handoff from a launcher-owned game and every journal write go through
  `MultiplayerBackgroundQueue`, as the settings do, and the views show them pending the same way. Until then the
  hand-off is the `Application.OpenURL` link above, and preferences stay in manager data.

## 9. Seams for later slices

| Seam | Filled by |
| --- | --- |
| `IMultiplayerVoiceSlot` in the VOICE sub-tab | The voice slice |
| `HostPresenter.PendingPolicies` | The Sandbox host policy |
| `ConnectingPresenter.ReportSynchronization(done, total)` | The network world's baseline (WS3.6) |
| The Internet list, invite codes and listed servers | The game-side control-plane client (WS7) |
| The session-handoff channel and the preference journal, through `MultiplayerBackgroundQueue` | The launcher's handoff and preference flows (WS6) |

## 10. Tests

- **Presenters** (`MultiplayerSessionsTests`): every sub-tab's states and transitions, the name, server name, player and
  password bounds, the disclosure's versioning and Hide my IP, the direct prompt contacting nothing when cancelled, the
  realm rules and "Leave current game?", every phase of the stepper, every failure's words and step, cancelling at every
  phase, retry, trust, the launcher hand-off and its links, copied details without an address, players only once Ready,
  moderation checked against the current snapshot's player key, the address sweep over every screen, and settled frames
  allocating nothing.
- **Wording** (`MultiplayerPrivacyTextTests`): the constants against the approved document, parsed.
- **Architecture** (`MultiplayerUiArchitectureTests`): no address-shaped member or network type beyond the typed address,
  kit-only views, Unity-free presenters, no logging, and the 400-line cap on every file of the interface and the overlay.
- **Tabs** (`ManagerTabLookupTests`): name-based lookup and the entry points that use it.
