# Slice 8: V5 retirement and acceptance

Begin only after slice 7 merges into `dev`. Read the
[canonical brief](../../GamemodeContractRedesign.md),
[evidence ledger](../Status.md), and [common execution rules](README.md).
Retire V5, finish migration and authoring guidance, then establish automated and
live acceptance for the integrated redesign. Do not waive pending game evidence.

## Retire and migrate without losing information

- Replace the V5 schema with a rejecting `{"not": {}}` stub and return actionable
  retirement/migration errors at all four dispatch/validation sites. Keep explicit
  version reporting, and leave historical references clearly marked as historical.
- Implement the V5-to-V6 migration command and route V3/V4/V5 through the same
  preservation/refusal rules after version-specific mechanical normalization.
- Inspect legacy arrays and each entry before conversion. Reject malformed
  shapes and wrong scalar types before any write, including
  when `--stub` is requested. Diagnostics identify file, original index, ID when
  available, and only genuinely missing required information. Ownership errors
  refuse normal migration; an explicitly requested invalid stub retains the
  original IDs and identifies the required author fix instead of rewriting them.
- Preserve untouched JSON values and property presence; formatting may change.
  Never infer factory implementations, worlds, targets, or spawn settings. Keep
  optional world requirements optional and distinguish absent from explicit null.
- Write successful migrations and explicitly requested stubs atomically. A stub
  with unresolved required decisions must still fail V6 validation; do not make a
  seemingly valid manifest by inventing placeholders or discarding old entries.
- Retire metadata-only `--gamemode` and `mod add|remove gamemode` operations with
  early guidance to the gamemode template and contribution fields. Reject obsolete
  model-level scaffold inputs before creating files or directories.

## Complete publication and compatibility cleanup

- Remove obsolete launch models, adapters, and model-level authoring paths; check
  source, tests, examples, generated templates, fakes, and SDK baselines. Rebuild
  after baseline updates. Preserve the established Worlds assembly identity.
- Make `docs/ManifestV6.md` the complete current common-field and contribution
  reference, including deliberate exclusions and migration guidance. Correct active
  compatibility policy, guides, template READMEs, website catalog/navigation, and
  retirement messages; derive README counts from the resulting tree.
- Keep the architecture report's historical evidence intact. Link GM-01 through
  GM-10 to actual regression tests, integration paths, and live evidence. Verify
  canonical planning links and external supersession pointers remain accurate.

## Automated and live acceptance

- Add migration tests for all three source versions, unchanged value/property
  preservation, malformed collections, original-index diagnostics, no-write
  failures, atomic success, intentionally invalid stubs, and obsolete scaffold
  inputs leaving no output. Validate migrated manifests with schema and readers.
- Run the complete relevant AGENTS verification set: Release; rebuilt seven C#
  harnesses and API checks; domain/data/CLI tests and analysis; Flutter tests and
  analysis; Windows debug build; Dart formatting/line caps; conformance closure;
  repository audits; and the full website publication/reference/search checks.
- Use an isolated profile against the installed game. Record exact package/game
  revisions, launch command, scene/world, expected/actual behavior, and log/artifact
  evidence. Preserve the user's regular profiles, saves, and game content.
  Establish both loader/manager isolation and native persistent-data isolation; a
  launcher profile or copied install does not prove the latter. The current runner
  writes under the normal installation even with `skipRuntimeInstall`, so pass an
  explicit isolated runtime layout through installation, staging, launch and logs.
  Verify actual runtime roots before gameplay and abort on normal-user save paths.
  Retain PID, start time and executable identity; never stop all processes matching
  the game's path. Test stale acknowledgements, PID reuse and unrelated processes.
  A separate Windows user/session or VM may require user assistance. Do not create
  accounts or copy normal authentication/save data as an implicit setup step.
- Exercise cold launch, actual Open Sandbox geometry/environment/kill-plane/spawn,
  both discovered-level sources, authored-marker worlds, Zombies, Sandbox F5 and
  pause behavior, Free Play with Sandbox absent, restart, and main-menu return.
- Inject startup and teardown faults: allocation then factory failure, canceled
  loading with late native completion, throwing disposer, owner unload, stale
  callback, and competing requests. Verify resource release, one terminal session
  outcome, and continued ability to launch after safe cleanup/drain.
- Never infer visual placement, native timing, or gameplay correctness from unit
  tests or logs alone. If an installed game or a usable visual/native test surface
  is unavailable, mark each affected case pending with its exact blocker.

## Handoff and completion

Update the ledger with independent implemented/connected/automated-tested/
game-verified evidence for every acceptance item. Submit the final slice against
`dev` and obtain CI on the current base. The redesign is complete only after all
slices are integrated, obsolete launch paths are removed, replacement documents
are published, and the required live cases have evidence. Report any remaining
blocked acceptance plainly; do not label a partial or unverified result complete.
