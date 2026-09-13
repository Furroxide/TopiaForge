# Sandbox native matrix completion (stage 5 design and contract)

Prepared 2026-09-11 for the authorized stages 3–6 work. This document is the implementation contract that closes the [nine-row gap table](sandbox-automation-stages-3-6.md#native-matrix-implementation-gaps). It defines protocol v2 between the native observer, the Windows broker, the driver manifest and the independent verifier. It is not execution evidence: native rows still require the admitted QA session and the [evidence ledger](Evidence.md) records actual results. Every native result keeps `qualifiesRelease: false`.

Genuine product limitations stay explicit and are never turned into passing branches: there is no shipping global mutation bridge, no validated build-2409 vehicle adapter, no live remote transport, and no remote backend in the isolated QA session. Those subcases report `unavailable` with a precise reason.

## 1. UI diagnostics additions (`TopiaForge.Mods.UnityUi`, `TopiaForge.ModManager`)

`TopiaForgeUiDiagnosticWidget` gains four measured fields, all serialized into native `ui.widgets` rows by the observer:

| Field | Type | Meaning |
| --- | --- | --- |
| `selected` | bool | List rows: the row's rendered selected state (`TopiaForgeListRow.IsSelected`). Other widgets: `false`. |
| `value` | string | Dropdown: caption text of the current option. Toggle: `true`/`false`. Slider: invariant value text. Input: current text. Otherwise `""`. Bounded to 256 characters. |
| `foreground` | string | `#rrggbb` of the first `TMP_Text` in the widget subtree, or `""`. |
| `background` | string | `#rrggbb` of the widget's own `Image` if its alpha ≥ 0.5, else the nearest ancestor `Image` with alpha ≥ 0.5, or `""`. |

New tags:

- Scroll containers created by the safe renderer are tagged `$scroll-<n>` (kind `scroll`), `n` counting from 0 in render order per surface render.
- The window/fullscreen chrome close button is tagged `$close` (kind `button`) on its surface.
- Toasts are tagged on the toast host owner `TopiaForgeToasts.DiagnosticsOwnerId` (`io.github.furroxide.topiaforge.ui.toasts`) with surface `$toast`, node `toast-<sequence>` (process-monotonic), kind `toast`, text = message, style = tone name. A toast that is pooled/inactive reports `visible: false`.

The Sandbox workbench gains one rendered control: `undo-last` (`Undo`, ghost style) in the roster action row, enabled while the workbench history is non-empty. It calls the existing `Undo()`; no new undo semantics are introduced.

## 2. Driver manifest v2 and broker actuation (`tools/TopiaForge.Acceptance.Windows`)

The manifest kind becomes `sandbox-native-driver-actions-v2` in `tests/TopiaForge.SandboxAcceptanceNative/driver-actions-v2.json`; v1 is removed. Closed vocabularies:

- Keys: `F5`, `Escape`, `Tab`, `W`, `Down`, `Up`, `Return`, `Space`, `MouseLeft`.
- Nodes: the v1 set plus `undo-last`, `catalog-kind`, `$close`, `$scroll-0`, `$scroll-1`, `$scroll-2`.
- Request operations: `unregister-source`, `request-session-stop`, `external-write`, `destroy-borrowed`, `register-competing-host`, `unregister-competing-host`, `control-cue`, `stop-control-cue`, `spawn-control-robot`, `despawn-control-robot`, `accessibility-high-contrast`, `accessibility-scale-150`, `accessibility-reduced-motion`, `accessibility-reset`.
- New atoms:
  - `scroll-into-view` `{nodeId, containerId}`: wheel inside the container's measured rect (3 ticks per attempt, at most 20 attempts) until the target widget reports `clipped: false`; refuses when the container is absent or the target never becomes visible. The wheel point is the vertical centre of the tallest horizontal band of the container not covered by any visible nested `scroll` or `list` widget (nested lists consume the wheel), inset by at least 4 px; a container with no such band is refused. The event records `probeX`/`probeY` (bottom-left client coordinates, `null` before any burst) and per-sample `{observedFrame, clipped, direction, probeX, probeY}`. No coordinate substitution.
  - `mouse-move` `{dx, dy}`: one relative `MOUSEEVENTF_MOVE`, each component in −400…400.
  - `key-hold` `{key, milliseconds}`: key down, wait 50…1000 ms, key up; always released in `finally`; the event records `requestedMilliseconds`, `heldMilliseconds` and `released`.
  - `aim` `{fact, maxIterations, gain}`: closed loop on the observer fact named by `fact` (`aimToGraphProp`): each iteration observes, then moves the mouse by `−yawDegrees·gain, −pitchDegrees·gain` (gain 1…20, at most 40 iterations, each component clamped to ±400 and recorded as `clamped`) until the fact reports `focused: true`; failure to converge is a failed step. The event records `iterations`, `converged` and every sample.

Manifest encoding: `schemaVersion` 2; a scenario carries `cycleSteps: [{cycle, steps}]` when its cycles differ and plain `steps` otherwise. A v1 manifest is refused with a message naming the retired kind. The per-atom `input` event is unchanged and follows the atom's measured event; persistence snapshots carry phase `before`, `during` (right after `begin`) and `after` for every scenario.

Screen baselines: the device profile may carry `screenBaselines: {root, entries: [{scenarioId, cycle, step, path, sha256, tolerance, masks: [{x, y, width, height}]}]}`. `root` is an absolute physical directory of reviewed BMP baselines. For a matching capture the broker records `baseline: {path, sha256, mismatchFraction, withinTolerance}` on the capture event; without a matching entry it records `baseline: null`. Baselines are never written by the broker.

## 3. Fixture and observer protocol v2 (`tests/TopiaForge.SandboxAcceptanceMod`, `tests/TopiaForge.SandboxAcceptanceNative`)

Wire request/response fields are unchanged; `operation` accepts the extended vocabulary above. Operations outside `prepare/begin/capture/advance/cleanup` are accepted only for the prepared cycle and only when the observer's expected next action matches (`external-write` → `external-write`, and so on), exactly like `unregister-source` today.

Operation semantics:

| Operation | Owner | Effect | Cleanup |
| --- | --- | --- | --- |
| `external-write` | native mod | Moves the borrowed fixture robot natively by +2 m on X, outside any lease. | None; End Session must preserve it and report a conflict. |
| `destroy-borrowed` | native mod | Destroys the borrowed fixture robot immediately and marks it retired. | `cleanup` treats an absent borrowed robot as complete. |
| `register-competing-host` / `unregister-competing-host` | safe fixture | Registers/removes an `ICreatorToolHost` (`competing`, priority 100) under the fixture owner that counts `CanOpen`, `Open`, `Close` calls and never opens. | `cleanup` unregisters it. |
| `control-cue` / `stop-control-cue` | safe fixture | Plays/stops a looping unrelated cue with id `sandbox-acceptance-control` through `Context.Audio`. | `cleanup` stops it. |
| `spawn-control-robot` / `despawn-control-robot` | safe fixture | Spawns/despawns one dormant RobotKit robot owned by the fixture, not by any content source. | `cleanup` despawns it. |
| `accessibility-*` | native mod | Sets global `TopiaForgeTheme` preferences (`HighContrast`, `UiScale` 1.5, `ReducedMotion`) or resets them. | `cleanup` resets. |

New facts (all captured per exchange, missing values are `unavailable` reasons, never zero):

| Fact | Type | Source |
| --- | --- | --- |
| `toasts` | array of `{nodeId, text, style, visible}` | `$toast` widgets of the toast host owner |
| `catalogSources` | array of `{id, displayName, state, entryCount}` | `ICreatorContentService.Catalog.Sources` |
| `interactions[].prompt` | string | production bridge definition prompt (added to the existing rows) |
| `focusedInteraction` | `{available, entityInstanceId}` | GameCode `UIState.InteractTarget` bridge, by reflection |
| `aimToGraphProp` | `{available, yawDegrees, pitchDegrees, distance, focused}` | camera ray versus the graph prop registered with prompt `ACCEPTANCE` |
| `playerAim` | `[x, y, z]` | local player aim ray direction |
| `personalityAssetIds` | int array (≤ 512) | all live `PersonalityAsset` instance ids |
| `audioSourceIds` | int array (≤ 512) | live `AudioSource` instance ids whose object name starts with `TopiaForge.Audio.` |
| `controlCuePlaying` | bool | fixture control cue playback |
| `controllerInstanceId` | int or null | `RuntimeHelpers.GetHashCode` of the live `SandboxController` reached through the Worlds session by allowlisted reflection |
| `projectRunning` | bool | workbench runner is running (allowlisted reflection) |
| `undoDepth` | int | workbench history count (allowlisted reflection) |
| `competingHost` | `{registered, canOpenCalls, openCalls, closeCalls}` | safe fixture |
| `accessibility` | `{highContrast, uiScale, reducedMotion, motionIntensity}` | `TopiaForgeTheme` |
| `borrowedRobotDestroyed` | bool | native mod |
| `controlRobotInstanceId` | int or 0 | safe fixture control robot |

## 4. Scenario recipes v2

Cycle counts: routing 2, catalog-editing 1, borrowed-robot 3, source-unload 2, hide-reopen 1, persistence-refusal 1, graph-rollback 2, lifecycle-routes 3, ten-cycles 10. Every step below is an observer postcondition and, independently, a verifier postcondition computed from the transcript.

| Scenario | Steps (v2) and independent postconditions |
| --- | --- |
| routing c1 | `open`; `accessibility-high-contrast` (all Sandbox widgets `highContrast: true`; every text/button/input widget with both colours has WCAG contrast ≥ 4.5); `accessibility-scale-150` (`uiScale` 1.5 and `hide-workbench` height ≥ 1.4× its baseline height); `accessibility-reduced-motion` (`reducedMotion: true`, `motionIntensity: 0`); `accessibility-reset`; `focus-next` (key `Tab`: the focused widget changes to a different node); `hide-f5` (key F5: hidden, session retained, HUD says `SESSION ACTIVE`); `reopen`; `duplicate-toggle` (two F5 taps 150 ms apart: hidden then visible, `creatorSessionCount` unchanged, `worldSessionId` unchanged); `end-session`. |
| routing c2 | `register-competing-host`; `open` (`activeHostId` is Sandbox, `competingHost.openCalls == 0`); `hide-f5`; `reopen` (still Sandbox, `openCalls == 0`); `end-session`; `unregister-competing-host` (`competingHost.registered == false`). |
| catalog-editing | `open`; `search-nonmatching` (`catalog-list` has zero rows and `spawn-selected` is disabled); `filter-robots` (`catalog-kind` value `Robots`, every `catalog-list/*` row id starts with `robotkit:`); `filter-all` (value `All content`); `$catalog` per entry: `spawn-catalog` (exactly one new live entity; `roster-list/<new roster id>` `selected: true`), `edit-transform` when position capable (position delta exactly (0, +1, 0) ± 0.001), `edit-rotation` when capable (rotation exactly (0, 0.7071068, 0, 0.7071068) ± 0.001), `edit-scale` when capable (scale exactly 1.25 ± 0.001), `duplicate` (one more entity, offset (+1, 0, +1) ± 0.01 from the source), `undo` (click `undo-last`: the duplicate is gone, `undoDepth` decreased by 1), `remove` (one fewer entity); `end-session`. Verifier additionally checks the reviewed expected inventory (section 5). |
| borrowed-robot c1 | as v1; `end-session` restores transform, brain and personality; the previewed `hackedPersonalityId` is absent from `personalityAssetIds` two frames after cleanup. |
| borrowed-robot c2 | `open`, `select-borrowed`, `edit-transform`, `edit-personality`, `external-write` (borrowed position X grows by 2 ± 0.01), `end-session` (a `$toast` with style `Warning` whose text contains `outside Creator Tools` or `restoration warnings`; borrowed position equals the external position, not the original; personality restored). |
| borrowed-robot c3 | `open`, `select-borrowed`, `edit-transform`, `destroy-borrowed` (`borrowedRobot` null, `borrowedRobotDestroyed: true`), `end-session` (no resurrection: `borrowedRobot` stays null, `cleanupErrors` empty, `robotEditLeaseCount` back to baseline). |
| source-unload c1 | `open`, `spawn-prop`, `duplicate`, `spawn-character` (catalog row `content:dev.topiaforge.sandbox-acceptance:character`, one new live robot), `spawn-control-robot`, `unregister-source` (fixture content and character gone, control robot alive, catalog lacks `dev.topiaforge.sandbox-acceptance:` ids, `catalogSources` still lists `robotopia.vehicles` as `Unavailable`), `end-session` (control robot still alive), `despawn-control-robot`. |
| source-unload c2 | `open`, `spawn-prop`, `run-graph`, `unregister-source` while running (graph-owned fixture objects gone, `cleanupErrors` empty, `projectRunning` false or graph stopped cleanly), `end-session`. |
| hide-reopen | `open`, `spawn-prop`, `select-borrowed`, `edit-transform`, `run-graph`, `move-while-visible` (`key-hold` W 300 ms: player position unchanged within 0.005), `hide-close` (click `$close`: hidden, session retained, HUD `SESSION ACTIVE`, `cursorLeaseCount` baseline), `move-player` (moved), `camera-hidden` (`mouse-move` 120,0: `playerAim` changed), `reopen` (`projectRunning: true`, graph audio still playing, borrowed transform still edited), `text-focus` (click `catalog-search`, type `W`: field text `W`, player position unchanged), `hide-f5`, `reopen`, `stop-graph`, `end-session`. |
| persistence-refusal | `observe-refusal` plus rendered reason: a Sandbox status caption contains `Sandbox isolation active.`; broker records persistence snapshots `before`, `during` (after `begin`) and `after`. Global denial/revocation subcases report `unavailable: no shipping global mutation bridge`. |
| graph-rollback c1 | `open`, `spawn-prop`, `control-cue` (`controlCuePlaying: true`), `run-graph`, `hide-f5`, `aim-graph-prop` (`aim` on `aimToGraphProp`), `interact` (key `MouseLeft`: a `$toast` containing `Sandbox acceptance interaction` and none containing `WRONG BRANCH`; `interactionCount` unchanged), `reopen`, `stop-graph` (graph cue absent in retained audio while the control cue remains detectable), `stop-control-cue`, `end-session` (graph audio released: every id in the observer's `graphAudioIds` is absent from `audioSourceIds` or listed in `audioSources` as `playing: false` under the idle pool name, never a name containing the graph cue id; the shipping audio service pools at most 24 idle sources instead of destroying them). |
| graph-rollback c2 | `open`, `spawn-prop`, `stop-before-start` (capture only: `stop-project` reports `enabled: false` while `projectRunning` is false, and the entity set is unchanged from the previous step), `run-graph`, `stop-graph`, `run-graph`, `stop-graph`, `end-session`. |
| lifecycle-routes c1 | `open`, `spawn-prop`, `stop-world-session` (StopAsync → Idle). |
| lifecycle-routes c2 | `open`, `spawn-prop`, `stop-world-session` (RestartAsync: the original session has left Running, shown by a phase other than Running or a new session id, and the workbench is not visible), `toggle-during-transition` (the broker sends F5 only after the observer advances, so this step asserts the restarted state: Running with a new session id and a different `controllerInstanceId`, no host, `creatorSessionCount` unchanged), `move-player` (input restored; compared with the previous step's session). |
| lifecycle-routes c3 | `open`, `spawn-prop`, `stop-world-session` (ReturnToMainMenuAsync), `toggle-in-menu` (F5 in Idle: `activeHostId` empty, `creatorSessionCount` unchanged). |
| ten-cycles | `open`, `spawn-prop`, `select-borrowed`, `edit-transform`, `edit-personality`, `edit-brain`, `hide-f5`, `move-player`, `camera-hidden`, `reopen`, `run-graph`, `stop-graph`, `end-session`; per cycle the verifier also requires: the previewed personality id destroyed (absent from `personalityAssetIds` two frames after cleanup), the graph audio ids released as defined for graph-rollback c1, `controllerInstanceId` unchanged within the cycle, and every interaction registration of the previous cycle inactive. |

Fault injection: every new postcondition has a synthetic transcript mutation in the Dart tests that must flip the scenario to `failed`, and every new broker atom has a refusal test in the broker self-test.

## 5. Reviewed expected inventory

`tests/TopiaForge.SandboxAcceptanceNative/expected-catalog-2409-v1.json` (`schemaVersion` 1, kind `sandbox-native-expected-catalog-v1`, `gameBuild` 2409) declares the reviewed inventory: `reviewed` (bool), `sources` (`id`, `expectedState`, `minimumEntries`) and `entries` (`rowId`, `kind`, `transformCapabilities`). The verifier resolves it beside the driver manifest. The verifier fails `catalog-editing` when a listed entry or source expectation is missing from the observed catalog. While `reviewed` is `false` the scenario cannot pass: it reports `unavailable` with reason `expected inventory baseline not reviewed`, even when every listed entry is present. A native run populates the list; a maintainer reviews it and sets `reviewed: true` in a separate change.

## 6. Residual limitations after this work

- Global mutation success, native vehicles, genuine remote participants and backend conversation completion remain `unavailable` by product scope.
- Human baseline acceptance of screenshots, physical audibility and subjective usability remain human checks.
- Native execution still requires the admitted QA session, reviewed device profile and, for visual baselines, reviewed baseline images.
