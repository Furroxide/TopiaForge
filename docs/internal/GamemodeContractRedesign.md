# Gamemode contract redesign

Status: approved implementation specification, 5 September 2026. This document
supersedes the external ManifestV6 stage-1 brief and stage-2 prompt. It describes
the required result, not a claim that the result exists. Read the
[evidence ledger](gamemode-contract/Status.md) for implementation and verification
status and the [execution prompts](gamemode-contract/prompts/README.md) for the
next bounded slice.

## 1. Authority and outcome

The launcher, CLI, in-game manager, and direct-game startup must select and
execute the same manifest-declared launch target against the exact effective
profile. The runtime owns one session lifecycle, reports success only after
world and gameplay readiness, and releases session resources even when startup
or cleanup fails. Declarations that cannot execute remain visible with reasons.

The accepted decisions below supersede contradictory historical prose:

- V6 replaces V5. Retire V5 with rejecting schemas, actionable readers and
  validators, and a one-time author migration. Do not preserve old binaries by
  retaining the old gamemode startup API.
- V6 contains worlds, gamemodes, and launch targets under `contributions`.
  `options`, `optionValues`, and `sessionExtensions` are excluded. Do not restore
  these speculative shapes while repairing the contract.
- Retire `GamemodeHost`, observer-driven startup, imperative launch declarations,
  and the two legacy transition preference/fallback flags at the scheduled
  activation boundaries. Notifications observe committed state only.
- The Worlds contract assembly keeps `AssemblyVersion` 0.1.0.0. The additive
  constraint applies to types compiled into `TopiaForge.Mods.Abstractions`, not
  every source file located in its directory. Amend AGENTS with that assembly
  distinction when changing the Worlds API.
- Preserve `.topiaforgemod`, the `topiaforge.mod.json` filename, package dependency
  ordering, profiles, inbox, logs, enablement, and restart-required semantics.
- Keep domain/resolution/state-machine logic Unity-free. Keep native adapters in
  the manager and existing game-side provider modules. Flutter state remains
  Bloc-based. Non-generated Dart files, including tests, stay at most 500 lines.
- All native in-game presentation uses the TopiaForgeUi kit and its accessibility,
  ownership, scrolling, and confirmation conventions. Clean-room rules apply.

The [architecture report](../GamemodeArchitectureReport.md) is historical problem
evidence. Its line numbers, proposed option shapes, and compatibility statements
are not normative. The ledger maps GM-01 through GM-10 to acceptance evidence.

## 2. Manifest and conformance contract

Use the V6 schema from the reviewed source stack as the starting point, keeping
the V5 common contract unchanged except for explicit V6 additions and retirement.
Package IDs remain at most 64 ASCII characters; declaration IDs have the separate
96-character ASCII grammar. Preserve the top-level `gamemodes` retired sentinel;
it must not become the declaration wrapper.

Give these rules descriptive names in diagnostics/docs instead of unexplained
historical R-numbers:

| Rule | Required behavior |
| --- | --- |
| Ownership | A declaration belongs beneath `package.name + "."`, with a nonempty suffix. |
| Uniqueness | Declaration IDs are unique across all contribution arrays, ordinal case-insensitive. |
| Binding | Omitted assembly means entryAssembly; explicit assembly is a safe package-relative DLL present in hashes. |
| Required references | Foreign manifest references belong to a required dependency, never an optional dependency. |
| Typed local references | Target modes and world consent name gamemodes; policy default/allow name worlds. Missing or wrong-kind local references fail. |
| Reachability | A mode without a local target is a warning, allowing another package to target it. |
| Policy | Fixed prohibits allow and overrides; list includes its default; default and allow never name discovered families or instances. |
| Pairing | Validate locally knowable spawn and transition requirements; validate complete cross-package pairings during resolution. |

Before constructing typed models, both readers enforce JSON primitive types,
presence, explicit-null rejection, conditional fields, bounds, and collection
uniqueness. An empty value does not make a prohibited field absent. Never coerce
strings to numbers, numbers to strings, or fractional sort keys to integers.
ASCII identifiers/type names and Unicode character-count limits must agree with
the schema. Optional false/zero values survive parsing and round trips.

The harness is a prerequisite for validation fixes:

- Index every JSON fixture recursively, excluding only the root `index.json` and
  root `fixture.schema.json`. Unknown/misspelled channel directories and root-level
  cases fail rather than disappear.
- Every manifest fixture states schema validity separately from shared semantic
  reader expectations. Execute the actual pinned Dart JSON Schema validator on
  the payload, including `contains`. Schema-valid semantic failures are normal.
- Equivalent reader operations share accept/reject, error-code, and structured
  normalized expectations. Successful cases require every contribution field,
  including display metadata, to appear in normalization with presence preserved.
  Do not add per-language exemptions to make a failing case pass.
- Wire producer/consumer differences must be explicit operations, not a general
  allowance for same-operation drift. Resolution reasons have ordered full tuples.
- Keep full-run C# registration and index/envelope self-tests. Add a regression
  before fixing each confirmed defect; show the intended pre-fix failure.

## 3. Resolution and observations

`EffectiveProfile` contains the exact enabled package selection, disabled installed
packages for diagnostics, profile identity/revision, and installation facts. Build
it from the installed package catalog and existing dependency planner; registry
entries cannot supply declarations or satisfy an installed dependency.

`LaunchRequest` selects a target and optional world/transition overrides.
`LaunchResolution` returns either an immutable `LaunchPlan` or all determinable
blocking reasons. A plan carries target, mode, concrete world ID, optional family
ID, selected transition, sorted immutable package identities, and package digest.
Keep the established deterministic package digest as a consistency check; package
integrity remains the installer's hash/receipt responsibility. Never retain mutable
caller lists, manifests, or selected declaration references underneath a precomputed
digest. Snapshot identities and any plan data needed after resolution.

Apply one longest-prefix owner lookup everywhere, including target selection and
disabled-package diagnostics. A longer installed owner cannot be bypassed to find
a declaration in a shorter package. Report namespace ambiguity when overlapping
ownership prevents a valid lookup; do not fabricate missing-declaration reasons
when the owner itself is ambiguous.

Required dependency edges and pinned ranges govern references written in
manifests: target mode, policy default/allow, and explicit world consent. A
player-selected open-policy world needs enabled ownership, compatibility, and
consent; it does not create a target-to-world dependency requirement.

`allowPlayerOverride` defaults to false. A policy's admissible set does not itself
grant permission to change the default. Open-policy consent applies to the default
too. Explicit `openTo` requires the world package's dependency on the mode owner
and its satisfied range; `openToAnyCompatible` does not invent such a reference.

Intersect world transitions with mode requirements. Auto prefers scene replacement
then additive arena. Only `player-choice` admits a player transition override.
Accumulate independent compatibility, consent, spawn, availability, and binding
failures before returning. Sort by ordinal code, subject, then subject version and
deduplicate complete tuples. Do not derive later checks from missing prerequisites.

Discovered families are declarations; concrete instances are observations. A
policy always has a static default. Only permitted player overrides can select an
observed instance. The plan's world ID is that instance, never its family prefix.
Determine NoAvailableTarget from the worlds the request actually permits.

Replace diagnostic `catalog.json` with an atomic, versioned observation envelope
containing profile identity/revision, producer package identity/version, effective
package-set digest, observation revision, discovered instances, and availability
reasons. Use the existing guarded filesystem boundaries and size limits. Ignore
observations whose provenance no longer matches. Observations enrich/narrow current
installed declarations; they never create launch targets or re-enable packages.
First run offers static content and explains why discovered content is unavailable.

At runtime, compare the immutable plan package set and digest to loaded packages,
then resolve the requested tuple against loaded manifests and bindings before
Preparing. A mismatch prevents all scene effects. Static resolution treats binding
as unknown unless a matching observation records a failure; runtime binding is
mandatory. Use GamemodeUnbound for modes and add WorldUnbound for failed provider
bindings rather than pretending the declaration is absent.

## 4. Runtime ownership and execution

### Public Worlds interfaces

- `IGamemodeFactory.StartAsync(IGamemodeSession, CancellationToken)` returns
  `Task<OperationResult<IGamemodeController>>`. Remove the redundant factory mode
  ID: the verified manifest declaration owns identity.
- `IWorldContentProvider.LoadAsync(IWorldLoadContext, CancellationToken)` returns
  `Task<OperationResult<IWorldInstance>>`.
- `IWorldDiscoverySource` extends the provider with bounded asynchronous discovery
  of immutable family-instance descriptors (identity and display metadata, never
  targets). Cancellation/limits apply to discovery as well as loading.
- `IWorldInstance` owns actual scene identity, optional placed content, resolved
  spawn, and disposal. Native objects stay behind game-side adapters.
- `IGamemodeSession` exposes immutable session/target/world identity, cancellation,
  child lifetime, a session-scoped mod context, and session-bound stop, restart,
  and return-to-menu operations. World load context carries the selected transition,
  spawn policy, instance/family identity, and the world owner's scoped context.

Bind only public, concrete types with public parameterless constructors from the
declared package assembly. Verify exact interface compatibility and package/hash
ownership without instantiating at catalog time. Create instances within the owned
startup scope. Publish successful bindings and removals atomically; a fault blocks
dependent targets with diagnostic attribution instead of hiding them.

Create a unique session ID and child scope before any provider/factory callback.
Rebuild resource-producing facades against that scope with unchanged package ID,
paths, permissions, capability checks, and dependency visibility. Forward scoped
events from the parent event source. Reusing services that captured the parent
lifetime would preserve partial-start leaks even with a new Lifetime property.
Parent unload cancels/disposes all children. Package registrations and session
resources remain separate ownership categories.

### Lifecycle and scene authority

The single orchestrator commits `Idle -> Preparing -> LoadingWorld -> StartingMode
-> Running -> Stopping`. Start completes successfully only at Running. A failed or
cancelled launch has one operation outcome; a session has one terminal notification
after stop. Notifications never start gameplay. Session handles reject stale IDs.

Validate a replacement before stopping a Running session. Preparing, loading,
starting, stopping, and quarantined native work reject all competing requests as
Busy/Conflict; there is no implicit queue or priority-based supersession. Preserve
existing multiplayer authority policy when admitting a transition.

One executor owns every framework scene dispatch: core scene API, Worlds, local
worlds, restart, and main-menu. Reuse generation matching and late-arrival quarantine.
Caller cancellation may finish its task but cannot release native busy ownership
until the engine operation actually drains or reaches a terminal failure. An
uncancellable load must not be treated as rolled back. Report an explicit recovery
to a known menu state after irreversible failure.

World adapters wait for scene completion, content placement, player readiness, and
the declared spawn to be applied before gameplay starts. Missing/ambiguous authored
markers fail startup. Implement generated Open Sandbox through its provider with
the existing arena, environment, and kill plane; loading the UgcPlay host scene
alone is not equivalent. Restore level and build-settings discovery sources.

Teardown cancels session work before invoking extension disposers, then attempts
every controller/content/resource/claim cleanup,
aggregate failures, clear owned references before extension callbacks, and publish
terminal notification in finally. Native scene changes apply sceneChangePolicy;
they do not silently create another controller. Move pause actions and Sandbox
creator-host registration into session scope. Free Play imposes no gameplay rules
after world readiness and must work without the Sandbox package.

## 5. Launcher, wire, state, and author tooling

Home, Setup, CLI, and the manager overlay consume the same launch-target catalog
and resolver. Offer only permitted world and transition changes. Keep unavailable
saved choices visible with their reasons and explicit repair actions. Common
presentation remains in Bloc/widgets and the UI kit; filesystem/process work stays
in data services.

Resolve again immediately before process creation. Reject a blocked target before
writing a launch instruction or spawning a process. Preserve exact empty profiles,
selected versions, and inherited manager state according to the existing package
selection rules. Safe mode explicitly starts at main-menu.

Profile launch wire version 4 replaces version 3. Every command carries a request
ID; its explicit command is main-menu or launch-target. Launch-target additionally
carries resolved target/mode/world and transition, immutable package identities,
and digest. The main-menu command cannot
fall through to remembered autoload. Only direct startup without a launcher command
may use the manager's own remembered target. Consume the guarded one-shot request
once; never modify a mod's config to convey launch intent.

Write atomic request-correlated progress/outcome documents under manager staging.
Expose them through the repository to Bloc/CLI and diagnostics. Process creation
success is distinct from session readiness. Missing acknowledgement is
unknown/unconfirmed, never proof of gameplay success. Runtime failures include
structured reasons and retain request/session attribution. Do not accept arbitrary
output paths from profile data.

Version durable target selection and migrate prior profile/manager selections
without losing unrelated state. A legacy mode/world tuple maps automatically only
when exactly one current target represents it and its policy permits the selection.
Otherwise retain the original tuple as unresolved. Never alias the retired Worlds
Sandbox mode to Free Play or the creator mode. Offer target reselection or explicit
main-menu as repair. Old diagnostic caches may be discarded.

V3/V4/V5 manifest migration first validates original legacy entry types/indexes,
then carries mechanically derivable data. Malformed legacy entries refuse even
with --stub; author incompleteness is not permission to discard malformed data.
Preserve unmodified JSON values and
presence; document formatting changes honestly. Missing implementation, target,
world, ownership, or spawn information requires author input. Do not invent values
from entryType, configuration defaults, menu code, or filenames. Optional world
requirements remain optional. Refusal writes nothing and names file/index/ID/field.
Write successful migrations atomically too. With --stub, atomically write retained
identities and x-migration-todo data while
leaving genuinely required data absent so validation rejects packing/publication.

Retire metadata-only --gamemode and mod add/remove gamemode interfaces with an
early actionable error pointing at the gamemode template and contribution fields.
Reject obsolete ModScaffoldOptions inputs before any directory/file creation.
Generated template tests must load declarations through production binding and
orchestration, not manually register the thing the template omitted.

## 6. Sequential delivery

All slices target dev and must be independently green. Reuse reviewed source
commits from the unmerged stack without merging the old broken sequence. Preserve
source branches and useful review history. Create each next branch only after the
preceding slice merges. Fetch/compare before work, before each push, and after long
builds; rebase rather than merge dev into feature branches. Explicitly name push
targets. Stage owned paths only; never use git add -A in the shared environment.

| Slice | Deliverable and activation boundary |
| --- | --- |
| 1 | This brief, status/evidence ledger, prompts, and corrected completion claims. |
| 2 | Unused V6 schema/readers/validators and complete conformance; alias and first-party manifests remain V5. |
| 3 | Corrected pure resolver, immutable plans, transport models/fixtures; no production switch. |
| 4 | Scoped contexts, lifecycle machinery, shared scene executor, fault-injection tests. |
| 5 | Bindings, real world providers/discovery/readiness, synthetic-package integration. |
| 6 | Atomic V6 alias/manifest/template flip, live declaration activation, consumer migration, old SDK startup API removal. |
| 7 | Launcher/CLI/overlay target selection, preflight, wire/state migration, observations/outcomes. |
| 8 | V5 retirement, author migration, obsolete models removed, complete public docs and game acceptance. |

During slice 6 only, an internal old-wire adapter may translate an unambiguous
legacy request into the new orchestrator. It is not another startup protocol and
must disappear in slice 7. Keep templates functional until their declarations have
a production consumer. Re-cut again if a slice exceeds a reviewable cohesive scope;
record the changed boundary before expanding work.

The old external briefs become supersession pointers. Internal execution prompts
describe prerequisites/deliverables rather than embedding stale remote SHAs, check
counts, permissions, or supposed permanent machine failures. The status ledger
records observed evidence at an exact revision, separately from required behavior.

ManifestV6.md becomes the complete public contract including common fields and
deliberate exclusions. V5 remains a retirement/migration page. Correct active
guides, compatibility policy, template READMEs, CLI help, website page catalog and
navigation in the slice that changes their behavior. Derive README guide counts
from the tree; internal documents do not add top-level guides.

## 7. Acceptance and evidence

Every slice records its changed-path scope, exact revision, test command/results,
known limitations, and remaining integration dependencies. Tests must detect the
defect before its fix. Do not claim a fresh build from a pre-existing binary probe.

- Contract: required/present/null/wrong-type cases; every conditional branch;
  ownership and dependency failures; 65/96/97-char IDs; Unicode text boundaries;
  full normalization; schema semantics; fixture omission/placement failures.
- Resolution: input-order invariance; nested owners; denied overrides; open consent
  and ranges; independent simultaneous failures; discovered instances; immutability;
  package-set revalidation against loaded state.
- Runtime: constructor/start allocations then throw; cancellation at every phase;
  throwing cleanup; unload; stale callbacks; synchronous and late native completion;
  shared scene admission; marker readiness; exactly one controller/outcome.
- Launcher: no process on blocked plan; exact-empty/pinned/disabled profiles;
  profile changes invalidating observations; explicit main-menu precedence; unknown
  runtime acknowledgement; durable unresolved choices; template end-to-end loading.
- Migration: all legacy selectors; malformed/mixed arrays; untouched values and
  extensions; indexed diagnostics; atomic writes; invalid stubs; no-write failures.

Run applicable AGENTS builds/tests, the freshly rebuilt seven-harness release
surface verification, C# formatting verification, pinned Flutter Dart formatting
and fatal-info analysis, Dart line-count audit, fixture/repository/legal audits,
docs publication check, and Flutter tests/Windows build for application changes.
Rebuild after changing embedded API baselines before any --no-build harness run.
Use a bounded short PATH for Windows Flutter child processes when needed. Compare
local launcher_data failures against the same clean revision/environment; never
inherit the old seven-failure claim as a permanent exemption.

Live acceptance uses an isolated profile and test content against the installed
game, with user assistance for visual checks. Capture cold launch, generated arena
geometry/spawn, both discovered sources, authored-marker maps, Zombies, Sandbox
F5/pause, Free Play without Sandbox, restart, main-menu return, and injected
startup/teardown faults. Record game build, package revisions, logs, outcomes, and
what was actually observed. Do not overwrite a normal player's profile or save.

Completion requires integrated slices, removed obsolete launch paths, published
replacement guidance, passing automated evidence, and recorded game acceptance.
Unavailable native-timing or visual checks stay explicitly pending.
