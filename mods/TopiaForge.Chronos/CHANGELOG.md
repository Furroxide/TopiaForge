# Changelog

## 0.2.0

- Target the TopiaForge 0.2 loader and SDK line.

## 0.1.0-rc.1

- Migrated to the owner-scoped TopiaForge V1 lifecycle, package, and SDK contracts.
- Preserved Robotopia's native pause ownership across Chronos lease changes, reset, and bounded stepping.
- In a multiplayer session, time is the shared world's: only the gamemode's server controller on the host can freeze, slow or step it, and every other caller, every part of a joined player included, gets `NotAuthoritative` with a sentence saying why. Joined players run at the host's time, a player who joins later from the moment it arrives, and leases that lose their authority when the game opens to other players or joins a session end, as at a scene change. In a session, Robotopia's pause menu no longer pauses the shared world, and leaving the session with the menu open brings single player's pause back. Nothing changes in single player.

## 0.1.0

- Initial public release.
