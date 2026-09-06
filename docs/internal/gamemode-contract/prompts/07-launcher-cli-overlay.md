# Slice 7: Launcher, CLI, and overlay integration

Begin only after slice 6 merges into `dev`. Read the
[canonical brief](../../GamemodeContractRedesign.md),
[evidence ledger](../Status.md), and [common execution rules](README.md).
Connect all user launch paths to the same active target contract and resolver.
Remove any temporary wire adapter from slice 6 when both sides move to wire v4.

## Selection and launch

- Build effective-profile inputs from exact installed, enabled, and pinned
  packages plus installation facts. Registry metadata must not supply launchable
  declarations. Preserve disabled/unavailable selections with actionable reasons.
- Make Home, Setup, CLI, and the manager overlay select launch targets. Offer
  only world and transition overrides the selected target permits. Keep Bloc/
  repository boundaries and use TopiaForgeUi for the overlay.
- Resolve immediately before process creation and reject missing/disabled providers
  and unsatisfied package requirements. The runtime then compares package identity
  and re-resolves against loaded manifests before scene work.
- Upgrade producer and consumer together from one-shot wire version 3 to 4,
  using the shared explicit command, request ID, target, resolved world/transition,
  immutable package identity, and digest models from slice 3. Every command,
  including main-menu, has a request ID.
- Explicit main-menu must override remembered autoload; safe mode starts at
  main-menu. Only direct game startup with no launcher command can use the
  manager's remembered choice. Remove contradictory fallback paths.

## Verified integration seams and traps

Refresh these locations after slice 6 merges; this inventory was read at `dde58f2`.

- Build one exact selection adapter shared by snapshot/preview and preflight from
  `manager_state_helpers.dart` installed catalog plus dependency planning. Include
  installed default-enabled IDs absent from manager state. Do not treat an unpinned
  `state.version` as an exact pin: mirror runtime scan/reconciliation. Return the
  selected set and structured blocks; `_profileSelectionError` currently discards
  its dependency selection and never calls `LaunchResolver`.
- `_loadWorldCatalog` in `storage_helpers.dart` currently merges registry content;
  reverse the existing `world_catalog_test_part.dart` expectation that endorses it.
  `_onProfileSelected` currently only swaps an ID. Rebuild profile-specific targets
  and observations and reject stale asynchronous results during rapid switching.
- `restart()` currently stops the game before `_startGame` reaches preflight.
  Resolve the requested replacement before termination and revalidate again before
  creation. Request-owned restart/acceptance tracks PID, start time and executable;
  it must not terminate every process with the same executable path.
- Version durable selection before `WorldSelection.fromJson` supplies historical
  defaults or `WorldCatalog.fromJson` injects fallback entries. Remove Home's
  unavailable-as-None display, Setup/overlay index-zero fallback and `.first` on
  possibly empty collections. Preserve unknown transition values for explicit repair.
  Profile load/save/import/export/duplicate all pass through the old schema-2
  validator and wire constructor; migrate them together and define persisted revisions.
- Port `StartupRecoveryPolicy`, `WorldLaunchArming` and profile consumption together.
  Preserve correlation through recovery/rejection, but force main-menu in safe mode.
  A launcher command cannot fall through to manager autoload when an old optional
  worldLaunch field is missing. Pass the launcher request ID through the runtime
  main-menu entry point instead of generating an unrelated acknowledgement ID.
- `_managerStaging` and the consumer currently check immediate-child names and
  leaf reparse attributes. Establish trusted-root containment and safe ancestors
  using the runtime directory guards, including symlink/junction regressions, for
  requests, observations and progress. Validate request-owned cleanup and forged
  outcome filenames without deleting unrelated staged files.
- `LaunchResult` currently means process creation and `_launchResultState` presents
  that as completed success. Preserve the distinction from confirmed Running and
  use the existing shared outcome transport models for request-correlated evidence.

## Observations, progress, and durable state

- Replace `catalog.json` with atomic versioned observations containing profile/
  revision, producer/package provenance, discovery instances, and availability
  reasons. Ignore mismatched observations. Observations cannot invent targets,
  resurrect disabled packages, or override installed manifest authority.
- Add request-correlated progress/outcome files under the existing guarded staging
  directory. Write atomically and validate version, identity, and provenance on
  reads. Ignore stale/partial/foreign messages and preserve path protections.
- Separate process-start success from session-start success. Present Preparing,
  LoadingWorld, StartingMode, Running, and failures from correlated runtime state.
  A process without a matching acknowledgement remains unknown/unconfirmed;
  neither timeout nor a process handle proves a gameplay session is running.
- Version durable selections. Migrate only unique valid mappings; retain the
  original legacy value when ambiguous or unavailable and offer explicit repair
  choices. Never silently map the retired Sandbox ID to another gamemode.
- Provide loading, empty, error, warning, focus, and no-overflow UI states. Surface
  blocking reasons consistently across CLI and UI instead of reducing everything
  to a generic launch failure.

## Acceptance

- Test profile switching, exact pinned versions, empty effective profiles, registry
  contamination, disabled providers, package drift, stale/foreign observations,
  explicit main-menu precedence, safe mode, and direct startup fallback.
- Test 65/96/97-character IDs through durable state, observation, CLI, and wire
  paths. Cover unavailable and ambiguous legacy selections without data loss.
- Test atomic/read-interruption behavior, request correlation, process success
  followed by startup failure, missing acknowledgement, Busy rejection, progress
  ordering, and cleanup of request-owned staged files without deleting others.
- Test Home/Setup/overlay allowed choices, failure explanations, and focus/overflow
  states. Run domain/data/CLI tests and fatal-info analysis, Flutter UI/app tests
  and analysis, Windows debug build, C# checks and rebuilt release harness,
  formatting, line limits, repository audits, and publication where docs change.
- Run Windows launcher_data tests and compare any claimed environment divergence
  against the matching clean base. Record actual CI and local results separately.
- Update the ledger with production paths now connected and remaining live QA.
  Submit only this slice. After its merge, create the separate release-preparation
  slice 7a; final acceptance slice 8 begins only after that prerequisite merges.
