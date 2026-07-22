# Live Robotopia acceptance

The safe SDK has an instrumented, non-distributable acceptance mod under
`tests/TopiaForge.SdkAcceptanceMod`. It uses only public V1 contracts and writes machine-readable
`TF-ACCEPT|PASS|case-id|detail` markers to the attributed manager log. The canonical case list is
`tests/live-game-acceptance.json`.

## External-only launch gate

This is an external launch gate, not an offline test. A valid pass requires an authorized native
Windows or Linux/Proton host running the supported Robotopia build with real keyboard, mouse,
gamepad, audio, microphone, and rendering paths. Source-only CI, unit tests, synthetic runtime
tests, and the static capability audit verify the harness and mappings, but they cannot mark a live
case as passed or waive missing Robotopia evidence.

Acceptance evidence is valid only for the exact frozen candidate package hashes recorded by the
harness in `acceptance-result.json` and `last-run.json`. Until both platform jobs retain that
evidence, the V1 gate remains blocked. Custom-world live acceptance remains scoped to the existing
Windows/Proton hosts. Mods execute as [trusted full-process code](PrivacyAndCapabilities.md); the
capability declarations checked here are disclosure, not a sandbox.

Both workflow runners must carry `topiaforge-game-build-2227`; the Linux host also carries
`proton`. These labels are reserved for authorized machines with the pinned Robotopia installation and
acceptance peripherals, not general-purpose self-hosted runners.

Run the complete launch-blocking matrix on an authorized Robotopia build-2227 host (all cases are
required by default):

```powershell
cd apps/topiaforge_cli
dart run bin/topiaforge.dart acceptance run --game-dir C:\Games\Robotopia
```

While it runs, a tester supplies keyboard, mouse, gamepad, modal, held-item, world-session, and
robot/dialogue/voice interactions. `--all` is retained as an explicit CI assertion and is
equivalent to the default:

```powershell
dart run bin/topiaforge.dart acceptance run --game-dir C:\Games\Robotopia --all --timeout-seconds 1800
```

The harness installs the current runtime and first-party mods, packs and validates the safe
acceptance mod, seeds a schema-1 config fixture, launches Robotopia, validates `last-run.json`, and
writes `acceptance-result.json`. A pass requires the exact package to be valid and loaded, an empty
root startup error, and every requested marker.

The Windows and Linux/Proton workflow also builds an extracted candidate developer payload, uses
only its packaged CLI to create a fresh minimal mod outside the extraction, and passes that project
to the harness. The harness runs `topiaforge dev --launch --no-tail`; success additionally requires
the unique package to be `valid` and `loaded` in the fresh run plus its exact attributed `OnLoad`
marker. This proves the promised `new mod` → `dev` journey in two authoring commands.

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
Chronos, Prompts, RobotKit targets, UGC overrides, and Worlds registrations. It reuses stable ids,
checks inactive handles, verifies callbacks stop after release, and performs a final reacquisition.
Hardware-, dialogue-, robot-, pause-, and session-specific handles remain in their dedicated live
cases rather than being misreported as automatic ten-cycle coverage.

The `integration.provider-scope` marker requires exactly one provider for each declared core module,
an installed optional UGC provider, and a deliberately absent optional provider that does not block
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

The supplied Robotopia directory does not exist. Select the installed build-2227 directory.

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
