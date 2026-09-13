# Private Windows Sandbox observation broker

This non-distributable Windows x64 developer tool belongs to the [Sandbox automation handoff](../../docs/internal/launch/sandbox-automation-stages-3-6.md) and implements the broker side of the [native matrix completion contract](../../docs/internal/launch/sandbox-native-matrix-completion.md). It is not a launcher, release qualifier or general desktop automation endpoint.

```powershell
dotnet run --project tools/TopiaForge.Acceptance.Windows -c Release -- --self-test
dotnet publish tools/TopiaForge.Acceptance.Windows -c Release -r win-x64 --self-contained false -o '<new-private-tool-directory>'
```

The contract tests use synthetic inputs, pixels and WAV bytes; they do not open a device, inject input or start a game. Run the self-test from the repository root: it loads the checked-in `tests/TopiaForge.SandboxAcceptanceNative/driver-actions-v2.json` and writes only one throwaway BMP under the temp directory. The published executable requires the .NET 10 Windows x64 runtime.

## Native execution

Use `topiaforge acceptance sandbox run` through the admitted [local lane](../run-sandbox-local-lane.ps1). The CLI retains the original game process, installs exact package bytes, verifies isolation and starts this broker with `--request <private-request.json>`. A request binds the run challenge, manager session, original game creation token, executable path, Windows primary-token identity, reviewed isolation record, device profile and driver manifest. Do not manufacture a request to bypass admission.

`--probe <new-private-observation.json>` can collect actual primary-token/known-folder identity and the default render endpoint from a signed-in interactive QA session. It neither opens a microphone nor approves isolation. First sign-in, actual Unity persistence-root observation and the private provisioning review remain separate prerequisites.

The broker opens a query-only handle to the exact game process and checks its foreground window, client geometry, DPI, monitor and session before actuation. It only sends declared keys, measured widget clicks, bounded synthetic text, measured wheel bursts over a declared scroll container, bounded relative mouse moves and bounded key holds. Missing, disabled or clipped controls stop the run; unmeasured coordinates are never substituted, and a control that stays clipped after the declared wheel attempts is a refusal, not a guess. A challenge-bound local named pipe identifies the same game process and exposes only finite fixture operations. Native progress hints schedule actions; they do not establish passing results.

Screenshots capture the actual foreground game client as bounded BMP files. WASAPI captures the approved render endpoint's loopback mix, which can include other applications or sessions using that endpoint. The microphone is never opened. Keep that endpoint reserved during a run. Independent verification reads the actual WAV and BMP bytes, checks reported measurements and detects the fixture graph's 599 Hz cue and its disappearance after stop. These checks do not replace human judgments about image or sound quality.

Persistence observation is read-only and limited to the admitted QA root and explicit file allowlist. It records `before` (prior to `begin`), `during` (immediately after `begin` and the first screenshot) and `after` (after fixture cleanup) hashes, watcher events and overflow. No save deletion, credential access, global mutation bridge or live backend call is part of this tool.

## Driver manifest v2

The broker accepts only `kind: "sandbox-native-driver-actions-v2"` with `schemaVersion: 2`; a `sandbox-native-driver-actions-v1` document is refused with the message `Driver manifest kind sandbox-native-driver-actions-v1 is retired; sandbox-native-driver-actions-v2 (schemaVersion 2) is required.` Top-level fields mirror v1. `operations` must equal the closed wire vocabulary in order: `prepare`, `begin`, `capture`, `advance`, the fourteen request operations below, `cleanup`. Cycle counts are fixed: routing 2, catalog-editing 1, borrowed-robot 3, source-unload 2, hide-reopen 1, persistence-refusal 1, graph-rollback 2, lifecycle-routes 3, ten-cycles 10. A scenario declares either `steps` (every cycle runs the same list) or `cycleSteps: [{cycle, steps}]` with exactly one entry per cycle in order; `catalog-editing` keeps `inventoryExpansion` (per entry: `spawn-catalog`, capability-gated `edit-transform`/`edit-rotation`/`edit-scale`, `duplicate`, `undo`, `remove`) and `lifecycle-routes` keeps `routes` plus `freshSessionPerCycle: true`. Every declared action must have a recipe of 1–12 atoms, and every step name must be a declared action (`$catalog` only in `catalog-editing`).

Closed vocabularies:

- Keys: `F5`, `Escape`, `Tab`, `W`, `Down`, `Up`, `Return`, `Space`, `MouseLeft` (the left button pressed at the current cursor position).
- Nodes: the v1 set plus `undo-last`, `catalog-kind`, `$close`, `$scroll-0`, `$scroll-1`, `$scroll-2`. Surfaces: `sandbox-creator-window` (default) and `$modal`.
- Request operations: `unregister-source`, `request-session-stop`, `external-write`, `destroy-borrowed`, `register-competing-host`, `unregister-competing-host`, `control-cue`, `stop-control-cue`, `spawn-control-robot`, `despawn-control-robot`, `accessibility-high-contrast`, `accessibility-scale-150`, `accessibility-reduced-motion`, `accessibility-reset`.
- Dynamic facts: `textFromFact` only `actionCatalogDisplayName`; `itemIdFromFact` only `borrowedRosterId`, `projectId`, `actionCatalogRowId`; `aim` facts only `aimToGraphProp`.

Atoms (v1 atoms unchanged unless noted):

| Atom | Fields | Bound and refusal |
| --- | --- | --- |
| `key` | `key` | Declared key only; 100 ms tap, released in `finally`. |
| `click`, `select-list-item`, `barrier`, `request`, `capture` | as v1 | `request.operation` must be a declared request operation. |
| `replace-text` | `nodeId`, `text` or `textFromFact` | v2 additionally accepts `text: ""`, which selects all and sends Backspace to clear the field. |
| `scroll-into-view` | `nodeId`, `containerId`, optional `itemIdFromFact` | Target must be a declared node (or declared list row), container one of `$scroll-0..2` and not the target. Observes the target; while it reports `clipped: true`, plans a wheel probe inside the container's measured rect but outside every visible nested widget of kind `scroll` or `list` on the surface (the catalog, roster and project virtual lists own a ScrollRect that would otherwise consume the wheel): each such rect blocks its full vertical span across the container, the tallest remaining horizontal band (at least 9 px high; ties prefer the lowest band) is chosen, and the probe is that band's vertical centre at the container's horizontal centre, so it stays at least 4 px from every edge. The cursor moves there and 3 wheel ticks are sent (direction from the measured centres: target below the container centre scrolls down), then the broker waits two frames. At most 20 attempts. Refuses when the target or container is absent from the measured surface, the container is hidden, no uncovered band exists (`Scroll container has no wheel point outside its nested scrolling widgets.`), the container is narrower or shorter than 9 px, or the target never reports `clipped: false`. Coordinates come only from the observed geometry. |
| `mouse-move` | `dx`, `dy` | Integers in −400…400; one relative `MOUSEEVENTF_MOVE`. |
| `key-hold` | `key`, `milliseconds` | Declared key; 50…1000 ms; key down, measured wait, key up in `finally`. |
| `aim` | `fact`, `maxIterations`, `gain` | `fact` = `aimToGraphProp`; `maxIterations` 1…40; `gain` 1…20. Each iteration observes the fact, which must be an object with exactly the keys `available`, `yawDegrees`, `pitchDegrees`, `distance`, `focused` (extra or missing keys, a non-object or `available: false` is a failed step) and, unless `focused` is true, moves the mouse by `round(-yawDegrees*gain), round(-pitchDegrees*gain)` clamped to ±400 per component (clamping is recorded), then waits two frames. Not converging within `maxIterations` is a failed step. |

Screenshots follow these steps: `open`, `reopen`, `hide-f5`, `hide-close`, `end-session`, `stop-world-session`, the four `accessibility-*` steps, `duplicate-toggle`, `toggle-during-transition`, `toggle-in-menu`, `search-nonmatching`, `filter-robots`, `filter-all`, plus one capture right after `begin` (recorded under step `prepare`). Loopback audio is captured after `run-graph`, `stop-graph`, `control-cue` and `stop-control-cue`.

Recipe assumptions to confirm on the first native run: `$scroll-0` is the left catalog/roster column, `$scroll-1` the project column and `$scroll-2` the transform/robot column (render order of the workbench's three scroll views); `catalog-kind` is a stock TMP dropdown whose opened list selects the current option, so `Down`/`Up` then `Return` change it by one position (options: All content, Robots, Characters, Items, Props, Vehicles); the `aim` gain of 5 assumes roughly 0.1–0.3° of camera yaw per mouse count.

## Screen baselines

The device profile (`sandbox-device-profile-v1`) may add `screenBaselines: {root, entries: [{scenarioId, cycle, step, path, sha256, tolerance, masks: [{x, y, width, height}]}]}`. `root` must be an absolute, physical (no mapped drive, no reparse point), existing directory; each `path` is a safe relative `.bmp` path beneath it; `sha256` is the lowercase digest of the reviewed file; `tolerance` is a number in 0…1; at most 256 entries, at most 64 masks per entry; `(scenarioId, cycle, step)` must be unique, with `step` a declared action or `prepare`. Masks use top-left image pixel coordinates and must lie inside the admitted display.

For every screenshot whose `(scenarioId, cycle, step)` matches an entry, the broker reads the baseline (exactly the broker's own top-down 32-bit BMP layout at the admitted dimensions), requires its bytes to hash to the entry's `sha256` before any comparison (a mismatch is a refusal), then computes `mismatchFraction` = mismatched unmasked pixels ÷ unmasked pixels, where a pixel mismatches when its largest B, G or R channel difference exceeds **8** on the 0–255 scale (alpha is ignored). A mask set covering every pixel is refused. `withinTolerance` is `mismatchFraction <= tolerance`; it is recorded, never enforced by the broker. Baselines are never written or modified by the broker.

## Transcript events

Every event carries `{sequence, scenarioId, cycle, stepIndex, step, kind, elapsedMilliseconds, data}`. The `data` fields per `kind`:

- `input` (unchanged, one per atom, recorded after the atom's own measured event below): `{action, kind, surfaceId, nodeId, observedFrame, sentEvents}`; `nodeId` is the atom's declared node (the list id for dynamic rows), `sentEvents` counts OS events the atom sent.
- `scroll` (`scroll-into-view`): `{nodeId, containerId, attempts, ticksPerAttempt, ticks, probeX, probeY, samples, visible}` — `nodeId` is the full target id (`list-id/row-id` for dynamic rows), `attempts` the wheel bursts sent, `ticksPerAttempt` 3, `ticks` = `attempts * 3`, `probeX`/`probeY` the bottom-left client point (numbers) where the most recent wheel burst was delivered or `null` when no burst was sent, `samples` one per observation `{observedFrame, clipped, direction, probeX, probeY}` with `direction` `"down"`, `"up"` or `""` when no burst followed and the sample's own `probeX`/`probeY` (`null` when no burst followed), `visible` true only when the last sample reports `clipped: false`. Recorded even when the atom fails.
- `mouse-move`: `{dx, dy}` as sent.
- `key-hold`: `{key, requestedMilliseconds, heldMilliseconds, released}` — `heldMilliseconds` is the measured time between the down and up events (`null` if the hold aborted before measurement), `released` the input state after the atom.
- `aim`: `{fact, gain, maxIterations, iterations, converged, samples}` — `samples` one per iteration `{iteration, observedFrame, yawDegrees, pitchDegrees, distance, focused, dx, dy, clamped}` where `dx`, `dy` are the move sent after that observation (0, 0 on the converging sample) and `clamped` says the computed move exceeded ±400. Recorded even when the atom fails.
- `capture`: `{path, width, height, distinctSampleColors, sha256, length, baseline}` — `baseline` is `null` without a matching profile entry, else `{path, sha256, mismatchFraction, withinTolerance}`.
- `audio`, `observation`, `failure`, `cleanup`: unchanged. `persistence`: `{phase, files, changedPaths, overflow, unexpectedWrite}` with `phase` now `before`, `during` or `after`.

## Cancellation and evidence

The CLI writes a challenge-bound, immutable `broker-cancel.json` on interruption, output overflow or timeout. The broker cancels work, attempts bounded fixture cleanup and releases its own keys/buttons in `finally`. The parent waits twenty seconds before a last-resort termination of its original broker process. Forced termination cannot establish successful cleanup. The broker never terminates a game by PID; the parent owns game exit through its original launch receipt.

Private `broker-transcript.json`, `broker-result.json`, screenshots and WAV files preserve correlated actions, observations, first failures and cleanup status. Existing outputs are not overwritten. The independent CLI verifier binds source, runtime, package and artifact hashes and recomputes per-scenario outcomes. All evidence remains supplementary development evidence with `qualifiesRelease: false`; unavailable or unexecuted branches remain incomplete.

Native execution and end-to-end validation are pending QA admission. Current limitations and exact recorded tests belong in the [launch evidence](../../docs/internal/launch/Evidence.md), not an inferred release approval.
