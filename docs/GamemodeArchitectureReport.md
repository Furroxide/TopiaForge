---
title: Gamemode architecture investigation
description: Evidence, root causes, and a pre-release redesign of gamemode discovery, launch, and lifecycle.
---

# Gamemode architecture investigation

Date: 2026-09-03. HEAD at investigation start: `9811ff8f78697f1302760b57918e942613f2fc43`.
Scope: the **current working tree, including existing uncommitted changes**, across manifests,
launcher domain/data/Flutter/CLI, the manager, Worlds, SDK contracts, first-party consumers,
templates, and tests. This investigation changes only this report.

## Assessment

The problems are architectural, with concrete user-visible failures. TopiaForge has no single
definition of a launchable gamemode and no authoritative transaction that takes it from selection
to a running session. Metadata, executable behavior, world selection, menu entries, scene loading,
and controller lifetime are connected through IDs, fallback rules, and event subscriptions that
different layers interpret differently.

Isolated probes reproduced all of the following:

- A profile enabling zero mods still offers gamemodes from an installed version and a newer,
  uninstalled registry version.
- Disabling a provider leaves its previously published mode and menu entry available.
- Launch preflight accepts a gamemode whose provider is absent from the effective profile.
- Two hosts can activate two controllers for the same gamemode.
- A controller factory can fail while the public load reports success; a later subscriber can
  even start a controller after the session has already ended.
- A failed factory can leave an update subscription alive until the mod itself unloads.

Source tracing also confirms contradictory launch-intent precedence, inconsistent world routing,
premature asynchronous success, and teardown paths that can skip the terminal session event.
The Unity-dependent scenarios were not exercised in the running game; their timing-dependent
visual consequences remain live acceptance work.

**Recommendation:** replace the current launch and session contract before release. A coordinated
manifest/SDK/profile change is justified. Incremental patches can contain individual failures,
but preserving the present split contract would keep producing the same classes of defect.

## What the current uncommitted work already improves

The working tree already introduces a one-shot `WorldLaunchIntent` in the launch profile, moves
remembered in-game launch settings into manager state, arms startup launch after mod loading,
adds a Home gamemode picker, and publishes menu entries through a dirty catalog writer.
Those are useful changes. This report does **not** count the old flat writes into the Worlds
config envelope, or the old single snapshot before later mods register, as remaining defects.
The findings below concern the current implementation after those changes.

Sources: [launch intent](../src/TopiaForge.ModManager.Core/WorldLaunchIntent.cs#L8),
[startup launch](../src/TopiaForge.ModManager/TopiaForgeModManagerPlugin.Gamemodes.cs#L47),
[catalog publication](../mods/TopiaForge.Worlds/WorldsService.Catalog.cs#L77).

## Findings

P1 means a correctness issue to resolve before release. P2 means an important contract or
authoring defect to resolve before treating the API as stable. “Probed” means an isolated
executable reproduction; “traced” means confirmed from the source path, without a live-game run.

| ID | Priority | Finding | Evidence |
| --- | --- | --- | --- |
| GM-01 | P1 | No canonical launch target or exclusive implementation owner | Probed and traced |
| GM-02 | P1 | Catalog and launch preflight disagree with the effective profile | Probed and traced |
| GM-03 | P1 | “None,” unavailable selections, and startup defaults have contradictory meanings | Probed serialization; traced integration |
| GM-04 | P1 | Selected world and load mode do not reliably identify the content loaded | Traced |
| GM-05 | P1 | Launch reports success before readiness, including after synchronous startup failure | Probed and traced |
| GM-06 | P1 | Failed startup has no session resource scope to clean up | Probed |
| GM-07 | P1 | A throwing content disposer can permanently interrupt teardown | Traced |
| GM-08 | P2 | Scene updates are interpreted as new gamemode sessions | Traced |
| GM-09 | P1 | Separate scene-loading paths can admit competing transitions | Traced; coordinator tests pass |
| GM-10 | P2 | The advertised custom-world spawn contract is not implemented | Traced |

### GM-01 — No canonical launch target or exclusive implementation owner

There are three separate descriptions of the same apparent feature:

1. Manifest `worldGamemodes`: ID, name, description only.
2. Runtime `GamemodeDefinition`: the same display metadata, with no behavior binding.
3. Runtime `GamemodeMenuEntry`: an independent ID that pairs a mode with an optional world.

The manifest cannot express the launch entry, its default world, supported world combinations,
or the binding to its controller. Runtime registrations do not establish that manifest metadata
and executable behavior agree. `GamemodeHost.Create` can omit registration entirely and subscribe
to an existing mode. Two such hosts both succeed and run simultaneously. The Worlds owner facade
receives `ownerModId` but discards it, and forwards `EndSession` to a global operation.
This is an accidental cross-feature ownership problem, not a claim of sandbox isolation.

The shipped Sandbox illustrates the ambiguity: Worlds publishes the Sandbox mode, while the
Sandbox mod attaches its controller to that mode. The definition can remain registered when
the actual Sandbox controller is absent or fails to initialize. The world template publishes
additional menu entries using that same mode. A mode therefore need not identify one
implementation or one launch destination.

This becomes visible before the first game run. Home finds a mode's world through
`menuEntryFor`, which returns the first matching entry. If no runtime entry has been published,
it retains the profile's previous world. Zombies declares its mode in the manifest but supplies
its desired world from runtime configuration. Two custom-world entries sharing Sandbox collapse
to one Home mode choice whose default depends on registration order.

**Reproduce:** attach two hosts to one registered mode and load it; both become active. Separately,
install a mode before its first runtime catalog publication and select it in a profile retaining
another world; the launcher carries that previous world into the launch.

**Change:** make launch targets explicit, and bind each executable gamemode to exactly one owned
factory. Model optional session extensions separately from gamemode implementations. Derive IDs
and descriptors from one declaration instead of repeating loosely related strings.

Sources: [manifest shape](../schemas/topiaforge.mod.v5.schema.json#L191),
[SDK registrations](../src/TopiaForge.Mods.Abstractions/Worlds.cs#L36),
[optional host registration](../src/TopiaForge.Mods.Abstractions/GamemodeHost.cs#L121),
[discarded owner](../mods/TopiaForge.Worlds/WorldsService.OwnerFacade.cs#L12),
[Sandbox initialization and attachment](../mods/TopiaForge.Sandbox/SandboxMod.cs#L41),
[Home world inference](../apps/topiaforge_launcher_flutter/lib/src/launcher_bloc_actions.dart#L83),
[first matching entry](../packages/launcher_domain/lib/src/models/world_models.dart#L245),
[Zombies runtime target](../mods/TopiaForge.Zombies/ZombiesMod.cs#L66).

### GM-02 — Catalog and preflight disagree with the effective profile

The launcher reads one historical `catalog.json` per game installation, preserves its worlds,
modes, and entries, and adds manifest modes from globally enabled installed mods. It also adds
modes from registry manifests by matching package ID alone. It does not require the registry
version to match installed bytes, or resolve the catalog from the selected profile's exact
enabled set and version pins. Conversely, a profile-enabled but globally disabled provider can
be absent from cold-start discovery.

The file's publisher describes it as a best-effort diagnostic catalog, yet the launcher uses it
for launch choices. It has no provider/version/profile provenance or generation to establish
freshness. The runtime also marks additions dirty but never marks registration removals dirty.
Removing a world or mode leaves referencing menu entries intact.

Launch preflight resolves packages and dependencies but does not validate `worldLaunch` against
that result. Safe ID syntax is accepted even when Worlds and the requested mode's provider will
not load. Runtime detection occurs after starting the game and generally becomes a log warning.

**Reproduced:** only `mode.mod@1.0.0` was installed, `2.0.0` existed in a local registry, and the
selected exact profile enabled zero mods. The catalog offered both versions' modes. Another
probe disabled a provider and retained its cached mode and entry. A third launched an exact
empty profile with that mode: the fake process starter was called and the repository returned
`started:true` with the contradictory configuration.

**Change:** build availability from the effective profile and exact package identities. Keep
registry discovery separate from installed launchability. Treat runtime snapshots as scoped,
revisioned observations. Validate the requested target against the resolved profile before
spawning the game, and revalidate its implementation binding inside the runtime.

Sources: [catalog assembly](../packages/launcher_data/lib/src/local_launcher_repository/storage_helpers.dart#L223),
[unversioned registry merge](../packages/launcher_data/lib/src/local_launcher_repository/storage_helpers.dart#L273),
[profile preflight](../packages/launcher_data/lib/src/local_launcher_repository/profile_launch_helpers.dart#L15),
[registration removal](../mods/TopiaForge.Worlds/WorldsService.Registrations.cs#L114),
[diagnostic-only publisher contract](../mods/TopiaForge.Worlds/WorldsService.Catalog.cs#L91).

### GM-03 — “None,” unavailable selections, and startup defaults conflict

Selecting None in Home or CLI sets `launchIntoGamemode:false`. Serialization then omits
`worldLaunch`. The manager interprets a null intent as permission to use its remembered
`AutoLoadOnStart` selection, even when a valid launcher profile explicitly chose normal play.
“No launcher command” and “the launcher requested normal play” are indistinguishable.

A different path produces the opposite presentation problem. If a saved selected mode is absent
from the catalog, Home displays None, but does not clear or invalidate the stored launch flag.
Launch still serializes the hidden mode request. Setup adds another interpretation: changing
its gamemode dropdown preserves the existing startup flag and world, while Home changes the
flag and attempts to choose a mode-specific world.

**Reproduce:** enable manager autoload, then launch a profile explicitly set to None; startup
falls back to the remembered mode. Separately, remove a selected mode from a snapshot while
retaining the profile; Home appears to select None while the launch payload still requests it.
The None wire omission was reproduced; manager precedence and UI divergence were source-traced.

**Change:** use an explicit command such as `main-menu` or `launch-target`. Apply manager defaults
only when there was no launcher command at all. Preserve unavailable saved selections as visible
errors. Home, Setup, CLI, and the repository must use one domain selection/planning operation.

**Status:** the wire half is done. `worldLaunch` now always carries `command`, and the manager
applies its remembered selection only when a run supplied no profile at all, so Home's None and
`--gamemode none` reach the ordinary menu. The rest of this finding stands: an unavailable saved
selection is still shown as None rather than as an error, and Home and Setup still interpret the
same dropdown differently.

Sources: [None selection](../apps/topiaforge_launcher_flutter/lib/src/launcher_bloc_actions.dart#L80),
[wire omission](../packages/launcher_domain/lib/src/models/profile_models.dart#L226),
[manager fallback](../src/TopiaForge.ModManager/TopiaForgeModManagerPlugin.Gamemodes.cs#L54),
[missing-mode display](../apps/topiaforge_launcher_flutter/lib/src/screens/home_launch_pane.dart#L174),
[Setup selection](../apps/topiaforge_launcher_flutter/lib/src/screens/setup_screen.dart#L249).

### GM-04 — Requested world and load mode do not identify actual content

Routing disagrees across entry points. The in-game card UI ignores each entry's `WorldId` and
launches a global selected world. The service entry API uses the entry world but falls back to
the first checkpoint or Open Sandbox if it is missing. Startup has another rule: use an explicit
world if registered, otherwise infer one from the first matching mode entry. There is no declared
world/gamemode compatibility check beyond both IDs existing.

The runtime further changes the meaning of those choices:

- Any non-custom world paired with the Sandbox mode is rerouted to Open Sandbox. The manager
  still saves and describes the requested world.
- The generic additive route builds the generated arena in the currently active scene. It does
  not materialize the selected world and has no gameplay-scene guard. A mode can therefore start
  over MainMenu through this route. The special Open Sandbox fallback has a guard, but this path
  bypasses it.
- Open Sandbox advertises additive support while its normal route starts a replacement play
  scene. The boolean capabilities do not consistently describe the engine operation or content.

**Reproduce:** publish two entries for one mode with different worlds; both in-game cards use
the global world. Register an ordinary additive-capable world and launch a non-Sandbox mode
from MainMenu with additive selected; control flow builds the generic arena over that scene.

**Change:** use a stable launch-target ID and an explicit world-provider plan. Distinguish world
content, gamemode rules, engine transition strategy, and supported combinations. Preserve an
explicit world override; reject an unavailable required world instead of silently substituting.
Keep scene mechanics out of ordinary selection unless users have a meaningful supported choice.

Sources: [in-game card action](../src/TopiaForge.ModManager/Overlay/GamemodesTab.cs#L95),
[startup router](../src/TopiaForge.ModManager/WorldLaunchRouter.cs#L51),
[runtime route selection](../mods/TopiaForge.Worlds/WorldsService.Loading.cs#L139),
[generic additive path](../mods/TopiaForge.Worlds/WorldsService.Loading.cs#L222),
[entry fallback](../mods/TopiaForge.Worlds/WorldsService.Loading.cs#L396),
[requested-world reporting](../src/TopiaForge.ModManager/TopiaForgeModManagerPlugin.Gamemodes.cs#L240).

### GM-05 — Success precedes readiness, and can follow startup failure

`LoadAsync` wraps synchronous dispatch in `Task.FromResult`. `StartSession` immediately sets
`CurrentSession`, broadcasts `SessionChanged`, logs success, and returns a successful session.
On asynchronous scene routes, success can precede scene arrival and even the start of custom
content creation, which is triggered by the scene callback. A later scene failure ends the
provisional session and logs a warning, after callers have received success and the overlay has
closed.

Zombies compensates with its own `WaitingForWorld` phase, checking the scene name, RobotKit,
and local player. Those checks cannot establish completion of custom content, particularly
when successive worlds use the same native play scene. Readiness has leaked into consumers.

Custom placement failure or timeout instead builds a generated arena while retaining the failed
custom world's identity. That fallback is not negotiated with the gamemode, which may require
objects or geometry only present in the requested content. The caller's cancellation token is
checked only before the synchronous dispatch; it does not represent the subsequent launch.

There is also a synchronous failure: a throwing host factory calls `EndSession`, but the ongoing
start-event multicast continues and the public load still returns success. A later host can
start after the terminal event. This was reproduced with the real `GamemodeHost` and the fake
world service, then checked against the production service's identical unconditional success
after event dispatch. A null-returning factory also leaves a successful session with no controller.

**Change:** separate request acceptance, loading, readiness, and terminal outcome. Finish
`LaunchAsync` only after world content, required spawn state, and gamemode startup succeed.
Publish committed lifecycle events after the operation determines its result. Use operation and
session identities to reject stale/reentrant completions. Fallback must be declared and reflected
in the actual resulting session; cancellation must cover the operation, with native retirement
handled separately when the engine cannot cancel a dispatched load.

Sources: [immediate task completion](../mods/TopiaForge.Worlds/WorldsService.Loading.cs#L15),
[unconditional success after callbacks](../mods/TopiaForge.Worlds/WorldsService.Sessions.cs#L440),
[deferred content creation](../mods/TopiaForge.Worlds/WorldsService.Sessions.cs#L272),
[silent content substitution](../mods/TopiaForge.Worlds/WorldsService.Sessions.cs#L390),
[consumer readiness heuristic](../mods/TopiaForge.Zombies/ZombiesController.Loop.cs#L122),
[factory failure](../src/TopiaForge.Mods.Abstractions/GamemodeHost.cs#L232).

### GM-06 — Failed startup has no session resource scope

`GamemodeHost` receives `Func<WorldSession,TController>`. It tracks the controller on the mod
lifetime only after the factory returns. If construction acquires subscriptions or leases and
then throws, the host has no controller reference to dispose. Its `created?.Dispose()` cannot
dispose an object whose constructor never returned. Mod-lifetime tracking can eventually clean
resources up, but does not provide cleanup at the failed session boundary.

**Reproduced:** a factory acquired an update subscription under the mod lifetime and then threw.
The session ended and the host had no controller, but one update subscription remained alive.
The SDK documentation's promise to dispose a partially built controller is stronger than this
factory contract can guarantee. Repeated failed attempts can accumulate session work in a
still-loaded mod.

Zombies already performs explicit rollback of partially created HUD, input, shop, conversation,
and update resources in its constructor. That protects that consumer, but demonstrates how
much cleanup responsibility remains outside the purported session host.

**Change:** allocate a child session lifetime before invoking any factory and pass it through a
session context. All session resources belong to that scope, including resources acquired before
the controller exists. Bind the session context's event, input, UI, and extension service facades
to that scope; merely exposing a child lifetime while services still acquire under the mod
lifetime would preserve the leak. Dispose it on failed start, stop, supersession, and owner unload. Keep
application/mod registrations and session resources as separate ownership categories.

Sources: [factory contract](../src/TopiaForge.Mods.Abstractions/GamemodeHost.cs#L80),
[post-construction tracking and cleanup](../src/TopiaForge.Mods.Abstractions/GamemodeHost.cs#L232),
[Zombies rollback](../mods/TopiaForge.Zombies/ZombiesController.cs#L109).

### GM-07 — A cleanup exception can interrupt teardown permanently

`EndSession` clears `CurrentSession` and then calls `UnloadArena` before notifying consumers.
`UnloadArena` directly invokes mod-provided `IWorldContent.Dispose()` without exception isolation
or a `finally` that completes the remaining work. If it throws, arena/environment cleanup and
`SessionEnded` are skipped. Retrying `EndSession` sees null and returns without completing them.
The host can remain active despite the provider reporting no current session.

Provider disposal calls `EndSession` before marking itself disposed or removing its native scene
subscriptions, so the same extension failure can also interrupt provider shutdown.

**Reproduce:** a custom-world content object whose `Dispose` throws, followed by EndSession,
another mode launch, or provider unload. This finding is confirmed by control flow; it was not
injected into a running Unity process during the audit.

**Change:** perform teardown through an owned scope that attempts every action, aggregates
failures, and always publishes exactly one terminal result. Clear owned references before
calling extension cleanup. A consumer failure must not prevent provider hooks, scene claims,
and other consumers' resources from being released.

Sources: [unsafe content disposal](../mods/TopiaForge.Worlds/WorldsService.Sessions.cs#L13),
[clear-before-notify sequence](../mods/TopiaForge.Worlds/WorldsService.Sessions.cs#L43),
[provider shutdown](../mods/TopiaForge.Worlds/WorldsService.Sessions.cs#L76).

### GM-08 — Scene updates are interpreted as new gamemode sessions

When a gameplay scene changes, Worlds constructs a new `WorldSession` carrying the previous
world ID, mode ID, and start time, then publishes `SessionChanged`. `GamemodeHost` treats every
matching notification as stop-and-create. A scene rebinding therefore disposes the round
controller and creates another one even though the provider represents it as the same session.
An active additive gameplay scene can reach this path as well as a native single-scene change.

There is no explicit session ID, no separate scene-update event, and no contract saying whether
the transition continues the current session or ends it. A new session of the same mode and a
scene update within its existing session both take the same stop-and-create path.

**Change:** give each session a unique identity and explicit lifecycle. Separate updates to a
running session from beginning another session. Define whether a native world change is allowed
within a mode; preserve its controller if so, or end it explicitly if not. Do not infer that
decision from an untyped `SessionChanged` notification.

Sources: [scene rebinding](../mods/TopiaForge.Worlds/WorldsService.Sessions.cs#L187),
[unconditional controller replacement](../src/TopiaForge.Mods.Abstractions/GamemodeHost.cs#L216),
[session shape](../src/TopiaForge.Mods.Abstractions/Worlds.cs#L390).

### GM-09 — Separate scene-loading paths admit competing transitions

Worlds serializes its own loads with `SceneTransitionTracker`. Core `Context.Scenes.LoadAsync`
uses a separate `UnitySceneBackend.activeLoad`. Their shared `SceneCoordinator` refuses an
existing claim only for automatic requests; another user-initiated request simply adds a new
claim. Its “superseding” message does not cancel or retire the old operation.

**Reproduce:** dispatch a Worlds launch, then call the core scene API before its native scene
arrives. The core API requests a user-initiated claim and its backend has no Worlds operation
recorded, so both paths can dispatch. The conflicting admission is definite; which scene wins
and how it appears to the player depends on native timing.

**Change:** use one transition executor for framework-originated world and scene loads. Admission
must operate on actual in-flight operations, with explicit priority, ownership, completion,
and retirement. Continue observing native game transitions, but distinguish them from approved
framework operations. An uncancellable engine operation cannot safely be “superseded” by adding
another claim.

Sources: [claim admission](../src/TopiaForge.ModManager/SceneCoordinator.cs#L74),
[core scene request](../src/TopiaForge.ModManager/UnitySceneService.cs#L68),
[separate core load guard](../src/TopiaForge.ModManager/UnitySceneService.cs#L258),
[Worlds load guard](../mods/TopiaForge.Worlds/WorldsService.Loading.cs#L118).

### GM-10 — The custom-world spawn contract is not implemented

`CustomWorldOptions.SpawnPointName` promises an authored descendant marking the player spawn.
The world template supplies it, and the Unity authoring validator requires a `SpawnPoint` marker.
The runtime never reads the option; placement and player guards use the native sandbox spawn.
An authored map can pass validation while the player starts outside its intended playable area.

**Change:** have world creation return resolved spawn data through the world-instance contract,
or implement the documented marker lookup through the appropriate adapter. Validate required
spawn readiness before the gamemode starts. Remove an unsupported option instead of publishing
a contract that cannot affect behavior.

Sources: [spawn option](../src/TopiaForge.Mods.Abstractions/CustomWorlds.cs#L49),
[authoring validation](../templates/TopiaForge.UnityWorldTemplate/Packages/io.github.furroxide.topiaforge.world-companion/Editor/WorldValidator.cs#L62),
[actual runtime spawn](../mods/TopiaForge.Worlds/WorldsService.Sessions.cs#L272),
[player guard placement](../mods/TopiaForge.Worlds/WorldsService.Sessions.cs#L373).

## Root causes

The implementation conflates concepts that have different owners and lifetimes:

| Concept | Current substitute | Consequence |
| --- | --- | --- |
| Launchable target | A mode ID plus inferred menu/world state | No stable, portable description of what Launch means |
| Provider availability | Global enablement plus cached and registry metadata | Offered targets do not match the profile that will execute |
| Mode implementation | A subscriber to a global event | No exclusive owner or transactional start result |
| Running session | A snapshot published when scene dispatch begins | Success precedes readiness and later failure cannot change the result |
| Session lifetime | A controller eventually tracked under the mod lifetime | Partial startup and cleanup failures escape the session boundary |
| Scene operation ownership | Claims and independent loading trackers | Each subsystem appears serialized while the combined system is not |

The useful local safeguards—dirty flags, debounce, scene quarantine, owner-bound leases,
fallbacks, and `GamemodeHost`—address pieces of these problems. They cannot establish invariants
that the shared contract does not express. Keeping the public surface additive encourages more
coordination flags and compatibility behavior around that missing contract.

## Recommended pre-release architecture

### 1. Declare worlds, gamemodes, and launch targets separately

Introduce a new manifest contract with three distinct concepts:

- **World declaration:** stable identity, owning package, content/provider binding, and semantic
  capabilities such as required spawn support. Dynamic local-world discovery remains a separate
  typed provider path with provenance.
- **Gamemode declaration:** stable identity, owning package, exactly one implementation binding,
  compatible world requirements, and typed launch options.
- **Launch target:** stable user-facing identity, a gamemode reference, its default world or an
  explicit world-selection policy, permitted overrides, and option defaults.

The declaration's package supplies ownership; cross-package references must resolve through
declared dependencies. Static validation checks references against the exact package graph.
Runtime startup checks that declared implementations bind successfully. Generated constants or
binding helpers should avoid manually duplicating IDs and descriptions in manifest and C# code.

Menus, Home, Setup, and CLI select the same launch target. A target can allow compatible world
overrides without pretending every world and every mode work together. Mode-specific world
configuration must be an explicit launch option or declaration input, not private configuration
that only a previous game process can reveal.

Separate Sandbox's creator gameplay from Worlds infrastructure. Give the creator gamemode its
own explicit implementation owner. If a neutral free-play mode is needed for arbitrary world
packages, define its behavior and ownership explicitly rather than making it synonymous with an
optional creator workbench attached to a provider-owned ID.

### 2. Resolve one explicit launch plan

In the framework-independent launcher domain, resolve the requested target against the effective
profile's exact packages, dependencies, world compatibility, and launch options. Return a plan
or structured blocking reasons. Flutter Bloc actions and CLI commands call this operation;
the repository enforces it before process creation.

Persist an explicit startup command: `main-menu` or `launch-target`. The absence of a launcher
command can permit a separately defined direct-game-start preference. An explicit main-menu
command must always win over remembered autoload settings.

Carry a request ID, target identity, resolved package identities, and options over the existing
one-shot launch channel. The runtime revalidates the plan against loaded/bound providers. Use
shared conformance fixtures across Dart and C# for serialization and resolution policy; their
different runtimes do not justify divergent semantics.

A runtime catalog becomes an observation with schema version, provider/package identity,
profile identity, revision, and availability reasons. Static installed declarations support
first launch. Runtime observations can confirm or enrich them, but cannot resurrect disabled
packages or turn registry-only content into installed content. Publish registration/removal
atomically and invalidate all dependent targets.

### 3. Own execution through a session state machine

Use explicit phases: `Idle -> Preparing -> LoadingWorld -> StartingMode -> Running -> Stopping`.
Failure and cancellation produce one terminal operation outcome. Create a unique session ID
and resource scope before invoking any provider or gamemode startup work.

The orchestrator invokes one gamemode factory with a session context containing the world
instance, session lifetime, cancellation, and controlled session operations. Observers receive
notifications after transitions commit; they do not implement the authoritative startup protocol.
Optional session extensions have their own registrations and cannot accidentally become a
second gamemode controller. Stop/restart handles identify the session they own, so a stale
callback cannot end a newer session.

World adapters own native scenes, content placement, spawn readiness, and engine-specific work.
Keep Unity-free state/plan logic outside those adapters and preserve the existing Core/Unity
boundary. Route framework scene requests through one transition executor. Reuse the valuable
late-arrival/quarantine logic behind that executor rather than maintaining independent guards.

`LaunchAsync` succeeds only when the session reaches Running. Expose progress and a structured
outcome for manager UI, logs, and launcher diagnostics; process-start success remains a separate
fact. Validate before replacing an existing scene. Where an engine operation cannot roll back,
report failure and use an explicit recovery path to a known state; do not promise transactional
restoration that Unity cannot provide.

Teardown must cancel session work, stop controllers, release world/resources/claims, and deliver
one terminal notification even if individual cleanup actions throw. Aggregate errors while
continuing cleanup. Scene updates within a session must not recreate its controller implicitly.

### 4. Use the pre-release window to replace the contract cleanly

The repository already permits breaking API changes across 0.x minor releases, but it also
contains stronger obligations to retain V5 readers indefinitely and keep contract assembly
identity frozen throughout 0.x. Those are policy choices, not technical necessities for this
investigation. They should be reconsidered before committing to a redesigned public surface.
See [Compatibility policy](CompatibilityPolicy.md#safe-sdk-packages) and its serialized-state
section.

Recommended migration posture:

1. Introduce a new manifest schema and a coordinated pre-release SDK version. Do not change V5
   field meanings in place. Migrate first-party packages and templates together.
2. Provide a one-time CLI migration where old metadata is sufficient. Require authors to supply
   missing world policy and implementation bindings; do not invent them from the first menu entry.
3. Retire the old ambiguous launch API instead of maintaining two runtime protocols. Rebuild
   first-party consumers, regenerate API baselines, and fail old incompatible packages clearly.
   Decide assembly/version identity deliberately for the new contract.
4. Version profile/manager state changes. Preserve explicit target choices when resolvable;
   otherwise retain them as unavailable with a repair path. Reset untrustworthy diagnostic caches.
5. Update compatibility documentation and authoring guidance to match the actual pre-release
   support promise. A source migration tool does not require permanent support for old binaries.

There is no need to discard `.topiaforgemod`, package dependency ordering, profiles, the package
inbox, logging, enable/disable behavior, or restart-required semantics. The breaking work should
target the gamemode contract and execution model that currently cannot enforce correctness.

## Verification and gaps

Existing focused checks run during the investigation:

```powershell
dotnet run --project tests\TopiaForge.ModManager.Tests\TopiaForge.ModManager.Tests.csproj -c Release --no-restore -- --testing-kit
dotnet run --project tests\TopiaForge.ModManager.Tests\TopiaForge.ModManager.Tests.csproj -c Release --no-build --no-restore -- --zombies-controller
dotnet run --project tests\TopiaForge.ModManager.Tests\TopiaForge.ModManager.Tests.csproj -c Release --no-build --no-restore -- --scene-coordinator
```

All passed. The SDK probe used the freshly built SDK/testing assemblies. The Dart probe used
the current launcher domain/data source, synthetic installed packages and a local registry,
temporary fixture directories, and an injected fake process starter. No real game process or
remote registry was used by that probe. Probe harnesses were temporary investigation aids;
they are not additions to the repository's regression suite.

| Isolated reproduction | Observed result |
| --- | --- |
| Attach two hosts to one registered mode | Both registrations succeed; both controllers active |
| Factory acquires update subscription, then throws | Load succeeds; no current session; one subscription remains |
| Factory returns null | Load succeeds; current session exists; no active controller |
| Earlier start handler ends session; later host receives event | Load succeeds; no current session; later host active |
| Attach a failing host to an already-running session | Host creation succeeds although replay ends the session |
| Exact empty profile; v1 installed; v2 only in registry | Catalog offers Sandbox, v1's mode, and v2's mode |
| Disable provider after catalog publication | Disabled provider's cached mode and entry remain offered |
| Request mode absent from exact effective profile | Fake process starter called; repository reports started |
| Explicit None serialization | `worldLaunch` key absent, enabling the manager fallback in GM-03 |

The fake world's event and success behavior was compared with production `StartSession`; that
supports the synchronous SDK findings. It does not establish live Unity scene timing. The
test project compiles selected pure Worlds helpers, not the production `WorldsService` as a
Unity-backed integration target. Zombies lifecycle tests prepare an already-loaded fake scene.
Existing launcher tests cover positive metadata merging and picker persistence. Passing these
tests does not verify a cold launcher-to-world-to-controller lifecycle.

The fake also differs from production on mode removal: disposing its registration removes the
definition without ending an active session, while Worlds ends that session. Shared behavioral
contract tests should exercise the same lifecycle cases against the fake and the production
orchestrator, so a convenient fake cannot silently define a weaker contract.

Sources: [test composition](../tests/TopiaForge.ModManager.Tests/TopiaForge.ModManager.Tests.csproj#L156),
[Zombies test setup](../tests/TopiaForge.ModManager.Tests/ZombiesControllerTests.Lifecycle.cs#L74),
[fake registration removal](../src/TopiaForge.Mods.Testing/FakeWorldGamemodeService.cs#L403),
[picker tests](../apps/topiaforge_launcher_flutter/test/widget_test.dart#L147).

No full solution/release build or full Flutter/Dart test matrix was run: no production code was
changed. No live-game claim is made for scene races, custom-content failure placement, teardown
fault injection, or spawn position. Those require the targeted acceptance cases below.

## Implementation order and acceptance gates

1. **Define and test the new contract.** Write shared fixtures for manifest references, target
   resolution, exact profile versions, and explicit main-menu intent. Resolve Sandbox ownership
   and world compatibility semantics before adding more picker behavior.
2. **Build the Unity-free session orchestrator and adapters.** Drive real production state-machine
   logic with controllable scene/content backends. Cover delayed readiness, failures, cancellation,
   reentrancy, throwing cleanup, owner unload, and late completion from a superseded operation.
3. **Migrate first-party implementations.** Port Worlds, Sandbox, Zombies, custom-world templates,
   test fakes, authoring validation, SDK docs, and baselines to owned declarations and session scopes.
4. **Unify launch surfaces and diagnostics.** Route Home, Setup, CLI, manager cards, and direct
   startup through the same plan semantics. Make missing targets actionable and preserve explicit
   choices. Remove historical-cache and unrelated-world fallback authority.
5. **Run the full relevant repository gates and live acceptance.** In a fresh installation, select
   a mode before any catalog exists; launch normal play with remembered autoload enabled; switch
   profiles/versions; disable/uninstall providers; launch two worlds sharing one mode; fail content
   loading and cleanup; cancel during native loading; repeatedly enter/exit; change gameplay
   scenes without resetting a continuing round; and verify authored spawn placement.

The release invariant should be simple to state and test: **the target shown to the player is
the target validated for their effective profile, and launch succeeds exactly when one owned
gamemode session is running on its declared world. Every failed or ended attempt releases its
session resources and produces one truthful terminal outcome.**

## Comparison with MTA:SA

Researched against official documentation and source on 2026-09-03.

MTA provides a useful reference because gamemodes, maps, and utility scripts share one resource
system. Resources have executable contents, dependency includes, exports, and start/stop state.
Each resource has its own Lua VM, and multiple resources can run concurrently. A gamemode is a
resource with an orchestration role; it does not require a second plugin mechanism.
[Official resource model](https://wiki.multitheftauto.com/wiki/Resources).

The bundled mapmanager identifies gamemode/map roles from metadata and manages a current pair.
Maps declare compatible gamemode resource names. Its normal switch path stops the current mode
and its map, or changes just the map; it does not stop unrelated utility resources. Dependencies
and resource-authored stop handlers can still affect other resources.
[Mapmanager contract](https://wiki.multitheftauto.com/wiki/Resource:Mapmanager),
[metadata](https://wiki.multitheftauto.com/wiki/Meta.xml),
[switch implementation](https://github.com/multitheftauto/mtasa-resources/blob/master/%5Bmanagers%5D/mapmanager/mapmanager_main.lua).

| Concern | MTA:SA | TopiaForge in this audit |
| --- | --- | --- |
| Shared execution model | Gamemodes, maps, and ordinary scripts are resources | Packages share a mod loader, but gamemode behavior is attached through a separate registry/event convention |
| Persistent utilities | Unrelated running resources survive a managed mode switch | Mod lifetimes exist, but their relationship to mode/map activation is not explicit |
| Mode identity | The selected gamemode names an executable resource | A mode ID can describe metadata with no controller, or multiple attached controllers |
| World compatibility | Maps declare supported gamemodes | Existing world and mode IDs can be paired without a semantic compatibility contract |
| Map changes | A different map resource can run under the same gamemode | Scene updates and new sessions can take the same controller replacement path |
| Ownership | Resource stop destroys its owned elements and VM state | Mod ownership is useful, but session startup and map teardown have weaker guarantees |

The cleanup boundary is particularly relevant. MTA's engine stops resource items, destroys its
element group and VM, and removes the VM's event handlers and keybinds; the VM destructor also
releases its timer manager. This supplies a lifecycle boundary below individual gamemode scripts.
[Resource cleanup](https://github.com/multitheftauto/mtasa-blue/blob/master/Server/mods/deathmatch/logic/CResource.cpp#L1181),
[VM cleanup](https://github.com/multitheftauto/mtasa-blue/blob/master/Server/mods/deathmatch/logic/lua/CLuaMain.cpp#L85).

That does not make every combination of scripts compatible, nor does it make mapmanager a fully
transactional readiness protocol. Its implementation uses deferred starts and publishes custom
start events before updating its tracked current resource. The one-pair rule is manager policy.
MTA's own element-tree documentation also warns that map-specific elements created by a
continuing gamemode still need cleanup when the map changes.
[Mapmanager lifecycle](https://github.com/multitheftauto/mtasa-resources/blob/master/%5Bmanagers%5D/mapmanager/mapmanager_main.lua),
[element ownership caveat](https://wiki.multitheftauto.com/wiki/Element_tree).

### Refinement to the proposed TopiaForge design

The architectural inference is to **unify activation and ownership**, while giving persistent
mods, gamemodes, and maps different lifetimes within the same system:

1. A package declares executable contributions and their roles. A gamemode remains a mod
   contribution with an exclusive activation policy. Services and ordinary mods use the same
   lifecycle machinery, dependency resolution, diagnostics, and scoped resource tracking.
2. Separate installation and eligibility from activation. A profile selects exact packages and
   enabled ordinary mods; a launch plan activates one selected gamemode and its chosen world.
   Other installed gamemodes may be registered without running gameplay code.
3. Persistent mod/service scopes survive a mode switch. Mode scopes survive compatible map
   changes. Map/round scopes release map-specific entities, callbacks, and settings. Shared
   dependencies remain alive while still required.
4. Use explicit contracts for collaboration: world capabilities, mode compatibility, extension
   services, and ownership of shared state. Metadata labels alone cannot make conflicting
   gameplay modifications compose correctly.
5. Treat normal Robotopia play as an explicit launch policy with ordinary mods active and no
   custom gamemode selected. It must not trigger remembered mode activation accidentally.

For example, a profile could run persistent performance/HUD mods and shared RobotKit/Chronos
services while a Zombies session owns its combat rules and a particular arena owns its map
content. Changing arenas should replace map-scoped work; changing to Sandbox should replace
mode-scoped work; unrelated persistent contributions should remain active. This is a proposed
TopiaForge behavior, not a claim about the current implementation.

We should retain C# contracts and existing package infrastructure. MTA can recreate per-resource
Lua VMs; TopiaForge's Mono/BepInEx boundary requires stopping owned behavior independently of
whether assembly bytes remain loaded. Robotopia's native campaign, scene bootstrap, and custom
content still require explicit adapters and readiness checks. MTA itself supplies a game
framework with minimal sandbox-style base gameplay, so its resource model does not directly solve
those Robotopia integration problems.
[TopiaForge runtime constraints](CompatibilityPolicy.md#safe-sdk-packages),
[MTA engine scope](https://github.com/multitheftauto/mtasa-blue#gameplay-content).
