# First-party mod catalog and acceptance flows

All first-party mods use the same manifest/package contracts available to community authors. Provider implementations
may use native APIs internally, while safe consumers receive only owner-bound contracts. Every release candidate
validates all sixteen source mods. Normal `pack --all` output contains fourteen non-DevTool packages; release
automation then adds the optional Creator Tools package explicitly. The developer-only UiGallery is tested but is
the sole source mod excluded from the resulting fifteen-package release payload.

Every manifest is schema version 5, constrains Robotopia to `0.0.2309`, constrains the loader and SDK to
`>=1.0.0-rc.1 <2.0.0`, and declares AGPL-3.0-or-later with a package-relative `LICENSE`.
Release packaging injects the reviewed shared mod license and verifies it in
every deterministic archive.

| Package | Role and dependencies | Candidate acceptance flow |
| --- | --- | --- |
| `io.github.furroxide.topiaforge.chronos` | Framework service for owner-tagged time leases | Exercise freeze, scale, input-driven time, bounded step, nested owners, a throwing subscriber, scene transition, and repeated disposal; confirm `timeScale` and `fixedDeltaTime` return to baseline. |
| `io.github.furroxide.topiaforge.creatorcontent` | Framework provider for authenticated content catalogs, reversible creator sessions, explicit native adapters, local event projects, mutation isolation, and the single F5 router | Register/unregister custom sources and scene adapters, inject factory/adapter faults, verify source-qualified ids, bounded discovery, exclusive leases, safe duplicate recipes, and deterministic ordering, recover project/index writes, route competing hosts, and confirm exact-once LIFO cleanup across scene replacement and unload. |
| `io.github.furroxide.topiaforge.creatortools` | Optional, first-install-disabled ordinary-game host for the shared Creator workbench | Enable only in a Creator profile; verify F5 eligibility, persistence-isolation acknowledgement, hide/reopen retention, native restoration, graph-only rollback, scene-transition cleanup, and ten leak-free lifecycles. See [Creator Tools](CreatorTools.md). |
| `io.github.furroxide.topiaforge.gravitygun` | Physics grab/pull/throw gameplay | Acquire and release valid props/robots, reject invalid/destroyed targets, charge and throw, change scene while holding, unload/reload, and confirm beam/model/input cleanup. |
| `io.github.furroxide.topiaforge.multiplayer` | Stable multiplayer contract preview and standalone loopback provider | Load in standalone, exercise generated registration and loopback commands/state/presentation, verify one logical execution on a listen-host-shaped rig, and confirm clean teardown. Live transport is not part of 1.0. |
| `io.github.furroxide.topiaforge.no-feedback-url` | Isolated shutdown-feedback Harmony patch | Verify the page is allowed on the first launch and suppressed later; confirm unsupported bindings fail only this mod and patch teardown is idempotent. |
| `io.github.furroxide.topiaforge.opposite-day` | Hidden global robot-intent inversion through Prompts | Enable the package and verify native and RobotKit-backed robot decisions choose the closest executable opposite, including negated instructions; confirm robots never disclose the directive, unsupported native bindings degrade cleanly, and unload restores ordinary behavior. |
| `io.github.furroxide.topiaforge.perffixes` | Behavior-preserving allocation/CPU patches | Apply each patch on build 2309, compare behavior, profile collision/camera steady state, unload/reload, and verify an unsupported signature fails closed without contaminating other mods. |
| `io.github.furroxide.topiaforge.performance` | Reversible HDRP/quality presets | Apply Off/Balanced/Performance/Potato and individual overrides, transition scenes, encounter missing HDRP features, then disable/unload; confirm every changed Robotopia setting is restored. |
| `io.github.furroxide.topiaforge.prompts` | Framework prompt-override registry and native robot-directive bridge | Register competing priorities, inspect deterministic winner/conflict diagnostics, verify the global robot directive composes without replacing native schemas or personality facts, dispose in varying order, throw from a consumer, unload an owner, and confirm no stale override remains. |
| `io.github.furroxide.topiaforge.robotkit` | Native robot, navigation, objective, remote-brain/conversation, and voice services | Spawn/move/chase/damage/despawn native robots; cancel reachability/objective work across scenes; run signed-out/offline paths; only after explicit approval enable bounded brain/STT tests; cancel requests/capture and unload during work. See [RobotKit.md](RobotKit.md) and [PrivacyAndCapabilities.md](PrivacyAndCapabilities.md). |
| `io.github.furroxide.topiaforge.sandbox` | Worlds + RobotKit + Creator Content gamemode | Open the F5 fullscreen workbench; browse, spawn, select, duplicate, transform, temporarily hide, configure robots, test conversations, and author bounded event graphs. Hide/reopen without teardown, then use destructive End Session & Restore and verify complete cleanup. See [Sandbox.md](Sandbox.md). |
| `io.github.furroxide.topiaforge.ugc.livesync` | Local-folder and Automerge preview sync | Start/stop both transports, reject malformed/oversized/racing snapshots and unsafe watch targets, handle sidecar loss/reconnect, transition scenes, unload twice, and verify typed status/errors and secret-free diagnostics. See [UgcLiveSync.md](UgcLiveSync.md). |
| `io.github.furroxide.topiaforge.uigallery` | F8 TopiaForgeUi development catalog; not a player payload | Inspect Paper/HUD widgets plus loading, empty, information, warning, error, success, disabled, keyboard focus, long/scrollable content, destructive modal, and toast states at all accessibility profiles; repeat create/clear/dispose and confirm allocator/tween/cursor/hotkey baselines. |
| `io.github.furroxide.topiaforge.worlds` | World/gamemode registration and scene ownership | Launch/exit the sandbox, arbitrate simultaneous scene requests, install and validate a custom bundle, test bad/missing prefab and save compatibility, transition/unload, and confirm pause actions/modal ownership and world cleanup. Custom bundles are Windows/Proton-only for v1. See [CustomWorlds.md](CustomWorlds.md). |
| `io.github.furroxide.topiaforge.zombies` | Worlds + RobotKit + Chronos wave-survival gamemode | Complete waves/shop/zapper/headshot/combo/ally flows; exercise pause/exit and repeated sessions; verify remote brain, JACK IN, and voice default off with deterministic broadcast/text fallbacks; opt in only for approved signed-in/offline/timeout/cancel tests; confirm time, input, robots, and TopiaForgeUi teardown. See the [Zombies worked example](Zombies.md). |

## Common automated contract

Each package must have focused automated coverage for:

- manifest identity/version/dependencies/capabilities and the common Robotopia/loader/SDK ranges;
- bounded configuration parsing, defaults, unknown fields, invalid values, and backward-readable saved state;
- partial load, repeated unload, handler/service ownership, exception isolation, and post-unload behavior;
- package contents, deterministic bytes, entry assembly/type, license/notices, and archive revalidation; and
- any pure gameplay decision/configuration seam used by the representative flow.

Automated tests cannot close Unity-object lifetime, input feel, visual accessibility, gameplay, profiler, microphone,
backend, or clean-host acceptance. Record those manual results for the exact candidate in
[LaunchBlockers.md](LaunchBlockers.md); do not convert unavailable evidence into a skip or assumed pass.
