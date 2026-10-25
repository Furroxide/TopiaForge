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
- **The frame boundary.** The game installs a crash reporter on Unity's log, so an exception that escapes the overlay's
  frame is a path into crash reports. The overlay runs the presenter, what shows outside the tab (the prompts, the
  toasts and the connecting overlay) and the tab itself as the stages of `MultiplayerOverlayFrame`, each through
  `MultiplayerFrameGuard`; the tab builds and hides through it, and the game's later answers to a launch or a return to
  the menu reach the screens through it too. A failing stage is caught and the rest of the frame, the plugin's
  included, runs on. Each distinct failure (its stage, type, redacted message and throwing method) is logged once,
  with its stack, through the manager's log and `MultiplayerDiagnosticRedactor`; at most 16 are, then one line says the
  rest are not. A frame that does not fail allocates nothing.
- **A provider that throws** while hosting or a join starts, which can happen from the frame that waited for the game,
  ends that attempt in this game's words, once: it is not tried again every frame.

## 2. The sub-tabs

| Sub-tab | Contents |
| --- | --- |
| BROWSE | LAN, Internet, Favorites and History, in a sortable, virtualized table: lock, name, gamemode, world, players, ping and mods (Ready, Needs N, Incompatible). JOIN, INSTALL & JOIN IN LAUNCHER, and favorites |
| HOST | The game to host (chosen at the menu from the games it can start, the running one otherwise), name (1 to 48 characters), players (2 to 16, 8 by default), password (optional, never kept), visibility (Public, Invite or LAN), and the host policy |
| CONNECT | An invite code or the invite's link, or an invite or server link pasted as the address, which need the TopiaForge service; or an address and password the player types |
| VOICE | A slot the voice slice fills (`IMultiplayerVoiceSlot`). Until then a placeholder, which says "This session offers no voice chat." while this game hosts, or has joined, a session without it |
| SESSION | The invite, with COPY INVITE CODE for the host; the players with kick, ban and mute for the host; and "End session for everyone" or "Leave server" |

- **Searching** the local network starts only when the player presses SEARCH LAN, and stops when they leave BROWSE or
  close the overlay. While it runs, each session heard is checked once, over the local network only, at most two at a
  time, for whether this game's mods fit. A host may list 512 packages this game lacks, so the selection names four
  problems and counts the rest; and a host may announce a new server id as each old one falls silent, so a check is
  kept only while the local network lists its server.
- **Games to host.** At the main menu HOST offers each launch choice that replaces the scene. A choice that adds an
  arena to a running game needs that game, and its launch is refused at the menu, so HOST leaves it out. Each choice,
  the running game's line, and the world a hosted session announces to players browsing for one name the world as
  players know it (`HostTargets.WorldNames`): a declared world by its declared name, a discovered one, such as a native
  level, by the name its discovery gave it. Only a world with neither shows its id. HOST selects the game last hosted,
  from the menu or opened in place, which the settings remember by launch target and world; a game the player picks
  stays picked, by the same key, however the list changes; with neither, HOST selects the first.
- **The TopiaForge service.** Whether this game reaches one comes from the runtime (`ServiceOrigin`, from the session
  launch or the manager's configuration) and shows in every status (`Internet.ServiceConfigured`). Without one, HOST
  keeps Public and Invite disabled with the reason, CONNECT takes addresses only, and the Internet list says the
  service is not set up; nothing is contacted. With one:
  - **HOST** offers Public and Invite. Players join them through the relay, and directly too only when the player
    turned Hide my IP off; no per-session prompt is needed. While such a session runs, HOST says how the service lists
    it: registering, listed, retrying with the reason, or failed. SESSION shows the host its invite code once the
    service has made one; it shows nowhere else and is never logged. COPY INVITE CODE puts it on the clipboard on the
    host's own click, so the host pastes it to its players; the toast that confirms it does not repeat the code.
  - **CONNECT** joins an invite, or a pasted invite or server link. The invite field also reads the code out of the
    invite's own `topiaforge://join/` link, and ignores spaces and line breaks around it, since players paste what the
    host shared. The check asks the service alone, never the host,
    and the join goes through the relay unless Hide my IP is off. The overlay names the session by its server, or as an
    invited session or a listed server, never by the code.
  - **The Internet list** points to the TopiaForge launcher, which owns the Internet browser; the game does not browse
    the service.
- **Host policy.** "Offer voice chat" works today: a session that offers none carries none, fixed for the hosting run,
  and every joining player learns so before it could talk ([MultiplayerVoice.md](MultiplayerVoice.md), section 2.12).
  Guests building, prop protection and per-player quotas are named seams the HOST view shows as not available yet.
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
  Hide my IP is on. For a typed host, CONNECT first checks the server under Hide my IP's rule; the provider then
  contacts nothing beyond the local network, a name server included, and says the session needs a direct connection.
  Only then does the prompt ask. A join through the service fails as `RelayUnavailable` when the relay cannot carry it;
  when the host takes direct connections (`Internet.HostAllowsDirect`), the prompt asks over the failure view, and
  allowing it starts the join again with a direct connection allowed for that join only. A host that keeps its address
  hidden gets no offer, since a direct connection would not help. Cancel, the safe choice, is the prompt's primary
  button; cancelling leaves Hide my IP on and contacts nothing. Allowing it applies to that session only.
- **Addresses.** No label, toast, log, copied detail or diagnostic holds an address. The only exceptions are the address
  the player types in CONNECT and the direct link handed to the launcher for it. Text another player wrote (a server's
  name, gamemode and world, a kick message, the host's admission lines, player names) is run through the runtime's
  redactor, attributed to the host where it is the host's own words. This game's own problem lines name the host's
  packages by the ids and versions its descriptor gives, and an id or a version may be four dotted numbers, an address
  the host chose; the screens drop those as the redactor does, and keep the real mod ids readable.
- **Plain text.** Every label the views make from screen text, every toast and every destructive confirmation renders
  as plain text, so markup a host or player writes shows as written and cannot restyle or hide this game's words.
- **Copy details** holds the failure's name and step, this game's words, the host's admission code and lines, what the
  host said, and the game and TopiaForge versions. Every line is redacted; the target and anything typed are not part of
  it.
- **INSTALL & JOIN IN LAUNCHER** hands the join to the TopiaForge launcher. When the launcher owns this game, it
  writes a `session-handoff` naming the server's listing or invite, on the background queue, and the launcher joins
  through its owned restart; a write that fails opens the link instead. Otherwise it hands a `topiaforge://join/…` link
  to the system with `Application.OpenURL`. A LAN or listed server's link names its server id; an invite's names its
  code; a direct link carries only the host and port the player typed, pinned by the server id the check found, and a
  server typed by address always goes as its link, since a handoff never carries an address. The launcher's consent
  flow takes it from there.

## 5. The connecting overlay

The provider resolves the host and checks this game's mods against its signed description before it connects; it asks
for a password, if the session needs one, before it connects; then the host admits the player; then the world
synchronizes. The stepper follows that order on the steps the plan names:

| Join phase | Step |
| --- | --- |
| Resolving | Resolving ("Finding the host") |
| Awaiting the password, preparing it, connecting, reconnecting | Connecting ("Password needed", "LAN", "Direct" or "Relay") |
| Synchronizing, before the host accepts | Authenticating ("Waiting for the host") |
| Synchronizing, while session follow prepares and loads the host's world | Loading world ("Getting your mods ready", "Loading the host's world", or for an arena "Loading the host's level (1/2)" and then "Building the host's arena (2/2)") |
| Synchronizing, while session follow waits for the host's world to arrive | Synchronizing ("Receiving the world", then n/m as the network world reports it) |
| Synchronizing, while session follow starts this player's controls | Spawning ("Starting your controls") |
| Synchronizing, with a readiness hold | Loading world (the follow's hold and the network world's, while the follow waits for the host's announcement) |
| Synchronizing | Synchronizing ("Receiving the world", or n/m once the network world reports it) |
| In session | every step done; Spawning completes until avatars land (WS3.10) |

Session follow (WS3.8, [MultiplayerRuntimeIntegration.md](MultiplayerRuntimeIntegration.md), section 19) runs the
host's world on the joined player, and the join completes once the player is Ready in it. A join to a host at its main
menu completes there, and adds "The host is at the main menu. You join their next world." A host that goes to its menu
later has the game follow it there with the same toast. For as long as the game waits at a menu, MULTIPLAYER's status
line, in place of the server joined, and SESSION, in place of "Playing on the host's session.", say why: those words,
or "The host is in a level your game could not load. You join their next world." at the fallback menu. Both go back
once the game follows the host's next world, and clear when it leaves. A join the follow ends shows
the follow's own failure, in this game's words and at its own step, with RETRY where joining again can help; a failure
of an earlier join never ends this one. After the join, when the host changes worlds or the game returns to the host's
world after straying, the overlay shows "Following the host" with the first four steps done, says "Back in the host's
world." once it runs, and its Cancel leaves the session. The follow's notices, such as "You returned to the main menu,
so you left the session.", show once each. "Leave server?" says "You leave the session and return to the main menu."
This also covers native-level follows after joining. A later follow failure shows its own words and RETRY where it
helps, including a failure that happens between two frames after the original join overlay closed. Dismissing it
keeps it closed, and a late failure from a cancelled follow does not reopen the overlay. A join or a follow that leaves
the game at the main menu because the host's level could not be loaded here is shown as what it is: Loading world not
done, "Your game could not load the level the host is in. You stay in the session and join the host's next world." in
the status line and once as a toast, and CLOSE, since nothing runs that Cancel would stop. The view stays until it is
closed, the player leaves, or the host's next world takes it over; a join there never says the host is at its menu.
Whenever nothing runs that Cancel would stop, as while a finished join lingers, the overlay offers CLOSE in its place.

Verifying mods is done once the host admits the player. A failure stops at its own step or at the step the join reached,
whichever is later, and steps it never ran show skipped: mods that do not match this game's check fail at Verifying
mods, with Connecting and Authenticating skipped. Every `InternalLiveFailure`, and every admission code of a refusal,
has its own words, and every one the player can do something about says what: try again, check the code, ask the host,
update, or that this game carries on by itself. The TopiaForge service's codes keep the Internet sessions' design
record's sentences and add that next step. A service turning requests away with a named delay is waited out on the
overlay, a day at most, which then joins again by itself, once; without a named delay RETRY is offered at once. A
server the service no longer lists leaves the recent servers, and a wrong or withdrawn invite offers no retry. RETRY
after the host refused the password, or turned away too many wrong ones, joins again without it, so the overlay asks
for the password in place instead of sending the refused one again. The provider finds a LAN session only among those
this game hears while it listens on the local network, and BROWSE stops listening when its tab closes, so a LAN join
whose session this game does not hear now, such as RETRY while the player plays, listens first ("Listening on your
network"), for 15 seconds at most as a launched LAN join does, and joins once the host is heard. A host no longer there
ends the join as "Session not found": "The session was not heard on your local network. Check that its host is still
hosting on this network, then try again." BROWSE, the overlay and a launched join share the browser, each releasing only
its own use of it. The overlay releases its share once the provider has looked the session up; a search still needed
by BROWSE or a launch keeps running, including when RETRY is cancelled. The last release stops a browser these screens
started, never an external search. Ownership is checked at the action, not from a cached status. Hearing the host while
the game is busy moves to Waiting and releases the overlay's share; once the game settles, a missing beacon gets a new
bounded listen. Relay and direct joins never listen. Once the link is up, the
Connecting step says the route the status reports, Relay or Direct. The failure view offers Retry where trying again
can help, Open launcher where the launcher can take the join over, Copy details, and Trust for a typed host that now
serves a different server,
or whose server could not be checked because this game's record of earlier ones could not be read
(`KnownHostsUnreadable`). CONNECT's check asks the same "Trust this server?" question for both, each with its own words.
A failure that replaces another without the view closing, as a RETRY that fails at once does, shows its own heading and
words, never the earlier failure's: the view binds them on every frame. Cancel, the close button and ESC stop a running
join at any step, and leaving is never refused.

The overlay and the prompts are persistent modals: joining from a running game returns to the main menu first, and a
modal that is not persistent is destroyed with the scene it opened in, which in the game left the join running with no
overlay and its failure unseen. A question about a game or a session holds only while that game or session does, so a
persistent one never outlives it:

- HOST's disclosure and direct prompt hold while hosting would still open the same game, or start the chosen one;
  "Leave current game?" holds while that game runs and no other join has started; "Leave server?" holds while this game
  is joined, and every host's question while it hosts.
- A question that stops holding, as when the game ends or the session drops while it shows, is withdrawn: it answers
  no, so nothing it guards happens, its modal closes, and a toast says that nothing was done. A yes that lands in the
  frame it stopped holding counts as no as well.
- The view shows one prompt at a time and closes the modal of one answered elsewhere (`PromptPresentation`). A modal
  that cannot be shown counts as no, so the next prompt can show.

## 6. The realm rules

The runtime holds the realm ([MultiplayerRuntimeIntegration.md](MultiplayerRuntimeIntegration.md), section 10). The
interface keeps no verdict of its own: it asks `EvaluateRealmChange` before it offers a choice, and goes online and
leaves only through `OpenToMultiplayer`, `Join` and `Leave`, never the control's own hosting and joining.
`SessionRealmGate` only reads the runtime's verdict as what to do next. The rules read the session orchestrator's
`RealmPhase`, which counts an idle session that is still changing as busy: a return to the main menu until the menu
has loaded, a native scene change, any other operation that holds the session, and shutdown.

- **Joining** from a running game asks "Leave current game?" through the destructive modal; confirming returns to the
  menu, and the join starts once the rules allow it, after the menu has loaded. A join asked for while the game is
  starting or stopping, the main menu loading included, waits on the connecting overlay, with Cancel, until the rules
  allow it; a game that runs by then asks "Leave current game?" first. The runtime asks the rules again as the join
  starts, so a game that began to change meanwhile is waited for, never joined. A host or a joined player ends that
  session first. A level the game's own menus loaded counts as a running game for a join, so it asks "Leave current
  game?" too.
- **A game started while a join resolves** is never replaced: when the join links up, session follow ends it with "You
  started a game while joining", and the game runs on. RETRY asks "Leave current game?" first.
- **A joined player** runs the host's world: a launch from GAMEMODES is refused while it is joined, since the world is
  the host's to choose; a level the game's own buttons load takes it back to the host's world; Stop and Restart are the
  host's; and the main menu leaves the session.
- **Hosting** a running solo game opens it in place. At the main menu the chosen game starts first, and hosting opens
  it once the rules say the game runs; one that stops first opens nothing.
- **Leaving** is never refused, busy or not: ending the session, leaving it and cancelling a join all go through the
  runtime's `Leave`. A provider that fails while leaving is said in this game's own words.
- **Frame cost.** HOST's labels ask the rules each frame, so `EvaluateRealmChange` allocates nothing.

## 7. Players and moderation

- **Once the host lists them.** A joining player's snapshot lists nobody until the new host's own list arrives, which
  it does before Ready, and keeps that list while a lost link reconnects. So SESSION lists players, and offers kick,
  ban and mute, as soon as this game hosts or is joined and the snapshot lists anyone: whatever it lists is that host's
  players under that host's ids. Before the join connects, the snapshot is solo play's, and nobody is listed.
- **The player's key.** Participant ids are reused across hosts and restarts, and a row can be a frame old. A selection
  keeps the player's key with the id, and every kick, ban or mute checks that key against the current snapshot before it
  fires, after its confirmation. When another player holds the id now, or nobody does, nothing happens and the player is
  told the list changed. A player without a key cannot be moderated.

## 8. Work off the main thread

- **Settings** live in `data/multiplayer-settings.json`: the acknowledged disclosure version, Hide my IP, the game
  last hosted by launch target and world, and favorites and history by server id. A member a reader does not know is
  ignored, so an older game reads the file as before, and a newer one ignores the display name and hosting settings an
  older game wrote there.
  They load and save on `MultiplayerBackgroundQueue`, one write at a time, coalesced; the tab notes a pending write and
  stays responsive. Settings that cannot be read are kept for the session only, so the file is never overwritten with
  defaults.
- **Launcher preferences.** The display name (2 to 24 characters) and the hosting defaults (server name, players,
  visibility and voice) are the launcher's (MultiplayerLaunchContract.md, section 7). The game reads the projection and
  its journal once at start, on `MultiplayerBackgroundQueue`; a change made in game takes effect at once and is recorded
  with `RecordPreferenceChange`, one write at a time, the newest of each preference only, and never kept in manager data
  as well. Without the launcher's channels the values last for the session.
- **Launcher channels.** Launch request V5's channels (`ILauncherChannels`, over `LaunchStagingStore`) can wait for a
  shared file for about 310 ms while they retry, so the interface never calls them on the main thread: preference reads
  and journal writes, handoffs and live joins' records run on `MultiplayerBackgroundQueue`, and live joins are looked for
  once a second on a pool thread. The views show a pending write as they show the settings'. The launcher applies the
  journal while the game is closed (`MultiplayerLaunchContract.md`, section 7), and its join flow takes the
  handoffs.

## 9. Sessions the launcher asks for

`LaunchSessionPresenter` starts what the TopiaForge launcher asks of this game, through the realm as the screens' own
flows start sessions (MultiplayerLaunchContract.md, section 8).

- **A launched session.** The session this game was launched to host or join waits, once the provider planned it, for
  the launch's own game: until its target runs, or its menu loaded. A host then opens in place when its game runs, or
  at the menu when the launch had no game; one whose game stopped before it opened opens nothing. A host whose session
  needs a password asks for it in game, in a masked field; the launcher never stages one, and nothing keeps it. A join
  goes through the connecting overlay, with "Leave current game?" while a game runs, and a LAN session is listened for
  first, for up to 15 seconds.
- **Shown before TopiaForge opens.** A launch reaches the screens before the player opened TopiaForge's overlay, so
  the overlay is built, closed, as soon as a question or a join needs the screen: the password question, "Leave
  current game?" and the connecting overlay show over the game or the menu, as the toasts already do.
- **Its own privacy.** A launched session keeps the privacy the launcher planned for it: neither asks the disclosure
  again, and a launched join never offers the per-session prompt, since only the launcher plans a change to it. Its own
  name and settings stand, and a running game opened in place is named for browsing players as HOST names it.
- **Live joins.** A game the launcher started takes the joins the launcher stages for it. Each is checked against the
  packages this game runs and the service it reaches, planned by the provider, and started as a launched join is; a join
  id seen before is ignored, and one launch starts at a time.
- **Records.** A launched session reports into the runtime's publisher, a live join under its own id: its progress as
  the provider's status moves, its one result, and its session's end. Only a status read after the session began
  speaks for it, as on the connecting overlay; a session still running when a newer launch begins is followed to its
  end; and a live join whose session runs as the game closes ends as the player leaving it.
- **The host's world in the records.** A launched join hands the realm its request as it joins, while its launch still
  waits for its result, and session follow runs the host's world under it (WS3.8): the join's records then carry the
  follow's phase and the host's world session, a success stands at Running with that session, and its end names it. A
  live join's records take the follow's progress through `IMultiplayerSessionEnvironment.SubscribeSessionProgress`. A
  join started in game, and RETRY after a launched join failed, hand no request, and their follow reports nothing.
- **Said in game too.** The player is looking at the game, not the launcher: a launch this game cannot start says why
  in a toast, a launched host whose hosting fails before it opens says so in HOST's words, and a LAN session that is
  looked for says so, and what to do next when it is never heard.
- **Back to the launcher.** A failure the launcher can fix, mods that differ or a server that changed since the launcher
  checked it ("Server no longer matches"), offers the launcher. A launched join goes back as the current session, which
  the launcher plans again.

## 10. Seams for later slices

| Seam | Filled by |
| --- | --- |
| `IMultiplayerVoiceSlot` in the VOICE sub-tab | The voice slice |
| `HostPresenter.PendingPolicies` | The Sandbox host policy |
| `ConnectingPresenter.ReportSynchronization(done, total)` | Unused: session follow (WS3.8) carries the network world's n/m (`NetworkWorld.Progress`) to the Synchronizing step through its own status (`SessionFollowStatus`) |
| The launcher's side of the handoff, live-join and journal channels this game writes and reads | The launcher's join and preference flows (WS6) |

## 11. Tests

- **Presenters** (`MultiplayerSessionsTests`, over the real `MultiplayerRuntime`): every sub-tab's states and
  transitions, the name, server name, player and password bounds, the disclosure's versioning and Hide my IP, the direct
  prompt contacting nothing when cancelled, the realm rules and "Leave current game?", a join asked for during a
  main-menu load waiting on the overlay until the menu has loaded, over a real session orchestrator, leaving never
  refused in any phase, every phase of the stepper, every failure's words and step, cancelling at every phase, retry,
  trust, the launcher hand-off and its links, copied details without an address, players once the host lists them,
  moderation checked against the current snapshot's player key, HOST offering only games the menu can start, BROWSE
  bounding what a host lists, this game's own problem lines dropping addresses a host put in ids and versions, the
  address sweep over every screen, invites and listed servers through the service and nothing without one, the
  relay-unavailable direct prompt and its cancel path, the service's failures as their codes say, a host's listing and
  invite, questions withdrawn once their game or session changes, a premise that cannot be read counting as broken, the
  bounded prompt queue, one prompt at a time with a withdrawn one's modal closed, a provider that throws while hosting
  or a join starts and whose own words never reach the screen, worlds named as players know them in HOST and in what a
  session announces, a refused password asked for again rather than resent, the host's invite code copied without a
  toast repeating it, a named delay waited out at any frame rate and then joined again by itself, every failure saying
  what to do next, an invite's link pasted as its code, the game last hosted offered first, and settled frames, a
  waiting question's included, allocating nothing with mods loaded. Over session follow's status: the follow's steps
  filling the stepper's last three, an arena's two loads, a host at its menu, a join the follow ends showing the
  follow's words at its step with RETRY only where it helps, an earlier join's failure never ending this one,
  "Following the host" after the join with its Cancel leaving, and the follow's notices once each.
- **Launched sessions** (`MultiplayerSessionsTests`, over the recording control): a launched host waiting for its game,
  opened in place or at the menu, its password typed in game, and a stopped game or a missing provider failing at once;
  a launched join under its own privacy and name with no direct prompt, leaving a running game only when told, a LAN
  session listened for, and one launch at a time; live joins checked and acted on once, through the staging store too;
  handoffs to the launcher that owns the game; the preferences as the launcher's journal apply makes them; frames
  with a launch at rest allocating nothing; and each launch's records its own: a join started after a wait settled by
  its own status, a running session followed past a newer launch, a command failing while its launch waits, a live
  join's end as the game closes, and a launch that cannot start saying so in game; a launched join handing the realm
  its request and RETRY after it failed handing none, and a live join's records taking its follow's progress under its
  id, at Running with the host's world session once in session, until they end. Over the real runtime and provider
  (`LaunchedSessionTests`, in the multiplayer harness): a hosted launch on its own port and privacy, a join pinned to
  the descriptor the launcher checked and refused when it differs, re-signed, another server's, or replayed from
  another endpoint, relay-only never going direct, a live join from a running game, a join after an earlier session,
  the handoff, and a preference change reaching the journal, with no channel touched on the frame's thread and no
  address in any record, handoff, toast or log.
- **Records** (`SessionLaunchRecordsTests`): every progress and outcome V2 fixture written byte for byte through the
  runtime's publisher and the staging store; and over the real orchestrator, a launched join whose session follow runs
  the host's world under its request: its own phase while it synchronizes, its success at Running with the host's world
  session, the host's next world moving the phase on, a followed world's end withheld, the provider's end as its
  terminal record, and a later join started in game writing nothing more.
- **The frame boundary** (`MultiplayerFrameGuardTests`): a throwing presenter, prompt stage or tab neither breaks the
  frame nor logs an address, in its words or its stack; each failure is logged once, and the log is bounded.
- **Wording** (`MultiplayerPrivacyTextTests`): the constants against the approved document, parsed.
- **Architecture** (`MultiplayerUiArchitectureTests`): no address-shaped member or network type beyond the typed address,
  kit-only views, Unity-free presenters, plain text for every label of screen text, every toast and every destructive
  confirmation, persistent modals, spacers that never stretch a row's height, going online and leaving only through the
  runtime's realm (read from the presenters' compiled code, so no spelling hides a call to the control), the launcher's
  channels called only on the background queue or the live-join poll (read the same way), the overlay's multiplayer
  stages running through the guard, no logging, and the 400-line cap on every file of the interface and the overlay.
- **Tabs** (`ManagerTabLookupTests`): name-based lookup and the entry points that use it.
