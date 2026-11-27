# Changelog

## 0.2.0

- Target the TopiaForge 0.2 loader and SDK line.
- Prepare map lore through Robotopia build 2640's catalog lore loader instead of refusing every published and
  packaged map at that step. The catalog address comes from the game's launch request or, failing that, its asset
  configuration, and only an address the game's own validator accepts is used; without one the map still fails
  closed. The log names the source, never the address. Native map play on 2640 is not yet confirmed.

## 0.1.0-rc.1

- Migrated to the owner-scoped TopiaForge V1 lifecycle, package, and SDK contracts.
- Load a local `.roboworld` / `.json` / `.json.gz` export through the game's own import host. Strictly
  local: no sign-in, no publish, no backend call. Configured with `enableLocalWorlds` and
  `localWorldFolder`; imports are confined to that folder.

## 0.6.0

- Initial public release.
