# Live Robotopia acceptance

The safe SDK has an instrumented, non-distributable acceptance mod under
`tests/TopiaForge.SdkAcceptanceMod`. It uses only public safe SDK contracts and writes machine-readable
`TF-ACCEPT|PASS|challenge|case-id|detail` markers to the attributed manager log. The canonical case list is
`tests/live-game-acceptance.json`.

## Administrator-controlled launch gates

This is a native launch gate, not an offline or GitHub-hosted test. The administrator-controlled
Windows workstation runs the complete Windows matrix as part of `release-admin.ps1`. For RC1, the
same workstation's Ubuntu 24.04 WSL2 environment runs the Proton matrix through WSLg against the
exact staged Linux archive. Both runs require the supported Robotopia build with real keyboard,
mouse, gamepad, audio, microphone, and rendered output. Source-only CI, a WSL build without the
actual WSLg/Proton game run, unit tests, synthetic runtime tests, and the static capability audit
cannot mark a live case as passed or waive missing Robotopia evidence. RC1 metadata explicitly
records that the WSL2/WSLg Proton run is same-host and non-independent.

Acceptance evidence is valid only for the exact frozen candidate package hashes recorded by the
harness in `acceptance-result.json` and `last-run.json`. Orchestrator-produced Proton evidence must
also bind the source SHA, release version, platform archive SHA-256 and size, canonical ecosystem
digest, Robotopia build, full case inventory, pinned Proton runtime identity, `WINEDLLOVERRIDES`,
execution environment, result, and scrubbed evidence digests. Until the automated Windows result and
the evidence for every platform in `artifactPolicy` match the candidate, `P0-GAME-01` stays
blocked.
Custom-world live acceptance remains scoped to authorized Windows/Proton hosts. Mods execute as
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

`acceptance-result.json` schema 2 records that challenge, the exact manager
`lastRunSessionId`, and the acceptance and generated-journey package receipts. A pass requires each
`last-run.json` `sourceSha256` and ordered critical-file digest inventory to match the bytes of the
package the harness actually installed. Stale sessions, replayed challenges, spoofed logger
sources, and different package bytes fail closed.

Run the complete launch-blocking matrix on an authorized Robotopia build-2409 host (all cases are
required by default):

```powershell
cd apps/topiaforge_cli
dart run bin/topiaforge.dart acceptance run --game-dir C:\Games\Robotopia
```

While it runs, a tester supplies keyboard, mouse, gamepad, modal, held-item, world-session, and
robot/dialogue/voice interactions. `--all` is retained as an explicit completeness assertion and
is equivalent to the default:

```powershell
dart run bin/topiaforge.dart acceptance run --game-dir C:\Games\Robotopia --all --timeout-seconds 1800
```

The harness installs the current runtime and first-party mods, packs and validates the safe
acceptance mod, seeds a schema-1 config fixture, launches Robotopia, validates `last-run.json`, and
writes `acceptance-result.json`. A pass requires the exact package to be valid and loaded, an empty
root startup error, and every requested marker.

## Creator workbench manual matrix

**Not a release gate.** `P0-CREATOR-01` and the challenge-bound evidence collector that attested it
were retired with the `0.x` governance change: they existed to prove an interactive workbench
session from a frozen candidate SHA, and the workbench is now a Sandbox feature rather than a
shipped package of its own. What remains is the checklist below — useful manual QA for anyone
touching `mods/TopiaForge.Sandbox/CreatorTools`, with no machine-checked evidence attached to it.
`P0-GAME-01`'s mod-load smoke is what a release actually turns on.

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

The local Windows and same-host WSL2/Proton runs also extract their candidate developer payload,
use only its packaged CLI to create a fresh minimal mod outside the extraction, and pass that
project to the harness. The harness runs `topiaforge dev --launch --no-tail`; success additionally
requires the unique package to be `valid` and `loaded` in the fresh run plus its exact attributed
`OnLoad` marker. This proves the promised `new mod` → `dev` journey in two authoring commands.

The optional extracted-release journey is configured with `--dev-cli`, `--dev-project`,
`--required-loaded-package`, and `--required-log-marker`. The options must be supplied together.
Use repeatable `--case <id>` options for a diagnostic subset; omitting them requires the full
canonical matrix.

Launch **SDK Acceptance World** from the Worlds menu (or run the mod-scoped `run-world` command),
interact with the cyan acceptance robot, then hold F9 while speaking and release it. These actions
exercise the custom-world, pause/teardown, interaction, microphone, transcription, brain-query, and
multi-turn dialogue contracts through safe APIs only.

The `lifecycle.ten-cycles` marker is emitted only after ten live acquire/release/reacquire cycles of
the automatable resource families named in `tests/live-game-acceptance.json`. The probe covers
explicit lifetime cleanup, events, scheduler work and cancellation, input, nested player-control
leases, asset/prefab/entity and interaction handles, audio, UI, localization, commands, extensions,
Chronos, Prompts, RobotKit targets, Creator Content sessions, and Worlds registrations. It reuses stable ids,
checks inactive handles, verifies callbacks stop after release, and performs a final reacquisition.
Hardware-, dialogue-, robot-, pause-, and session-specific handles remain in their dedicated live
cases rather than being misreported as automatic ten-cycle coverage.

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
