# Changelog

## 0.1.0-rc.1

- Migrated to the owner-scoped TopiaForge V1 lifecycle, package, and SDK contracts.
- Load a local `.roboworld` / `.json` / `.json.gz` export through the game's own import host. Strictly
  local: no sign-in, no publish, no backend call. Configured with `enableLocalWorlds` and
  `localWorldFolder`; imports are confined to that folder.

## 0.6.0

- Initial public release.
