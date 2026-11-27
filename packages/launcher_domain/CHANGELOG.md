## Unreleased (0.2.0)

- Normalize unknown outcome V2 failure/end strings to `other` with fixed
  recovery advice, keeping every other validation rule strict. Older readers
  still reject unknown reasons.

## 0.1.0-rc.1

- Added the strict manifest V5, SemVer, dependency, compatibility, capability,
  profile, multiplayer admission/inactive-reason, and package-selection
  contracts used by the V1 launcher and CLI.
- Added `world-follow-failed` to session launch failures and session end
  reasons, so a join whose world follow fails keeps the game's fixed advice and
  the last followed world.
- Settle host refusals and removals that arrive while the join receives the
  world, and re-plan changed admission there as while connecting.

## 0.1.1

- Pre-stable release metadata for TopiaForge 0.1.1.
