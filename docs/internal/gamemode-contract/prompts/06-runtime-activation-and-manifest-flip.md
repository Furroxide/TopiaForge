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

## Keep this intermediate commit usable

- If the existing launcher wire needs compatibility until slice 7, use one small
  old-wire adapter that translates requests into the new resolver/orchestrator.
  It must not retain an independent scene loader or controller startup protocol.
  Record its exact removal target in the ledger and slice 7 handoff.
- Update generated gamemode/world package source, manifests, and instructions
  together. Every advertised template must build and load with the newly active
  contract before this slice lands.
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
