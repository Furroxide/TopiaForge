# Launch preparation evidence

Updated 2026-09-10. These are source and local-preparation observations. **No final-main candidate, qualified game/authoring evidence or new gate approval is established.** Private originals are retained locally; [the action list](NextActions.md) supplies current next steps.

## Reviewed source and automated verification

The reviewed release source is `437733854795c11e684eac9d59d6bc52ada9516e`, tree `976326d02fd68b19a2e10bde923e15510df441a6`. Local reviewed commit `7a43966c88e28a5702253be5175e0c10d23277d3` has that same tree. [PR #124](https://github.com/Furroxide/TopiaForge/pull/124) integrated normally on September 8. The private inventory verifies 59 source files: 58 raw-byte matches and one documented Git EOL-normalized website test.

For that source, [main-targeted CI](https://github.com/Furroxide/TopiaForge/actions/runs/34228318402) and [push CI](https://github.com/Furroxide/TopiaForge/actions/runs/34228313322) each passed 16/16 jobs, including 585 Windows data tests and 521 CLI tests with four platform skips per suite. Documentation publication passed 50 website tests, 27 pages, 127 Markdown files, three Dart reference packages and 2,286 built-link checks. These counts describe those recorded runs, not this later documentation refresh.

[Push CodeQL](https://github.com/Furroxide/TopiaForge/actions/runs/34228313075) and [PR CodeQL](https://github.com/Furroxide/TopiaForge/actions/runs/34228314930) passed; the retained PR119 alert inventory was empty. [Packaging dry run](https://github.com/Furroxide/TopiaForge/actions/runs/34228314143) passed 11/11 jobs, all three synthetic archives' smoke/path/update/rollback/template/residue checks, matching canonical payloads, eleven SDK audits, seven C# harnesses and 363 fixtures. Hosted synthetic archives neither qualify a private candidate nor expand Windows-only RC1 support.

A September 9 read-only observation still found [PR #119](https://github.com/Furroxide/TopiaForge/pull/119) open/draft at the same release head, auto-merge disabled. `main` was `f7d154a5bc5880b75f9f4a4bbaf9a34a5158c76e`; `dev` was `c0be924a23c6739ce5e1daa114e729261e8bd13d`. Refresh remote state before integration. The synthetic PR merge is not an actual promotion into `main`.

## Pinned Unity setup and EditMode tests

The user explicitly authorized installation, licence acceptance, `.unityPackage` registration and the fourteen prepared tests. The official Unity installer was reverified at 4,033,958,912 bytes, SHA-256 `6e9f5f189079460cad6603ef5a5a8a3919fd6fe178cc579784ec788155c8a3c3`, with valid Unity Technologies SF Authenticode. Installation completed September 9 with exit 0. Installed Editor `6000.0.23f1_1c4764c07fb4` also had a valid publisher signature.

Existing-licence admission succeeded during actual execution. `TopiaForge.WorldCompanion.Editor.Tests.WorldValidatorTests` produced **14 unique expected NUnit cases, all passed**, zero failed/skipped/inconclusive, with confirmed original Editor exit 0 and no timeout. The prepared project used 81 inputs matching Git blobs at `eda671d40b51cddff098f34437510be552c6e46e`; that template subtree is unchanged through reviewed release `4377338`.

| Retained artifact | SHA-256 |
| --- | --- |
| NUnit XML | `43e078c5a1df31e961d40d8a8a9124cac748a1505aa138703c9eae1311352600` |
| Private Editor log | `e9f1363903e8a12406a19e3c8fe9aeddff879c78b6db6e2feb6fbc41c3e4c373` |
| Original source inventory | `1a00b99f9a5e25c73ce4b45ad7ee08f4c9927691daac6632bb31f5eb70532b85` |

All 81 inputs matched before execution. Unity import changed only disposable `ProjectSettings/VFXManager.asset`, populating VFX resources/version fields; all C#, assembly definitions and package pins remained unchanged. Independent result inspection confirmed counts, names, source hashes and this import difference. These fourteen tests are distinct from the sixteen candidate Unity lifecycle cycles, fifteen SDK cases, ten game cycles and thirty-six gamemode cases. None of those native candidate obligations is marked complete here.

## Retained source audits

The private dossier retains passing trademark-notice consistency and exhaustive source asset-license coverage audits, with fifteen and fourteen regression tests respectively. They establish their mechanical scope only. Earlier failed assertions and their repairs remain recorded against their actual failed logs. No rights or redistribution conclusion is supplied by these passes.

## RoboAPI HTTPS checks

The existing evidence note records 23 actual loopback HTTPS cases covering response parsing, synthetic token reload after 401, 429/500/503 refusals, 301/302/303/307/308 refusal without destination connection, default-client TLS certificate rejection with a positive control, and null-client refusal. The rebuilt full manager harness passed at its recorded source. Tests used synthetic credentials and local servers, without a trust-store entry or global TLS callback. A temporary test-certificate key container was disposed by its owner.

These tests do not authorize a live backend, revoke actual credentials, establish microphone consent or prove native scene/UX/accessibility outcomes. Initial certificate-fixture failures are preserved as setup failures. No test was rerun merely to prepare the reviewer requests.

## Current preparation and missing evidence

The three reviewer requests, QA plan, token instructions, signing recovery checklist, Sandbox automation plan, independent-tester handoff and native UX/accessibility operator handoff are prepared. Actual review decisions, QA admission, audit-token configuration, signing recovery and native acceptance remain pending. The standard QA account, restricted roots and two verified game copies have since been provisioned; see the stages 3–6 checkpoint below. Credential closure is explicitly deferred; native UX preparation is complete and its execution remains pending. The required neutral root paths were absent during the September 9 metadata check, and the user chose to retain the plan without provisioning.

The user subsequently authorized **“Implement the first two stages”** of the Sandbox automation plan: the nine-scenario specification/verifier and offline lifecycle/rollback regressions, including applicable tests. That source work is implemented and verified in the [stages 1–2 checkpoint](#sandbox-automation-stages-1-2-verification) below. Earlier source-test counts and documentation validation retain their original source scope. That initial reply alone did not authorize new Editor/game runs or provisioning. The subsequent all-stages and QA replies superseded that scope limit, as recorded below; no gate approval follows from either implementation authorization.

The latest secret-name-only observation listed the existing update-signing secret and no governance audit-token secret. No secret value was read. The unchanged machine readiness SHA-256 is `5b41655fd38a28c814ac0c7228cb953e379711d26501ba903fd496ee730b605d`.

## Local private archive and documentation refresh

The ignored `.dart_tool/rc1-review/` directory now contains a hash-verified copy of the original private packet plus `unity-editmode-20260909/` with the exact XML/log, execution receipt and input inventory. `repository-import-receipt-20260909.json` records 637 verified files from the initial import. Original histories and the original sibling-worktree packet remain intact. Raw paths, process identities and incident records remain private. This archive is evidence storage, not a neutral SDK/pub cache or build input.

The current repository documentation refresh consolidates safe preparation material and corrects active platform, source-status and staging-order contradictions. Its local validation is recorded below; previous CI must not be relabeled as validation of new edits. No source-gate or release-policy mutation is part of this refresh.

### September 9 documentation validation

The current repository is a detached checkout of reviewed release `4377338`, with this documentation refresh left as local, uncommitted changes. Existing untracked launcher assets were retained. No branch was reset and no merge, commit or push was performed.

Locked website dependency restoration succeeded with Node `24.18.0` after using a bounded executable path for npm child processes; initial executable-lookup failures are setup failures, not passing builds. The full website check passed all 50 tests (zero skips), generated 26 canonical Starlight pages, passed Astro diagnostics (29 files, zero errors/warnings/hints), built 27 pages, built C# references with zero warnings/errors, and generated domain/data Dart references with zero warnings/errors.

The full command then failed in bundled Dartdoc `9.0.4` while precaching the unchanged Flutter UI dependency graph: `DocumentationComment._stripDocImports`, `RangeError (end): 0..9089: 9202`, exit 255. This matches the previously reproduced Windows CRLF issue in the [redesign ledger](../gamemode-contract/Status.md). Search generation and final built-link validation were not reached. No SDK patch, toolchain upgrade or check exemption was applied. A fresh complete Linux documentation build is still required on the integrated refresh revision; this is documentation CI, not Linux game/platform acceptance.

Independent checks passed for all 141 repository Markdown files, all 533 repository JSON/YAML files, current generated content (26 pages, five snippets from three template projects), README inventory counts, rename residue and diff whitespace. Private evidence remains excluded from generated site content and release payloads. The private full-run log is retained in the ignored review archive as `documentation-refresh-full-check-20260909.log`.

## Sandbox automation stages 1-2 verification

The authorized first two stages are complete in the local, uncommitted working tree based on `437733854795c11e684eac9d59d6bc52ada9516e`. The [implementation handoff](sandbox-automation-stages-1-2.md) maps the nine specification rows to current assertions, documents the bounded offline developer verifier and lists its 25 residual requirements. It always reports `qualifiesRelease: false`; no integrated native observer, C# measurement exporter or candidate qualification was added.

The final Release solution build passed with **zero warnings and zero errors**. All five required C# executables passed: ModManager, ModRuntime, Mods.Analyzers, Mods.Multiplayer.Generators and Mods.Multiplayer. The full manager run includes the SDK API audit, 21 new rollback checks, nine new graph lifecycle cases and two added runner cases. The focused module run passed all 19 suites. The Testing API baseline records 26 intentional additive lines and no removals for the fake robot editor helpers; other SDK baselines remain unchanged.

CLI analysis reported no issues. The complete CLI suite passed **613 tests with four existing platform skips**, including 92 new specification/verifier/counter/tool cases. The changed Dart files were unchanged between that run and final C# verification. The test-only synthetic observations are parser/verifier fixtures, never measured execution evidence. All non-generated Dart files remain within the 500-line cap.

Five initial rollback mutations, five initial graph mutations and three final lifecycle-review mutations each failed their intended assertion, with exact source bytes restored. Two graph reentrancy defects and two final lifecycle review defects were also observed failing before correction. The final full manager run then caught a generic `object` confirmation token under the existing safe-consumer source audit; a private typed token corrected it, with the audit unchanged. An accidental loading-status encoding change was restored before the final passing build. These failed runs and corrections remain recorded; none is relabeled as a pass.

Private final build/test logs and their hashes are in `.dart_tool/rc1-review/sandbox-stages12-final-csharp-verification.json`; CLI logs use the `sandbox-stages12-cli-` prefix. Mutation and pre-fix receipts are in `sandbox-automation-20260909/`, with the source-audit repair in `sandbox-stages12-typed-owner-repair.json`. The final source/document inventory and documentation checks are recorded in `sandbox-stages12-final-validation-20260909.json`. Earlier verification logs are retained under their original names.

Final documentation checks passed all 50 website tests with zero skips, links in 143 Markdown files, all 536 JSON/YAML files, generated-content consistency (26 pages, five snippets from three template projects), README counts and rename residue. C# reference generation was rerun for the new public testing helpers and passed with zero warnings/errors. A heading-anchor mismatch found during the link check was corrected; its failed log is retained. These checks do not replace the fresh complete Linux documentation CI still required after the earlier Windows UI Dartdoc CRLF failure; final search/built-link coverage remains pending there.
A final read-only GitHub observation still found PR #119 open/draft at `4377338`, with no auto-merge; `main` and `dev` remained at the SHAs recorded above. Protected `release` still listed only the update-signing secret name; the governance audit token remained absent. No values were read. The readiness register retains SHA-256 `5b41655fd38a28c814ac0c7228cb953e379711d26501ba903fd496ee730b605d`; release policy, gate approvals and candidate status are unchanged.

At the historical stages 1–2 checkpoint, stages 3–6 were outside authorization and no new Editor/game execution or provisioning had occurred. The later explicit replies superseded that implementation boundary; the current checkpoint below records the resulting work. No commit, push, merge, release staging or publication has occurred. The native UX/accessibility operator handoff is prepared. The user-deferred credential closure, planned-only neutral setup, actual reviewer decisions and all frozen-candidate native acceptance obligations remain pending.


## Sandbox automation stages 3-6 checkpoint

The user subsequently authorized all remaining stages, standard-account QA provisioning on the drive with most free space, the existing build-2409 binary source, themselves as human operator, the main display and default render audio output. Microphone recording remains off. These decisions supersede the earlier implementation boundary; they do not supply gate approvals or candidate qualification.

The [stage 3–6 handoff](sandbox-automation-stages-3-6.md) records the production-UI Editor fixture, native fixture/observer, Windows input/capture broker, strict native annex and byte verifier, and supplementary hosted/local CI lanes. Source implementation remains unfinished across the [full nine-row native gap table](sandbox-automation-stages-3-6.md#native-matrix-implementation-gaps), including interaction/asynchronous completion and viewport scrolling. This is authorized implementation work, distinct from native acceptance awaiting admission and from unavailable product capabilities. The first-sign-in and host-measurement checkpoints below supersede the earlier setup status. No nine-row native pass is claimed.

Actual pinned-Editor evidence is retained in the private `.dart_tool/sandbox-editor/20260909T182411Z-3648dcc8ce4d4517b8190e12f1731bd3/evidence` directory. That run passed **127 workbench assertions**, detected **five deliberate Unity-state faults**, and completed **ten Editor workbench cycles** at measured 1920×1080, DPI 96, Direct3D 11. A separate Editor process passed the existing **sixteen UI lifecycle cycles**. Both original processes exited 0 with cleanup confirmed; `qualifiesRelease` and human visual approval remained false. The run found and repaired a clipped warning HUD and an overcrowded project-action row. Initial environment, GameView-size, layout and smoke-adapter failures remain retained.

The subsequent raycast-ancestry refinement, total-run deadline and explicit non-distributable fixture declaration changed source after that green Editor run. Pure compilation validates their source, but a fresh exact-source Editor execution remains pending while the operator initializes the QA session. The green receipt is not relabeled as a result for those later edits.

The current Release solution build passed with **zero warnings and zero errors**. All five required C# harnesses passed. The manager source audit initially rejected the two new UI test projects; the repair adds only those exact non-distributable fixtures to its allowlist and explicitly declares the Editor fixture unsafe. The earlier failures are preserved. Native observer contracts passed **23 checks**. The Windows broker passed **29 broker/input/cancellation contracts and 17 audio-format/request contracts**, without opening devices. C# API reference generation passed with zero warnings/errors.

The independent media verifier recomputes actual BMP pixels and WAV samples, detects the fixture's 599 Hz graph cue and its disappearance after stop, and rejects forged metrics, malformed media and invalid packet metadata. Its 26 media tests and the separate 22 transcript tests passed. Loopback records the approved render-endpoint mix; these bytes cannot establish physical speaker quality, microphone behavior or human visual approval.

The standard `TopiaForgeQA` account and restricted roots were created successfully. D: had 345,291,829,248 bytes free when selected. Two copies of the authorized game binary source each contain **409 files / 5,428,015,421 bytes**, with source and both destination hashes matching. Saves, personal configuration, runtime injection and logs were excluded. The source installation was unchanged. Private receipts are under `.dart_tool/rc1-review/qa-provisioning-20260909/`; this establishes preparation, not independent official-archive authenticity or admitted isolation.

At the earlier checkpoint, the secure password/first-sign-in request was unanswered and no completion receipt or initialized Windows profile was observed. The later verified password completion is recorded below; first sign-in was still pending at that checkpoint and is superseded by the later confirmation below. No password value was read, and no elapsed-time default was selected. Game execution, native device measurements, actual Unity persistent-root observation, isolation/device record review and final admission were pending at that checkpoint; the later host measurement below supersedes only the metadata portion.

Current source build/test logs are retained in `.dart_tool/rc1-review/sandbox-stages36-validation-20260909T184052Z/`. Final CLI/documentation results are recorded separately below when complete. No live backend, microphone, final-main candidate, release staging, publication or new gate approval is established. Credential closure remains deferred, neutral release build setup remains a plan, and readiness remains **NO-SHIP**.

### Current integrated validation and external state

The first full CLI attempt completed with **828 passed, four existing platform skips and four failures**: two command timeouts and two relocated-SDK failures. A serial retry, with unchanged limits, completed with **830 passed, four existing platform skips and three failures**. The packaged CLI's seven-template lifecycle passed on that retry; the repository template test and two command tests still failed. These are failed suite results, not passing validation.

The retained serial log includes a 100-second public NuGet `FindPackagesByIdAsync` HTTP timeout for `microsoft.testplatform.testhost`. Later missing synthetic feed files followed test teardown and are secondary failures. A separate direct HTTPS GET to that exact index returned HTTP 200 / 5,176 bytes in 292 ms at 19:31:53Z. That observation does not explain the restore timeout or prove global cache contention. The subsequent focused retry is recorded below; no timeout, test or TLS check was disabled.

Current documentation checks passed all **50 website tests**, links in **146 Markdown files**, **542 JSON/YAML files**, and generated-content consistency (26 pages / five snippets / three template projects). C# reference generation passed with zero warnings/errors. The first content check rejected maintainer-only links from a published guide; the repair uses explicit repository-source links, and the failed check remains retained. All non-generated Dart files stayed within 500 lines; CLI and launcher-data analysis reported no issues. The complete Linux docs CI requirement remains unchanged.

A fresh GitHub metadata observation at 19:24:32Z still found PR #119 open/draft at `4377338` with auto-merge disabled; `main` and `dev` remained at their previously recorded SHAs. The protected release environment listed the update-signing secret name and no governance audit-token name. No secret value was read. The readiness register still has SHA-256 `5b41655fd38a28c814ac0c7228cb953e379711d26501ba903fd496ee730b605d`.

The Windows broker was published locally from its already-built outputs for QA preparation. This is a private developer executable, not a release candidate. The later completed CLI/package preparation and D: input staging have separate immutable receipts, recorded below.

### Focused retry and verified QA development inputs

All three cases that failed in the serial CLI run passed in a focused retry using fresh, test-owned .NET/NuGet caches and unchanged source and time limits: **three passed, zero failed, zero skipped**, in 267 seconds. The serial suite had already passed the packaged CLI seven-template lifecycle. The two failed full-suite receipts remain failures; **there is no single passing full-suite receipt** at this checkpoint. The results suggest cache/environment sensitivity but do not establish its exact cause. The private `native-agent-final-checkpoint.json` binds the original logs, retry, analysis and compiled artifacts; no user-wide cache was deleted or configuration weakened.

The final CLI compiled and both native command help checks passed. Its SHA-256 is `4074e660f0349850539f3731e606cd9e939684c9e7c9a5d00d16f80c1030984d`. The current Editor fixture compiled with zero warnings/errors; this did not execute Editor. All six development packages built successfully: Worlds, RobotKit, CreatorContent, Sandbox and the two non-distributable acceptance fixtures. Their archive hashes are retained in the private `package-build-receipt.json`.

At 19:46:15Z, development input staging completed under `D:\TopiaForgeQA`: **2,695 source entries**, **11 loader DLLs**, the compiled CLI, five broker runtime files and **six packages**. The source snapshot is `D:\TopiaForgeQA\source\TopiaForge`; tools are in `D:\TopiaForgeQA\tools\sandbox-development-20260909`; exact inventories are in `D:\TopiaForgeQA\state\sandbox-development-20260909`. Every copy was compared with its source SHA-256. The independent Dart verifier subsequently checked the complete source snapshot, absence of extra inputs, all package archive bytes, manifest identities/versions and critical package files. Its receipt is `qa-provisioning-20260909/input-staging-independent-verification.json` in the ignored review packet.

The immutable source inventory digest is `0d201dc13bd1a4eb050b11c99ebeb1b87563954a7e5750dba08c9f86ca6a8376`; the package inventory digest is `5cea9a4adb5c98dcf2ad959b99e36b73a850cd63633376933ec17936be164e78`. The snapshot records the dirty workspace at base `4377338`, not a clean or final-main candidate. This later ledger update and completion-wording corrections are intentionally absent from that frozen snapshot. Subsequent code changes require a new reviewed input snapshot and rebuilt affected artifacts; do not rewrite these receipts.

ACL metadata confirms QA read/execute on source/tools and Modify on its state directory. The administrator-only bootstrap directory remains protected with no QA access. No Git metadata, personal SDK/cache/profile material or unrelated `assets/launcher/` content was staged. The 11 loader files remain separately bound to actual installed bytes during a later admitted run; they were not installed into the QA game by this staging action. Staging and independent verification launched no game, opened no capture device and conferred no isolation or release approval.

### QA password initialization confirmed

The user requested reopening the local secure prompt, then explicitly reported **“It is set.”** The helper completion receipt confirms the password was set at **20:02:57Z on 2026-09-09**. Only safe metadata was read; the password and bootstrap credential contents were not read. The account remains enabled. At the subsequent 20:04:28Z metadata observation, its Windows profile was still absent and no QA profile was loaded.

Private immutable records are `qa-provisioning-20260909/password-initialization-20260909T200214Z.json` and `initialization-observation-20260909T200428Z.json` in the ignored review packet. This supersedes the earlier password-pending observation, without changing that historical receipt or the frozen source snapshot. At that checkpoint the remaining user action was a normal first sign-in to `TopiaForgeQA`, followed by an explicit reply; the later confirmation is recorded below. Measured identity/known folders, actual game persistence root, approved devices and isolation admission were pending at that checkpoint; the later host measurement below supersedes only the metadata portion. No game run or gate approval follows from setting the password.

### QA first sign-in confirmed; host measurement preparation

The user explicitly reported **“I've logged in.”** Native Windows profile metadata confirmed the enabled QA account has an initialized, loaded, nonspecial profile with status 0. Session enumeration through WTS showed the normal account active and the QA session disconnected after switching back. No environment variables or profile paths were substituted to manufacture this observation. The private initialization observation retains exact identities and paths outside Git.

The [bounded host-probe runner](../../../tools/run-sandbox-qa-host-probe.ps1) and [native desktop metadata helper](../../../tools/sandbox/qa-host-desktop.cs) prepare the next read-only observation. They require the intended standard QA account, bind all five staged broker runtime files, wait for that session's active/unlocked default desktop, and read primary-token/known-folder, primary-display, keyboard/mouse device-name and default-render-endpoint metadata. They send no input, take no screenshots, capture no audio, open no microphone and launch no game. The probe has a finite execution deadline and immutable private output; waiting or timeout is never user approval.

The prepared one-time local task uses the existing QA interactive logon at limited privilege, with no stored password or recurring trigger, and removes only its verified own task definition afterward. This follows [Microsoft's interactive task-principal contract](https://learn.microsoft.com/en-us/powershell/module/scheduledtasks/new-scheduledtaskprincipal?view=windowsserver2025-ps). Its actual dispatch/completion must be checked in the private receipt; preparation alone does not establish execution. The fixture snapshot under D: is unchanged.

At that host-preparation checkpoint, the actual Unity persistent-data path still needed a distinct provisioning-only measurement before a real isolation record could be reviewed. The existing acceptance runner requires that value before launch, so a separate early runtime observer was being implemented at this checkpoint; no invented expected path, deliberately rejected acceptance acknowledgement or placeholder reviewer approval will be used. Native matrix completion, actual host admission and all release prerequisites remain open.

The one-time host-probe task was dispatched at **20:42:29Z** using the standard QA interactive logon. Its child-start receipt confirms execution in QA session 2, waiting for that session's active/unlocked desktop. This is actual dispatch evidence, not a completed host measurement. The private `host-probe-dispatch-20260909T203826Z.json` and staged `probe-started.json` retain the correlation. The next operator action at that dispatch checkpoint was to switch to QA briefly, leave its desktop unlocked for measurement, then return and report completion. The helper's nine-minute wait budget produces a timeout/refusal if no interactive desktop becomes available; it never substitutes for approval or changes release readiness.
The first host-probe attempt timed out at 20:51:30Z while waiting for an active/unlocked QA desktop; no host/device observation, capture or game run occurred, and its temporary task was removed. After the user asked whether they could switch, a fresh one-time attempt was prepared under `host-probe-20260909T205545Z`. Its separate private dispatch/start/result receipts were then inspected; the successful outcome is recorded below and the first timeout remains retained.

### QA host measurements and outbound isolation confirmed

The user returned from QA and explicitly reported “I switched back now to the current user.” The fresh host probe completed at **20:58:37Z** in the standard QA session: `completed: true`, exit code `0`, and `ownedProcessExitConfirmed: true`. It measured the executing primary-token identity, native profile/LocalAppDataLow folders, default render endpoint, primary display (**3440×1440 at DPI 96**) and keyboard/mouse device identifiers. The records show no input sent, screenshots, audio capture, microphone access or game execution. The temporary task completed with result `0` and was removed at **20:58:39Z**. These facts establish host metadata, not a device reservation, physical UX result or admitted isolation. Raw identities, logon values and device paths remain private.

Private original records (plain local paths): `D:\TopiaForgeQA\state\host-probe-20260909T205545Z\host-probe-result.json`, `host-observation.json` and `desktop-observation.json`; correlated task completion is `.dart_tool/rc1-review/qa-provisioning-20260909/host-probe-task-completion-20260909T205545Z.json`. The first attempt's timeout and the frozen tools used for both attempts remain unchanged. Later observer builds do not relabel this host measurement.

At **21:12:55Z**, the scoped [QA firewall provisioner](../../../tools/set-sandbox-qa-network-isolation.ps1) created and verified `TopiaForge-QA-Isolation-20260909`, an enabled outbound block for exactly `D:\TopiaForgeQA\game\Robotopia.exe`. It covers all firewall profiles, protocols, addresses, ports and interfaces. Existing Domain/Private/Public profiles were already enabled and the required firewall services were running; no global firewall setting or normal source-installation rule was changed. The fixed program's SHA-256 is `bc09e7595418ccd3c088a451617573ff50a9e015cd2ce1a35ff6e3403794976d`. Private immutable receipt: `.dart_tool/rc1-review/qa-provisioning-20260909/outbound-isolation-20260909.json`. Its flags explicitly retain `gameExecuted: false`, `isolationAdmitted: false` and `qualifiesRelease: false`.

The [runtime provisioning observer](runtime-provisioning-observer.md) is implemented and source-validated separately. The authorized inert headless launch may measure the real Unity persistence path before an attributable private isolation review; it requires exact reviewed inputs, the active/unlocked QA identity and the effective QA-only outbound block. At that host/firewall checkpoint, no provisioning game run or actual Unity path observation had occurred; the later failed runtime attempt below supersedes that execution status. A successful later measurement still cannot supply reviewer approval, native scenario coverage or candidate qualification. Actual persistence observation was still pending at that checkpoint; private review/admission, the unfinished native matrix, exact-source Editor rerun and all existing release prerequisites remain open.


### Runtime provisioning probe staged and independently verified

At **2026-09-09T21:32:28.5896644Z**, a new provisioning bundle was staged at `D:\TopiaForgeQA\tools\runtime-probe-20260909T213213Z`, with separate private wrapper output under the matching `state` directory and native output under `D:\TopiaForgeQA\evidence\runtime-provisioning`. At staging, the QA game had **444 verified input files**: the original 409 binary files, 22 vendored BepInEx files and all thirteen required loader DLLs. The source-game copy and earlier development source/tools snapshot remain unchanged. No fixture packages or manager state were installed by this step.

The source checkpoint is `runtime-observer-source-validation/final-source-receipt-v2.json`, SHA-256 `92b3fb530953e7f0f520f8747a4e10b927f4bff3a0689f700ca551f9f7faa95e`. It binds 35 source files, all thirteen loader DLLs and the separate **192-file** self-contained broker. The revised broker passed 83 provisioning, 29 existing broker and seventeen waveform checks; the runtime observer passed forty checks and the loader build had zero warnings/errors. Earlier full C# results apply only to their recorded unchanged runtime sources/loader bytes; no new full-suite run is claimed here.

The [QA wrapper](../../../tools/run-sandbox-runtime-provisioning.ps1) holds verified read leases through execution and writes an explicit failure result even when original-broker cleanup cannot be confirmed. Its 24 source checks passed, including mutation refusal and cleanup failures. At **2026-09-09T21:33:29.3463034Z**, an independent check rehashed all 444 game inputs and 192 broker files, checked QA read/execute on tools and Modify on private output, and retained the exact launch/request/helper/wrapper files in the ignored repository packet. Running the staged wrapper from the normal account exited 1 before output creation or broker/game execution. The actual manager-state root remained absent at that staging check; the later runtime attempt created its own retained state.

Private records are under `.dart_tool/rc1-review/qa-provisioning-20260909/`: `runtime-probe-preparation-20260909T213213Z.json`, `runtime-probe-inputs-20260909T213213Z/verification.json`, and `runtime-wrapper-validation-20260909T2130-v2/validation.json`. The successful host measurements were also copied and hash-verified into `host-measurements-20260909T205545Z/`, retaining their original D: copies. At that staging checkpoint, task dispatch and the original game's runtime observation/exit still needed separate evidence; the later failed execution is recorded below. Staging, source checks, wrong-account refusal and task cleanup do not supply an observed persistence path or isolation approval.

The one-time runtime-provisioning task was dispatched at **2026-09-09T21:36:17.5838266Z** using the existing standard QA interactive logon at limited privilege. Its child-start record confirms QA session 2, waiting for its active, unlocked default desktop. At the dispatch observation, native output was empty and manager state was absent: no game execution or persistence result was established. Private correlation records are `runtime-probe-dispatch-20260909T213213Z.json` and the staged `probe-started.json`. The next operator action at that dispatch checkpoint was to switch to QA, leave the desktop unlocked for the bounded probe, then return and explicitly report completion. The nine-minute desktop wait may refuse on timeout; it never supplies authorization or a successful observation. Inspect the actual wrapper/native/cleanup receipts before any retry or admission.

### Runtime observation recorded; shutdown failed

The operator reported completing the requested wait. The actual provisioning game process started in the QA session. A distinct raw runtime observation was written at **21:38:39.457Z**, recording the actual Unity persistence path and native identity privately with `managerInitialized: false`, `isolationAdmitted: false` and `qualifiesRelease: false`. That raw record is retained failure evidence; no measured path, account identity, logon value or device identifier is copied into tracked documentation.

The attempt **failed** at **21:40:08.567Z**: the original game did not exit before its deadline. The native result records `original-process-timeout` and the pre-cleanup `original-process-exit-unconfirmed` failure. Cleanup then force-terminated only the retained original game process and confirmed its exit, so the final fields are `originalProcessExitConfirmed: true` and `forceTerminated: true`. The result remains `status: failed` with no accepted observation digest. A raw path file and confirmed forced cleanup do not satisfy the required successful, unforced outcome.

The broker itself exited with code `1`, with its original exit confirmed and no forced broker termination. The one-time task was removed at **21:40:10Z**, retaining task result `1`. The native recovery marker is absent after confirmed cleanup. No Sandbox scenario, isolation admission, release qualification or reviewer gate passed.

Private records (plain local paths): `D:\TopiaForgeQA\evidence\runtime-provisioning\probe-40580fcad2ef02b1fd58df15a2bd0b8a\provisioning-result.json`; the corresponding raw observation remains under `D:\TopiaForgeQA\game\BepInEx\TopiaForge\staging`; wrapper result `D:\TopiaForgeQA\state\runtime-probe-20260909T213213Z\runtime-probe-result.json`; task completion `.dart_tool/rc1-review/qa-provisioning-20260909/runtime-probe-task-completion-20260909T213213Z.json`. Preserve the used game tree, raw observation, logs, inputs, native result and cleanup receipts unchanged. A retry must use separately reviewed clean inputs rather than delete this state or overwrite the failure.

A narrow correction to defer the shutdown request until the first Unity update is under implementation. No corrected-source validation or retry is claimed at this checkpoint. Validate the correction and new exact bytes, complete a clean unforced probe, then obtain an attributable private provisioning review. Native matrix implementation/execution, exact-source Editor rerun, existing non-game approvals and final candidate prerequisites remain pending.

The complete failed-run retention is `.dart_tool/rc1-review/qa-provisioning-20260909/runtime-probe-failed-20260909T213213Z/retention.json`: eleven original records/logs were copied and SHA-256 verified. A separate correlation check matched the observation to the launch/request challenge, request bytes, original process identity and time window; it explicitly preserves `attemptPassed: false`. All 35 exact v2 source files were also archived under `runtime-observer-source-v2/` before the shutdown correction. The observed QA profile directory was not inspected successfully from the normal account because Windows denied access; that denial is not evidence of an absent directory, and no profile ACL was changed.


### Deferred-quit correction and clean retry staged

The corrected source records the observation during the early plugin callback and defers one Unity quit request until the first subsequent Update, before the normal readiness guard. Fixed phase diagnostics distinguish observation, Update, returned quit call and Unity's quit callback; none is treated as proof of native process exit. Reentrancy, repeated frames and refusal cannot cause a quit loop or manager initialization. The cause of the first timeout remains uncertain, and the correction's native effect remains unverified until the retry completes.

All six required C# build/regression commands passed between **21:49:32Z and 21:50:38Z on September 9 UTC**, with zero solution/loader build warnings or errors. The focused observer/lifecycle checks passed **53/53**. Frozen v3 receipt: `runtime-observer-quit-validation-v3/final-source-receipt-v3.json`, SHA-256 `4458cdfb72281a76e696785e98d6d18ffa32d51346d63085b9ec5d1ccdd7ab17`. All 37 source files are archived under `runtime-observer-source-v3/`. Among the thirteen loader DLLs, only `TopiaForge.ModManager.dll` changed; its SHA-256 is `9770323f0648754918b4b10925fce2e3295636bbe0bcb0d15bde97a702c32bd6`. The 192-file v2 broker remains byte-identical with its earlier checks.

The [fresh-game copier](../../../tools/copy-sandbox-qa-provisioning-game.ps1) passed 47 source checks and a read-only census of all 409 source files/27 directories with no extra content, alternate streams or links. It then created **`D:\TopiaForgeQA\game-provisioning-20260909T215734Z`**, copied and verified the complete 5,428,015,421-byte game, and confirmed protected QA Modify access. It did not rename, delete or overwrite the failed `game` tree. At **21:58:46Z**, the scoped firewall helper verified a separate outbound block, `TopiaForge-QA-Provisioning-20260909T215734Z`, for exactly the new game's `Robotopia.exe`; the first block remains intact.

At **2026-09-09T21:59:14.5490245Z**, the v3 loader and complete broker/wrapper were staged in a new `runtime-probe-20260909T215900Z` tools/state pair. Independent verification at **2026-09-09T22:00:29.5845546Z** rehashed all **444 game/runtime inputs and 192 broker files**, checked ACLs, retained exact private launch inputs, and confirmed wrong-account refusal before any new wrapper/native output. All eleven first-attempt retained source artifacts remained byte-identical. The new manager-state root was still absent. Private receipts: `game-provisioning-copy-20260909T215734Z.json`, `outbound-isolation-provisioning-20260909T215734Z.json`, `runtime-probe-preparation-20260909T215900Z.json`, and `runtime-probe-inputs-20260909T215900Z/verification.json`, all under the ignored QA review packet. This is a reviewed clean retry preparation, not a passing runtime observation, isolation approval or acceptance result.

The corrected retry task was dispatched at **2026-09-09T22:04:22.2879476Z** using the QA interactive logon at limited privilege. The child-start receipt confirms QA session 2 waiting for its active, unlocked desktop. At dispatch verification, the new manager state was absent and only the first failed native run directory existed. Private correlation: `runtime-probe-dispatch-20260909T215900Z.json` and `state/runtime-probe-20260909T215900Z/probe-started.json`. The operator must switch to QA for the bounded retry, return and explicitly report completion; task start and elapsed time do not establish native success.

## 2026-09-10 prerelease preparation and development-source checks

These observations are separate from candidate qualification. Release
stabilization PR #129 is based on `6d4082d`; the original working directory
remains a dirty development tree based on `4377338`.

The isolated website repair uses Astro 7.2.8, Sharp 0.35.4, smol-toml 1.8.0 and
SVGO 4.1.0. Local validation passed 50 website tests, Astro checking of 29 files
with no findings, and a 27-page build with search. npm audit reports zero
vulnerabilities. Local Node was 24.19.0, which satisfies website development
requirements but is not the pinned production Node 24.18.0. npm installation
succeeded with optional Windows WASM cleanup warnings. All 128 source Markdown
files, 533 JSON/YAML files and the asset licence audit passed. Required hosted
validation remains the authority for the integrated source.

The original dirty tree passed a Release solution build with zero warnings or
errors, the ModManager, analyzer, multiplayer-generator and multiplayer
harnesses, the Editor harness compilation without opening Unity, 29 Windows
broker cases, 17 loopback cases, 83 provisioning contracts, 23 native protocol
cases and 44 Sandbox CI admission cases. A production-backed creator-source
disposal regression failed before the cleanup fix and passed afterward for
Stop Project and graph despawn. The post-fix Release build and full ModManager
harness passed.

The first full ModRuntime run exceeded its unchanged 180-second generated-build
deadline. A fresh-cache diagnostic build passed in 106.35 seconds; NuGet restore
accounted for 1.76 minutes. The original timeout discarded subprocess output,
so its exact cause remains unproven. A narrow diagnostic change retains output
without increasing the deadline. The full rerun passed with fresh test-owned
NuGet HTTP/plugin caches. Preserve both attempts.

Dart 3.12.2 analysis passed for domain, data and CLI. The domain suite passed
1,079 tests. The data suite recorded 576 passes, four platform skips and nine
failures: seven fixtures could not resolve PowerShell from spawned command
shells, and two restore/discovery tests timed out with locked-fixture cleanup
errors. Do not call this a passing full data run. The initial wrapper invocation
also failed due to tool lookup; the first root-directory test invocation failed
because Dart requires the package working directory. These are retained,
separate attempts. The 589 non-generated Dart files satisfy the 500-line cap.

Raw source-validation logs and diagnostic probes remain under ignored
`.dart_tool/rc1-prep-validation/`. They contain development paths and are not
release assets. No Editor/game execution, reviewer approval, production build,
candidate acceptance, tag or publication resulted from these checks.

### Integrated website repair and bounded development test diagnosis

PR #129 was normally squash-merged into the release branch at
`f0ef217485bb4519e202387c2f68757316d746f2`. The main-target
[CI](https://github.com/Furroxide/TopiaForge/actions/runs/34419132880) and
[release-push CI](https://github.com/Furroxide/TopiaForge/actions/runs/34419131141)
each passed all 16 jobs. Both CodeQL runs passed. The
[packaging dry run](https://github.com/Furroxide/TopiaForge/actions/runs/34419133863)
passed all 11 jobs. These runs validate that release head; they do not qualify
production bytes or cover the separate dirty automation tree.

The original dirty tree's inherited PATH contained 15,447 characters. Bounded
probes confirmed that spawned cmd.exe shells could resolve their required
PowerShell dependency with an 8,100-character PATH but not an 8,300-character
PATH. A short process-local PATH corrected those fixture launch failures;
no global environment settings or product behavior were changed. The full
data rerun passed 584 tests with four existing Windows symlink skips and one
remaining real SDK restore timeout at the unchanged two-minute deadline.
A focused rerun with fresh test-owned NuGet HTTP/plugin caches still timed
out. Both attempts remain failures. Only the two restore processes verified
against this task's exact generated fixture roots and identities were stopped;
confirmed exit and fixture evidence are retained. No unexplained result was
reclassified as a pass.

The ten focused CLI Sandbox test files passed all 312 tests. CLI analysis
passed; no complete CLI-suite pass is claimed for this dirty source. The full
release-head CI results above apply to their separate integrated source.
Private logs are `dirty-source-tests-launcher_data-bounded-path.log`,
`dirty-source-tests-launcher_data-restore-isolated-cache.log`,
`dirty-source-cli-sandbox-tests.log`, and `owned-process-cleanup.json` under
the existing ignored validation directory. No game execution or acceptance
result was produced by this diagnosis.

### Corrected provisioning retry stopped before broker startup

A read-only 2026-09-10 check of the existing retry receipts establishes that
the wrapper started at **2026-09-09T22:04:23.3639444Z** and ended at
**22:13:23.7606409Z** with `completed: false`, `errorType: TimeoutException`
and `brokerStarted: false`. The source's matching failure path is its
nine-minute active/unlocked QA desktop wait. No broker or game was started
by this retry; it does not test the corrected Unity shutdown behavior.
`ownedBrokerExitConfirmed` is true because no broker was created, and
`brokerForceTerminated`, `isolationAdmitted` and `qualifiesRelease` are false.

The existing task-completion record at **22:13:25.4401301Z** records task result
1 and `taskRemoved: true`; the named one-time task is no longer present.
The wrapper receipt was copied without overwriting the original into ignored
`qa-provisioning-20260909/runtime-probe-desktop-timeout-20260909T215900Z/` and
SHA-256 verified as
`582a2e3180fbd550b18cd4f198eae3b8d07d388c094b3e2ca691862b742acda4`.
Both this desktop-wait failure and the earlier in-game shutdown failure remain
retained. No fresh task, game run, admission, input or capture was started.
Any further attempt needs reviewed unused wrapper outputs, verified inputs and
the active QA desktop; do not overwrite either failed attempt.

### Bounded generated-process cleanup verified

The isolated shipping cleanup patch's first revision `cf6d957` passed all
16 hosted CI jobs. Review then identified an unbounded wait in its timeout
diagnostic path. The signed `ec03955` follow-up gives process-exit and stream
collection one five-second cleanup budget without changing execution deadlines.
A controlled two-second timeout returned in 2.060 seconds with both streams;
a descendant holding the redirected pipes returned in 7.014 seconds with the
original timeout and explicit unavailable-output markers. Test-owned children
were confirmed exited or cleaned up. The full Runtime harness then passed in
22.26 seconds, with zero runtime-project build warnings/errors and passing
changed-file formatting. Fresh hosted checks cover that follow-up.

The same verified helper/fixture change replaced only this task's earlier
diagnostic edit in the original checkout. Its full no-argument Release Runtime
harness passed in 24.58 seconds using a short process-local PATH and fresh
owned caches. Logs remain in ignored
`rc1-prep-validation/runtime-bounded-20260910-003558/`.
A subsequent name-only check still found the governance audit token absent;
`TOPIAFORGE_UPDATE_ED25519_PRIVATE_KEY_B64` exists. No secret value was read.

### Sandbox cleanup stabilization integrated

[PR #130](https://github.com/Furroxide/TopiaForge/pull/130) was normally
squash-merged at **2026-09-10T00:45:15Z**, after explicit authorization limited
to that stabilization PR. The merge is
`0ea59e7941b1d56f515ac34f89819f31f16c7137`, tree
`befbabaeaed9bdc55d4b14185af4c49804afe65c`; this exactly matches the checked
`ec03955438ef3bdcac277d7c416f6633e46d89dd` tree. GitHub reports the merge
signature verified and valid. No main merge, tag or publication was performed.

[Final PR CI](https://github.com/Furroxide/TopiaForge/actions/runs/34421914989)
passed all 16 jobs and CodeQL passed. A PR-description update canceled one
policy run, causing its aggregate check to fail; the replacement policy run
and the explicit rerun of the canceled attempt both passed without changing
rules or source. Both automated review threads were resolved: timeout cleanup
was bounded and tested; the identity-based roster cleanup guard was retained
following independent review and its existing reentrant regression. All other
required checks passed before integration. Fresh integrated-head CI and the
package dry run remain separately required.

### Final integrated release-head validation

Exact source `0ea59e7941b1d56f515ac34f89819f31f16c7137`, tree
`befbabaeaed9bdc55d4b14185af4c49804afe65c`, passed:

- [Main-target CI](https://github.com/Furroxide/TopiaForge/actions/runs/34422629258):
  all 16 jobs, including seven templates and the complete documentation,
  reference and search build.
- [Release-push CI](https://github.com/Furroxide/TopiaForge/actions/runs/34422624948):
  all 16 jobs.
- [PR CodeQL](https://github.com/Furroxide/TopiaForge/actions/runs/34422626680)
  and [push CodeQL](https://github.com/Furroxide/TopiaForge/actions/runs/34422624897).
- [Release package dry run](https://github.com/Furroxide/TopiaForge/actions/runs/34422625501):
  all 11 jobs, including packaged templates, signed update/forced rollback and
  all three platform archive checks. This does not expand Windows-only RC1 scope.

Required dependency review, PR policy, registry validation and Unity source
validation passed. PR #119 has no unresolved review threads; its body was
updated with this exact source and the remaining prerequisites. It stays draft,
with auto-merge off. No final-main merge, production candidate, RC1 tag or
GitHub release was created. The four non-game review records, governance audit
token, QA admission/preparation, pending scope decision and later candidate
acceptance remain unresolved. The user authorized only PR #130's stabilization
merge; that answer did not choose the scope or clear another item.

Verified workflow metadata and CI logs are retained in ignored
`rc1-prep-validation/release-head-0ea59e7/`. The main-target log SHA-256 is
`4e22ea9d4428b95bbb54bac4ab6265bd8a171a53dd6098f7631bc2c9f8122bce`;
the release-push log SHA-256 is
`fd9063f5cc1bba6a836ab5e21c72e07815b0ee71374922626f9b11aa721e8fbd`.
Local launch-document refreshes and the current handoff remain in the original
development checkout for normal reviewed integration alongside actual gate/scope
changes; unrelated unfinished automation/provisioning source has been preserved.

## Fresh provisioning retry armed — 2026-09-11

At 17:12:51Z a fresh one-time retry was armed for the next `TopiaForgeQA` sign-in. The account was signed out, so a temporary account-specific logon trigger starts the standard-user driver after sign-in. The administrator watcher removes that exact task after completion or expiry. Sign-in waiting and execution have separate bounded budgets. This is an armed attempt, not a passing result or admission.

Preparation reverified all 444 game files, 409 source-game files and 192 broker files, the exact outbound block and private ACLs. The unused separate game copy was retained. Executable source still matches the v3 checkpoint; only the already updated observer runbook differs. The new driver measures the current QA token/session and known folders before generating immutable launch input under its private state directory. The executable bundle remains read-only to QA. The staged wrapper differs by one guard: its generated launch input must reside in its bound state directory. No existing snapshot or failed attempt was overwritten.

Parsing, wrong-account refusal and harmless owned-child success/failure/timeout cleanup checks passed. A completion or failure notice is requested only in the executing QA session. The native result still requires independent review. Safe private receipt reference: `.dart_tool/rc1-review/qa-provisioning-20260909/runtime-retry-preparation-20260911T171020Z.json`; dispatch receipt uses the same timestamp. No game execution, successful shutdown, isolation approval or release qualification is claimed at this checkpoint.

## Provisioning retry reached Unity quit but timed out — 2026-09-11

The user completed the requested QA sign-in and reported the completion/failure notice. The driver started at 17:15:18Z, refreshed the QA token/session and host/device metadata successfully, and launched the exact verified game. The runtime recorded a correlated private observation at 17:15:35.8147503Z with the manager inactive. The loader log confirms all three shutdown phases: the first Update requested Unity quit, the quit call returned, and Unity invoked OnApplicationQuit. This establishes that the v3 deferred quit path executed; it does not establish successful process shutdown.

The original game still exceeded the unchanged 90-second deadline. The native result at 17:17:04.7499198Z is **failed**, with forced termination and subsequent original-process exit confirmed. The broker and wrapper returned 1 without themselves being force-terminated. The operator notice request succeeded; the temporary task was removed at 17:17:06.9508876Z. Follow-up inspection found no remaining Robotopia/broker process and no recovery marker. No new attempt is queued.

Nineteen files from this attempt were copied and hash-verified into private repository evidence. Independent correlation verified the request/challenge, request and launch-input hashes, actual process/token identity, timestamp bounds and raw observation. This forensic correlation does not replace the native failed result or generate an acceptance acknowledgement. Safe private retention reference: `.dart_tool/rc1-review/qa-provisioning-20260909/runtime-retry-failed-20260911T171020Z/retention.json`. Preserve both used game trees and all three attempt histories. The separate `game-provisioning-20260909T215734Z` tree is now used and cannot be offered again as an untouched copy.

The broker console streams are empty. A tightly scoped administrator check found no Player.log for this run; no old logs or credentials were read and no profile permissions were changed. Unity's [Player command-line reference](https://docs.unity3d.com/6000.0/Documentation/Manual/PlayerCommandLineArguments.html) documents that `-nographics` disables output logs, explaining this diagnostic gap. The precise native/game shutdown cause remains unconfirmed; do not attribute it to a particular Unity defect, treat OnApplicationQuit as exit, increase the deadline or force a passing result.

Before involving the operator again, prepare and review a bounded diagnostic launch mode that actually retains Unity shutdown output (and verify that logging in a controlled fixture), with an exact new private log destination and no arbitrary command arguments. Preserve the standard QA identity, owned-process controls, network block, inactive manager and unforced-exit success requirement. Add meaningful command/path and cleanup checks, validate the final source and broker, and prepare unused game/output identities. Do not automatically fall back to a different graphics mode or repeat the unchanged failed recipe. Actual isolation review, native Sandbox acceptance and release gates remain pending.

## Diagnostic launch mode verified and v5 retry prepared — 2026-09-11

The diagnostic launch mode described in the [observer runbook](runtime-provisioning-observer.md#diagnostic-launch-mode-verified-and-v5-retry-prepared-2026-09-11) was verified on a controlled fixture before any new operator involvement. `tools/run-sandbox-fixture-shutdown.ps1` created a minimal Unity `6000.0.23f1` project, built a Windows player that quits from its first `Update`, and ran it through the broker's own launch, log and wait path (`--fixture-shutdown`). The player exited unforced with code 0 after 679 ms; the retained player log holds 2,135 bytes despite `-nographics`. The fixture receipt, result, player log, build and console logs are copied and hash-verified into the private v4 checkpoint directory. This is mechanism evidence only; the game's shutdown remains unverified.

The v4 source checkpoint receipt (`runtime-observer-shutdown-diagnostics-v4/final-source-receipt-v4.json`) records 44 exact source hashes, the seven required C# validation logs (solution build 0 warnings and 0 errors; manager, runtime, analyzers, generators and multiplayer harnesses passed; 53 focused observer checks), 100 launcher-contract checks in the published self-contained broker v3 (192 files; 29 broker and 17 waveform checks unchanged) and the thirteen rebuilt loader hashes. The worktree content is snapshotted at `refs/snapshots/rc1-automation-worktree-v4` (commit `2aac31f7647f9120a19092526e9cc1c4f6458630`) on top of release head `0ea59e7`. The first runtime-harness attempt failed with exit code 127 because the isolated worktree lacked its Flutter SDK link and locked pub dependencies; that failure is preserved in `checks-attempts.jsonl`, and the rerun with a short process-local PATH passed. `nativeGameExecuted`, `isolationAdmitted` and `qualifiesRelease` are `false`.

Retry `20260911T183026Z` is prepared, not executed. The unused fresh copy `game-provisioning-20260911T180717Z` (409 files re-verified against the approved inventory and the source copy) received the 22 vendored BepInEx files and 13 loader assemblies (444 files). The bundle holds 197 files (192 broker files plus wrapper, desktop helper, v5 driver, launch template and manifest); the state root is empty; private ACLs were re-checked. Under the normal user the v5 driver refused with "requires the intended standard interactive QA account", wrote no state and created no native run (`retry-driver-v5-wrong-account-20260911T183026Z.json`). The firewall rule `TopiaForge-QA-Provisioning-20260911T180717Z` is not created yet; the elevated step creates and verifies it. No game ran, no task is registered, no gate changed, and no operator session has been requested beyond the user's confirmation of availability.

## Launcher-data restore timeout diagnosed — 2026-09-11

The earlier two-minute overrun was not a Dart or Flutter SDK restore. It was the `launcher_data` test that scaffolds a mod project into `%TEMP%` and runs two NuGet `dotnet restore` children for the mod SDK (`packages/launcher_data/test/local_developer_repository_templates_test.dart`, two-minute test deadline; the restore child itself is bounded at five minutes in `lib/src/local_developer_repository/sdk_restore.dart`). The stale fixtures from the failed runs date the stall precisely: the main project's restore output appeared 204 seconds after scaffolding, about two 100-second NuGet request timeouts to `api.nuget.org`, and the two fresh-cache reruns produced no restore output at all. Firewall events, the QA-only block rules, port exhaustion, Defender, proxies and credential plugins were checked and excluded for both windows. The cause was an environmental, time-bound failure of nuget.org round-trips from `dotnet.exe`; every documented stall also coincided with orphaned restore children from earlier timed-out tests still running. No limit was raised and nothing was suppressed.

Today, with a short process-local PATH and no orphaned children, the same step passes in about two seconds and an isolated restore of the scaffold's test-project shape completes in one to two seconds. The locked pub dependencies resolve offline in under three seconds; the Flutter SDK cache is fully populated, so no first-run download exists to time out on. Results under the short PATH, run from each package directory: `launcher_domain` analyze pass and 1,079 tests pass; `launcher_data` analyze pass and 585 tests pass with 4 skips in 27 seconds; `launcher_ui` analyze and tests pass; the Flutter launcher analyze pass and 75 tests pass. The literal `dart test packages\...` and `flutter test packages\...` forms fail from the repository root because those commands must run inside the package; the bootstrap script already uses the package as the working directory, and the agent guide now says so. Under the long interactive PATH, `dart.bat` fails immediately because `cmd.exe` sees an empty `PATH`; that, not the code, was the harness failure. The CLI package suite is reported with the stage-5 verifier work.

## Sandbox native matrix protocol v2 implemented and verified offline — 2026-09-13

The [native matrix completion contract](sandbox-native-matrix-completion.md) is implemented across the UI kit, loader, Sandbox workbench, safe fixture mod, native observer, Windows broker and the independent Dart verifier on the isolated branch `fix/rc1-sandbox-automation` (base `0ea59e7`). These are offline and Editor checks of the harness contract; no native game execution occurred and every annex or observation stays `qualifiesRelease: false`.

C# checks on the final source with a short process-local PATH: solution build 0 warnings and 0 errors; ModManager, ModRuntime, analyzer, multiplayer generator and multiplayer harnesses passed (the ModManager harness includes the new UI diagnostics contract and Undo control tests); the safe fixture mod, native observer and Editor-check projects build with 0 warnings and 0 errors; the observer contract tests pass 546 checks. The Windows broker self-test passes 183 broker, 17 waveform and 100 provisioning checks; the same source in an isolated WSL Ubuntu 24.04 copy with the pinned SDK 10.0.301 passes 183, 17 and 98 (the two Windows-only checks are skipped), after a cross-platform correction to the provisioning contract tests. The CLI package passes `dart format --set-exit-if-changed`, `dart analyze` and 921 tests with 4 platform skips; no non-generated Dart file exceeds 500 lines; `pubspec.lock` is unchanged. The Flutter Windows debug build passed. The Markdown link check passes for 149 files. Local sandbox CI admission tests pass 44 checks.

The pinned Editor lane on the final fixture source (run `20260913T183403Z-32a6e2cb858c406a8f443d580bd03d25`, Unity `6000.0.23f1_1c4764c07fb4`) passed: 216 measured checks, 5 negative detections, 10 Editor workbench cycles, 16 lifecycle cycles with toast diagnostics, cleanup confirmed, `humanVisualApproval: false`; `editor-observations.json` sha256 `b91572159f3f70ace954880125aa2e70cad3962344c7da96a1e219893b4a7536`, `runner-summary.json` sha256 `2994cb94f3554238cb7de55e43a26322f5a951b7ce8778c1f20db57c1fdfa6ff`. The first rerun (`20260913T182939Z-9e96fb1df625419ca22ee6bd301cc82d`) failed the new `toast-sequence-monotonic` check and is preserved: a pooled toast re-presented after dismissal kept its exit position and zero alpha because `Restack` only animates views parked at the origin, so re-presented toasts were invisible in the product. `TopiaForgeToasts.Present` now resets the entrance position on every presentation. Raw Editor evidence stays outside Git under `.dart_tool/sandbox-editor/`.

Three contract decisions were reconciled between the observer and the verifier during implementation and are recorded in the contract: graph audio is released to the idle pool (at most 24 retained sources named `TopiaForge.Audio.Pooled`) rather than destroyed, so the release rule checks names and playback instead of literal absence; lifecycle route 2 asserts the restarted state after the observer advances because the broker actuates F5 only then; `stop-before-start` is observe-only and asserts the rendered disabled Stop control. Known execution risks that only a native run can settle: RobotKit ground snapping could shift duplicated robot rows on Y, the yaw and pitch sign convention assumes non-inverted mouse-look, and the expected inventory file is unreviewed and empty, so `catalog-editing` reports `unavailable` by design until a maintainer reviews it.

Six PowerShell analyzer findings inherited from the earlier local scripts were repaired without suppressions: the QA copy script is ASCII-only (the reserved-name superscripts are regex escapes) and its protected-root function and the provisioning ACL function support `ShouldProcess`; the provisioning script builds the generated bootstrap secret directly into a read-only `SecureString` with no plaintext conversion cmdlet; the sandbox CI test helpers no longer use a state-changing verb. `Invoke-ScriptAnalyzer` over `tools` now reports 0 findings with the CI settings. During this work two commands resolved a relative path against the process directory and altered the untracked copy script in the main checkout; it was restored and verified byte-identical to the 2026-09-11 snapshot blob (`990e31e9…`), and the main checkout is otherwise untouched.

