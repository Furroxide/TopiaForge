# Slice 8a: V5 retirement and authored-world fixes

Begin only after slice 7 and the separate release-preparation slice 7a merge into `dev`. Read the
[canonical brief](../../GamemodeContractRedesign.md),
[evidence ledger](../Status.md), and [common execution rules](README.md).
Retire V5, finish migration and authoring guidance, and align authored marker
validation with runtime loading. Isolated acceptance tooling and actual game
evidence belong to the sequential [8b handoff](08b-isolated-acceptance.md).

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

## Automated verification

- Add migration tests for all three source versions, unchanged value/property
  preservation, malformed collections, original-index diagnostics, no-write
  failures, atomic success, intentionally invalid stubs, and obsolete scaffold
  inputs leaving no output. Validate migrated manifests with schema and readers.
- Run the complete relevant AGENTS verification set: Release; rebuilt seven C#
  harnesses and API checks; domain/data/CLI tests and analysis; Flutter tests and
  analysis; Windows debug build; Dart formatting/line caps; conformance closure;
  repository audits; and the full website publication/reference/search checks.
- Recheck Windows data-test failures against the matching clean base. Record actual
  results rather than inheriting a permanent local-failure exemption.
- Record the pinned Unity EditMode and live prefab spawn checks as pending if the
  editor/game is unavailable. Linked helper tests cannot establish engine timing.

## Handoff and completion

Update the ledger with independent implemented/connected/automated-tested/
game-verified evidence for every acceptance item. Submit this bounded slice against
`dev` and obtain CI on the current base. The redesign is complete only after all
slices are integrated, obsolete launch paths are removed, replacement documents
are published, and the required live cases have evidence. Report any remaining
blocked acceptance plainly; do not label a partial or unverified result complete.


## Authoring evidence to reconcile before acceptance

The slice-7 command-guide review confirmed that the Unity companion exports the
configured prefab, not `Example.unity`; the template introduction now says so.
The original `WorldValidator.CheckSpawnPoint` accepted the first matching marker,
whereas runtime startup rejects ambiguity. Require exactly one ordinal match,
including prefab root and inactive descendants, before any export/configuration
writes. Preserve the prefab's authored root name during owned instantiation; Unity's
added `(Clone)` suffix must not invalidate an authored root marker. Add failing
regressions and align editor/runtime behavior before
claiming the generated-world authoring journey is complete. Also verify
`Assets/World/README.md` environment and kill-plane claims against the active bundle
provider, and remove obsolete registration-code instructions. Keep the pinned
6000.0.23f1 authoring editor distinct from the installed game's runtime version.


## Review obligations carried from the superseded retirement PR

The 8 September review of [PR #105](https://github.com/Furroxide/TopiaForge/pull/105)
confirmed that two comments require this slice's atomic retirement change:

- [V4 retirement guidance](https://github.com/Furroxide/TopiaForge/pull/105#discussion_r3929384378):
  update `ManifestValidator` and the other dispatch/validation messages to V6 only
  when V3/V4/V5 migration actually produces V6. At the merged slice-7a baseline, V4-to-V5 guidance matched
  the temporary implementation. This slice changes the actual command and all
  retirement messages together.
- [Standalone V5 test naming](https://github.com/Furroxide/TopiaForge/pull/105#discussion_r3929384409):
  replace the still-valid V5 acceptance assertion with retirement coverage, and
  name new V6 tests accurately. At the slice-7a baseline `AllowsStandaloneV5` really
  parsed V5; the historical PR's misleading V6 body was not integrated.

The same review's suppressed `v6-valid-session.json` finding must be checked during
fixture conversion: every fixture's version and explicit schema URL must agree.
That historical V6 fixture never landed; the slice-7a V5 session fixture correctly
used the V5 schema. Do not weaken these checks or copy the mismatched historical
fixture while retiring V5. Resolve the two linked threads only after the new
implementation and migration tests land. Preserve the prepared 8b source and its
review findings; create its branch from current dev only after this PR merges.
