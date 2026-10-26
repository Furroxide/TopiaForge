# Map worlds

A map declaration lets a gamemode play a published robotopia.gg map without loading code.
Map worlds use manifest V6 and require stable TopiaForge 0.2.0 or later. The 0.1
loader refuses that floor before running the package. Existing V6 packages are unchanged.

```sh
topiaforge world add map https://robotopia.gg/map/ExampleMap/ --project example.mode
topiaforge check world ExampleMap --scene courtyard
topiaforge new mod example.mode --template gamemode --map ExampleMap
```

The authoring command downloads the project once, selects its only scene (use `--scene`
for several), checks its spawn locations and writes a survey under `obj/`. It adds the
Worlds dependency, raises the loader and SDK floors, and points the selected launch
target at the map with scene replacement. Use `--target` if several targets exist.
The project does not receive the downloaded map bytes. To author without a connection:

```sh
topiaforge world add map ExampleMap --offline --scene courtyard --name "Courtyard"
```

Offline authoring still needs `check world` later. A scene with several spawn locations
needs one unique editor name and `--spawn`. The runtime uses the game's default scene
when `content.scene` is omitted; the CLI pins the scene it inspected.

Published maps are downloaded when playing. The launcher discloses this on the selected
target before launch. Each play revalidates the cached copy; a network failure uses a
hash-verified saved copy with an offline notice. A removed map (404 or 410) is refused
and its saved copy is discarded. Redirects and untrusted origins are refused.

The map cache holds at most 256 MiB. Downloads are bounded to 8 MiB, expanded JSON to
32 MiB and projects to 50,000 entities. Duplicate keys, invalid identities, parent
cycles and ambiguous spawns are refused before native import. Settings → Clear
downloaded maps removes this cache while Robotopia is confirmed closed. Clearing it
means published maps need a connection on their next play.

For a bundled map, use `content.file` instead of `content.map`, mark the file for
copying to its declared package path and pack it normally. The runtime checks the
packed SHA-256 before parsing it. TF1011 explains invalid declarations at build time.

This release enables map worlds in single player. Opening one to multiplayer is
refused until session content identity and transfer support are available. Import,
spawn, cancellation and cleanup use the shared world transition owner. Native catalog
lore and respawn integration still require verification against the installed game.
