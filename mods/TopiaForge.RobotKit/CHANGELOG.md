# Changelog

## Unreleased

- Added guarded map-world MuniciPal composition and bounded discovery refusal diagnostics. Native character qualification remains pending.
- When a MuniciPal's only unprepared catalogue references are its personality and about references, RobotKit now offers it body-only: it spawns Dormant with native talk off, and Autonomous spawns, `SetBrainMode(Autonomous)` and enabling native talk return `Unavailable` with the reason. Body-part, voice and other catalogue gaps still refuse. A `Spawn` that names a missing MuniciPal now returns a fixed sentence for the discovery code, which a gamemode can show to players. Native qualification on 2597 remains pending.
- Empty native-prefab discovery keeps retrying beside a current composition; definite absent-character discovery backs off without surviving world or scene changes.
- Missing native walk `ShouldIgnore` metadata now fails the Critical compatibility gate.
- Restricted temporary scene editing to eligible native scene robots. Discovery excludes RobotKit-managed robots;
  `TryResolve` turns one into a target that allows only a personality preview. A personality preview or waking a
  native brain is refused on a guest's PC and in host code running on a guest's input. Previews and snapshot
  restoration stop if a scene target comes under RobotKit management.
- Host callbacks acting for a guest now receive `NotAuthoritative` when requesting an Autonomous spawn, before any robot is created. Dormant requests keep their existing native-talk normalization; client requests and replicas remain Dormant. The testing kit models these callback restrictions and distinguishes player dismissals from host hides.

- Added automatic private earshot speech for host-owned robots, native voice matching and scoped cancellation; guests synthesize locally and retain dormant brains.

- Robot dialogue capture now holds the runtime microphone lease, yields to native recording, and returns Conflict when preempted without ending the native recording.

- A composed robot whose native context cannot be verified now makes `Spawn` return `Unavailable` with its own message. If the context stays unverified for two seconds, the composed type leaves `RobotTypes` until discovery finds it again, while robots already spawned from it keep running; a definite replacement, an ended lifetime or a failing identity read still destroys them.

## 0.2.0

- Target the TopiaForge 0.2 loader and SDK line.

## 0.1.0-rc.1

- Added optional dynamic composition of the Prompts global robot directive into structured brain and conversation requests.
- Migrated to the owner-scoped TopiaForge V1 lifecycle, package, and SDK contracts.
- Voice input's log line no longer names the microphone, since a device's name can carry its owner's name and the log goes into diagnostic bundles. It now says only that voice input is available.
- Logs, and the failure messages RobotKit returns to mods, no longer carry exception messages or stack traces, which can name local paths such as the RoboAPI token file's. They say what failed, the exception's type and, for the token, what to do next.
- A failing objective target resolver logs its target kind instead of its registered name, since mods can derive those names from paths or player data.
- In a multiplayer session other players appear as the game's own robots: RobotKit offers the runtime a dormant copy of the scene's robot, in each player's colour, for every player's avatar, and `Chase` can follow another player's avatar.

## 0.8.0

- Initial public release.
