# Live Robotopia acceptance

The safe SDK has an instrumented, non-distributable acceptance mod under
`tests/TopiaForge.SdkAcceptanceMod`. It uses only public safe SDK contracts and writes machine-readable
`TF-ACCEPT|PASS|challenge|case-id|detail` markers to the attributed manager log. The canonical case list is
`tests/live-game-acceptance.json`.

## Administrator-controlled launch gates

The administrator-controlled Windows workstation must run the complete Windows
matrix against the frozen candidate. RC1 is Windows x64 only; Linux/Proton
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
without Sandbox, restart/menu, and startup/teardown/native-drain failures. All
fifteen SDK cases and ten game lifecycle cycles remain required. Sixteen Unity
6000.0.23f1 authoring cycles are a separate requirement; they do not replace game
cycles. Runtime Unity 6000.0.31f1 is a different version identity.

After testing, the reviewed `release-candidate-acceptance-v1.json` binds those
observations to the exact source, payload inventory, handoff and game/authoring
receipts. Its companion decision may approve only the tracked GAME row; other
gate decisions cannot change. `release-admin.ps1 qualify` validates the records
and freezes `accepted` state before any staging. Evidence references require
actual reviewer authorization; synthetic fixture hashes never count as evidence.

Acceptance evidence is valid only for the exact frozen candidate package hashes recorded by the
harness in `acceptance-result.json` and `last-run.json`. Until the automated Windows result and
the evidence for every platform in `artifactPolicy` match the candidate, `P0-GAME-01` stays
blocked. RC1 custom-world live acceptance remains scoped to authorized Windows hosts.
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

Run the complete launch-blocking matrix on an authorized Robotopia build-2409 host (all cases are
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

`P0-CREATOR-01` and its separate challenge-bound workbench collector were retired
with the `0.x` governance change. The workbench is a Sandbox feature, and the
checklist below remains additional manual QA for
`mods/TopiaForge.Sandbox/CreatorTools`. It does not create a separate creator gate.
The Sandbox F5/pause and session-cleanup cases in the gamemode release inventory
remain mandatory under `P0-GAME-01`; a mod-load smoke does not replace that full
SDK and redesign acceptance matrix.

On an authorized build-2409 host, the workbench checks are:

1. In Sandbox, press F5 and confirm Sandbox wins routing. Menus, scene transitions, Worlds
   sessions, connected remote multiplayer, and headless processes must reject the global host.
2. Spawn curated items, environment props, and every available RobotKit robot type. Exercise search, filters,
   selection, transform, duplicate, temporary remove, undo, and explicit End Session cleanup.
3. Move a pre-existing robot and preview autonomous personality and brain changes. End the session
   and verify location, personality, and brain mode restore exactly.
4. Register test-mod character and validated vehicle factories, spawn them, then unload their source.
   Verify instances and entries disappear safely. If build 2409 exposes no validated native vehicle
   adapter, verify that source is visibly empty or degraded.
5. Hide the workbench with F5 and its close affordance. Player controls must return while the session,
   spawns, edits, graph state, and isolation lease remain; the warning HUD must remain visible. Reopen
   and verify it is the same session.
6. Before global mutation, capture save and checkpoint hashes. Acknowledge isolation once, mutate the
   scene, then End Session and confirm both hashes are unchanged. Also revoke or make isolation
   unavailable and confirm mutation fails closed and any active session restores immediately.
7. Run a bounded branching event project, then Stop it. Graph-owned content, edits, conversation, and
   audio must roll back while an unrelated manual session spawn remains.
8. While a global session is active, replace the scene, start a Worlds transition/session, admit a
   remote participant, and unload a source/mod in separate runs. Each route must restore owned and
   borrowed state and release controls.
9. Repeat open, spawn, edit, hide, reopen, graph run/stop, and End Session ten times. No object, lease,
   input, UI, interaction, conversation, audio, callback, or persistence-state count may grow between
   cycles.

The Unity-free lifecycle suite protects the same ownership and rollback policies offline, so a
change that breaks them fails there first; this matrix is what catches the native-only behaviour it
cannot see.

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

The supplied Robotopia directory does not exist. Select the installed build-2409 directory.

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
