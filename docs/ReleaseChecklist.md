# Initial release checklist

Use this checklist from a clean, frozen release candidate. A checked box requires a command log, artifact, or reviewed
QA record from the exact candidate SHA. Warnings, failures, and unavailable checks need an explicit disposition; they
are never silently waived. Candidate-specific open items are in [`LaunchBlockers.md`](LaunchBlockers.md).

## 1. Scope, policy, and ownership

- [x] Product version is `1.0.0-rc.1`; components/mods version independently; initial release has no rollback target.
- [x] RC discovery is GitHub Releases only; stable Pages/manual and official registry feeds exclude prereleases.
- [x] The stale `release/0.1.1` line is retired and is neither reused nor deleted during RC preparation.
- [x] Robotopia support is build `2227` only (`0.0.2227`); public-latest drift is release-fatal.
- [x] Unity is exactly `6000.0.23f1`; no fallback editor is accepted.
- [x] Launcher upgrades are manual; custom worlds are Windows/Proton-only; official community submissions are closed.
- [x] Remote AI, player-token, microphone, and STT features default off and declare descriptive capabilities.
- [ ] Owner/legal approves every decision in [`ReleaseLicenseInventory.md`](ReleaseLicenseInventory.md); replace
      `OWNER_DECISION_REQUIRED`, `NOASSERTION`, no-grant placeholders, and conflicting SDK package declarations.
- [ ] Owner/legal approves Robotopia/brand/art/font/compatibility/injection rights and all third-party dispositions.
- [ ] Privacy/backend/security owners approve remote data flows, retention, consent, cost, abuse, and incident policy.
- [ ] Security/product owners approve first-party package trust, origin, revocation, and installed-user recovery.
- [ ] The owners named in [`ReleaseOperations.md`](ReleaseOperations.md) confirm that security, support, release,
      incident, revocation, and rollback channels are monitored or delegated.

## 2. Candidate and toolchains

- [ ] Review and commit the remediation without discarding unrelated user work; freeze one candidate SHA.
- [ ] Create a protected, annotated `v1.0.0-rc.1` tag on that SHA through the approved administrator process.
- [ ] Confirm `global.json` resolves exactly .NET SDK `10.0.301` with roll-forward disabled and runtime `10.0.9`.
- [ ] Confirm Dart `3.12.2`, Flutter `3.44.6`, Node `24.16.0`, and Unity `6000.0.23f1` on every applicable runner.
- [ ] Probe the public latest-build manifest and verify both pinned build-2227 archive paths and SHA-256 values.
- [ ] Confirm all LFS objects, immutable BepInEx inputs, UnityDoorstop source, and managed references are present.

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
- [ ] Runtime repair rejects links/special files, preserves modes, stages atomically, and restores on failure.
- [ ] UGC inspection uses strict bounded UTF-8/JSON/gzip, typed issues, stable regular files, deterministic selection,
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

- [ ] Sidecar `npm ci`, every-module syntax check, 23 tests, production dependency tree, and `npm audit` pass.
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
      build-2227 Unity/Mono profile supplies the verified Memory/Buffers/Unsafe dependency closure, and every DLL in
      the Windows Robotopia-executed BepInEx overlay hashes identically to its canonical payload copy.
- [ ] All seven C# templates scaffold, validate, build/package twice, and retain safe non-publishable defaults.
- [ ] Explicit `--author`/`--license` scaffolding is tested; MIT/Apache text is generated only after selection and
      other expressions require safe repeatable `--license-file` inputs.
- [ ] Three VPM packages/listings build twice and pass direct manifest, license, notice, target, and hash inspection.
- [ ] One canonical deterministic `ecosystem-dist` contains exactly 15 released mods plus three VPM packages;
      UiGallery remains validated but absent, Creator Tools is explicitly packed, and every platform consumes
      identical nested bytes.
- [ ] Strict registry schema/semantic/dependency/license/package/all-version validation passes.
- [ ] Merge-base/index comparison proves append-only history: no deletion, rename, reorder, mutation, downgrade, or
      conflicting duplicate; only unique strictly newer versions are prepended.
- [ ] Official Pages index contains first-party entries only; self-hosted registry workflows remain documented.

## 7. Exact Unity validation

- [ ] Invoke only `/Applications/Unity/Hub/Editor/6000.0.23f1/Unity.app/Contents/MacOS/Unity` locally.
- [ ] Rebuild TopiaForgeUi twice; bytes, embedded manifest, editor provenance, hashes, neutral font names, assets, and docs agree.
- [ ] Rebuild a representative world twice; target, world manifest, companion tooling, VPM inputs, and bytes agree.
- [ ] Run repeated TopiaForgeUi create/dispose and scene-transition lifecycle smoke; all allocator/subscription state returns
      to baseline.
- [ ] UiGallery covers loading, empty, information, warning, error, success, disabled, focus, long/scroll content,
      destructive modal, toast, scale, contrast, and reduced-motion states.
- [ ] Authorized Robotopia/profiler QA validates all 16 source-mod flows, every declared GameCompat binding, lifecycle isolation, save behavior,
      TopiaForgeUi usage, accessibility propagation, and zero steady-state allocation regressions.
- [ ] The full `game-sdk-acceptance.yml` matrix passes from the frozen SHA on both Windows and Linux/Proton with
      all canonical markers, main-thread assertions, ten resource cycles, exact package hashes, and uploaded evidence.
- [ ] An independent clean-machine author with only Robotopia, the release archive, and its pinned .NET SDK creates
      and launches a working safe code mod in at most five commands, without a source checkout or Unity installation.

## 8. Platform release archives

- [ ] Build Windows x64, Linux x64, and macOS universal independently with `fail-fast: false` from the frozen SHA.
- [ ] Directly inspect final extracted archives for missing/extra/duplicate/linked entries, case collisions, modes,
      executability, hashes, notices, runtime assets, nested payload equality, secrets, and update metadata.
- [ ] Windows executables have approved Authenticode SHA-256 signatures and HTTPS RFC 3161 timestamps and pass
      `signtool verify /pa /all /tw` after final extraction.
- [ ] macOS launcher, GameCompat, and frameworks are universal; Dart AOT ships separate runnable arm64/x64 executables
      behind the dispatcher (never `lipo` Dart AOT executables).
- [ ] The explicitly non-distributable macOS technical dry run passes direct launch and embedded-CLI smoke tests. Its
      ad-hoc signature omits hardened runtime because ad-hoc code has no common Team ID; publication mode rejects it.
- [ ] Every macOS Mach-O is Developer ID/expected-Team signed; the app is notarized, stapled, quarantined, and passes
      deep/strict codesign, `stapler validate`, and Gatekeeper after final extraction on arm64 and Intel.
- [ ] Linux executable modes, native launcher/CLI, and discovery/path/process/repair/custom-world assumptions for
      Robotopia's Windows build under Proton pass on a clean host.
- [ ] Clean-machine install, repair, profiles, dependency preview, normal/safe-mode launch, failure recovery,
      diagnostics, manual update, and uninstall pass for each supported platform.
- [ ] Native visual/accessibility QA covers all screens and state families at 800x600, 100–200% text scale, high
      contrast, reduced motion, keyboard-only/focus, screen reader, long paths, and no-overflow behavior.

## 9. Release metadata and protected publication

- [ ] Generate deterministic `release-bom.json`, SPDX 2.3 SBOM, and `SHA256SUMS` for the candidate SHA and exact assets.
- [ ] BOM/SBOM include versions, toolchains, build/Unity/BepInEx provenance, licenses/notices, nested hashes, sizes, and
      expected assets; independent verification passes.
- [ ] `manual-releases.json` format 2 is `manualOnly: true` and contains only absolute credential-free HTTPS release
      and artifact URLs, SHA-256, size, and platform.
- [ ] Required contexts protect the candidate: `Required / CI validation`, `Required / Release packages`,
      `Required / Registry validation`, and trusted-candidate `Required / Unity validation`.
- [ ] Fork PR validation is secretless. Release, signing, Unity-license, attestation, and Pages privileges exist only in
      protected trusted workflows/environments with required reviewers.
- [ ] Pages builds in an unprivileged temporary tree; the no-checkout deploy job has only Pages/OIDC write authority.
- [ ] Publication validates everything before creating/resuming an exact matching draft; same-name/different-digest
      assets fail; exact reruns are no-ops; no clobber/tag creation/tag mutation/automatic publication exists.
- [ ] GitHub reports every uploaded size/digest/state complete; an authorized owner reviews notes, BOM/SBOM, checksums,
      signatures, notarization, native and Robotopia-runtime QA, and all blocker dispositions before manually publishing.

## 10. Final decision

- [ ] Rerun [`LaunchBlockers.md`](LaunchBlockers.md) against the frozen SHA: every P0 closed, every P1 closed or given a
      dated owner disposition, zero critical/high defects, zero unexplained warnings/failures/flakes, and zero skips.
- [ ] Record an explicit **SHIP** decision by the project owner and release manager. Until then, the decision is
      **NO-SHIP**.
