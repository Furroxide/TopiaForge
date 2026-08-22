# Initial release checklist

Use this checklist from a clean, frozen release candidate. A checked box requires a command log, artifact, or reviewed
QA record from the exact candidate SHA. Warnings, failures, and unavailable checks need an explicit disposition; they
are never silently waived. Candidate-specific open items are in [`LaunchBlockers.md`](LaunchBlockers.md).
Machine setup and the resumable command sequence are in
[`AdminRelease.md`](AdminRelease.md).

## 1. Scope, policy, and ownership

- [x] Product version is `0.1.0-rc.1`; components/mods version independently; initial release has no rollback target.
- [ ] `release/release-readiness.json` is committed on the frozen candidate,
      matches that exact version and SHA, approves every P0 gate, and either
      approves or records an allowed manually reviewed accepted-risk decision
      for every P1 gate. Its `evidenceIds` are attestation references, not
      machine-resolved evidence: the protected release approver must verify
      their existence and reviewer authorization. The catalog remains
      `blocked` before that decision.
- [x] RC discovery is GitHub Releases only; stable Pages/manual and official registry feeds exclude prereleases.
- [x] The stale `release/0.1.1` line is retired and is neither reused nor deleted during RC preparation.
- [x] Robotopia support is build `2409` (`0.0.2409`); public-latest drift is release-fatal.
- [x] Unity is exactly `6000.0.23f1`; no fallback editor is accepted.
- [x] Launcher updates use signed GitHub prerelease metadata, explicit
      confirmation, whole-package replacement, health-gated rollback, and a
      verified manual fallback; custom worlds remain Windows/Proton-only.
- [x] Remote AI, player-token, microphone, and STT features default off and declare descriptive capabilities.
- [x] TopiaForge-owned release surfaces use AGPL-3.0-or-later with
      `Copyright (C) 2026 furroxide`; third-party terms remain unchanged and
      the redistribution inventory in
      [`ReleaseLicenseInventory.md`](ReleaseLicenseInventory.md) is reconciled.
- [x] DCO 1.1 is checked in and post-cutover commits require valid
      `Signed-off-by` trailers; pre-`v0.1.0-rc.1` history is grandfathered.
- [ ] Owner/legal approves Robotopia/brand/art/font/compatibility/injection rights and all third-party dispositions.
- [ ] Privacy/backend/security owners approve remote data flows, retention, consent, cost, abuse, and incident policy.
- [ ] Security/product owners approve first-party package trust, origin, revocation, and installed-user recovery.
- [ ] The owners named in [`ReleaseOperations.md`](ReleaseOperations.md) confirm that security, support, release,
      incident, revocation, and rollback channels are monitored or delegated.

## 2. Candidate and toolchains

- [ ] Review and commit the remediation without discarding unrelated user work; freeze one candidate SHA.
- [ ] Create a protected, annotated `v0.1.0-rc.1` tag on that SHA through the approved administrator process.
- [ ] Confirm `global.json` resolves exactly .NET SDK `10.0.301` with roll-forward disabled and runtime `10.0.9`.
- [ ] Confirm Dart `3.12.2`, Flutter `3.44.6`, and Node `24.18.0` in each
      applicable production environment, plus Unity `6000.0.23f1` on the
      Windows production builder.
- [ ] Probe the public latest-build manifest and verify both pinned build-2409 archive paths and SHA-256 values.
- [ ] Confirm all LFS objects, immutable BepInEx inputs, UnityDoorstop source, and managed references are present.
- [ ] Git LFS is installed on Windows and in Ubuntu; `git lfs fsck` succeeds
      and every tracked LFS path is materialized in both the admin checkout and
      the exact-SHA WSL clone.

## 3. Source, contracts, and tests

- [ ] `dotnet build TopiaForge.slnx -c Release` passes with zero warnings/errors.
- [ ] All 12 public SDK projects `dotnet pack` with warnings as errors; every NuGet archive is valid and its
      dependencies, readme, analyzer/generator assets, and `buildTransitive` props/targets match the package contract.
- [ ] `dotnet run --project tests/TopiaForge.ModManager.Tests/TopiaForge.ModManager.Tests.csproj -c Release` passes.
- [ ] `TopiaForge.ModRuntime.Tests`, `TopiaForge.ModPackageValidator.Tests`, and `TopiaForge.ManagedRefs.Tests`
      execute and pass; compiling their executable projects is not sufficient.
- [ ] `TopiaForge.Mods.Analyzers.Tests`, `TopiaForge.Mods.Multiplayer.Generators.Tests`, and
      `TopiaForge.Mods.Multiplayer.Tests` execute and pass; compiling their executable projects is not sufficient.
- [ ] Multiplayer generator goldens and compile-failure cases cover bounded codecs, stable IDs/wire revision,
      registration, unsupported payloads, and predicted-handler side-effect/nondeterminism rejection.
- [ ] The deterministic server-plus-two-client matrix passes accepted/rejected prediction, rollback/replay,
      cross-state transactions, ownership/transfer, stale input, late join/reconnect-before-Ready, disconnect
      cancellation, and latency/loss/duplication/reordering without listen-host double execution.
- [ ] Required/optional/client-local/server-only admission and exact-profile tests produce structured mismatch
      reports; packed session mods always synchronize and hash the generated contract lock.
- [ ] The real-game acceptance mod binds a generated contract through the standalone loopback provider; dedicated
      test hosts expose no local-player/presentation access. Live transport and Robotopia hosting remain explicitly
      unsupported pending [`MultiplayerHostingFeasibility.md`](MultiplayerHostingFeasibility.md).
- [ ] The Counter and Drone multiplayer dogfood samples compile against only the stable package surface and exercise
      generated state/commands plus owner-predicted replicated objects.
- [ ] Strict C# audit, generated public-API baseline, and production bounded-read scan pass.
- [ ] Exact-SDK `dart pub get --enforce-lockfile` / `flutter pub get --enforce-lockfile` succeeds for every tracked
      lockfile without a diff; `dart format --output=none --set-exit-if-changed` passes for every Dart package/app.
- [ ] Domain/data/CLI `dart analyze` and `dart test` pass.
- [ ] UI/app `flutter analyze` and `flutter test` pass.
- [ ] The documentation aggregate builds Starlight, DocFX C# API, dartdoc API, unified search, and all source/built
      links without errors, warnings, or missing multiplayer reference pages.
- [ ] C#/Dart SemVer, manifest, compatibility, unknown-field, dependency, conflict, pin, order, and state parity passes.
- [ ] Every non-generated Dart file is at most 500 lines; launcher state remains BLoC-only.
- [ ] Core remains Unity-free; domain remains Flutter/filesystem/process/network/archive-free; consumer UI uses TopiaForgeUi.
- [ ] No unresolved conflict marker, unsafe fallback, swallowed failure, duplicate unsafe archive/process path, or
      unexplained analyzer/build warning remains.

## 4. Security and untrusted inputs

- [ ] Archive tests cover traversal, links/special files, Unicode/case folding, Windows device/ADS names, malformed
      ZIP/ZIP64, decompression bounds, deterministic output, atomic replacement, interruption, and rollback.
- [ ] Signed-update tests cover exact-byte Ed25519 verification, wrong keys,
      tampering, GitHub reconciliation, downgrade/replay/channel policy,
      bounded HTTP, all adversarial archive cases, and interruption before and
      after every journal transition on all three install layouts.
- [ ] Runtime repair rejects links/special files, preserves modes, stages atomically, and restores on failure.
- [ ] World inspection uses strict bounded UTF-8/JSON/gzip, typed issues, stable regular files, deterministic selection,
      structural validation, race detection, and surfaced errors.
- [ ] Diagnostics enforce 4 MiB/log and 16 MiB total caps, streaming tails, link rejection, secret redaction,
      truncation metadata, hashes, and atomic ZIP replacement.
- [ ] Sidecar setup uses the checked-in lockfile, `npm ci --ignore-scripts --no-fund --no-audit`, trusted regular
      paths, no shell, bounded/coalesced output, timeouts, and restrictive session access rules.
- [ ] Download/registry/update paths require bounded reads, timeouts, HTTPS, no URL credentials/query/fragment,
      digest validation, partial-download recovery, atomic writes, and rollback.
- [ ] Install/update confirmation shows source, SHA-256, aggregate dependency capabilities, and arbitrary-code risk.
- [ ] Credentials exposed to any inherited Xcode scheme-pre-action log are revoked/rotated; affected logs are removed
      under the approved retention policy; a sanitized-launch sentinel build contains no credential values.

## 5. Sidecar and repository hygiene

- [ ] Sidecar `npm ci`, every-module syntax check, 24 tests, production dependency tree, and `npm audit` pass.
- [ ] actionlint passes every workflow; all external actions are pinned to full commit SHA values.
- [ ] PSScriptAnalyzer `1.25.0`, PowerShell parser, bash syntax, and shellcheck pass for repository-owned scripts.
- [ ] JSON, YAML, JSON Schema, Markdown-link, Git LFS, binary-attribute, conflict-marker, and LF audits pass.
- [ ] Immutable vendored BepInEx files match provenance even where upstream line endings or lint exceptions differ.

## 6. Mods, templates, registry, and ecosystem payload

- [ ] All 16 first-party source manifests, projects, and current-version changelogs align; all 15 release-payload
      packages validate; normal `pack --all` emits 14 non-DevTool packages and release automation adds only Creator
      Tools; every payload mod is packed twice byte-identically and the resulting archive is inspected
      for manifest, assembly, license paths, links, names, and collisions.
- [ ] The packaged metadata validator rejects bad PE/type/constructor/SDK/TFM fixtures without loading mod code, and
      every first-party archive is independently scanned for loader-owned SDK DLL/PDB files.
- [ ] The canonical 14-assembly loader payload contains the exact pinned Metadata/Immutable bytes and notices; the
      build-2409 Unity/Mono profile supplies the verified Memory/Buffers/Unsafe dependency closure, and every DLL in
      the Windows Robotopia-executed BepInEx overlay hashes identically to its canonical payload copy.
- [ ] All seven C# templates scaffold, validate, build/package twice, and retain safe non-publishable defaults.
- [ ] Explicit `--author`/`--license` scaffolding is tested; AGPL text is generated by default; MIT/Apache text after explicit selection and
      other expressions require safe repeatable `--license-file` inputs.
- [ ] Three VPM packages/listings build twice and pass direct manifest, license, notice, target, and hash inspection.
- [ ] One canonical deterministic `ecosystem-dist` contains exactly 15 released mods plus three VPM packages;
      UiGallery remains validated but absent, and every platform consumes
      identical nested bytes.
- [ ] Strict registry schema/semantic/dependency/license/package/all-version validation passes.
- [ ] Merge-base/index comparison proves append-only history: no deletion, rename, reorder, mutation, downgrade, or
      conflicting duplicate; only unique strictly newer versions are prepended.
- [ ] Official Pages index contains first-party entries only; self-hosted registry workflows remain documented.

## 7. Exact Unity validation

- [ ] Invoke only `C:\Program Files\Unity\Hub\Editor\6000.0.23f1\Editor\Unity.exe` on the administrator-controlled
      Windows workstation.
- [ ] Rebuild TopiaForgeUi twice; bytes, embedded manifest, editor provenance, hashes, neutral font names, assets, and docs agree.
- [ ] Rebuild a representative world twice; target, world manifest, companion tooling, VPM inputs, and bytes agree.
- [ ] Run repeated TopiaForgeUi create/dispose and scene-transition lifecycle smoke; all allocator/subscription state returns
      to baseline.
- [ ] UiGallery covers loading, empty, information, warning, error, success, disabled, focus, long/scroll content,
      destructive modal, toast, scale, contrast, and reduced-motion states.
- [ ] Authorized Robotopia/profiler QA validates all 16 source-mod flows, every declared GameCompat binding, lifecycle isolation, save behavior,
      TopiaForgeUi usage, accessibility propagation, and zero steady-state allocation regressions.
- [ ] Local Windows acceptance passes from the frozen SHA with all canonical markers, main-thread assertions, ten
      resource cycles, exact package hashes, and a scrubbed validation summary.
- [ ] The separate local Windows Creator-workbench descriptor covers every required interactive build-2409 case,
      records at least ten lifecycle cycles and unchanged save/checkpoint state, and matches its retained evidence
      bundle and exact Windows archive (launcher, CLI, and GameCompat extractor
      Authenticode-signed and RFC 3161 timestamped by the pinned certificate).
- [ ] *(Out of RC1; `0.1.0-rc.2`.)* The Proton evidence bundle matches the exact Linux archive digest and covers real
      discovery, path/process, repair, custom-world, runtime, and uninstall behavior with Proton `10.0-4`.
      Metadata records that this RC1 evidence is non-independent; build output without the actual game run is not
      accepted.
- [ ] An independent clean-machine author with only Robotopia, the release archive, and its pinned .NET SDK creates
      and launches a working safe code mod in at most five commands, without a source checkout or Unity installation.

## 8. Platform release archives

- [ ] On a clean checkout of the frozen SHA, build Windows x64 on the administrator workstation. *(Linux x64 in
      Ubuntu 24.04 under WSL2 is out of RC1 and returns in `0.1.0-rc.2`.)*
- [ ] Build the canonical ecosystem twice byte-identically before distribution and prove every platform archive
      contains that exact ecosystem digest.
- [ ] Directly inspect final extracted archives for missing/extra/duplicate/linked entries, case collisions, modes,
      executability, hashes, notices, runtime assets, nested payload equality, secrets, and update metadata.
- [ ] RC1's Windows CLI, GameCompat extractor, and launcher all have valid
      Authenticode signatures from the exact reviewed leaf-certificate
      SHA-256 pin and valid HTTPS RFC 3161 timestamps. Unsigned, partly signed,
      untimestamped, expired-at-signing, mismatched, or invalid output fails.
- [ ] *(Out of RC1; `0.1.0-rc.2`.)* Linux executable modes, native launcher/CLI, and
      discovery/path/process/repair/custom-world assumptions for Robotopia's Windows build under Proton pass on a
      clean host that can reach a GPU Vulkan implementation.
- [ ] Clean-machine install, repair, profiles, dependency preview, normal/safe-mode launch, failure recovery,
      diagnostics, confirmed in-app update, forced rollback, manual fallback,
      and uninstall pass for each supported platform.
- [ ] Native visual/accessibility QA covers all screens and state families at 800x600, 100–200% text scale, high
      contrast, reduced motion, keyboard-only/focus, screen reader, long paths, and no-overflow behavior.

## 9. Release metadata and protected publication

- [ ] Each platform emits a deterministic `release-platform-bundle-v1` manifest, and the administrator stages one
      `release-handoff-v1` manifest binding version, source SHA, platform asset digests/sizes, canonical ecosystem
      digest, pinned toolchains, signing state, validation results, and scrubbed QA evidence digests.
- [ ] The administrator stages `release-handoff-v1.json.p7s`, a detached CMS
      signature over the exact handoff bytes from the same pinned Windows
      code-signing certificate. It contains exactly one cryptographically
      verified RFC 3161 timestamp; the signer and TSA chains, EKUs, and
      validity at the timestamp instant all verify. Hosted verification checks
      and digest-binds those exact P7S bytes before trusting any platform
      archive.
- [ ] A credential-free `VerifyOnly` rerun succeeds after the local PFX,
      password, and timestamp URL are removed, while a new signing operation
      still rejects missing credentials.
- [ ] Public deterministic metadata contains no usernames, hostnames, local
      paths, machine/run-specific timestamps, credentials, or raw game logs.
      Standards-required fixed epoch values remain allowed when deterministic.
- [ ] Generate deterministic `release-bom.json`, SPDX 2.3 SBOM, and `SHA256SUMS` for the candidate SHA and exact assets.
- [ ] Generate `topiaforge-update-v1.json` and its Ed25519 sidecar from the
      exact uploaded archive bytes; independently verify signature, key ID,
      immutable URLs, hashes, sizes, entry counts, expanded sizes, and layouts.
- [ ] BOM/SBOM include versions, toolchains, build/Unity/BepInEx provenance, licenses/notices, nested hashes, sizes, and
      actual platform signing status, exception use, and expected assets;
      independent verification passes.
- [ ] `manual-releases.json` format 2 is `manualOnly: true` and contains only absolute credential-free HTTPS release
      and artifact URLs, SHA-256, size, and platform.
- [ ] Required contexts protect the candidate: `Required / CI validation`,
      `Required / PR policy`, `Required / Dependency review`,
      `Required / Release packages`, `Required / Registry validation`, and
      `Required / Unity source validation`; local production evidence is
      verified by the protected finalizer instead of a credentialed hosted
      Unity/game check.
- [ ] Required hosted checks resolve to the exact checked-in workflow IDs/paths,
      expected event and `release/<version>` head ref/SHA, and successful current
      run attempt; same-name checks with different provenance are rejected.
- [ ] Fork PR validation is secretless. The GitHub-held update-signing key, attestation, publication, and Pages
      privileges exist only in protected workflows/environments with required reviewers; platform-signing, Unity,
      and Robotopia credentials remain only on administrator-controlled machines.
- [ ] The protected `release` environment contains a dedicated read-only
      `TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN`, scoped only to this repository and
      `Administration: read` plus `Actions: read`; its implicit Metadata read
      covers the remaining audited endpoints. It has no write permission of
      any kind.
- [ ] The protected update-signing seed has been independently recovery-tested,
      and every plaintext local duplicate has been removed without exposing or
      rotating the verified GitHub-held value.
- [ ] Pages builds in an unprivileged temporary tree; the no-checkout deploy job has only Pages/OIDC write authority.
- [ ] The administrator flow validates everything before creating/resuming an exact matching draft; a
      same-name/different-digest asset fails, and the workflow never creates or mutates a tag.
- [ ] Protected release-environment approval is the final human checkpoint. GitHub verifies rather than builds,
      generates the signed update metadata and custom verifier attestation, rechecks every asset, and publishes
      automatically. Exact reruns verify without mutation; published-byte or metadata mismatches fail closed.
- [ ] `release-admin dispatch` has persisted its unique request ID and exact
      tag/SHA-matched GitHub run ID in phase `dispatch-requested`; `resume`
      verifies its repository-scoped REST record names the exact
      `.github/workflows/release.yml` path, then watches that run or reruns that
      same failed/cancelled run ID. Phase
      `published` is recorded only after run success and exact immutable
      prerelease verification. No replacement workflow dispatch was started
      manually.
- [ ] Every workflow-dispatch attempt was journaled before the remote call. If
      an attempted request had no stored run ID, `resume` exhausted the bounded
      registration grace first, bound any matching run without duplication,
      or failed closed for a later resume. It never automatically redispatched
      a journaled request.
- [ ] A stranded-finalizer rehearsal accepts only policy-declared generated
      metadata from the pinned Actions bot/App, recomputes exact bytes, repairs
      only an Actions-owned generated starter, and publishes without replacing
      any complete or admin-staged asset.
- [ ] Admin preflight and the protected finalizer both prove immutable releases,
      exact `furroxide` release review with admin bypass disabled and only `v*`
      tags allowed, and active release/version-tag lifecycle rules.
- [ ] Complete a non-publishing two-platform rehearsal before deleting the obsolete live `unity-validation` and
      `game-acceptance` environments.

## 10. Final decision

- [ ] Re-run `release validate-readiness` against the frozen target SHA
      immediately before tag creation and again after protected-environment
      approval; the exact committed decision and BOM binding remain ready.
- [ ] Rerun [`LaunchBlockers.md`](LaunchBlockers.md) against the frozen SHA: every P0 closed, every P1 closed or given a
      dated owner disposition, zero critical/high defects, zero unexplained warnings/failures/flakes, and zero skips.
- [ ] Record an explicit **SHIP** decision by the project owner and release manager. Until then, the decision is
      **NO-SHIP**.
