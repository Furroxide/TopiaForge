# Slice 2: Unused V6 contract and conformance

Begin only after slice 1 merges into `dev`. Read the
[canonical brief](../../GamemodeContractRedesign.md),
[evidence ledger](../Status.md), and [common execution rules](README.md).
Implement the new contract as unused support: the canonical schema alias and
all first-party manifests/templates remain V5 throughout this slice.

## Build the oracle before adding validation

- Reuse the historical reader extraction where still necessary to respect the
  Dart 500-line cap. Inspect it against current `dev` instead of assuming its
  old line counts or surrounding code still match.
- Build a cross-language fixture harness that recursively enumerates the whole
  corpus, excluding only root `index.json` and root `fixture.schema.json`. Reject
  orphan cases, misplaced files, unknown channels, and missing
  expectations. Register its C# suite in the default no-argument test run.
- Validate manifest payloads against the V6 JSON schema using the repository's
  pinned Dart schema implementation. Record schema acceptance separately from
  reader/semantic acceptance; semantic invalidity need not be schema invalidity.
- Require common normalized results and error codes for equivalent C#/Dart
  operations. Normalize every contribution field, including display metadata and
  absent versus explicit values. Do not collapse values before comparison.

## Implement the contract repairs

- Add the V6 schema, both readers, and both validators. Use `contributions`;
  preserve the top-level retired `gamemodes` sentinel. Reject `options`,
  `optionValues`, and `sessionExtensions` through the closed numbered schema.
- Inspect raw JSON types and field presence before deserialization can coerce or
  default them. Reject scalar coercion, fractional integers, explicit nulls where
  absent is the only optional form, and prohibited fields even when empty.
- Apply identical ASCII ID/type grammars, Unicode character-count limits,
  collection bounds/uniqueness, and conditional-field rules. Do not use UTF-16
  code-unit length in one reader and Unicode scalar length in the other.
- Validate declaration ownership and local reference kinds. `world.default` must
  resolve to a permitted static world, and `openTo` to a gamemode. Reject dangling
  consent and duplicate IDs across any collections required to be unique.
- Keep package IDs capped at 64 and declaration IDs separately capped at 96.
  Widen C# `WorldLaunchIntent` and Dart `WorldSelection` acceptance with the
  declaration grammar in this same slice; trace existing profile/catalog paths
  so a valid 65-96-character declaration is not rejected downstream.

## Acceptance

- Add failing regression cases before fixes for Unicode identifiers, raw-type
  coercion, fractional integers, explicit null, empty forbidden fields, duplicate
  collections, wrong-kind references, dangling consent, and Unicode text bounds.
- Cover 64/65/96/97-character boundaries in readers and existing selection/intent
  serialization. Package and declaration limits remain distinct.
- Prove harness closure by exercising its rejection of unexpected and unlisted
  fixture files, not only by running the known fixture names.
- The canonical alias still points at V5; no production manifest/template declares
  `contributions`. V5 behavior and existing first-party tests remain green.
- Run Release plus the rebuilt C# release-surface harness, launcher_domain tests,
  formatting and fatal-info analysis, line-count audit, relevant repository
  audits, and any additional package tests touched by ID propagation.
- Record exact fixture results and remaining semantic/resolver work in the ledger.
  Submit only this slice; branch slice 3 after its merge into `dev`.
