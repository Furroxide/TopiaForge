# Slice 3: Pure resolution and transport models

Begin only after slice 2 merges into `dev`. Read the
[canonical brief](../../GamemodeContractRedesign.md),
[evidence ledger](../Status.md), and [common execution rules](README.md).
Implement deterministic C#/Dart resolution and immutable transport models without
switching production launch, profile writers, or runtime activation.

## Resolver behavior

- Implement one longest-prefix ownership algorithm used for targets, gamemodes,
  static worlds, discovery families, instances, and disabled-package diagnostics.
  Match ownership at ID segment boundaries. A disabled historical version cannot
  compete with its enabled logical package, and a shorter declaration cannot make
  the unique longer namespace ambiguous. Never fall back to a shorter owner
  when the longest matching package is disabled, missing, or otherwise invalid.
- Resolve only against the exact effective-profile package set. Check declared
  reference dependencies and version requirements. An open-policy player world
  selection needs compatibility and consent but does not fabricate a dependency
  from the target package to the world package.
- Enforce consent on open-policy defaults as well as overrides. An absent
  `allowPlayerOverride` is false. Implement every fixed, list, and open
  policy branch from the canonical brief without permissive fallbacks.
- Exclude discovered families and instances from static defaults/allow lists.
  Resolve a selected instance to its concrete world identity and retain its
  family separately. Require the correct producer/package context for discoveries.
- Accumulate all independently determinable blocking reasons; deduplicate and
  sort them ordinally by code, subject, and version. Avoid a first-error shortcut
  or dependent secondary errors for values that could not be resolved.
- Copy immutable package identities and requirements into the plan. Neither a
  later input mutation nor catalog refresh may change an already produced plan.
  Snapshot mutable manifests and selected declarations too; retain no caller-owned
  references.
  Separate the authoritative plan from its public transport descriptor. Retain the
  original request, including absent overrides, and expose package-set comparison
  plus re-resolution against loaded manifests and fresh matching runtime bindings.
  Cached observation success never proves that current-process code is bound.

## Transport models and boundaries

- Define the inactive wire-v4 models for explicit command, request ID, target,
  resolved world/transition, exact package identities, and digest. Every command,
  including main-menu, carries request/profile identity, revision, package identities
  and digest. Main-menu forbids a plan; launch-target requires a consistent plan.
  Define matching outcome/progress and versioned observation models required by
  slice 7. Retain launch acknowledgement separately from terminal session outcome.
  Native draining is orthogonal to the six lifecycle phases.
- Make C#/Dart serialization fixtures normative for equivalent operations,
  including absent values and ordinal ordering. Use one shared canonical digest
  representation from the brief; do not hash ordinary serializer output whose
  ordering may differ by language.
- Keep models framework independent and testable. Do not read the filesystem,
  registry, Unity state, or global profile state from the resolver. Do not activate
  wire v4 until its producer and consumer move together in slice 7.
- Correct historical fixtures that accidentally allow discovered static defaults
  or incomplete policy enforcement; preserve their old cases as regression intent.

## Acceptance

- Cover longest-prefix ownership permutations, prefix segment collisions,
  overlapping owners, disabled providers, missing/version-mismatched declared
  dependencies, all policies, default consent, and absent override permission.
- Cover open-policy external player selection with no invented dependency,
  concrete discovery identity versus family, forbidden discovered defaults,
  immutable plans, equivalent input ordering, and multiple simultaneous failures.
  Test family availability propagation, mismatched observations, current binding
  proof, selected target/mode/world installation compatibility, and NoAvailableTarget
  only when matching observations cover every otherwise valid admitted candidate.
- Verify 65/96/97-character identifiers across all new plan and transport paths.
  Round-trip C#/Dart payloads and compare the same digest for reordered source
  maps using the canonical representation.
- Existing production V5 launch behavior remains unchanged. New resolver code
  has no production caller and cannot silently change current profile wire output.
- Run the relevant C# and Dart tests, rebuilt release-surface verification,
  formatting, fatal-info analysis, line-count and fixture-closure audits. Update
  the ledger with pure-model coverage, explicitly marking integration pending.
- Submit only this slice and wait for its merge before branching slice 4.
