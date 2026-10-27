# Saved published maps

Save a public map in **Setup → Saved published maps → Add map**, with its map page
link or id, explicit scene id and a display name. TopiaForge Worlds discovers the
saved scenes after the next game start. Select one in Free Play's world picker.
The picker uses the runtime's observed worlds; saving a reference does not declare
a static world or alter a gamemode's launch target.

The CLI manages the same local list:

```sh
topiaforge world source add example-map --scene courtyard --name "Courtyard"
topiaforge world source list
topiaforge world source remove example-map --scene courtyard
```

An official `https://robotopia.gg/map/example-map/` link also works. A source is
identified by its exact map id and scene id; saving it again updates its name.
Different scenes remain separate choices. The official editor exposes the scene
id. Listing and saving need no network request or account credentials.

Playing uses the same validated download, byte-exact cache, native import, spawn
and cleanup path as a [declared map world](MapWorlds.md). A map played through one
route can use the same saved bytes through the other. Offline playback requires
a previously downloaded copy. An unpublished map or missing scene gives the same
failure as a declared map. Maps currently support single player; a loaded map
cannot be promoted to a multiplayer host.

Removing a source removes its reference only. Use **Settings → Clear downloaded
maps** while the game is closed to remove downloaded bytes. Changes to saved
references apply on the next game start; the launcher stages a complete validated
list during launch admission. A damaged list refuses loading with repair guidance
and is never silently replaced by an edit.

If the list is damaged, restore a known-good `published-map-sources-v1.json` backup
in the launcher's data folder, then reopen Setup or run `world source list`. Keep
the damaged bytes for diagnosis. Neither Setup nor the CLI automatically discards
or repairs malformed entries; normal add/remove operations require a valid list.

The list is versioned, bounded to 4,096 sources and 1 MiB, and stores only a
canonical public map id, scene id and name. It accepts no credentials, live
document reference, custom endpoint or arbitrary download URL. Writers serialize
through a local file lease and replace the list atomically. Linked storage paths
are refused.

Worlds provides the discovery family `io.github.furroxide.topiaforge.worlds.map`.
Its instance identity includes a digest of the exact map/scene pair and normally
ends in the map id. When that would exceed the declaration's 96-character bound,
it uses a digest suffix instead. This never changes or truncates the map store's
canonical cache key. Discovery validates the full list before limiting results,
and checks the source again when loading a selected world.

Cache index and removal-marker filenames use the SHA-256 of the exact map id,
and each index records that id. This keeps case-distinct publications separate
on Windows, including their validators and offline copies. Older indexes without
an exact source identity are not used offline: play each map once with a connection
to create its verified index. Existing content blobs remain subject to the shared
cache limit and **Clear downloaded maps**.

The feature requires the stable 0.2 SDK/loader floor. Package versions and release
activation are tracked separately. Native public-map resolution and play remain
subject to the map-world compatibility checks for the installed game build.
