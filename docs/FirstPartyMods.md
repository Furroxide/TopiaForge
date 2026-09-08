# First-party mod catalog and acceptance flows

All first-party mods use the same manifest/package contracts available to community authors. Provider implementations
may use native APIs internally, while safe consumers receive only owner-bound contracts. Candidate acceptance must
validate all fourteen source mods under `mods/`. Normal `pack --all` output contains thirteen first-party mod
packages for the release payload. UiGallery is the sole manifest with `category: "DevTool"`; it remains a required
source QA surface and is excluded unless `--include-dev-mods` is explicitly supplied. Free Play is a declaration
inside Worlds, not a separate source mod or package.

Every manifest is schema version 6 and declares AGPL-3.0-or-later with a package-relative `LICENSE`. Robotopia
compatibility follows the per-mod rule in [the compatibility policy](CompatibilityPolicy.md): mods with
native bindings pin `0.0.2409`, and SDK-only mods declare `>=0.0.2409 <0.0.2600`. The loader and SDK are
constrained to `>=0.1.0-rc.1 <0.2.0`.
Release packaging injects the reviewed shared mod license and verifies it in
every deterministic archive.

| Package | Role and dependencies | Candidate acceptance flow |
| --- | --- | --- |
| `io.github.furroxide.topiaforge.chronos` | Framework service for owner-tagged time leases | Exercise freeze, scale, input-driven time, bounded step, nested owners, a throwing subscriber, scene transition, and repeated disposal; confirm `timeScale` and `fixedDeltaTime` return to baseline. |
| `io.github.furroxide.topiaforge.creatorcontent` | Framework provider for authenticated content catalogs, reversible creator sessions, explicit native adapters, local event projects, mutation isolation, and the single F5 router | Register/unregister custom sources and scene adapters, inject factory/adapter faults, verify source-qualified ids, bounded discovery, exclusive leases, safe duplicate recipes, and deterministic ordering, recover project/index writes, route competing hosts, and confirm exact-once LIFO cleanup across scene replacement and unload. |
| `io.github.furroxide.topiaforge.gravitygun` | Physics grab/pull/throw gameplay | Acquire and release valid props/robots, reject invalid/destroyed targets, charge and throw, change scene while holding, unload/reload, and confirm beam/model/input cleanup. |
| `io.github.furroxide.topiaforge.multiplayer` | Multiplayer contract preview and standalone loopback provider | Load in standalone, exercise generated registration and loopback commands/state/presentation, verify one logical execution on a listen-host-shaped rig, and confirm clean teardown. Live transport is outside the current release scope. |
| `io.github.furroxide.topiaforge.no-feedback-url` | Isolated shutdown-feedback Harmony patch | Verify the page is allowed on the first launch and suppressed later; confirm unsupported bindings fail only this mod and patch teardown is idempotent. |
| `io.github.furroxide.topiaforge.opposite-day` | Hidden global robot-intent inversion through Prompts | Enable the package and verify native and RobotKit-backed robot decisions choose the closest executable opposite, including negated instructions; confirm robots never disclose the directive, unsupported native bindings degrade cleanly, and unload restores ordinary behavior. |
| `io.github.furroxide.topiaforge.perffixes` | Behavior-preserving allocation/CPU patches | Apply each patch on build 2409, compare behavior, profile collision/camera steady state, unload/reload, and verify an unsupported signature fails closed without contaminating other mods. |
| `io.github.furroxide.topiaforge.performance` | Reversible HDRP/quality presets | Apply Off/Balanced/Performance/Potato and individual overrides, transition scenes, encounter missing HDRP features, then disable/unload; confirm every changed Robotopia setting is restored. |
| `io.github.furroxide.topiaforge.prompts` | Framework prompt-override registry and native robot-directive bridge | Register competing priorities, inspect deterministic winner/conflict diagnostics, verify the global robot directive composes without replacing native schemas or personality facts, dispose in varying order, throw from a consumer, unload an owner, and confirm no stale override remains. |
| `io.github.furroxide.topiaforge.robotkit` | Native robot, navigation, objective, remote-brain/conversation, and voice services | Spawn/move/chase/damage/despawn native robots; cancel reachability/objective work across scenes; run signed-out/offline paths; only after explicit approval enable bounded brain/STT tests; cancel requests/capture and unload during work. See [RobotKit.md](RobotKit.md) and [PrivacyAndCapabilities.md](PrivacyAndCapabilities.md). |
| `io.github.furroxide.topiaforge.sandbox` | Declared creator gamemode using Worlds + RobotKit + Creator Content; F5 host registration belongs to its session | Launch the Sandbox target and open the F5 fullscreen workbench; browse, spawn, select, duplicate, transform, temporarily hide, configure robots, test conversations, and author bounded event graphs. Hide/reopen without teardown, then use destructive End Session & Restore and verify creator cleanup. Restart and return to main-menu; confirm the old F5 host and pause actions retire with the session. See [Sandbox.md](Sandbox.md). |
| `io.github.furroxide.topiaforge.uigallery` | F8 TopiaForgeUi development catalog; not a player payload | Inspect Paper/HUD widgets plus loading, empty, information, warning, error, success, disabled, keyboard focus, long/scrollable content, destructive modal, and toast states at all accessibility profiles; repeat create/clear/dispose and confirm allocator/tween/cursor/hotkey baselines. |
| `io.github.furroxide.topiaforge.worlds` | Open Sandbox provider, curated-level and build-scene discovery, Free Play, local-content and pause services; no package dependencies. The loader owns declaration activation, sessions and native transitions. | Verify Open Sandbox geometry, environment, resolved spawn and kill plane; load instances from both discovery sources; install and validate a custom bundle, including missing/ambiguous authored markers and bad/missing prefabs. Reject competing transitions, inject startup/cleanup failures, restart, return to main-menu and unload; confirm exact session and world cleanup. Custom-world acceptance is Windows/Proton-only for 0.x. See [CustomWorlds.md](CustomWorlds.md). |
| `io.github.furroxide.topiaforge.zombies` | Declared wave-survival gamemode using Worlds + RobotKit + Chronos; pause actions belong to its session | Complete waves/shop/zapper/headshot/combo/ally flows; exercise pause/exit and repeated sessions; verify remote brain, JACK IN, and voice default off with deterministic broadcast/text fallbacks; opt in only for approved signed-in/offline/timeout/cancel tests; confirm time, input, robots, and TopiaForgeUi teardown. See the [Zombies worked example](Zombies.md). |

## Declared launch targets

The three first-party targets are `io.github.furroxide.topiaforge.worlds.freeplay.menu`,
`io.github.furroxide.topiaforge.sandbox.creator.menu` and `io.github.furroxide.topiaforge.zombies.menu`.
All default to Worlds' generated Open Sandbox. Sandbox and Zombies use a fixed world policy and require
an additive-arena transition. Free Play uses an open policy with player override enabled: choices must
satisfy world compatibility and consent, including the default. Discovered instances are selectable
through this permitted override, never substituted into a static default. These targets all choose
transitions automatically; they do not offer a separate player transition override.

Free Play adds no creator or wave-survival rules to the prepared world. Its acceptance must include a
cold launch with Sandbox absent, Open Sandbox and both discovery sources, then restart and main-menu
return. Confirm scene/content/player/spawn readiness before Running, and that scene replacement ends
the old controller. Also verify a disabled Worlds package cannot supply any of these targets' worlds.
The retired `io.github.furroxide.topiaforge.worlds.sandbox` ID is not an alias for Free Play; unresolved
legacy selections require explicit repair. These are required candidate checks, not recorded game passes.

## Common automated contract

Each package must have focused automated coverage for:

- manifest identity/version/dependencies/capabilities and the common Robotopia/loader/SDK ranges;
- declared implementation binding, target/world resolution and session-scoped startup/cleanup where applicable;
- bounded configuration parsing, defaults, unknown fields, invalid values, and backward-readable saved state;
- partial load, repeated unload, handler/service ownership, exception isolation, and post-unload behavior;
- package contents, deterministic bytes, entry assembly/type, license/notices, and archive revalidation; and
- any pure gameplay decision/configuration seam used by the representative flow.

Automated tests cannot close Unity-object lifetime, input feel, visual accessibility, gameplay, profiler, microphone,
backend, or clean-host acceptance. Record those manual results for the exact candidate in
[LaunchBlockers.md](LaunchBlockers.md); do not convert unavailable evidence into a skip or assumed pass.
