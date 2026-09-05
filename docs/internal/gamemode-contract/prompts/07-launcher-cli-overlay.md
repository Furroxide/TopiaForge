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
  Submit only this slice; branch slice 8 after merge.
