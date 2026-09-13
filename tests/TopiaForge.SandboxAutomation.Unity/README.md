# Sandbox Editor integration

Run from the repository root in PowerShell 7 on Windows with the installed, licensed Unity **6000.0.23f1**:

```powershell
./tools/run-sandbox-editor-automation.ps1
# Optional NEW physical private evidence directory; existing output is never replaced:
./tools/run-sandbox-editor-automation.ps1 -OutputDirectory 'D:\ReviewedPrivateEvidence\editor\fresh-run'
```

The runner builds this `netstandard2.1` fixture, stages its assemblies, copies the pinned authoring project into a fresh `.dart_tool/sandbox-editor/<run>/tools/unity-ui-bundle`, and starts only the Editor. A standard Windows identity/known-folder environment and private UPM cache preserve the existing licence. No activation arguments, game executable, OS input, user account creation or QA provisioning occur here. One total timeout covers the isolated build and both Editor processes; cleanup retains a bounded grace period. raw logs, source/assembly digests and screenshots remain in the printed private evidence directory.

The fixture compiles the **actual** `CreatorWorkbench` and `OwnerUiService` source files, with the actual `TopiaForge.Mods.UnityUi` assembly. `EditorContext` forwards non-UI calls to deterministic `FakeModContext` services. The existing smoke keeps its sixteen cycles and every baseline assertion; its adapter now counts actual `HotkeyRegistrationStore` entries and independently compares the dispatch-cache length instead of casting the store to `ICollection`.

The narrow friend-test declarations permit the same internal composition validation and graph retention as production; no test-only replacement workbench is used.

## Measurements and assertions

- A fixed GameView size is configured through an Editor-only adapter; the fixture requires **measured 1920×1080**, independently of command-line flags. It records actual DPI, GPU and graphics API.
- Button activation uses `EventSystem.RaycastAll` at measured widget centers followed by Unity pointer dispatch. The highest raycast result must belong to the requested tagged widget and have a click handler. These are synthetic Unity events, not physical mouse/keyboard or OS foreground-window evidence.
- Production search focus survives host accessibility changes. Actual graphic colors repaint, measured control height changes, and effective high contrast/scale/reduced-motion values are checked. Project actions and critical toolbar controls must fit their masks.
- Hide retains the deterministic session, releases the fake control lease and leaves a fully contained warning HUD. Reopen, destructive cancellation and confirmation use the real rendered buttons and modal.
- Ten additional Editor workbench open/hide/reopen/end cycles return deterministic session/control resources; final owner hosts, actual scene canvases, theme subscribers, cursor leases, dismiss scopes and diagnostic widget tags return to the independently captured baseline.
- Five deliberate mutations alter real Unity state: lost focus, out-of-bounds control, missing HUD, leaked canvas and leaked theme subscriber. Each must fail its corresponding normal assertion and be restored before continuing.

`TopiaForgeUiDiagnostics.Enable(ownerId)` must precede surface construction; its disposable lease releases tags. `Capture(ownerId)` reads fresh immutable primitive geometry/semantics on demand. There is no per-frame diagnostic worker, Unity-object return value or actuation method. Existing safe node ids are used, virtual rows are `list-id/item-id`, surfaces/body use `$surface`/`$body`, and modal buttons use `$modal` with `confirm`/`cancel`.

Three PNGs capture the production retained hierarchy: default workbench, accessibility profile, and hidden-session HUD. A temporary camera renders the actual overlay hierarchy into a texture; overlay mode is restored synchronously. Geometry and input assertions run against the normal overlay. This camera capture is explicitly recorded and is not a native game/device screenshot. Render outputs must contain actual color variation; semantic assertions remain authoritative.

## Diagnostics fields, tags and Undo (protocol v2)

`EditorWorkbenchChecks.Diagnostics.cs` exercises the protocol v2 additions of the [native matrix completion contract](../../docs/internal/launch/sandbox-native-matrix-completion.md) against the real rendered workbench: a selected roster row reports `Selected`, the catalog kind dropdown reports its caption as `Value`, measured `Foreground`/`Background` colours parse as `#rrggbb`, the chrome close button is tagged `$close`, renderer-created scroll containers are tagged `$scroll-0`, `$scroll-1` and `$scroll-2` in render order, a presented toast appears under the `$toast` surface as `toast-<sequence>` with its tone as style (and measures `visible: false` once pooled), and the roster `undo-last` control reverts a duplicate. `SandboxEditorAutomation.cs` refuses a run whose `editor-observations.json` lacks these check ids, so an older fixture cannot pass by omission. The five deliberate mutations above are unchanged.

## Evidence boundary

`runner-summary.json` (`sandbox-editor-run-v1`) records source revision **and dirty state**, exact source/assembly/Editor hashes, timeout/exit status, and `cleanupConfirmed` for the owned Editor processes. `editor-observations.json` (`sandbox-editor-observations-v1`) records measured checks, five negative detections, screenshot hashes and ten Editor workbench cycles. `ui-lifecycle-observations.json` is the existing sixteen-cycle smoke result from a **separate Editor process**; `ui-lifecycle-editor.log` remains separate too.

Exit 0 requires both Editor runs to pass. `qualifiesRelease` is always false. The observation provenance is local and unauthenticated. Screenshots are review artifacts, **not automatically accepted golden baselines**; `humanVisualApproval` remains false. These results do not close OS input, native saves/scenes, actual graph audio, game-control suppression, physical devices, independent testers or release gates. Keep the fourteen validator tests, sixteen authoring cycles, ten native game cycles, fifteen SDK cases and thirty-six gamemode cases distinct.

The protocol v2 diagnostics checks reproduced and repaired a third real defect on 2026-09-13: a pooled toast view re-presented after its dismissal kept the exit position and zero alpha of that dismissal, because `Restack` only slides and fades in views parked at the origin, so every re-presented toast stayed invisible. `Present` now resets the entrance position on every presentation; the failed run (`20260913T182939Z`) is preserved beside the passing rerun (`20260913T183403Z`, 216 checks, 5 negative detections, 16 lifecycle cycles). The new visual checks reproduced and repaired two real defects: the HUD column did not stretch into its panel (and restoration instructions exceeded its height); the seven project actions occupied a row wider than their non-horizontal-scroll column. HUD layout now provides room for the warning, and project commands occupy three short rows. Private failed runs are preserved alongside later passing evidence.

See the [implementation plan](../../docs/internal/launch/sandbox-automation-implementation-plan.md), [launch evidence](../../docs/internal/launch/Evidence.md), and [live acceptance matrix](../../docs/LiveGameAcceptance.md#creator-workbench-manual-matrix) for the separate remaining native and human requirements.
