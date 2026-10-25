# Changelog

## 0.1.0-rc.1

- Added optional dynamic composition of the Prompts global robot directive into structured brain and conversation requests.
- Migrated to the owner-scoped TopiaForge V1 lifecycle, package, and SDK contracts.
- Voice input's log line no longer names the microphone, since a device's name can carry its owner's name and the log goes into diagnostic bundles. It now says only that voice input is available.
- Logs, and the failure messages RobotKit returns to mods, no longer carry exception messages or stack traces, which can name local paths such as the RoboAPI token file's. They say what failed, the exception's type and, for the token, what to do next.
- A failing objective target resolver logs its target kind instead of its registered name, since mods can derive those names from paths or player data.
- In a multiplayer session other players appear as the game's own robots: RobotKit offers the runtime a dormant copy of the scene's robot, in each player's colour, for every player's avatar, and `Chase` can follow another player's avatar.

## 0.8.0

- Initial public release.
