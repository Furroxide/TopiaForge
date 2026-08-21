# Initial release blocker register

Last audited: 2026-07-31. Product candidate: `1.0.0-rc.1`. Recommendation: **NO-SHIP**.

Scope change on 2026-08-12: **Linux is out of `1.0.0-rc.1`** and returns in `1.0.0-rc.2`.
The administrator host cannot reach a GPU Vulkan implementation inside WSL2, and
Robotopia's Direct3D 12 renderer requires it through VKD3D, so no credible Proton
acceptance evidence was obtainable. RC1 ships Windows x64 only. See `P0-LINUX-01`.

A first-party mod audit on 2026-07-27 found and fixed one critical and two high-severity engineering defects that
the prior remediation had missed (see [First-party mod audit](#first-party-mod-audit-2026-07-27) below). No further
local critical- or high-severity engineering *product* defect is known as of that audit. The release remains blocked by
decisions, credentials, protected-host configuration, and native Robotopia-runtime acceptance that cannot be supplied
by source changes. The strict publication gates intentionally continue to reject the candidate until those items are
closed.

One blocker was an exception to that framing because it was supplied by a source change: the native CreatorTools
evidence collector. That collector, its challenge-bound acceptance runner, the `release-windows-creator-evidence-v2`
descriptor, and the three real verifiers now exist, so `release-admin.ps1` no longer refuses to build. It is tracked as
`P0-CREATOR-01` below and remains open only for the same reason as its neighbours: it now waits on an external input,
an authorized interactive Robotopia build-2309 session, whose evidence must come from the frozen candidate SHA. Every
release gate is therefore implemented and waits only on an external input.

This register records a pre-freeze working-tree preflight on the date above. It does not attest a future commit or a
release candidate SHA. Close an item only with evidence from the frozen candidate SHA; do not treat an unavailable
host, credential, or human review as a waived check. The repeatable procedure is in
[`ReleaseChecklist.md`](ReleaseChecklist.md), and the full component/contract map is in
[`ArchitectureInventory.md`](ArchitectureInventory.md).

Priority meanings:

- **P0** — required before any public release.
- **P1** — required before general availability unless the owner records an explicit, dated, scope-limited
  disposition.
- **P2** — a conditional future gate; it is not a v1 blocker while the stated conservative constraint remains true.

## Verification matrix

`PASS` means the locally applicable gate passed on this working tree. `FAIL` is an expected hard-stop that correctly
rejected a non-distributable candidate. `NEEDS RERUN` means the implementation gate is present, but its retained
artifact evidence predates the V1 reset and must be regenerated from the frozen candidate. `BLOCKED` requires
authority, credentials, hardware, hosted configuration, or manual evidence unavailable to this audit. No required
check is silently skipped.

| Gate family | Result | Retained evidence |
| --- | --- | --- |
| Whole-repository component and contract inventory | PASS | All source, app, package, mod, template, tool, schema, test, documentation, and workflow surfaces are mapped in `ArchitectureInventory.md`. |
| C# Release solution | PASS | The current 47-project solution builds on SDK `10.0.301` / runtime `10.0.9` with zero warnings and errors. Exact-SHA hosted CI and administrator-built release evidence must still be regenerated after the candidate freezes. |
| C# regression harness | PASS | The complete ModManager, ModRuntime, analyzer, multiplayer-generator, and multiplayer test executables pass, including the current RobotKit/Creator and public-API baselines. |
| C# boundaries and public SDK surface | PASS | Unity-free Core, Unity/BepInEx runtime isolation, strict audit, generated API baselines, bounded production-read scans, and the current SDK package surface pass with Creator Content included. |
| Dart formatting and analyzers | PASS | All tracked Dart sources were formatted; domain, data, UI, app, and CLI analyzers report no issues and every non-generated file is at most 500 lines. |
| Dart domain/data tests | PASS | 203 domain and 362 data tests passed (four environment-specific data cases skipped), including Manifest V5 dispatch, multiplayer admission, signed launcher updates, deterministic package-inbox planning, runtime repair, and receipt provenance/repair behavior. |
| Flutter UI/app tests | PASS | 3 shared-UI and 66 launcher tests passed in isolated Windows test processes, including BLoC lifecycle, all signed-update states, scaling, contrast, focus, install/repair confirmation, safe mode, recovery, health handshake, and Xcode payload/logging configuration. |
| CLI tests | PASS | All 190 CLI tests passed with Dart `3.12.2` (four platform-capability cases skipped), including V4-to-V5 migration, packaging, registry, Unity probing, UGC, signed release metadata, final-archive and handoff validation, and the relocated seven-template lifecycle. |
| C#/Dart contract parity | PASS | Manifest V5, V4 retirement, SemVer 2.0, build mapping, multiplayer admission, canonical fields, unknown fields, dependencies, pins, conflicts, load order, and state fixtures agree. |
| Sidecar install/runtime/security | PASS | Lockfile `npm ci`, syntax checks, 24 tests (22 passed and two Windows signal-delivery cases skipped), production dependency tree, and audit passed with zero vulnerabilities. |
| Archive, UGC, diagnostic, repair, and process hardening | PASS | Adversarial traversal/link/collision/size/race/rollback/redaction/timeout regressions passed. Transaction recovery passes interruptions before and after every phase on all three layouts; the real Windows archive also passed a locally signed `rc.1` → synthetic `rc.2` swap and forced-health-failure rollback. |
| First-party mods | NEEDS RERUN | Repeat deterministic packing and managed-assembly validation for all 16 source mods, the 14-package normal non-DevTool output, and the 15-package release payload with Creator Tools added explicitly; UiGallery remains excluded. |
| C# author templates | PASS | All seven template families scaffolded from a release-like payload, restored, relocated, built, tested, packed, validated, installed with full receipt checks, and rebuilt after extraction removal; each real platform-archive job repeats that lifecycle. Defaults remain deliberately non-publishable. |
| VPM and canonical ecosystem payload | NEEDS RERUN | The retained ecosystem evidence predates Creator Content and Creator Tools. Rebuild and compare two independent three-VPM plus 15-mod release trees from the frozen candidate. |
| Exact-Unity TopiaForgeUi build | PASS | Unity `6000.0.23f1`; two builds matched SHA-256 `3cc6624f2a3a5fabc83c4fde49b32f859869e1d1e202afdaf91a888089f9fedb`. |
| Exact-Unity representative world build | PASS | Two current-tree builds matched SHA-256 `afa3e9195e8e03199b414f8a5c9002e9f89831041a63c7e1c9b8eef173d9057d`; manifests, editor provenance, and companion/VPM inputs matched. |
| Exact-Unity lifecycle smoke | NEEDS RERUN | A current-tree Unity `6000.0.23f1` run executed the managed validator and all 16 lifecycle cycles successfully with zero retained-resource delta. The administrator-controlled Windows release flow must regenerate and scrub that evidence from the frozen candidate. |
| Robotopia compatibility | PASS | Build `2309`; 219 bindings, 198 verifiable offline, 21 explicitly uncheckable offline, zero errors, warnings, or indeterminate findings; safe GravityGun, OppositeDay, Sandbox, and Zombies have no native binding declarations. |
| Public build freshness | PASS | A fresh 2026-07-31 public probe confirms both public platform records identify build `2309`; CI/release fail if the public latest manifest changes. |
| BepInEx/UnityDoorstop provenance | PASS | Pinned BepInEx `5.4.23.5` archives and extracted trees, UnityDoorstop commit/source, hashes, modes, and notices validate. |
| macOS release package | OUT OF RC1 | Generic packaging remains in source, but macOS is not in RC1 policy, catalog, update metadata, handoff, or public assets. It requires a separately reviewed future release. |
| Release-policy/BOM/SBOM/checksum machinery | PASS | Strict policy and metadata regressions cover AGPL-3.0-or-later, actual platform trust, signed update metadata/sidecar, checksums, BOM, SBOM, and immutable asset inventory. |
| Repository and CI hygiene | PASS | actionlint `1.7.7`, PowerShell/bash parsing, 164 JSON/YAML files, 118 Markdown files, 1,943 built HTML links, action pins, conflict markers, LF policy, and the 381-file non-generated Dart line cap passed. PSScriptAnalyzer `1.25.0` is rerun after every release-script edit. |
| Credential exposure containment | BLOCKED | The affected workspace DerivedData and launcher build logs were removed, and a scrubbed exact-toolchain sentinel build passed; 13 newly produced Xcode activity logs contained no credential-shaped variable names. Credential owners must still rotate the previously exposed values and confirm revocation. See `P0-CRED-01`. |
| Strict distributable-release policy | NEEDS RERUN | RC1 policy is scoped to Windows x64 only, forbids signing exceptions, and requires an exact nonzero Windows certificate SHA-256 pin plus an authenticated detached CMS handoff. |
| Windows x64 RC1 package and clean-host run | BLOCKED | Requires a reviewed code-signing certificate/PFX, RFC 3161 timestamp service, a frozen clean candidate, exact timestamped-signature verification, Unity/Robotopia evidence, and clean-machine QA; see `P0-WIN-01`. |
| Linux x64 package and Proton run | OUT OF RC1 | The WSL2 builder is fully provisioned and every pinned Linux toolchain verifies, but no GPU-backed Vulkan implementation is reachable there: NVIDIA ships no Vulkan ICD for WSL2 and Ubuntu does not package Mesa's Dozen driver, leaving only software lavapipe. Robotopia's Direct3D 12 renderer reaches Proton through VKD3D, which requires Vulkan, so the working OpenGL-over-d3d12 path cannot serve it. RC1 is therefore Windows-only; see `P0-LINUX-01`. |
| Native CreatorTools evidence collector | BLOCKED | Implemented, not yet attested. `CreatorAcceptanceRecorder` emits challenge-bound per-case markers from observed workbench transitions for all nine `creator.*` cases, `topiaforge acceptance creator` binds them to the exact `last-run.json` session and CreatorTools package receipt, and the three `Assert-WindowsCreator*` verifiers in `tools/release-admin.ps1` now perform real `release-windows-creator-evidence-v2` verification instead of throwing. Save and checkpoint bytes are compared across End Session from the real `player_data.json.gz` document. The gate stays BLOCKED because no evidence has been produced from an authorized interactive build-2309 session at the frozen candidate SHA; see `P0-CREATOR-01`. |
| Authorized Robotopia build-2309 acceptance | BLOCKED | A local Windows startup smoke passed on 2026-07-28: BepInEx loaded TopiaForge, detected `0.0.2309`, consumed all 16 staged packages, loaded every enabled first-party mod, initialized the native prompt/performance/UI bridges, and left Robotopia responsive. The complete dynamic-binding, reload, recovery, multiplayer, and profiler matrix still requires retained evidence from the frozen candidate; see `P0-GAME-01`. |
| Native UX/accessibility acceptance | BLOCKED | Screen Recording permission prevented screenshot comparison; screen-reader and native-platform manual QA remain; see `P1-UX-01`. |
| Project license and OSS redistribution inventory | FAIL | TopiaForge-owned surfaces use AGPL-3.0-or-later and DCO 1.1 governs post-cutover contributions, but the notice inventory was a fixed allowlist that never covered the Unity TextMesh Pro directory. EmojiOne shipped with no redistribution grant, Liberation Sans shipped with no notice, and Quicksand was sourced from the Robotopia web bundle. All fixed; see the re-opened `P0-OSS-01`. IP/brand authority remains tracked separately in `P0-IP-01`. |
| Privacy/backend authorization and package trust policy | BLOCKED | Remote features default off, but owner approval is still required; see `P0-PRIV-01` and `P0-TRUST-01`. |
| GitHub rulesets, environments, secrets, tag, and attestations | BLOCKED | Repository administration and credential owners must configure and prove the trusted path; see `P0-HOST-01`. |
| Frozen candidate admin matrix and reviewed release record | BLOCKED | This audit intentionally leaves uncommitted changes and creates no tag/release; see `P0-CAND-01`. |
| Independent player/author clean-machine acceptance | BLOCKED | Requires external participants and supported native hosts; see `P1-E2E-01`. |

Recompute matrix totals from the frozen release SHA after the remaining
administrator-orchestrated, live-game, native UX, and owner-evidence gates run.

Every gate below whose exit criteria are met by a reviewed record has a matching entry in
[`release/release-readiness.json`](../release/release-readiness.json), validated
against its schema at the exact candidate SHA. The readiness decision previously
carried only the four owner-decision P0 gates and the three P1 gates, which left
`P0-WIN-01`, `P0-GAME-01`, `P0-HOST-01`, and `P0-CAND-01`
release-fatal here but invisible to the machine decision. They are now recorded
gates, so the computed status cannot reach `ready` while any of them is
unresolved.

The decision carries **twelve** gates for RC1. `P0-LINUX-01` is deliberately absent
because Linux is out of this candidate; restoring it belongs to `1.0.0-rc.2`
alongside the policy archive entry and both schema gate contracts. `P0-OSS-01` is
present: it was re-opened on 2026-08-06 and the readiness contract must be able to
carry it rather than infer it from the legal inventory passing.

`P0-CREATOR-01` is deliberately not a readiness entry. The code it required now
exists, but it is still not closed by an attestation: it is closed by evidence
that only an authorized interactive build-2309 session can produce, and that
evidence is already enforced more strictly than a recorded decision could be.
The verifiers reject any descriptor whose challenge, `last-run.json` session,
CreatorTools package receipt, acceptance-result digest, case set, cycle count,
or save/checkpoint bytes do not match the exact candidate. Recording it as an
approvable gate would make it weaker, not stronger.

The remaining informational exceptions are explained, not waived:
`dotnet format` reports expected workspace-loader
diagnostics for the intentional Unity compile/reference split while finding no formatting changes; Flutter reports newer
packages outside current compatible constraints; Node lists optional non-host
native packages; and 21 GameCompat bindings are explicitly uncheckable offline
and therefore belong to the Robotopia acceptance gate.

## Build-2309 runtime adaptation (2026-07-28)

The strict build-2309 audit found no removed or changed declared binding. Live
startup did expose an independent loader defect: BepInEx 5 parses
`BepInPlugin.Version` as `System.Version`, so the semantic prerelease value `1.0.0-rc.1` caused it to skip the
TopiaForge plugin as invalid before `Awake`. The plugin now advertises the numeric core `1.0.0` to BepInEx while the
runtime, package manifests, and compatibility engine retain the full `1.0.0-rc.1` SemVer. `VersionUtilTests` locks the
two identities together, and the repaired live install loaded the plugin and consumed all 16 staged packages.

## First-party mod audit (2026-07-27)

An audit of all sixteen first-party mods found three engineering defects, all fixed in source. Each is listed with
the mechanical gate that now prevents its recurrence, because every one of them escaped for the same structural
reason: the affected sources reference UnityEngine and so were never compiled into the offline test assembly.

| Defect | Severity | Fix | Gate |
| --- | --- | --- | --- |
| Launching a custom world blocked the main thread on `ICustomWorldContent.CreateAsync`. Because SDK asset tasks complete from a main-thread `AssetBundleCreateRequest` callback, the wait stopped the update pump that would have completed it and hung Robotopia permanently, with the arena fallback unreachable. | Critical | `WorldsService` now arms the creation and drains it from `UpdateTransition`, with cancellation, the existing 30s transition timeout, arena fallback, and main-thread release of content that arrives after a cancel. | `ModConcurrencyConventionTests`, `PendingWorldContentLoadTests`, analyzer `TF1008` |
| `WorldsService.WriteCatalog` threw out of `WorldsMod.OnLoad` and blocked the main thread on a disk write. A read-only data directory, full disk, or file lock failed the Worlds provider outright, taking Zombies, Sandbox, UiGallery, and Creator Tools down with it — for a diagnostic file. | High | The catalog write is best-effort and asynchronous; failures are logged and never propagate out of `OnLoad`. | `ModConcurrencyConventionTests` |
| Gravity Gun's `ConfigDefinition` supplied no validator, so `Normalize()` ran only on the default factory. A stored document with `NaN`, negative, or inverted hold bounds reached `IEntityMotion.MoveToward` unclamped and corrupted the held rigidbody. | High | Config types now declare `ISelfNormalizingConfig` and the SDK normalizes on every validated path, so this cannot be omitted. | `ModConcurrencyConventionTests`, `FirstPartyConfigTests` |

Three lower-severity hardening changes landed in the same pass: Creator Content session cleanup is now fault-isolated
per handle so one throwing scene adapter cannot strand the reversible-session teardown behind it; RobotKit releases
the microphone on a defensive capture path that previously skipped it; and RobotKit drops its cached player token on
unload, which matters because Mono never unloads the assembly.

### Root causes addressed

Fixing the three defects individually would have left the conditions that produced them, so each was traced to a
cause and closed at that level.

- **The SDK offered no way to drive asynchronous work from the game loop.** Twelve files hand-rolled the same
  `IsCompleted` poll, and the one hand-roll that got it wrong hung the game. `PendingOperation<T>` is now a
  supported SDK primitive covering cancellation, timeout, restart-while-draining, and release of a result that
  arrives after the caller stopped wanting it. Worlds and the SDK acceptance mod use it; `TF1008` and the docs
  point at it. Existing hand-rolled drains remain correct and can adopt it incrementally.
- **Config normalization was opt-in by convention.** `ISelfNormalizingConfig` moves it into
  `ConfigDefinition<T>`, so a config type that declares it is normalized on defaults, load, migration, and save.
  Forgetting a hand-copied validator lambda is no longer possible.
- **First-party mods never ran the SDK's own analyzer.** All sixteen now import the analyzer package's real
  props/targets, so they build under exactly the MSBuild contract community authors get and the two populations
  cannot drift. Mods that are genuinely native declare `TopiaForgeSafeProject=false`; main-thread rules still
  apply to them, because opting out of the safe profile does not leave the game loop.

Dogfooding the analyzer immediately paid for itself, finding four further issues: a latent blocking wait in
`TopiaForge.SdkAcceptanceMod` (the reference example authors copy from); five mods copying reference-only SDK
assemblies into their build output (`TF1003`), inconsistent with the other eleven; a `TF1005` false positive that
rejected any mod declaring its own `LoadConfig`/`SaveConfig`/`GetService` member; and a `TF1008` scope bug that
rejected a correct drain split across a partial class or written as `task?.IsCompleted`. The two analyzer bugs
would have reached community authors in the shipped SDK package.

Manual acceptance is not closed by these fixes. The custom-world flow in
[`FirstPartyMods.md`](FirstPartyMods.md) — install and validate a bundle, then confirm a deliberately corrupt bundle
falls back to the generated arena — must still be recorded against the frozen candidate, per the standing caveat that
automated tests cannot close Unity object lifetime.

## P0 blockers

- [x] **P0-LIC-01 — License owned surfaces under AGPL-3.0-or-later and adopt DCO 1.1.**

  Owner: project owner.

  Current state: root and independently distributed TopiaForge-owned surfaces
  use AGPL-3.0-or-later with `Copyright (C) 2026 furroxide`; release policy is
  approved, first-party mod and VPM packages carry the text, and DCO 1.1 is
  checked in. Author-owned scaffolds default to the same terms. This supersedes
  the earlier MIT declaration.

  Evidence: [`ReleaseLicenseInventory.md`](ReleaseLicenseInventory.md),
  `LICENSE`, `DCO`, `CONTRIBUTING.md`, strict release policy, package, registry,
  BOM, SBOM, and archive-notice validation.

- [ ] **P0-IP-01 — Approve the rights basis and public naming for Robotopia integration and assets.**

  Owner: project owner, Robotopia owner, and IP/trademark counsel.

  Exit criteria: retain written authority or an approved clean-room/non-affiliation basis for the Robotopia and
  TopiaForge names, Robotopia injection, compatibility extraction/baselines, registry claims, web-derived art, adapted
  icons, fonts, and custom-world content. Remove or replace any item that lacks a distributable rights basis and
  record provenance, transformation, hash, license, and approver for retained assets.

- [ ] **P0-OSS-01 — Complete the third-party redistribution audit.** *(re-opened 2026-08-06)*

  Owner: open-source compliance/legal and release engineering.

  Current state: BepInEx, Harmony, MonoMod, Cecil, UnityDoorstop, .NET, MetadataLoadContext, Flutter/Dart, and SPDX
  data remain mechanically verified. UnityDoorstop corresponding source is bundled.

  This gate was previously marked complete on the strength of "font provenance/notices are mechanically verified".
  That claim did not hold. The legal inventory in `release_metadata_inventory.dart` is a fixed allowlist of licence
  texts, and it named only the two launcher fonts. It therefore never reached
  `tools/unity-ui-bundle/Assets/TextMesh Pro/`, and three defects shipped in the release archives undetected:
  the EmojiOne sprite sheet with no redistribution grant, Liberation Sans with no notice entry at all, and a
  Quicksand copy taken from the Robotopia web bundle that was not byte-identical to upstream. All three are fixed,
  and the Liberation licence text is now in the inventory.

  The structural limitation remains and must be closed before this gate is signed: the check verifies that *listed*
  licence files exist, not that every redistributed asset *has* a licence. A newly added unlicensed asset would still
  pass — which is precisely how EmojiOne survived, since it carried an attribution text rather than a licence file.

  Exit criteria: the source inventory verifies the LGPL corresponding-source
  method, OFL derivative/font treatment, notice placement, and original license
  terms. Exact final BOM/SBOM/archive bytes are rechecked at publication.

- [ ] **P0-PRIV-01 — Approve remote AI, player-token, microphone, and speech-to-text behavior.**

  Owner: backend owner, Robotopia owner, privacy/legal, security, and product.

  Current state: canonical descriptive capabilities are present; `remote-ai` is the sole remote-inference label; Zombies live-brain
  and voice defaults are off; no request is sent to the RoboAPI backend, no token value is read, and no audio is
  captured without explicit configuration. One qualification, corrected on 2026-08-13: RobotKit's availability probe
  runs on every mod load and scene change, and it tests for the presence of the credential file and enumerates
  microphone device names. It no longer parses or caches the token — that happens only on the request path, behind the
  consumer opt-in — and enumerating device names starts no capture.

  Exit criteria: authorize the backend use; document destination, purpose, authentication, consent, cost, retention,
  deletion, abuse/rate limits, transcript/history handling, incident response, and jurisdictional requirements; review
  launcher disclosures; test signed-out, denied, offline, rate-limited, timeout, cancellation, and revocation paths.

- [ ] **P0-TRUST-01 — Approve the package trust and first-party publication model.**

  Owner: security, product, registry, and release owners.

  Current state: the launcher discloses source, digest, aggregate capabilities, and arbitrary-code risk. Permissions
  are explicitly descriptive, not a sandbox. Official community submissions/deployment are closed; self-hosted
  registries remain supported.

  Exit criteria: approve how first-party keys/digests and download origins are trusted, how a compromised package is
  revoked, how installed users are warned/recovered, and who may authorize an official payload. Do not market
  capability declarations as containment.

- [ ] **P0-WIN-01 — Produce and validate the Windows x64 archive.**

  Owner: Windows release QA.

  Exit criteria: build from the frozen SHA on the administrator Windows
  workstation. Require the CLI, GameCompat extractor, and launcher to have
  valid Authenticode signatures from the exact reviewed leaf-certificate
  SHA-256 pin and valid HTTPS RFC 3161 timestamps; reject unsigned, partly
  signed, untimestamped, expired-at-signing, mismatched, or invalid output.
  Inspect hashes, links, modes/notices, and runtime assets;
  exercise clean install/repair/profile/safe-mode/failure/diagnostics/confirmed
  update, forced rollback, and uninstall journeys.

- **P0-LINUX-01 — Produce and validate Linux x64 and Proton behavior.** *(OUT OF RC1;
  deferred to `1.0.0-rc.2` on 2026-08-12)*

  Owner: Linux/Proton release QA.

  Why it is deferred: the WSL2 builder is fully provisioned on the administrator host and
  every pinned Linux toolchain verifies exactly — clang `18.1.3`, CMake `3.28.3`, Ninja
  `1.11.1`, GTK `3.24.41`, .NET `10.0.301`, Node `24.18.0`, Flutter `3.44.6`, Dart `3.12.2`.
  The blocker is the graphics stack, not the toolchain. NVIDIA ships no Vulkan ICD for
  WSL2 and Ubuntu 24.04 does not package Mesa's Dozen (`dzn`) Vulkan-over-D3D12 driver, so
  the only Vulkan implementation reachable inside WSLg is software lavapipe. Robotopia
  ships the Direct3D 12 Agility SDK and reaches Proton through VKD3D, which requires
  Vulkan; the OpenGL path that *does* run on the GPU there (Mesa d3d12,
  `GALLIUM_DRIVER=d3d12`) cannot serve Direct3D 12 at all. Acceptance could therefore only
  have been recorded against software rendering, or against a forced non-default renderer
  no real Proton player would use. This gate explicitly rejects a build-only run, and that
  bar was written precisely to stop this kind of shortcut, so the platform is deferred
  rather than weakened.

  RC1 consequences: `release/release-policy.json` targets Windows x64 only, the readiness
  and BOM gate contracts drop `P0-LINUX-01`, and the orchestrator's WSL
  build, Proton acceptance, and their preflight checks are gated on the policy rather than
  removed. Linux support is untouched in the source tree.

  Exit criteria for `1.0.0-rc.2`: restore the Linux archive to the policy, the gate to both
  schemas, and `P0-LINUX-01` to the readiness decision, then build Linux x64 from the frozen
  SHA and inspect final ZIP executable modes, links, checksums, notices, and bundled
  payloads. Run the actual Robotopia build-2309 matrix through Proton with
  `WINEDLLOVERRIDES=winhttp=n,b` **on a host that can reach a GPU Vulkan implementation**,
  exercise the native Linux launcher/CLI, discovery, path translation, process launch,
  runtime repair, custom-world, recovery, and uninstall flows, and return a scrubbed
  evidence bundle tied to the exact archive digest. A build-only run still does not pass.
  If that host is not the Windows administrator workstation, the evidence contract's
  `wsl2-wslg` execution environment and the orchestrator's WSL-driven collection must be
  reworked first.

- [ ] **P0-GAME-01 — Complete authorized build-2309 runtime and first-party-mod acceptance.**

  Owner: runtime/mod QA with authorized Robotopia access.

  Exit criteria: on build `2309`, test startup/shutdown, repeated scenes, safe mode, reloads, enable/disable,
  dependency order, package inbox, collision isolation, partial failures, restart-required state, save compatibility,
  all 16 source-mod flows, TopiaForgeUi-only UI, dirty updates, and resource teardown. Verify every declared GameCompat binding and
  record profiler evidence of no steady-state allocation regressions or task/callback leaks.

- [ ] **P0-CREATOR-01 — Attest the native CreatorTools evidence collector from a live build-2309 run.**

  Owner: runtime/SDK engineering.

  Current state: the collector is implemented and the source work is complete; the gate is open only for want of a
  live run. `CreatorAcceptanceRecorder` in `mods/Shared/CreatorTools` emits `TF-CREATOR|PASS|<challenge>|<case>`
  markers for all nine `creator.*` cases, and each case passes only when every one of its required workbench
  transitions was actually observed, so partial instrumentation fails closed rather than reporting a false pass. The
  recorder is inert unless a 64-hex challenge was provisioned into the CreatorTools config, so ordinary play cannot
  emit evidence. `topiaforge acceptance creator` issues that challenge, tails `manager.log`, and binds the result to
  the exact `last-run.json` session and CreatorTools package receipt. Save and checkpoint state are compared across
  End Session from the real `player_data.json.gz` document — decompressed before hashing, since a gzip header
  embeds an mtime that would otherwise read as a spurious change — with the checkpoint cursor and `<id>_reached`
  flags digested separately from the rest of the save. The three `Assert-WindowsCreator*` verifiers perform real
  `release-windows-creator-evidence-v2` verification, and `new-windows-creator-evidence.ps1` derives evidence from
  the challenge-bound acceptance result instead of from artifact presence.

  Exit criteria: retained evidence from an authorized interactive build-2309 session at the frozen candidate SHA in
  which all nine cases pass, at least ten clean lifecycle cycles complete, and save and checkpoint bytes are
  unchanged. Adversarial rejection is already proven by `tools/test-release-admin.ps1`, which fails the run if
  spoofed-challenge, spoofed-session, spoofed-result-digest, wrong-package-receipt, replayed prior-run,
  missing-case, extra-case, short-cycle, mutated-save, or mutated-checkpoint evidence is accepted. Producing the
  live evidence belongs to `P0-GAME-01`.

- [ ] **P0-HOST-01 — Configure and prove the protected verifier/publisher path.**

  Owner: GitHub administrator, security, and credential owners.

  Current state: the protected `release` environment has the required reviewer,
  a `v*` deployment restriction, and the GitHub-held Ed25519 update key. The
  dedicated protected `TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN` is not configured,
  and a plaintext duplicate of the update-signing seed remains on the
  administrator workstation pending independently verified recovery/removal.
  The local GitHub CLI is authenticated with repository-admin permission. The
  replacement path still needs a non-publishing rehearsal and
  immutable-release verification.

  Exit criteria: configure required aggregate contexts (`Required / CI validation`,
  `Required / PR policy`, `Required / Dependency review`,
  `Required / Release packages`, `Required / Registry validation`, and
  `Required / Unity source validation`); protect the release environment with
  a reviewer; keep the GitHub Ed25519 update key plus a repository-scoped,
  read-only governance-audit token there; prove fork PRs are secretless;
  independently recovery-test the protected update seed and remove plaintext
  local duplicates; enable reviewed Pages and immutable-release policy; protect creation of the annotated
  `v1.0.0-rc.1` tag while forbidding mutation/deletion; retain an administrator-reviewed dry run. Complete one
  non-publishing two-platform rehearsal before deleting the obsolete live `unity-validation` and
  `game-acceptance` environments.

- [ ] **P0-CRED-01 — Rotate credentials exposed through the local Xcode build log.**

  Owner: credential owners and security.

  Current state: an audit build inherited credential-shaped API/GitHub variables from its parent application, and
  Xcode's required Flutter scheme pre-action included them in its local build log. No value was written to tracked
  repository files. Repository-owned PBX shell phases now disable environment logging, release child processes strip
  explicit and secret-shaped variables, and contributor documentation requires reopening Xcode from a sanitized
  context. Xcode's scheme pre-action logging itself cannot be disabled by the repository while that prepare action is
  required. The two affected workspace DerivedData directories and the affected launcher build-log directory were
  permanently removed on 2026-07-22. A subsequent build launched with an allowlisted environment, Flutter `3.44.6`,
  Dart `3.12.2`, and CocoaPods `1.16.2` succeeded; a name-only scan of all 13 new Xcode activity logs found no
  credential-shaped variables or values.

  Exit criteria: revoke and rotate every credential present in the affected audit/Xcode log; remove the affected
  local DerivedData and task logs under the applicable retention policy; confirm the old credentials no longer work;
  review local and GitHub secrets for least privilege; and retain a sentinel build proving a sanitized Xcode launch does not
  expose credentials.

- [ ] **P0-CAND-01 — Freeze and attest one candidate SHA.**

  Owner: release manager.

  Current state: the original dirty worktree is preserved locally as recovery commit `5ed7e20` on
  `safety/pre-rc1-worktree-20260722`. Its delta was transplanted onto `feat/v1-rc1-candidate` from current `dev`;
  this register does not designate either commit as the frozen candidate. No tag, release, signature, or publication
  has been created.

  Exit criteria: integrate the topic through `dev`, cut and stabilize `release/1.0.0-rc.1`, merge it to `main`, and
  approve the release notes; create the protected annotated `v1.0.0-rc.1` tag on the exact verified `main` SHA through
  the authorized process; run every local/native/Unity gate without unexplained
  warnings or skips; generate and independently verify the candidate BOM, SPDX SBOM, `SHA256SUMS`, nested digests,
  sizes, signatures, provenance, platform manifests, handoff manifest, and manual-release index. The administrator
  may stage only a matching draft, and GitHub may publish it automatically only after protected release-environment
  approval and exact-byte verification. Neither path may create/mutate the tag or replace mismatched assets.

## P1 acceptance gates

- [ ] **P1-UX-01 — Complete native visual and accessibility acceptance.**

  Owner: product/accessibility QA.

  Exit criteria: capture and review Home, Setup, Mods, Browse, Profiles, Diagnostics, Settings, and Developer flows on
  every supported native host at 800x600 and larger; cover empty/loading/warning/error/destructive/recovery states,
  keyboard-only navigation, focus restoration, 100–200% text scaling, high contrast, reduced motion, screen readers,
  long paths, and no-overflow behavior. Local automated coverage is green, but macOS denied Screen Recording to this
  audit, so screenshot comparison was not fabricated.

- [ ] **P1-E2E-01 — Run independent clean-machine player and author journeys.**

  Owner: release/community QA.

  Exit criteria: a player discovers Robotopia, installs/repairs BepInEx, installs the canonical package set, previews
  capabilities/dependencies, launches normally and in safe mode, diagnoses a failure, updates manually, and recovers.
  Separately, a new author uses only published docs to install prerequisites, scaffold with explicit author/license,
  build/test/package/validate, publish to a self-hosted registry, install through the launcher, diagnose, and update.

- [ ] **P1-SUPPORT-01 — Name public support and incident owners.**

  Owner: project/community/security owners.

  Current state: [`ReleaseOperations.md`](ReleaseOperations.md), `SUPPORT.md`, and `SECURITY.md` name `@furroxide` as
  interim support, security-intake, release, incident, revocation, and rollback owner with a best-effort support model.

  Exit criteria: the named owner confirms the channels are monitored, names delegates where needed, and approves the
  response expectations, vulnerability intake, takedown/escalation path, compatibility/deprecation promise,
  release-note ownership, and launch/on-call coverage before publication.

## P2 conditional gates and frozen v1 scope

- [x] **P2-UPDATE-01 — Launcher updates are signed, confirmed, and
  recoverable.** Prereleases use Ed25519-signed GitHub release metadata,
  bounded downloads/extraction, whole-package atomic replacement, health-gated
  rollback, and idempotent recovery. `manual-releases.json` format 2 remains
  stable-only and manual-only. See [`LauncherUpdates.md`](LauncherUpdates.md).
- [x] **P2-REGISTRY-01 — Official community submissions remain closed.** Official indexes contain first-party entries
  only. Opening submissions requires namespace ownership, moderation, malware review, transfer/dispute, yank,
  revocation, appeal, and installed-user response governance plus tests.
- [x] **P2-WORLDS-01 — Custom worlds are Windows/Proton-only for v1.** Do not advertise native macOS Robotopia support.
- [x] **P2-COMPAT-01 — Build `2309` is the sole supported Robotopia build.** Numeric build `N` maps to SemVer `0.0.N`.
  Any change in the public latest manifest stops release for a new compatibility audit; unknown constrained versions
  block mods but never block an empty safe-mode launch.

## Ship decision

**NO-SHIP.** Local remediation is release-credible, but the strict policy and production trust gates correctly fail,
and all P0/P1 evidence above must be tied to a frozen candidate. The recommendation may change only after every P0 is
closed, each P1 is closed or receives an explicit dated disposition, the final matrix is rerun against the candidate
SHA, and no new critical/high finding or unexplained warning remains.
