# Slice 6: Runtime activation and manifest/template flip

Begin only after slice 5 merges into `dev`. Read the
[canonical brief](../../GamemodeContractRedesign.md),
[evidence ledger](../Status.md), and [common execution rules](README.md).
Activate V6 as one coherent slice: schema alias, all first-party declarations,
working templates, consumer migration, and removal of the old startup protocol.

## Atomic activation

- Enumerate the actual first-party manifests and affected templates at this
  revision; do not assume a historical count. Migrate them to V6 contributions
  and flip `topiaforge.mod.schema.json` in the same slice.
- Make declarations instantiate real implementations through the production
  binder. Migrate gamemodes, worlds, discovery sources, and launch targets without
  placeholder factories or metadata-only generated packages.
- Retire `GamemodeHost` and SessionChanged-as-start across SDK, runtime, fakes,
  tests, consumers, and templates. Notifications only observe committed state.
  Regenerate affected API baselines and rebuild their test projects before checks.
- Retire the old `...worlds.sandbox` declaration without silently mapping durable
  selections to another mode. Preserve an unavailable legacy selection until
  slice 7 supplies explicit repair choices.
- Introduce and test Free Play as new gameplay code that works without Sandbox.
  Move Zombies pause actions and Sandbox creator-host registration into their
  session scopes, including F5, pause, restart, and main-menu paths.

## Production composition seams from slice 5

- Configure the exact effective selection with `ModRuntime.ConfigureSessionSelection`
  before `Load`, then compose the live session environment from `SessionBindings`
  snapshots and the existing process dispatcher/executor. Do not reconstruct the
  selected package set from only successful loads or registry descriptions.
  The plugin currently supplies only `loadOrder.OrderedPackages`. Freeze every
  enabled selected manifest, retain excluded selections with their recorded errors,
  and preserve disabled/missing ownership for diagnostics. Feed the exact configured
  selection to runtime loading rather than shrinking it after load-order failure.
- Use `RuntimeWorldDiscovery` for source instantiation and atomic observations;
  enumeration never creates a source directly. Preserve its bounds, deadline,
  owner work lease and stale-attempt rejection.
- Replace the manager plugin's `TopiaForgeModManagerPlugin.Gamemodes` routing and
  the Worlds service's controller/session ownership with the one orchestrator.
  Any retained view/notification adapter delegates exclusively to committed state.
  The pause bridge currently ends a session synchronously and then invokes a captured
  vanilla exit handler. Replace that pair with the bound orchestrator operation;
  it must not initiate a second native transition.
  Native asset and failed-package cleanup barriers remain part of launch admission.
- Migrate `SandboxMod` and `ZombiesMod` from package-owned GamemodeHost callbacks
  to declared factories using the supplied session context. Config, commands and
  service visibility retain package attribution; controller resources, pause actions
  and creator-host registrations acquire the child lifetime.
- Bind Worlds declarations to the actual `OpenSandboxProvider`,
  `CuratedLevelDiscoverySource` and `BuildSceneDiscoverySource`, and retain closed
  built-in activation for bundle/game-scene declarations. Do not add duplicated
  implementation IDs or a mod-assembly dependency to Manager.
- Extend game-compatibility source auditing to the Manager native world adapter
  before activating it. The existing reflection auditor only scans mod folders.
  Tighten the checkpoint and scene-loader parameter/return contracts to the observed
  awaitables, and declare the public no-argument launch-request constructor used
  by the shared no-op importer helper. Prove these checks fail for incompatible
  metadata before the fixes; an existing type-name binding alone is insufficient.
  Map `io.github.furroxide.topiaforge.modmanager` to its source folder and the
  source-linked `mods/Shared/UgcNoOpLaunchRequest.cs`. Current 0.0.2409 metadata
  records `LoadSceneImpl(Boolean, CheckpointAsset) -> UniTask`,
  `SceneUtil.LoadScene(String, CancellationToken) -> UniTask<Scene>` and public
  static `PlayerController.FindPlayer() -> PlayerController`. The checker currently
  treats empty parameter lists as name-only and does not enforce visibility/static
  constraints; add exact zero-arity/visibility/static checks with regression tests.
  Declare `UgcPlayLauncherLastRun()` and its three public instance String fields,
  plus public static `UgcPlayLaunchRequest.Create(UgcPlayLauncherLastRun)`.
  Replace fictitious `LevelEntry` bindings with `LevelEntryPoints+Entry`; capture
  missing nested-member and awaiter metadata before claiming coverage. Baseline
  references name assembly `UniTask`, not the old uncheckable
  `Cysharp.Threading.Tasks` entry. Promote readiness/import-suppression requirements
  from cosmetic Optional classifications where activation now depends on them.

## Keep this intermediate commit usable

- If the existing launcher wire needs compatibility until slice 7, use one small
  old-wire adapter that translates requests into the new resolver/orchestrator.
  It must not retain an independent scene loader or controller startup protocol.
  Record its exact removal target in the ledger and slice 7 handoff.
- Update generated gamemode/world package source, manifests, and instructions
  together. Every advertised template must build and load with the newly active
  contract before this slice lands.
  Flip both current-schema constants with the alias: all seven templates inherit
  the base schema. Reject obsolete model-level gamemode scaffold inputs before any
  filesystem creation; `ModScaffoldOptions.applyTo` currently injects retired fields.
  This early rejection belongs with activation even though full migration tooling
  and remaining CLI retirement documentation land in slice 8.
- Update active guides and template READMEs that would otherwise teach a removed
  API. Complete the new reference incrementally, with full retirement material in
  slice 8. Keep links and website publication green at this intermediate revision.
- Preserve assembly identity `0.1.0.0`, package/runtime operational boundaries,
  enablement and restart semantics, and the shared Busy transition policy.

## Acceptance

- Validate every first-party and generated manifest against the canonical V6
  alias and both readers. Prove V5 remains accepted only through explicit V5
  dispatch until the retirement slice; the canonical schema represents V6.
- Generate gamemode and world packages into isolated temporary directories,
  compile them, and load them via production binding/orchestration. No test-only
  registrations and no generated package that compiles but cannot activate.
- Search and test for removed GamemodeHost/startup-event usage. Prove exactly one
  controller starts, session-owned registrations disappear on teardown, and Free
  Play activates when Sandbox is absent.
- Run Release, rebuilt seven-harness release verification, relevant domain/data/
  CLI/template checks, formatting, analyzers, line audits, repository audits, and
  full documentation publication. Scope Flutter checks to any changed integration.
- Do not claim F5/pause, spawn, or scene teardown works in Unity from unit tests.
  Record each unrun live case as pending for slice 8.
- Update the ledger and submit this coherent activation slice. Branch slice 7
  only after merge; do not split the alias from manifest/template migration.
