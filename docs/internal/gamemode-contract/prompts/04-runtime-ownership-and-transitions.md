# Slice 4: Runtime ownership and transition foundations

Begin only after slice 3 merges into `dev`. Read the
[canonical brief](../../GamemodeContractRedesign.md),
[evidence ledger](../Status.md), and [common execution rules](README.md).
Build the lifecycle, resource ownership, and single native transition foundation.
Keep the V6 declaration activation and old public startup-API removal for slice 6.

## Session transaction and scope

- Implement `Idle -> Preparing -> LoadingWorld -> StartingMode -> Running ->
  Stopping` with one authoritative owner. Competing launches return Busy during
  Preparing, LoadingWorld, StartingMode, Stopping, and native drain. Validate a
  replacement before stopping a Running session.
- Allocate immutable session/target/world identity, cancellation token, child
  lifetime, scoped mod context, and session-bound stop/restart/main-menu handles
  before invoking any consumer constructor or startup callback.
- Rebind resource-producing service facades to the child scope, preserving the
  package identity, capabilities, and dependency visibility. Replacing only the
  exposed `Lifetime` while handing out package-lifetime facades is insufficient.
- Commit state before publishing notifications. Notifications observe the session;
  they cannot create controllers. Launch reports success only after Running.
  This applies to the new lifecycle; the old V5 startup host remains until slice 6.
- Cleanup cancels the session token before any extension disposer, tries every
  owned disposer, aggregates failures, releases
  ownership, and emits one terminal session outcome. Make cleanup idempotent.
  Session-bound handles and stale callbacks cannot stop or mutate a later session.
- Introduce the async startup/provider interfaces required by the canonical brief
  without activating incomplete first-party declarations. Keep the Worlds SDK
  assembly identity at `0.1.0.0` and update/rebuild owned API baselines as needed.

## Existing ownership seams to verify

Use `ModContext`, `OwnerModLifetime` and `IGameplayContextFactory` as the starting
points. Create a scope for each participating package; preserve public package
identity while keeping internal resource ownership unique to that scope.

- Recreate files, events, commands and extension facades as well as gameplay
  facades. Extension factories capture their facade's lifetime; reusing a parent
  extension cache leaks session resources into package ownership.
- Forward package event delivery into active child contexts. Preserve lookup of
  package localization catalogs while disposing child registrations separately.
- A child UI host must not unregister package or sibling hotkeys when it stops.
  `UiHost.Dispose` currently unregisters by public owner ID; introduce separate
  internal registration ownership and test sibling survival.
- Define how a child spawns from package-owned asset handles. Current facade
  reference-equality ownership must not force package assets into session cleanup
  or let a child dispose the package's asset bundle.
- Guard direct mutations after stop, including player damage/heal, entity
  transform/destruction, command execution and world/creator operations. Resource
  registration checks alone do not prevent stale callbacks changing a new session.
- Keep Unity access and callbacks on the host dispatcher. Controlled asynchronous
  tests must exercise completion from other threads as well as synchronous results.
- The guarantee covers resources allocated through scoped framework services and
  explicitly tracked disposers. Test constructor failure with such allocations;
  arbitrary unmanaged/global allocations require their own author cleanup contract.

## Native transition executor

- Make Worlds loads, core scene requests, local world loading, restart, and
  main-menu changes acquire the same native transition owner. Route current
  entrypoints through the executor while preserving existing visible behavior.
- Do not release ownership when a caller cancels if native engine work can still
  complete. Drain or safely retire that operation before admitting another one.
  Preserve late-arrival/quarantine handling and multiplayer authority checks.
- Apply `sceneChangePolicy` to external/native scene changes without treating
  every scene notification as a request to construct another controller.
- Keep orchestration/ownership logic testable without Unity; isolate engine
  interaction behind adapters in the existing Unity-dependent layer.

## Acceptance

- Use controlled fakes and fault injection for allocation followed by constructor
  failure, startup failure, cancellation in every phase, throwing cleanup, owner
  unload, and stale callback completion. Assert every tracked resource is released
  and all later disposers run after an earlier disposer throws. Assert every
  extension disposer observes cancellation.
- Prove exactly one controller/session owner, one terminal outcome, correct
  committed-state notification ordering, and no success before Running.
- Hold a fake native operation after cancellation and verify new Worlds, core,
  local-world, restart, and main-menu requests all remain Busy until drain ends.
  Cover late arrival, safe retirement, and multiplayer authority rejection.
- Run Release, the relevant harnesses and rebuilt release-surface script; register
  suites in default full runs. Run consumer tests for any facade/executor changes.
- Preserve functioning V5 runtime behavior until the atomic activation slice.
  Record Unity scene timing and native fault acceptance as pending game checks.
- Update the ledger and submit this slice alone. Branch slice 5 only after merge.
