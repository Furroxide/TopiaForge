# Runtime provisioning observer

Prepared 2026-09-09. This observer measures the verified development game's actual `UnityEngine.Application.persistentDataPath` before the private isolation record exists. It does not launch a candidate, run Sandbox acceptance, approve provisioning or close a release gate. See the [QA setup](isolated-qa-setup-plan.md), [decisions](Decisions.md) and [evidence ledger](Evidence.md) for current execution status.

The existing broker `--probe` measures the executing primary token, native known folders and default render endpoint; it does not measure Unity persistence. Use its actual private identity observation to prepare the input below. Never guess the vendor/game directory, substitute environment variables, fabricate reviewer evidence or use a rejected acceptance acknowledgement to discover the path.

## Before execution

Use the signed-in, active and unlocked QA session and a reviewed new development-game preparation under `D:\TopiaForgeQA`. Keep its source, executable tools, launch JSON and private output within the authorized QA layout. Native QA profile/LocalAppDataLow paths come from Windows and need not be on D:. Do not copy personal profiles, saves, tokens, caches or licence files. The administrator-only bootstrap credential directory is not an input.

Review exact game, BepInEx, current loader and self-contained broker bytes. The previously frozen source/tools snapshot remains immutable: stage later observer builds separately and retain their source/build receipts. `gameRoot\BepInEx\TopiaForge` must not exist; this command requires unused manager state and creates only its own provisioning staging there. Preserve prior attempts instead of deleting their state to make a retry fit.

An actual Windows Firewall rule must already block outbound traffic for exactly `gameRoot\Robotopia.exe`. The [read-only firewall check](../../../tools/TopiaForge.Acceptance.Windows/ProvisioningFirewall.cs) requires:

- Enabled rule, direction outbound (`2`), action block (`0`), all profiles (`2147483647`) and all IP protocols (`256`).
- No restricted local/remote addresses or ports; these fields must be empty or `*`. `InterfaceTypes` must be `All`, with no selected interface list and no service restriction.
- Domain, private and public firewall profiles enabled; `BFE` and `MpsSvc` running.

The observer does not create, alter or remove firewall rules. Provision and review the exact rule separately. Keep it effective throughout execution; the game's own startup can contact a backend even though the manager remains inactive.

## Closed launch input

Invoke the staged broker with exactly:

```text
TopiaForge.Acceptance.Windows.exe --observe-runtime <absolute-private-launch-json>
```

The argument identifies a UTF-8 JSON object with exactly these fields. This is a grammar, not a completed admission record:

| Field | Required value |
| --- | --- |
| `schemaVersion` | Integer `1` |
| `kind` | `sandbox-provisioning-launch-v1` |
| `qaRoot` | Existing physical QA storage root |
| `gameRoot` | Existing writable verified development-game copy |
| `sourceGameRoot` | Existing separate verified binary source copy |
| `outputRoot` | Existing private evidence root |
| `normalUserSid`, `normalUserProfile` | Actual normal-user identity, distinct from the executing QA identity |
| `expectedIdentity` | Object with exactly `userSid`, `logonId`, `sessionId`, `userProfile`, `localAppDataLow` |
| `outboundBlockRuleName` | Exact existing rule name, 1–128 characters |
| `files` | Complete prelaunch game-tree inventory; array of objects with exactly `path` and `sha256` |

`userSid` and `normalUserSid` use `S-1-5-21-A-B-C-RID` with decimal components; `logonId` is sixteen lowercase hexadecimal characters; `sessionId` is a positive 32-bit integer. The QA profile and LocalAppDataLow must exist, with the latter inside the former. Values must match the executing native primary token and known-folder APIs.

Root paths are absolute local physical paths without reparse points or mapped drives. `gameRoot`, `sourceGameRoot` and `outputRoot` must be distinct, non-overlapping descendants of `qaRoot`, separate from normal-user storage. Strings cannot contain control characters. Unknown/duplicate fields, including `persistentDataRoot`, `reviewerEvidence`, arbitrary commands or additional launch arguments, are rejected.

JSON has a 4 MiB limit, depth limit 24 and parser-token limit 24,000. The inventory's separate 1–10,000 entry bound does not override those limits. Each `path` is a safe relative forward-slash path of at most 255 characters, unique ignoring case; each `sha256` is 64 lowercase hexadecimal characters. Every existing game-tree file must be inventoried and unchanged. The streaming limits are 2 GiB (2,147,483,648 bytes) per file and 8 GiB (8,589,934,592 bytes) for the complete game-tree inventory. Both inventory verification and retained read leases enforce these bounds before hashing an over-limit file. `Robotopia.exe` and `UnityPlayer.dll` must match the corresponding source-copy hashes.

Game-input paths use a separate [filename validator](../../../tools/TopiaForge.Acceptance.Windows/ProvisioningGameInputs.cs). Each ordinary segment starts with an ASCII letter or digit and contains only ASCII letters, digits, dots, underscores, spaces, parentheses and hyphens. Trailing segment spaces/dots, reserved device names, empty segments, traversal, backslashes, alternate streams and reparse points are refused. The exact root file `.doorstop_version` is the sole leading-dot exception. This accepts the measured `Robotopia_Data/Managed/02 City Streets.dll` and `Robotopia_Data/Resources/unity default resources`; evidence artifact paths retain their stricter grammar.

The inventory includes the complete verified game and BepInEx files, plus exactly these thirteen direct children of `BepInEx/plugins/TopiaForge.ModManager/`:

```text
System.Collections.Immutable.dll
System.Reflection.Metadata.dll
TopiaForge.ModManager.dll
TopiaForge.ModManager.Core.dll
TopiaForge.Mods.Abstractions.dll
TopiaForge.Mods.Chronos.dll
TopiaForge.Mods.CreatorContent.dll
TopiaForge.Mods.Interop.Unity.dll
TopiaForge.Mods.Multiplayer.dll
TopiaForge.Mods.Prompts.dll
TopiaForge.Mods.RobotKit.dll
TopiaForge.Mods.UnityUi.dll
TopiaForge.Mods.Worlds.dll
```

Do not include nested duplicate loader assemblies, other plugins, additional BepInEx patchers, fixture packages or manager state/configuration. The [input parser and inventory verifier](../../../tools/TopiaForge.Acceptance.Windows/ProvisioningLaunch.cs) and [input read leases](../../../tools/TopiaForge.Acceptance.Windows/ProvisioningInputLease.cs) enforce the executable contract; the reviewed input must also satisfy this preparation inventory.

## Observation and ownership

The [launcher](../../../tools/TopiaForge.Acceptance.Windows/ProvisioningLauncher.cs) holds the same machine-wide `Global\TopiaForgeSandboxAcceptanceV1` mutex as the Editor/game lanes. It refuses an existing recovery marker; an abandoned mutex creates a persistent recovery marker before refusal.

The [original-process owner](../../../tools/TopiaForge.Acceptance.Windows/ProvisioningProcess.cs) creates only the fixed QA `Robotopia.exe`, suspended, with `-batchmode -nographics -noaudio`. A one-process, kill-on-close job is assigned atomically at creation. It corroborates the original process handle, executable, native creation token and primary identity before resuming. No command interpreter, inherited handles, child processes, device actuation, capture or microphone is requested. Reviewed input files remain read-locked.

A fresh `probe-<32 lowercase hexadecimal characters>` request ID and 256-bit challenge bind the original process, expected identity, exact game root and request bytes. The internal request expires after 120 seconds. It contains no expected persistence root and no approval reference.

The [early plugin branch](../../../src/TopiaForge.ModManager/TopiaForgeModManagerPlugin.Provisioning.cs) calls the [runtime observer](../../../src/TopiaForge.ModManager.Core/ProvisioningRuntimeObservation.cs) before normal manager storage, package loading or UI initialization. Both successful and refused provisioning modes remain inactive. Successful observation reads the real Unity path and native identity, verifies the path is under measured QA LocalAppDataLow, and writes once. It then arms a provisioning-only quit request in the [initialization lifetime](../../../src/TopiaForge.ModManager/PluginInitializationLifetime.cs). The first subsequent Unity `Update` consumes that request before the normal `ready` guard. Refusal cannot arm it; repeated frames, reentrancy and a throwing request cannot issue another quit. The manager remains uninitialized throughout.

Fixed `Provisioning:` log messages distinguish observation recording, the first `Update`, return from `Application.Quit`, and `OnApplicationQuit`. Logging failure cannot prevent the request. These messages are diagnostics, not process-exit receipts. Unity documents that its [wantsToQuit event](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Application-wantsToQuit.html) can cancel shutdown; the observer does not override cancellation handlers. Only the parent's retained original process handle establishes exit.

## Private evidence and recovery

Retain all records privately. Generated paths are:

| Location | Contents |
| --- | --- |
| `gameRoot\BepInEx\TopiaForge\staging\provisioning-request-<requestId>.json` | Finite, process-correlated request |
| Same directory, `provisioning-observation-<requestId>.json` | Distinct runtime observation, maximum 64 KiB |
| `outputRoot\<requestId>\launch-receipt.json` | Original process/identity, input/request digests, game inventory and firewall-rule reference |
| Same run directory, `runtime-observation.json` | Hash-verified retained copy, read only after confirmed original exit |
| Same run directory, `provisioning-result.json` | Outcome, original exit/force-termination facts, observation digest and failures |
| `outputRoot\sandbox-recovery-required.json` | Persistent ownership/recovery marker |

The runtime observation kind is `sandbox-provisioning-runtime-observation-v1`; result kind is `sandbox-provisioning-result-v1`. All carry `isolationAdmitted: false` and `qualifiesRelease: false`; the runtime observation also records `managerInitialized: false`. No acceptance acknowledgement or release evidence is produced. Early refusal can occur before a run directory exists; missing output never means success.

The parent waits up to its fixed 90-second runtime deadline, then may terminate only the original retained process and wait up to fifteen seconds for exit. Interruption, timeout, forced termination, malformed/mismatched observation or changed inputs prevent an `observed` result. Exit code `0` requires the complete correlated observation and confirmed unforced original exit; refusal/failure is nonzero. Cleanup clears only this run's unchanged recovery marker after confirmed original exit (or confirmed that no owned process was created). An unconfirmed result leaves recovery pending. Never kill by process name or reused PID, bypass a marker, overwrite a run or automatically retry.

Headless startup may fail before reaching the plugin or Unity may fail to quit. Preserve that failure; there is no automatic graphical fallback. Any revised observation approach needs concrete review before execution. Game/BepInEx startup can create QA-only files despite the manager remaining inactive; this observer is not a zero-write game launch.

A complete successful provisioning run supplies a measured persistence path for later provisioning review. It does not authorize the private isolation record, device reservation, native scenarios or candidate qualification by itself.

## First QA attempt and shutdown repair

The 2026-09-09 attempt `probe-40580fcad2ef02b1fd58df15a2bd0b8a` produced its distinct raw observation at `21:38:39.4576704Z`, but the original game did not exit within ninety seconds. The parent force-terminated that original process and confirmed exit; the native result at `21:40:08Z` is **failed**, with `forceTerminated: true`. The broker exited with code `1` and the one-time task was removed. The raw observation does not convert this into a successful provisioning run or admission.

The initial source issued its only quit request inside the early plugin `Awake` branch. Its later `Update` returned immediately because normal manager readiness was deliberately false. The retained startup log has no refusal or quit-phase diagnosis, so the exact Unity/game shutdown mechanism remains unconfirmed. The v3 source repair defers the single request to the first subsequent `Update` and adds the phase diagnostics above. Its deferred quit and OnApplicationQuit phases were observed on 2026-09-11, but successful unforced shutdown remains unverified; the existing ninety-second deadline and unforced-original-exit success condition are unchanged. Original evidence stays at the private plain path `D:\TopiaForgeQA\evidence\runtime-provisioning\probe-40580fcad2ef02b1fd58df15a2bd0b8a`; the request and raw observation remain in the original game's provisioning staging directory.

## Source verification

The initial implementation checkpoint passed 40 runtime-observer checks, 48 launcher-contract checks, the existing 29 broker and seventeen waveform checks, a zero-warning/error solution build and all required C# regression executables. The revised inventory checkpoint passes 83 launcher-contract checks, including the measured filenames, streaming boundaries, nested-plugin rejection and unchanged evidence-path grammar; its published self-contained broker also passes the 29 broker and seventeen waveform checks. The v3 shutdown repair passes 53 focused runtime-observer/lifecycle checks; its additional checks cover deferred dispatch, refusal, normal startup, repeated frames, reentrancy and callback failure. Its full C# validation and thirteen rebuilt loader hashes are recorded separately in the v3 receipt below. The v2 broker bundle remains unchanged.

A metadata census using the compiled game-input validator accepted all 409 source-game files and 22 vendored Windows BepInEx files: 5,429,810,381 bytes combined, with the largest game asset at 1,621,538,808 bytes. This confirms the measured input paths and sizes fit; the launch still performs complete hash verification and read leasing. These are source and metadata checks; consult the evidence ledger for actual QA execution.

Current private source/build receipt (plain local path): `.dart_tool/rc1-review/qa-provisioning-20260909/runtime-observer-quit-validation-v3/final-source-receipt-v3.json`. It records the v3 source hashes, all thirteen rebuilt loader hashes and individual C# validation results, and references the unchanged v2 broker bundle. Its predecessor `.dart_tool/rc1-review/qa-provisioning-20260909/runtime-observer-source-validation/final-source-receipt-v2.json` and sibling `game-input-census-v2.json` retain the broker/inventory validation. The v1/v2 receipts and bundles, archived v2 source and original QA attempt remain historical, immutable evidence. Subsequent source changes require new receipts.

## Corrected retry outcome

The v3 source correction was validated and staged separately. Its dispatched
wrapper then timed out waiting for the active QA desktop at 22:13:23Z on
2026-09-09, before starting the broker. Task result 1 and removal were recorded.
This attempt supplies no native verification of the corrected shutdown path.
Preserve its wrapper receipt and the earlier shutdown failure; use reverified
inputs and new unused wrapper outputs for any later authorized attempt. The
[evidence ledger](Evidence.md#corrected-provisioning-retry-stopped-before-broker-startup)
records the exact outcome and private retention boundary.

## Fresh provisioning retry armed — 2026-09-11

At 17:12:51Z a fresh one-time retry was armed for the next `TopiaForgeQA` sign-in. The account was signed out, so a temporary account-specific logon trigger starts the standard-user driver after sign-in. The administrator watcher removes that exact task after completion or expiry. Sign-in waiting and execution have separate bounded budgets. This was the preparation checkpoint; the completed failed outcome is recorded below.

Preparation reverified all 444 game files, 409 source-game files and 192 broker files, the exact outbound block and private ACLs. The unused separate game copy was retained. Executable source still matches the v3 checkpoint; only the already updated observer runbook differs. The new driver measures the current QA token/session and known folders before generating immutable launch input under its private state directory. The executable bundle remains read-only to QA. The staged wrapper differs by one guard: its generated launch input must reside in its bound state directory. No existing snapshot or failed attempt was overwritten.

Parsing, wrong-account refusal and harmless owned-child success/failure/timeout cleanup checks passed. A completion or failure notice is requested only in the executing QA session. The native result still requires independent review. Safe private receipt reference: `.dart_tool/rc1-review/qa-provisioning-20260909/runtime-retry-preparation-20260911T171020Z.json`; dispatch receipt uses the same timestamp. This paragraph describes preparation only; the later failed execution below supersedes its pending status.

## Provisioning retry reached Unity quit but timed out — 2026-09-11

The user completed the requested QA sign-in and reported the completion/failure notice. The driver started at 17:15:18Z, refreshed the QA token/session and host/device metadata successfully, and launched the exact verified game. The runtime recorded a correlated private observation at 17:15:35.8147503Z with the manager inactive. The loader log confirms all three shutdown phases: the first Update requested Unity quit, the quit call returned, and Unity invoked OnApplicationQuit. This establishes that the v3 deferred quit path executed; it does not establish successful process shutdown.

The original game still exceeded the unchanged 90-second deadline. The native result at 17:17:04.7499198Z is **failed**, with forced termination and subsequent original-process exit confirmed. The broker and wrapper returned 1 without themselves being force-terminated. The operator notice request succeeded; the temporary task was removed at 17:17:06.9508876Z. Follow-up inspection found no remaining Robotopia/broker process and no recovery marker. No new attempt is queued.

Nineteen files from this attempt were copied and hash-verified into private repository evidence. Independent correlation verified the request/challenge, request and launch-input hashes, actual process/token identity, timestamp bounds and raw observation. This forensic correlation does not replace the native failed result or generate an acceptance acknowledgement. Safe private retention reference: `.dart_tool/rc1-review/qa-provisioning-20260909/runtime-retry-failed-20260911T171020Z/retention.json`. Preserve both used game trees and all three attempt histories. The separate `game-provisioning-20260909T215734Z` tree is now used and cannot be offered again as an untouched copy.

The broker console streams are empty. A tightly scoped administrator check found no Player.log for this run; no old logs or credentials were read and no profile permissions were changed. Unity's [Player command-line reference](https://docs.unity3d.com/6000.0/Documentation/Manual/PlayerCommandLineArguments.html) documents that `-nographics` disables output logs, explaining this diagnostic gap. The precise native/game shutdown cause remains unconfirmed; do not attribute it to a particular Unity defect, treat OnApplicationQuit as exit, increase the deadline or force a passing result.

Before involving the operator again, prepare and review a bounded diagnostic launch mode that actually retains Unity shutdown output (and verify that logging in a controlled fixture), with an exact new private log destination and no arbitrary command arguments. Preserve the standard QA identity, owned-process controls, network block, inactive manager and unforced-exit success requirement. Add meaningful command/path and cleanup checks, validate the final source and broker, and prepare unused game/output identities. Do not automatically fall back to a different graphics mode or repeat the unchanged failed recipe. Actual isolation review, native Sandbox acceptance and release gates remain pending.

