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

## Authored entities and anchors

In a gamemode, `session.World.RequireMap()` returns the imported map's index.
`World.Map` is null for other worlds. The index contains every authored record,
parents before children, even when the game produced no usable live marker.
`map.Entity("entry")` gives its metadata and `IEntity`; check `Entity.IsAlive`
before registering interactions. `TryGetAuthored(hit.Entity, out var authored)`
resolves an interaction or raycast handle back to its authored id. `FindByModel`
finds all records using a model; repeated names are not identities.

`MapTransform` preserves editor position, scale and Euler-or-quaternion rotation.
Use `map.ToWorld(...)` for world-space coordinates. The native adapter calls the
game's conversion helpers and applies the imported root transform. The testing
kit pins the editor-X reflection; native root, rotation and prefab expansion
behavior still needs in-game qualification. The index is built once, with no
per-frame work. After teardown its lookups refuse and entity handles are dead.

Survey a declared world and track selected objects in a copied anchors file:

```sh
topiaforge world survey example.mode.map
topiaforge world survey example.mode.map --anchors map-anchors.json --update
```

```json
{
  "schemaVersion": 1,
  "anchors": [
    { "key": "entry", "entityId": "entry", "required": true },
    { "key": "crate", "entityId": "crate", "name": "Supply crate",
      "modelId": "kit/box", "editorPosition": { "x": 12, "y": 2, "z": 3 },
      "positionTolerance": 1, "required": true }
  ]
}
```

Include it as `<None Update="map-anchors.json" CopyToOutputDirectory="PreserveNewest" />`.
`map.ResolveAnchors("map-anchors.json")` reads from the consuming gamemode's
package. It matches ids first, then an exact name and model within the configured
editor-space distance (1 metre by default). Two candidates are ambiguous. Required
anchors need a usable live entity. Read `IsComplete` and `Report` before indexing a
resolved key. Missing or malformed files return `InvalidArgument` with file/field
guidance. Never treat map names as rich text or use authored ids as paths.

Survey refreshes the ignored snapshot under `obj/`; `--update` rewrites matched
anchors only when all required anchors resolve. Optional misses retain their
original data. `--offline` checks an existing matching published-map snapshot and
explicitly reports that its revision was not refreshed. Packaged maps are read
locally. TF1012 checks launch-target wiring; TF1013 offers surveyed entity ids for
code and copied anchors files. No survey means no TF1013 warning.

For an extra map inside a running single-player session, await
`ILocalWorldService.ImportMapAsync(sessionId, path, cancellationToken)`. It uses the
configured local import folder, validates bytes, dispatches through the shared
transition owner and returns the same index contract. Stale sessions and cancelled
or failed imports release their content. Test with `FakeAuthoredMap.FromProject`,
its builder, or `GamemodeSessionRunner.WithMap(map)`; keep native checks separate
from fake results.
