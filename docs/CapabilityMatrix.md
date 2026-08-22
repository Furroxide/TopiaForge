# V1 capability coverage

TopiaForge V1 is frozen only when every promised Robotopia modding goal has all five forms of evidence: a public safe API, a compiled SDK-only sample, a task guide, deterministic testing fakes, and a live Robotopia acceptance case. The canonical machine-readable mapping is [`capability-matrix.json`](capability-matrix.json); the offline test suite validates its paths, assembly-qualified type names, and acceptance-case IDs.

The offline validator proves that every row is wired to compiled, native-free source and published
documentation; it does not claim that gameplay worked. Only retained results from the
[administrator-controlled live-Robotopia gates](LiveGameAcceptance.md#administrator-controlled-launch-gates)
satisfy the final column.

| Modder goal | Safe API packages | Compiled acceptance | Guide | Testing support | Live evidence |
|---|---|---|---|---|---|
| Utility mods | Abstractions authoring services | SDK Acceptance Mod | [Core services](CoreServices.md) | In-memory config/storage plus captured localization, commands, and diagnostics | Config migration, storage, localization, commands |
| Input and UI | Abstractions input and TopiaForgeUi facade | Sandbox, Zombies, and SDK Acceptance Mod | [UI kit](UiKit.md) | Deterministic input and captured UI | Keyboard/mouse/gamepad, focus, accessibility |
| Gameplay abilities | Process-local player, entity, physics, time, and scheduler abstractions | GravityGun, Zombies, and SDK Acceptance Mod | [Core services](CoreServices.md) | Local-player/entity/physics/time/scheduler fakes | Loop phases, local-player health/control, ten in-session resource cycles plus repeated offline load/unload/failure cycles |
| Interactions and creator content | Abstractions interactions/items/assets/audio plus Creator Content | Creator Tools, Sandbox, and SDK Acceptance Mod | [Creator Tools](CreatorTools.md) | Interaction/item/asset/audio plus creator catalog/project/mutation fakes | Queries, items, prefabs, authenticated catalogs, reversible sessions, project storage, and graceful failure |
| Worlds and modes | Abstractions scenes and detailed transition events, and Worlds | Zombies and SDK Acceptance Mod | [Custom worlds](CustomWorlds.md) and [Core services](CoreServices.md#scene-transition-semantics) | Scene/event and world/gamemode fakes | Initial scene and session teardown |
| Robots and story | RobotKit plus optional safe live-player identity and scene editor | Sandbox, Zombies, and SDK Acceptance Mod | [RobotKit](RobotKit.md) and [Zombies worked example](Zombies.md) | Full RobotKit fake suite | Robot body, movement, objectives, dialogue, voice, brain query, and temporary edit restoration |
| Mod integration | Abstractions extensions, Prompts, and Chronos | SDK Acceptance Mod | [Modules](Modules.md) | Extension, prompt, and time-control fakes | Provider scope/cardinality and optional-provider isolation |

The live cases and required ten-cycle threshold are defined in [`tests/live-game-acceptance.json`](../tests/live-game-acceptance.json). Run `topiaforge acceptance run` from a complete checkout on an authorized Windows or Linux/Proton Robotopia host; see [Live Robotopia acceptance](LiveGameAcceptance.md).
