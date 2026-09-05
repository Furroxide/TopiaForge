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
