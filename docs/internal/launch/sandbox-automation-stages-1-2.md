# Sandbox automation stages 1–2: implementation handoff

Updated 2026-09-09. The user authorized the scenario specification/verifier and offline lifecycle/rollback implementation, including tests. Those source changes and applicable local verification are complete; exact results and preserved failures are recorded in [Evidence](Evidence.md#sandbox-automation-stages-1-2-verification). Later stages remain governed by the [implementation plan](sandbox-automation-implementation-plan.md) and [Decisions](Decisions.md).

This is maintainer documentation. The implementation supplies offline contracts and regressions, with no new Editor/game execution, QA provisioning, candidate qualification or release-gate change.

## What exists

The [nine-scenario specification](../../../tests/sandbox-workbench-acceptance-v1.json) pins schema version 1, game build 2409 and scope `supplementary-offline-contracts`. Each row names fixture actions, cleanup, negative cases, typed oracles and residual requirements. The [specification schema](../../../schemas/topiaforge.sandbox-workbench-acceptance-v1.schema.json) and [observation schema](../../../schemas/topiaforge.sandbox-workbench-offline-observations-v1.schema.json) document the two separate input formats.

The framework-independent [specification parser](../../../apps/topiaforge_cli/lib/src/sandbox_acceptance/sandbox_specification.dart), [bounded JSON helpers](../../../apps/topiaforge_cli/lib/src/sandbox_acceptance/sandbox_json.dart) and [observation verifier](../../../apps/topiaforge_cli/lib/src/sandbox_acceptance/sandbox_verifier.dart) calculate outcomes from supplied before/after measurements. The [developer tool](../../../apps/topiaforge_cli/tool/verify_sandbox_observations.dart) reads two bounded ordinary files and prints the verification result. It performs no installation, process launch or gate write.

There is no integrated C# measurement exporter, native report generator, admitted observation broker or production `acceptance sandbox run` command yet. Current C# regressions execute their assertions directly. The Dart [test fixture](../../../apps/topiaforge_cli/test/sandbox_acceptance_fixture.dart) deliberately fabricates input only to test verifier behavior; its documents must never be retained as measured execution evidence. The proposed native `sandbox-workbench-automation-v1` annex remains future work, separate from the implemented offline observation format.

## Developer commands

From the repository root, run the relevant C# suites:

```powershell
dotnet run --project tests\TopiaForge.ModManager.Tests\TopiaForge.ModManager.Tests.csproj -c Release -- --creator-workbench
dotnet run --project tests\TopiaForge.ModManager.Tests\TopiaForge.ModManager.Tests.csproj -c Release -- --creator-event-graph
dotnet run --project tests\TopiaForge.ModManager.Tests\TopiaForge.ModManager.Tests.csproj -c Release -- --module-contracts
```

Run Dart commands from `apps/topiaforge_cli`. Replace the quoted observation placeholder with an ordinary file containing separately measured offline facts; the tool does not collect those facts:

```powershell
Set-Location apps\topiaforge_cli
dart analyze
dart test
dart run tool/verify_sandbox_observations.dart ..\..\tests\sandbox-workbench-acceptance-v1.json '<absolute path to offline-observations.json>'
```

Exit `0` means every supplied offline scenario meets its contract. Exit `1` means at least one scenario is failed or missing; exit `2` means malformed, unsupported or unreadable input; exit `64` means incorrect arguments. A zero exit never establishes game acceptance or release readiness.

## Observation contract and trust boundary

The observation root contains exactly `schemaVersion`, `kind`, `scope`, `specSha256`, `runId`, `sourceRevision` and `observations`. Required identity values are integer `1`, `sandbox-workbench-offline-observations-v1` and `supplementary-offline-contracts`. `runId` is a bounded lowercase hyphenated identifier; `sourceRevision` must be 40 lowercase hexadecimal characters.

`specSha256` must equal the lowercase SHA-256 of the exact specification bytes supplied to this invocation. Whitespace and line-ending changes therefore change the binding. This detects substitution against those bytes; it does not authenticate the specification's author, the reported revision or the observer. Preserve the actual spec bytes with any private observation record.

Each observation has only `scenarioId` and `samples`. Each sample has only a one-based integer `sequence`, `before` and `after`. Both measurement maps must contain exactly that scenario's oracle names. Counts are integers from 0 through 1,000,000; fingerprints are 64 lowercase hexadecimal characters; labels are bounded lowercase alphanumeric/hyphen strings starting with a letter. A fingerprint is a supplied observation: the verifier validates its shape and comparison, without reading or authenticating a referenced game artifact.

The fixed scenario order is `routing`, `catalog-editing`, `borrowed-robot`, `source-unload`, `hide-reopen`, `persistence-refusal`, `graph-rollback`, `lifecycle-routes`, `ten-cycles`. The specification must contain all nine in that order. Observations may arrive in a different order but cannot repeat or invent a scenario. A missing row produces `missing`; an observed row produces `passed` or `failed` from its measurements.

Rows 1–8 require one sample each; `ten-cycles` requires ten complete samples numbered 1 through 10. Every positive `equals` count is an exercise counter: its `before` value must be zero and its `after` value must equal the prescribed count for that sample. Reusing a prior nonzero counter fails. An `unchanged` oracle compares after with before, and its baseline must remain identical across all samples of the row. Every intermediate cycle is checked; later cleanup cannot erase an earlier failure or disguise growth by moving the baseline.

Both parsers reject unknown/missing fields, duplicate JSON properties, unsupported identity/version, invalid types and structural excess. Each document is bounded to 256 KiB, depth 16 and 12,000 nodes, with bounded strings and collections. JSON Schema validation alone cannot establish spec-dependent metric names, byte binding, stable baselines or passing results; the application verifier remains necessary. Driver `PASS` fields and native-scope claims are outside the grammar.

Every result reports unauthenticated offline provenance and `qualifiesRelease: false`, even if all nine statuses are `passed`. The checked-in spec retains **25 residual requirements**: 15 require an environment, four describe unimplemented capabilities and six require human review. They span Editor, game, OS input, physical-device and human lanes. Unsupported global persistence success, native vehicles and genuine remote-session branches remain explicit; none becomes passed through a fake substitute.

## Coverage map

This maps existing and new C# assertions to requirements; it is not an observation report or a claim that the harness exports every specification metric.

| Spec row | Current executable regression coverage | Remaining distinction |
| --- | --- | --- |
| 1 Routing | [CreatorContentTests](../../../tests/TopiaForge.ModManager.Tests/CreatorContentTests.cs): router priority, one configurable hotkey and registration retirement; [runner tests](../../../tests/TopiaForge.ModManager.Tests/CreatorEventGraphRunnerTests.cs): simulated multiplayer policy. | Synthetic input/callbacks do not prove real F5, rendering or connected remote peers. |
| 2 Catalog/editing | Content session bounds, spawn/duplicate ownership and factory failures; [scene-adapter tests](../../../tests/TopiaForge.ModManager.Tests/CreatorSceneAdapterTests.cs): identity, exclusive edits, restoration and reservation release. Workbench fixtures use real declarative callbacks over fake services. | Every shipped native catalog type, hit region and visual selection still needs its own admitted fixture and observation. |
| 3 Borrowed robot | [Rollback tests](../../../tests/TopiaForge.ModManager.Tests/SandboxWorkbenchRollbackTests.cs): transform/rotation/scale, brain and personality identity restoration; outside-writer conflicts; missing targets; rejected previews; throwing cleanup. | Fake state does not establish actual Unity object destruction or native brain/personality restoration. |
| 4 Source unload | [Existing lifecycle tests](../../../tests/TopiaForge.ModManager.Tests/CreatorWorkbenchLifecycleTests.cs) prune source-owned content; [graph lifecycle tests](../../../tests/TopiaForge.ModManager.Tests/SandboxGraphLifecycleTests.cs) exercise reentrant remove/spawn and same-target removal with one disposal/event. | Actual manager package unload and native character/vehicle observations remain later work. |
| 5 Hide/reopen | Existing lifecycle and new rollback/graph loops retain sessions, edits and graph resources while releasing/reacquiring control ownership; captured HUD text remains covered. | Lease and captured-node checks do not measure real movement, camera, focus or visible HUD usability. |
| 6 Persistence refusal | Unavailable mutation actions fail closed, including queued callbacks beyond disabled controls. Ten synthetic acknowledgement/edit/spawn/revocation cycles return ownership and borrowed state to baseline. | No QA save/checkpoint bytes are measured by these tests; no shipping successful global persistence bridge was added. |
| 7 Graph rollback | Nine graph lifecycle cases cover all graph resource categories, manual-owner preservation, late interaction/conversation/audio completion, asynchronous failure, throwing disposers and attempted Run during Stop. Runner tests add stop-before-start and queued/pending-work cancellation. | Captured audio handles are not sound recordings; borrowed robot restoration is covered by the separate rollback fixture. |
| 8 Lifecycle routes | Session loss, isolation revocation, destroyed borrowed targets, reentrant session cleanup/acquisition, stale confirmations within or across sessions and retired graph callbacks are exercised independently. | Actual scene replacement, Worlds/Sandbox unloading and native player recovery still require admitted execution. |
| 9 Ten cycles | Complementary fixtures run ten owned/borrowed Sandbox cycles, ten graph resource cycles with hide/reopen and late callbacks, and ten synthetic isolation revocations. Immediate/post-update counters and stable lifetime baselines detect retained resources without GC or retries. | These are separate deterministic fixtures, not one integrated complete native loop or generated ten-row observation record. |

## Source behavior and SDK maintenance

Session and roster cleanup now explicitly restore robot/native edit leases, preserve outside-writer changes, report conflicts/errors and attempt subsequent cleanup after a provider throws. Manual Remove, bulk cleanup, Undo, dead-entry pruning and binding/spawn-failure cleanup consume the restoration result. The result-dropping roster `Dispose` path was removed. [Cleanup](../../../mods/TopiaForge.Sandbox/CreatorTools/CreatorWorkbench.Cleanup.cs) is grouped separately from the core workbench; failure messaging no longer claims unconditional restoration.

The shared [confirmation helper](../../../mods/TopiaForge.Sandbox/CreatorTools/CreatorWorkbench.Confirmation.cs) binds each modal to its exact owner and session. Isolation, End Session, Delete Project and Resolve Native Bindings reject retired callbacks, including same-session replacements and synchronous modal completion. Isolation acquisition rechecks session ownership after both provider Acquire and previous-lease disposal; returned leases are disposed if either callback retires the session.

Graph cleanup detaches run ownership, attempts every resource category and reports failures. Interaction callbacks must match both the current run and registration; failed/cancelled conversation work releases its handle, and completed audio is disposed. Run is refused while Stop is cleaning up. Reentrant same-target graph removal observes an already-retired mapping, preventing duplicate disposal and removal events.

[FakeRobotSceneEditorService and FakeRobotEditTarget](../../../src/TopiaForge.Mods.Testing/FakeRobotSceneEditorService.cs) are additive public testing helpers with independently mutable fixture state, exclusive edit ownership and deterministic failures. Fake objective and mutation-isolation handles now release their lifetime registrations promptly. Review the intentional additions against the [Testing API baseline](../../../baselines/topiaforge.mods.testing.api.txt); do not accept unrelated SDK baseline changes. The helpers require explicit extension registration and provide no native persistence capability.

## Recorded validation and next work

The latest focused C# module run passed all 19 suites, including 21 rollback checks, nine graph lifecycle cases and two added runner cases. Five initial rollback seeded defects, five initial graph seeded defects and two graph reentrancy defects failed their intended assertions. The final review added eight rollback checks, captured two actual pre-fix failures and detected three further seeded modal-owner, dead-cleanup-reporting and lease-disposal regressions; the restored module suite then passed. Mutation checks restored original source bytes and recorded private receipts under `.dart_tool/rc1-review/sandbox-automation-20260909/`. These results are source tests, not candidate evidence.

The CLI analysis reported no issues. Its full suite passed 613 tests with four existing platform skips, including 92 new specification/verifier/counter/tool tests. Negative cases cover every oracle, stale exercise counts, intermediate-cycle leaks, drifting baselines, changed spec bytes, malformed inputs, forged result fields and tool exit codes. The final Release solution build passed with zero warnings/errors, and all five required C# harnesses passed after the final fixes. The Testing API baseline has 26 intentional additions and zero removals; the full manager harness passed the SDK audit. The complete run also caught the generic modal-owner type under the existing safe-consumer rule; a private typed token fixed it without changing that rule. The failed log and final passing logs remain in the [evidence ledger](Evidence.md#sandbox-automation-stages-1-2-verification).

The user has since explicitly authorized stages 3–6; the [later handoff](sandbox-automation-stages-3-6.md) and [current evidence](Evidence.md#sandbox-automation-stages-3-6-checkpoint) supersede this historical scope boundary. Continue the [implementation plan](sandbox-automation-implementation-plan.md): pinned Editor UI, admitted native broker/isolation, complete native scenarios, then CI/release integration. Use the [QA setup plan](isolated-qa-setup-plan.md), [native UX operator handoff](native-ux-accessibility-handoff.md) and [independent tester handoff](independent-tester-handoff.md). The existing 15 SDK cases, ten game cycles, 36 gamemode cases and sixteen pinned-Unity authoring cycles remain required; P0-GAME-01 and all review/provisioning dependencies remain unchanged.
