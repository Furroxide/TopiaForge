# Live Robotopia acceptance

The safe SDK has an instrumented, non-distributable acceptance mod under
`tests/TopiaForge.SdkAcceptanceMod`. It uses only public safe SDK contracts and writes machine-readable
`TF-ACCEPT|PASS|challenge|case-id|detail` markers to the attributed manager log. The canonical case list is
`tests/live-game-acceptance.json`.

## Administrator-controlled launch gates

Live game acceptance is optional for `0.1.0-rc.1`. On 2026-09-24 the project
owner made all game QA optional for RC1, so `P0-GAME-01` is advisory: a
candidate may skip the run, and then records exactly that (`result: "not-run"`)
instead of any game evidence. The skipped run is never reported as passed, and
the sixteen Unity authoring cycles below stay mandatory either way.

When live acceptance is run, nothing on this page changes: the
administrator-controlled Windows workstation must run the complete Windows
matrix against the frozen candidate with the same harness, isolation and
evidence rules. RC1 is Windows x64 only; Linux/Proton
acceptance is unavailable until native isolation is implemented and reviewed.
Real keyboard, mouse, gamepad, audio, microphone,
and rendered output are required. Unit tests and source-only CI cannot mark a live
native or visual case as passed.

Use a separate Windows user/session or VM that isolates Unity persistent data,
with authorized access to the verified installed game. An alternate launcher or
BepInEx profile alone is insufficient. Record a redacted isolation attestation;
do not access or copy a normal player's authentication data or saves. Retain the
original process-creation identity and stop only that owned process.

The redesign adds the complete case matrix in
[`tests/gamemode-release-acceptance.json`](../tests/gamemode-release-acceptance.json).
Its 36 cases include generated Open Sandbox geometry, environment and kill plane,
both discovery sources, authored markers, Zombies, Sandbox F5/pause, Free Play
without Sandbox, restart/menu, and startup/teardown/native-drain failures. When
the live run is performed, all fifteen SDK cases and ten game lifecycle cycles
remain required. Sixteen Unity 6000.0.23f1 authoring cycles are a separate
requirement that applies whether or not the live run happens; they do not replace
game cycles. Runtime Unity 6000.0.31f1 is a different version identity.

After testing, the reviewed `release-candidate-acceptance-v1.json` binds those
observations to the exact source, payload inventory, handoff and game/authoring
receipts. Its companion decision may approve only the tracked GAME row; other
gate decisions cannot change. A candidate that did not run live acceptance
instead records `result: "not-run"` with the owner's disposition reference and
the Unity authoring receipt, carries no game cases, isolation or reviewer
evidence, and keeps the tracked GAME row unchanged. `release-admin.ps1 qualify`
validates the records and freezes `accepted` state before any staging. Evidence
references require actual reviewer authorization; synthetic fixture hashes never
count as evidence.

Acceptance evidence is valid only for the exact frozen candidate package hashes recorded by the
harness in `acceptance-result.json` and `last-run.json`. Until the automated Windows result and
the evidence for every platform in `artifactPolicy` match the candidate, `P0-GAME-01` stays
blocked; while the gate is advisory by disposition, a blocked GAME row no longer holds an RC1
candidate. RC1 custom-world live acceptance remains scoped to authorized Windows hosts.
The old Proton runner and its schema2 evidence path are retired; changing the platform policy
cannot restore them. The internal `AdminRelease.md` runbook records the future Linux prerequisites.
Any future same-host evidence must disclose that it is non-independent. Mods execute as
[trusted full-process code](PrivacyAndCapabilities.md); the capability declarations checked here
are disclosure, not a sandbox.

Raw game logs remain on the QA hosts. Only bounded validation summaries and evidence digests enter
the deterministic `release-platform-bundle-v1` and aggregate `release-handoff-v1` manifests. Those
public manifests exclude usernames, hostnames, local paths, timestamps, credentials, and raw logs.
GitHub verifies this evidence as part of finalization; it does not execute Robotopia.

Each automatic run creates a cryptographically unpredictable 256-bit challenge before launch. The
non-distributable acceptance config displays it in-game and the acceptance mod includes it in every
counted result marker. The harness accepts a marker only when `ManagerFileLogger` attributes the
exact structured line to `dev.topiaforge.sdk-acceptance`; substring matches and messages from other
mods do not count. The generated-journey load marker must likewise be an exact attributed message
from the generated package ID.

`acceptance-result.json` schema 3 records that challenge, the exact manager
`lastRunSessionId`, and the acceptance and generated-journey package receipts. A pass requires each
`last-run.json` `sourceSha256` and ordered critical-file digest inventory to match the bytes of the
package the harness actually installed. Stale sessions, replayed challenges, spoofed logger
sources, and different package bytes fail closed. The required private `isolation`
object records the provisioned isolation kind, the actual provisioning-record and
acknowledgement byte hashes, the full correlated runtime acknowledgement, and
`processExitConfirmed: true`. The recorded game directory is the admitted QA
installation. These records contain private paths and OS identities; keep them on
the QA host. Public qualification carries reviewed evidence hashes and results,
never the full acknowledgement, SID, user paths or raw logs.

Run the complete matrix on an authorized Robotopia build-2478 host (all cases are
required by default):

```powershell
cd apps/topiaforge_cli
dart run bin/topiaforge.dart acceptance run --game-dir C:\Games\Robotopia --isolation-record C:\QA\isolation.json --output C:\QA\evidence
```

While it runs, a tester supplies keyboard, mouse, gamepad, modal, held-item, world-session, and
robot/dialogue/voice interactions. `--all` is retained as an explicit completeness assertion and
is equivalent to the default:

```powershell
dart run bin/topiaforge.dart acceptance run --game-dir C:\Games\Robotopia --isolation-record C:\QA\isolation.json --output C:\QA\evidence --all --timeout-seconds 1800
```

Before any acceptance staging, installation, config or launch writes, the harness
requires an existing provisioned QA layout. `--game-dir` identifies the verified
source installation; the record supplies a separate pre-provisioned `gameRoot`,
launcher data root, evidence root and actual Unity persistent-data root. The
command's output path must match the record. The operator must already be in the
approved separate Windows user/session or VM; the tool does not create accounts,
load another user's profile or copy authentication/save data.

The private record uses `schemaVersion: 1` with `kind` (`windows-user` or
`virtual-machine`), `sourceGameRoot`, `gameRoot`, `launcherRoot`, `outputRoot`,
`persistentDataRoot`, `userSid`, `userProfile`, `localAppDataLow`, `normalUserSid`,
`normalUserProfile` and nonempty `reviewerEvidence`. Paths are absolute local paths
without linked ancestors. Supply measured values and actual reviewer evidence;
a sample placeholder is not proof of isolation. `USERPROFILE` or `APPDATA`
environment overrides do not establish the primary OS identity or Unity save root.

The harness installs into the admitted layout, packs and validates the acceptance
mod, seeds its config, and launches through the production resolver. A private
request sidecar binds the exact V4 profile bytes, challenge, provisioning-record
hash, expected primary-token identity and runtime roots. The suspended native
child is inspected before resume. Before manager persistence or package loading,
the plugin measures its own OS identity and Unity persistent-data path, validates
the sidecar and publishes a write-once correlated acknowledgement. Isolation
admission does not establish Running; the normal session outcome is still required.

A pass requires the exact packages to be valid and loaded, an empty root startup
error, every requested marker and confirmed exit of the original owned process.
Skip flags never bypass isolation or supply live success evidence. Missing,
stale or mismatched acknowledgements remain failure/unconfirmed; preserve the
layout when owned-process termination cannot be confirmed.

## Creator workbench manual matrix

`P0-CREATOR-01` and its separate release-evidence collection pipeline were retired
with the `0.x` governance change. The workbench is a Sandbox feature, and the
checklist below remains additional manual QA for
`mods/TopiaForge.Sandbox/CreatorTools`. It does not create a separate creator gate.
Whenever live acceptance runs, the Sandbox F5/pause and session-cleanup cases in the
gamemode release inventory remain mandatory under `P0-GAME-01`; a mod-load smoke does
not replace that full SDK and redesign acceptance matrix.

The shipped controller opens a `CreatorProjectScope.Sandbox` session. Its production
global-mutation safety service is currently unavailable: global success paths below
require a separately implemented and reviewed native bridge. Exercise refusal and
unchanged persistence now; record unsupported success paths as unavailable, never
as passed. Dormant creator recorder code is not a release evidence source.

On an authorized build-2478 host, the workbench checks are:

1. In Sandbox, press F5 and confirm Sandbox wins routing. Menus, scene transitions, Worlds
   sessions, connected remote multiplayer, and headless processes must reject the global host.
2. Spawn curated items, environment props, and every available RobotKit robot type. Exercise search, filters,
   selection, transform, duplicate, temporary remove, undo, and explicit End Session cleanup.
3. Move a pre-existing robot and preview autonomous personality and brain changes. End the session
   and verify location, personality, and brain mode restore exactly when no external writer intervened.
   In a separate conflict case, preserve external changes and require an observable restoration
   warning. Source cleanup now propagates the lease's Conflict/error result, attempts subsequent
   cleanup and reports incomplete restoration; offline rollback regressions exercise that behavior.
   Independently verify the actual native state and visible warning on the admitted candidate.
   Passing fake-service assertions does not complete this native reporting check.
4. Register test-mod character and validated vehicle factories, spawn them, then unload their source.
   Verify instances and entries disappear safely. If build 2478 exposes no validated native vehicle
   adapter, verify that source is visibly empty or degraded.
5. Hide the workbench with F5 and its close affordance. Player controls must return while the session,
   spawns, edits, graph state, and any acquired isolation lease remain; the warning HUD must remain visible. Current Sandbox acquires no global persistence lease. Reopen
   and verify it is the same session.
6. Capture approved QA save/checkpoint hashes and attempt the currently unavailable global
   mutation path: require refusal and unchanged hashes. If a validated native bridge is later
   approved, also exercise its positive path: acknowledge isolation, mutate, End Session and
   require unchanged hashes; revoke isolation and require refusal plus immediate restoration.
7. Run a bounded branching event project, then Stop it. Graph-owned content, edits, conversation, and
   audio must roll back while an unrelated manual session spawn remains.
8. During a supported Sandbox session, exercise available scene/Worlds transition and source/mod
   unload routes separately; require owned/borrowed cleanup and released controls. Unsupported
   global or remote admission must refuse safely. Repeat every global/remote success path only
   after that capability is implemented and approved; refusal does not prove that success path.
9. Repeat open, spawn, edit, hide, reopen, graph run/stop, and End Session ten times. No object, lease,
   input, UI, interaction, conversation, audio, callback, or persistence-state count may grow between
   cycles.

The Unity-free lifecycle and rollback suites exercise ownership, conflict reporting, throwing
cleanup, stale callbacks and repeated-cycle baselines offline. The supplementary
[nine-scenario specification](../tests/sandbox-workbench-acceptance-v1.json) and
[offline verifier](../apps/topiaforge_cli/lib/src/sandbox_acceptance/sandbox_verifier.dart)
retain explicit native, Editor, device and human requirements. Their observations are
unauthenticated offline facts and cannot qualify a release. This manual matrix still requires the
actual game state, input and visual behavior that fake services cannot observe.

The supplementary [Sandbox native runner and verifier](https://github.com/Furroxide/TopiaForge/blob/main/docs/internal/launch/sandbox-automation-stages-3-6.md)
now have a separate closed native-annex schema, original-process isolation binding,
Windows input/capture broker and exact-byte media verification. Local Editor results
are recorded in [launch evidence](https://github.com/Furroxide/TopiaForge/blob/main/docs/internal/launch/Evidence.md#sandbox-automation-stages-3-6-checkpoint).
Game execution still requires admitted QA identity and actual persistence/device records;
native matrix protocol v2 closes the actuation gaps in source. Development annexes always
report `qualifiesRelease: false` and do not replace this candidate acceptance matrix.
Like the live run itself, this supplementary native matrix is optional for RC1.

The local Windows run extracts its candidate developer payload, uses only its
packaged CLI to create a fresh minimal mod outside the extraction, and passes that
project to the harness. The Windows harness runs
`topiaforge dev --launch --no-tail --target dev.topiaforge.sdk-acceptance.menu`; success additionally
requires the unique package to be `valid` and `loaded` in the fresh run plus its exact attributed
`OnLoad` marker. This proves the promised `new mod` → `dev` journey in two authoring commands.

The optional extracted-release journey is configured with `--dev-cli`, `--dev-project`,
`--required-loaded-package`, and `--required-log-marker`. The options must be supplied together.
Use repeatable `--case <id>` options for a diagnostic subset; omitting them requires the full
canonical matrix.

Select the **SDK Acceptance World** launch target
(`dev.topiaforge.sdk-acceptance.menu`) in the manager. Its manifest declares the bundle world and
`AcceptanceGamemodeFactory`; module loading and session notifications do not start controllers.
Interact with the cyan acceptance robot, then hold F9 while speaking and release it. After all ten
lifecycle cycles finish, use **FINISH SDK ACCEPTANCE** in the pause companion or the mod-scoped
`finish-world` command. Both request main-menu return through the captured session.

The world case emits PASS only after committed Running and Idle have both been observed and the
controller plus its tracked session-scope cleanup marker have been released. A process starting or
a factory returning does not satisfy this case. The automated acceptance driver
already selects `dev.topiaforge.sdk-acceptance.menu` through the production CLI
launch command, including the generated development journey. Its owned process receipt, isolation acknowledgement and request-correlated
runtime outcome must all agree. Actual isolated game evidence remains pending
until this complete path and the manual matrix are exercised on the candidate.

The `lifecycle.ten-cycles` marker is emitted only after ten live acquire/release/reacquire cycles of
the automatable resource families named in `tests/live-game-acceptance.json`. The probe covers
explicit lifetime cleanup, events, scheduler work and cancellation, input, nested player-control
leases, asset/prefab/entity and interaction handles, audio, UI, localization, commands, extensions,
Chronos, Prompts, RobotKit targets, Creator Content sessions, and session-owned pause registrations
and callback leases. Every cycle uses the immutable context supplied to the declared gamemode
session. It reuses stable ids, checks inactive handles, verifies callbacks stop after release, and
performs a final reacquisition.

Hardware input, dialogue, robot interaction, actual pause-button actions, and session transitions
still require their dedicated live cases. Automatically releasing a pause registration does not
prove the native pause UI or a scene transition worked.

The `integration.provider-scope` marker requires exactly one provider for each declared core module,
and a deliberately absent optional provider that does not block
this consumer from loading. A private probe contract then verifies singleton conflict reporting,
multiple-provider registration order, deterministic first selection, and idempotent early release.
This case does not claim to inject a corrupt package; corrupt optional-provider isolation remains a
synthetic runtime integration test.

The `integration.multiplayer-loopback` marker verifies the real in-game preview provider through the same declared
extension dependency used by ordinary mods. The acceptance mod binds a generated contract, registers snapshot-backed
state, submits a bounded typed command, verifies its canonical response/state, and observes its accepted presentation
event. It also requires a ready interactive standalone session with both logical client and server sides and a
connected local participant. It does not claim that live transport or dedicated Robotopia hosting is available.

## TFACCEPT100

The checkout is incomplete. Restore `tests/live-game-acceptance.json`.

## TFACCEPT101

No Robotopia directory was supplied. Set `ROBOTOPIA_GAME_DIR` or pass `--game-dir`.

## TFACCEPT102

The supplied Robotopia directory does not exist. Select the installed build-2478 directory.

## TFACCEPT103

The harness and acceptance specification use different schema versions. Update them together.

## TFACCEPT104

A requested case id is not in the canonical specification.

## TFACCEPT105

Only part of the extracted-release journey was configured. Supply all four journey options or none.

## TFACCEPT106

The extracted-release journey cannot be combined with `--skip-launch` because its load marker would
not be attributable to the fresh run.

## TFACCEPT107

The packaged CLI executable does not exist. Extract or rebuild the candidate developer payload.

## TFACCEPT108

The release-generated project does not exist. Create it outside the extracted payload with that
payload's `topiaforge new mod` command.

## TFACCEPT110

A CLI install, pack, validation, or launch stage failed. Follow the preceding CLI remediation.

## TFACCEPT111

The packaged CLI's `dev` command failed. Follow the preceding stable `TFDEV` diagnostic.

## TFACCEPT120

The acceptance package was not produced or the provided path is wrong.

## TFACCEPT170

One or more live markers, package outcomes, or startup checks failed. Keep Robotopia focused for
interactive cases and inspect the emitted result, `manager.log`, and `last-run.json`.
