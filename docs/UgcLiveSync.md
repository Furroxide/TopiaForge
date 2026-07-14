# UGC Live Content Sync

Robotopia's game runtime ships the **consumer half** of a live user-generated-content (UGC) pipeline:
it can import a level (`UgcExportProject`) from a file or subscribe to an external editor's
[Automerge](https://automerge.org/) CRDT document over a `wss://` sync server, then **incrementally
patch the running scene** — no restart. This document specifies the pieces a mod and a Unity-Editor
companion need to drive that pipeline:

- the **`IUgcLiveSyncService`** SDK surface (`Robotopia.Mods.Abstractions`),
- the **shared `UgcExportProject` JSON contract** (the single source of truth both the Unity exporter
  and the game importer must agree on),
- the **two content channels** (local watch folder, and Automerge live), and
- the **security model**, **asset catalog**, and **end-to-end verification**.

> The game is **preview/play only**. Nothing here writes edits *back* to the Automerge document
> (`UgcAutomergeSyncClient.EditAsync` is intentionally never wired). All authoring happens in the
> Unity companion or the external web editor.

All access to game (`GameCode`) types is **clean-room reflection** — no mod or tool references
`GameCode.dll`. The framework mod `Robotopia.UgcLiveSync` reproduces the game's own
`UgcLiveSyncController.ApplyMaterializedProjectJson` logic against the game's **public** import/diff/patch
APIs.

---

## Architecture

```
                       Unity Editor companion (authoring)
                                 │  export UgcExportProject JSON (atomic write)
        ┌────────────────────────┼─────────────────────────────┐
        │ (1) LOCAL DEV channel   │   (2) AUTOMERGE channel (parity / interop)
        ▼                         │                              ▼
  watch folder ──FileSystemWatcher│                   external web editor / Node sidecar
        │ (bg thread → queue)     │                              │ writes Automerge doc
        ▼ main-thread Pump()      │                              ▼ wss:// sync server
  UgcExportLoader.LoadProjectFromBytes        game's native UgcLiveSyncController
        │                                                  │ (configured via UgcPlayLaunchRequest)
        ▼                                                  ▼
  UgcProjectDiffer.Diff(prev,next) ──► UgcScenePatcher.ApplyPatches  ◄── (same engine)
        │ (false → full rebuild)                            │
        ▼                                                   ▼
  UgcImportHostSceneController.ImportProject (full build)   running UgcPlay scene updates live
```

Both channels terminate in the **game's own incremental engine**. The local channel never touches
`UgcLiveSyncController` (which is welded to the Automerge WebSocket client); it drives
`UgcImportHostSceneController` + `UgcProjectDiffer` + `UgcScenePatcher` directly.

### Channel 1 — Local dev (primary, zero external deps)

The Unity companion exports `UgcExportProject` JSON (`.json` or `.json.gz`) to a **watch folder**.
The mod's `FileSystemWatcher` enqueues changes (background thread); the per-frame `Pump()` (Unity main
thread) reads the newest file and applies it exactly like the game's live controller:

- **first snapshot** → `UgcImportHostSceneController.ImportProject(project, sceneId, label)` (full build),
  then a fresh `UgcScenePatcher(SceneRoot, BuiltInAssetMap, EnvironmentPrefabMap, controller)` +
  `RefreshEntityIndex()`;
- **subsequent snapshots** → `UgcProjectDiffer.Diff(prev, next, sceneId)` →
  `UgcScenePatcher.ApplyPatches(next, scene, patches)`; if it returns `false`, fall back to
  `ImportProject` + rebuild the patcher.

### Channel 2 — Automerge live (parity / interop)

The mod builds a `UgcPlayLaunchRequest` (`Mode = LiveAutomerge`, `LiveDocumentUrl`, `LiveSyncUrl`,
`LiveSceneId`) and loads `UgcPlay`. The scene's `UgcImportPlayBootstrap` consumes the request and
starts the game's own `UgcLiveSyncController` against the editor's document. This makes the existing
**web editor** interoperate unchanged. Writing the Automerge document *from Unity* is an optional
Node-sidecar extension (see [below](#automerge-writer-node-sidecar)).

**Editor URL form:** `https://<host>/?project=<automerge-doc-id-or-url>&scene=<sceneId>`. The
`project` query param is normalized to an `automerge:`-prefixed document id; the sync server must be a
`wss://` endpoint (default `https://automerge-repo-sync-server-main.onrender.com`, upgraded to `wss`
at runtime).

---

## Shared schema contract — `UgcExportProject` JSON

This is the **single source of truth**. The Unity exporter writes it; the game importer
(`UgcExportLoader` → `JsonConvert.DeserializeObject<UgcExportProject>`) reads it. Property names below
are the exact Newtonsoft `[JsonProperty]` names — **they must match byte-for-byte**. The golden fixture
[`tests/fixtures/ugc/sample-project.json`](../tests/fixtures/ugc/sample-project.json) exercises every
shape and is validated by the .NET test harness.

### Project (root)

| Field | JSON key | Type | Notes |
|---|---|---|---|
| version | `version` | string | export format version |
| name | `name` | string | |
| created | `created` | string | ISO-8601 |
| modified | `modified` | string | ISO-8601 |
| assets | `assets` | `{ id: url }` | downloadable asset id → URL |
| localAssets | `local-assets` | `{ id: object }` | each value has a `type` discriminator (below) |
| scenes | `scenes` | `{ id: Scene }` | importer picks the requested scene id, else the first by ordinal id |

### Scene

| Field | JSON key | Type |
|---|---|---|
| id | `id` | string (defaults to the map key if blank) |
| name | `name` | string |
| environment | `environment` | string (env preset key, e.g. `day`/`night`) |
| created / modified | `created` / `modified` | string |
| entities | `entities` | `{ id: Entity }` |

### Entity

| Field | JSON key | Type |
|---|---|---|
| id | `id` | string (defaults to the map key if blank) |
| name | `name` | string |
| parent | `parent` | string? (parent entity id; `null`/absent = scene root) |
| components | `components` | `EntityComponents` |

### EntityComponents

All component fields are **nullable / optional**; only present ones are built. Unknown sibling keys are
captured by `[JsonExtensionData]` into `extraComponents` (tolerated, but a **change** to them forces a
full rebuild).

| Field | JSON key | Type |
|---|---|---|
| transform | `transform` | `{ position: Vec3, rotation: Rot, scale: Vec3 }` |
| modelRenderer | `model-renderer` | `{ "model": assetId }` |
| prefabInstance | `prefab-instance` | `{ "scene": string, "model": assetId, "accessory": string }` |
| spawnLocation | `spawn-location` | `{}` (marker — player spawn point) |
| poi | `poi` | `{ "about": string[], "visualDescription": string, "hidden": bool }` |
| aoi | `aoi` | `{ "about": string[], "visualDescription": string, "size": Vec3? }` |
| agent | `agent` | `{ "about": string[], "visualDescription": string, "personality": assetUri }` |
| (unknown) | — | captured by `extraComponents` (`JsonExtensionData`) |

`Vec3` = `{ "x": float, "y": float, "z": float }`.
`Rot` = `{ "x": float, "y": float, "z": float, "w": float? }` — quaternion when `w` present, else Euler XYZ (degrees).

### Local assets (`local-assets`)

Each value is an opaque object with a `type` discriminator. Recognized types: **`lore`**,
**`lore-collection`**, **`personality`** (others only log a warning, they don't fail the import). For
incremental dependency tracking the importer reads `lore` and `knowledge` string arrays inside these
objects, so an edit to a referenced lore asset re-creates the entities that reference it.

---

## Coordinate handedness (the exporter MUST invert this)

The game converts UGC coordinates to Unity space — the exporter must apply the **inverse** so a
round-trip is identity. Verified from `UgcVector3Value` / `UgcRotationHelper`:

- **Position:** game computes `new Vector3(-x, y, z)` → **negate X on export**.
- **Scale:** identity `(x, y, z)` → no change.
- **Rotation:** game applies a basis sandwich `B · R · B` with `B = Scale(-1, 1, 1)`, accepting a
  quaternion `(x, y, z, w)` or, when `w` is null, an Euler-XYZ (degrees) triple. Because `B` is its own
  inverse, the exporter converts a Unity rotation `R_unity` to UGC space as `R_ugc = B · R_unity · B`
  and emits it as a quaternion.

The golden fixture pins a known case: `ent-root` at UGC position `(1, 0, 2)` ⇒ Unity `(-1, 0, 2)`,
rotation identity. A regression on either side fails the test rather than shipping a mirrored scene.

---

## Asset catalog & origin alignment

The game resolves a component's `model`/`prefab-instance` asset id through `UgcBuiltInAssetMap`
(`assetId → prefab` + an optional `localPositionOffset` that aligns the prefab pivot to the UGC editor
origin). **Unresolved ids render as placeholder cubes.** To make a modder's *own* prefabs appear, the
mod registers **runtime overrides** (`UgcAssetOverride`) via the SDK; the bridge calls
`UgcBuiltInAssetMap.SetRuntimeOverride(assetId, prefab, offset)`. The prefab is supplied by the modder
as a loaded `UnityEngine.GameObject` (e.g. via `context.LoadAssetBundle(...)` and `context.LoadAsset<GameObject>(...)`
from the `Robotopia.Assets` framework mod).

---

## Which edits are incremental vs. full rebuild

`UgcProjectDiffer` emits incremental patches for: entity add/remove, metadata (name), transform, and
the `visual` / `spawn-location` / `poi` / `aoi` / `agent` component groups, plus environment and
local-asset changes. It forces a **full rebuild** when an entity's `parent` changes
(`EntityParentChangedPatch`) or an `extraComponents` (unknown) field changes. The exporter should keep
entity **ids and parents stable** across exports to stay on the fast (incremental) path.

---

## SDK: `IUgcLiveSyncService`

Registered by `Robotopia.UgcLiveSync` and resolved via `context.GetService<IUgcLiveSyncService>()`
(same pattern as `IWorldGamemodeService`). Unity-free (prefabs as `object`, offsets as `float[3]`).
See [`src/Robotopia.Mods.Abstractions/UgcLiveSync.cs`](../src/Robotopia.Mods.Abstractions/UgcLiveSync.cs)
for the full surface and `///` docs. Summary:

- `StartLocalSession(UgcLiveSyncRequest)` / `StartAutomergeSession(UgcLiveSyncRequest)` / `Stop()`
- `Status`, `CurrentSession`
- `RegisterAssetOverride(UgcAssetOverride)` / `ClearAssetOverrides()` / `AssetOverrides`
- events: `SessionStarted`, `SnapshotImported`, `PatchApplied` (carries `IsFullRebuild`), `SyncError`, `SessionStopped`

---

## Config

The mod reads `config/robotopia.ugc.livesync.json` (`BepInEx/RobotopiaModManager/config/<modId>.json`)
via `IModContext.LoadConfig`. Keys (mirrored by the Dart launcher/CLI domain model):

| Key | Type | Default |
|---|---|---|
| `transport` | `"localFolder"` \| `"automerge"` | `localFolder` |
| `watchFolder` | string | the game's default UGC import folder |
| `editorUrl` | string | empty |
| `documentUrl` | string | empty |
| `syncServerUrl` | string | `https://automerge-repo-sync-server-main.onrender.com` |
| `sceneId` | string | empty (first scene) |
| `autoConnectOnStart` | bool | `false` |
| `maxSnapshotBytes` | int | `16777216` (16 MiB) |
| `debounceMilliseconds` | int | `200` |

Asset overrides are **not** configured here (they need a live `UnityEngine.GameObject` prefab) — register them
programmatically via `IUgcLiveSyncService.RegisterAssetOverride`.

**Auto-connect yields to a live world session.** `autoConnectOnStart` connects with
`SceneTransitionPriority.Automatic` (see the *Scene Coordination* section of [Modding.md](Modding.md)): if a
world/gamemode session is live or still loading when the connect fires — e.g. the Worlds auto-launcher was
configured on the same start — the UGC play scene is **not** loaded over it. The session still starts in the
`WaitingForScene` state and attaches automatically the next time the UGC play scene comes up (for example when
the player launches the Sandbox). Explicit connects from the in-game overlay are user-initiated and load the
play scene immediately.

---

## Security model

- The **watch folder** is read as arbitrary JSON. The mod validates each snapshot before applying it
  (size cap `maxSnapshotBytes`, gzip/UTF-8 sniff, parse guard) and **rejects + logs** bad input rather
  than crashing the game. The previous good snapshot is retained so the next valid write recovers.
- The **sync URL** must be an absolute `https://` or `wss://` endpoint. Plaintext schemes,
  embedded credentials, fragments, malformed escapes, and oversized URLs are rejected before an
  existing healthy session is stopped. The Automerge channel is **opt-in** and the endpoint is
  surfaced to the user (config or the in-game panel), never silently dialed.
- The manifest declares descriptive `permissions`: `scene-management`, `ugc-livesync`,
  `filesystem-watch`, `network`.

---

## End-to-end verification

1. `dotnet build RobotopiaModManager.slnx -c Release` — the mod compiles with **zero `GameCode`
   references** (clean-room). Run the .NET test harness and `dart test`.
2. `robotopia dev-install`, then launch `Robotopia.exe` (winhttp/doorstop auto-loads BepInEx).
3. **Local dev:** F10 → Gamemodes → "UGC Live" (or the in-mod panel); start a local session on a folder;
   in Unity toggle Live Sync and move/add/remove an entity; the scene updates **with no restart, no
   flicker**.
4. **Automerge:** paste an editor URL `https://host/?project=<doc>&scene=<id>`, Start; `UgcPlay` loads,
   the native controller connects (`wss`) and applies the initial snapshot; edits in the web editor land live.

**Success log lines** (mirroring the Worlds `Auto-launch:` / `Scene loaded: UgcPlay` proof):

- `manager.log`: `Robotopia UgcLiveSync loaded`, `UGC live sync: watching <folder>`, and per snapshot
  `UGC live sync: applied incremental patch (<n> patches)` or `UGC live sync: full rebuild (<reason>)`;
  a bad file logs `UGC live sync: rejected snapshot (<reason>)` and the game stays up.
- `Player.log` (Automerge): the game's own `UgcLiveSyncController: connected to live Automerge sync`,
  `initial live Automerge import completed`, `applied live Automerge revision <n>`.

---

## Automerge writer (Node sidecar)

For full parity with the devs' web editor (and remote multi-user collaboration), the **writer** side of the
Automerge channel ships as a small Node sidecar at [`tools/ugc-automerge-sidecar`](../tools/ugc-automerge-sidecar)
using the official [`@automerge/automerge-repo`](https://github.com/automerge/automerge-repo). It publishes a
`UgcExportProject` (the same JSON the Unity companion exports) to an Automerge document on the sync server; the
game reads that document natively. The local-folder channel needs none of this — the sidecar is only for
web-editor parity / remote collaboration.

Run it via the CLI (which verifies Node 20+, locates the trusted sidecar, and runs the checked-in lockfile with
`npm ci --ignore-scripts --no-fund --no-audit` on first use):

```powershell
# One-shot publish (prints the document URL to paste into the in-game UGC Live "Automerge" field):
robotopia ugc publish --file path\to\project.json --scene main

# Watch the Unity companion's export folder and re-publish on every change:
robotopia ugc watch path\to\watch-folder --scene main

# Verify Node + deps + resolved config without connecting:
robotopia ugc check
```

Pass `--doc <automerge-url>` to publish into an existing document (e.g. one the web editor already created);
omit it to create a new one. `--sync <url>` overrides the default sync server (auto-upgraded to `wss://`).
The full data flow for this channel is: Unity companion → export JSON → sidecar → Automerge document →
game's `UgcLiveSyncController`.

## Launcher cockpit: auto-detect + one-button Go Live (no manual scripts)

The QuantumWorks launcher's **UGC Live Sync** pane is a cockpit that removes the manual steps:

- **Auto-detected connection values.** The sidecar emits a machine-readable `ROBOTOPIA_UGC_SESSION {json}` line
  (and an optional `--session-file`); when the launcher starts the publisher it captures the live document URL
  from that line and **writes it straight into the game's `config/robotopia.ugc.livesync.json`** — no copy/paste.
- **Game → launcher status handshake.** The mod writes `config/robotopia.ugc.livesync.status.json` (default watch
  folder, current status, connected document, applied scenes). The cockpit reads it to pre-fill the watch folder
  with the game's default, show live diagnostics chips, and populate a scene dropdown (also parsed from the newest
  exported project in the watch folder).
- **Go Live** runs the whole pipeline in one click: ensure the sidecar deps, start the publisher (capturing the
  document URL), deploy the config with auto-connect, and launch the game — which connects on the menu scene via
  the mod's `TickAutoConnect`.
- **Clean Up Live Sync** stops the launcher publisher, writes a one-shot stop command for the running game,
  clears captured Automerge connection state, removes stale status/session files, and disables auto-connect
  while preserving watch-folder and scene preferences.

Terminal equivalents: `robotopia ugc status [--watch folder]` (prints the handshake + watch-folder scenes) and
`robotopia ugc go-live`; use `robotopia ugc cleanup` to stop and clear transient live state.

## CLI: `ugc setup` and `ugc dev` (one-command authoring loop)

`robotopia ugc setup` configures live sync without the launcher: it persists the settings into the project's
`robotopia.project.json` **and** deploys the game runtime config in one shot.

```powershell
robotopia ugc setup --watch C:\ugc-watch --scene main --auto-connect
robotopia ugc setup --transport automerge --doc automerge:abc --sync https://my-sync-server
robotopia ugc setup --watch C:\ugc-watch --no-deploy     # project only (no game install needed)
```

The watch folder resolves as: `--watch` → the game's advertised default (status handshake) → `<project>/ugc-watch`
(created automatically). When no game install is detected the deploy step is skipped with a warning, so the
command works on authoring-only machines.

`robotopia ugc dev` is the dedicated **Unity-side** command: it sets up and launches a live-sync-connected Unity
Editor instance in one go.

```powershell
robotopia ugc dev --new my-world            # scaffold a Unity world project, then everything below
robotopia ugc dev                           # cwd Unity project, or most recently opened registered world
robotopia ugc dev --project my-world        # a registered project by name (or an explicit path)
robotopia ugc dev --launch-game             # also deploy + launch the game (full go-live loop)
robotopia ugc dev --dry-run                 # print the resolved plan (project, editor, folders) — no side effects
```

It resolves/creates the world project, ensures `Packages/com.robotopia.ugc-companion` (VPM-resolving deps;
`--update-companion` re-copies it), writes the **companion seed** `ProjectSettings/RobotopiaUgcCompanion.json`
(watch folder, scene, `liveSync: true`), deploys the game config with auto-connect, and launches the matching
Unity editor via `-projectPath`. With `--transport automerge` it also starts the publisher sidecar (detached,
`--session-file`) and injects the captured document URL into the game config.

On project load the companion's `UgcCompanionSeed` bootstrap applies the seed to the `UgcDevDaemon` EditorPrefs
and opens the **UGC Live Sync** window with Live Sync already ON — set an Export root and save the scene to
publish the first snapshot. The seed applies once per `seededUtc` stamp (recorded as `appliedUtc`), so it never
clobbers manual window changes on later domain reloads; re-running `ugc dev` re-arms it. Note that the daemon's
live store is still machine-global EditorPrefs — when switching between projects on the same machine, go through
`ugc dev` so the seed re-points the watch folder.

### Command reference

| Command | What it does |
|---|---|
| `robotopia ugc setup [--transport localFolder\|automerge] [--watch folder] [--sync url] [--doc url] [--scene id] [--auto-connect\|--no-auto-connect] [--debounce ms] [--max-snapshot bytes] [--project path] [--no-deploy]` | Persist live-sync settings to `robotopia.project.json` and deploy the game runtime config in one shot. |
| `robotopia ugc dev [--project path\|name] [--new name [--dir path]] [--watch folder] [--scene id] [--scene-name n] [--environment env] [--transport localFolder\|automerge] [--doc url] [--update-companion] [--launch-game] [--dry-run]` | Resolve/create the Unity world project, seed the companion, deploy the game config, and launch Unity connected; `--dry-run` prints the resolved plan with no side effects. |
| `robotopia ugc check [--watch folder] [--sync url]` | Verify Node + sidecar deps + resolved config without connecting. |
| `robotopia ugc status [--watch folder]` | Print the game's status handshake and the watch folder's scenes. |
| `robotopia ugc publish --file <project.json> [--sync url] [--doc url] [--scene id] [--session-file path]` | One-shot publish of an export JSON to an Automerge document (prints the document URL). |
| `robotopia ugc watch <folder> [--sync url] [--doc url] [--scene id] [--session-file path]` | Watch an export folder and re-publish on every change. |
| `robotopia ugc go-live` | Deploy the project's live-sync config with auto-connect on and launch the game. |
| `robotopia ugc cleanup [--project path]` | Request the running game to stop live sync, clear transient Automerge state, and deploy a non-auto-connect config. |

Ready to ship the mod that carries your content? See [PublishingYourMod.md](PublishingYourMod.md).

See [CreatorCompanion.md](CreatorCompanion.md) for the surrounding Creator-Companion workflow (projects, Unity
templates, and VPM packages).
