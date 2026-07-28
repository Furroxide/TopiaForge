# Initial release blocker register

Last audited: 2026-07-28. Product candidate: `1.0.0-rc.1`. Recommendation: **NO-SHIP**.

A first-party mod audit on 2026-07-27 found and fixed one critical and two high-severity engineering defects that
the prior remediation had missed (see [First-party mod audit](#first-party-mod-audit-2026-07-27) below). No further
local critical- or high-severity engineering defect is known as of that audit. The release remains blocked by
decisions, credentials, protected-host configuration, and native Robotopia-runtime acceptance that cannot be supplied
by source changes. The strict publication gates intentionally continue to reject the candidate until those items are
closed.

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
| C# Release solution | NEEDS RERUN | The retained 43-project evidence predates Opposite Day and the Creator additions. Rebuild the current 47-project solution on SDK `10.0.301` / runtime `10.0.9` from the frozen candidate. |
| C# regression harness | NEEDS RERUN | Focused Opposite Day, module-contract, manifest, Prompts API-baseline, and GameCompat checks pass, but the complete current harness and the concurrently changed RobotKit API baseline must be rerun after Creator Content/RobotKit integration is complete. |
| C# boundaries and public SDK surface | NEEDS RERUN | Unity-free Core, Unity/BepInEx runtime isolation, strict audit, generated API baselines, bounded production-read scans, and all 12 SDK package audits must pass with Creator Content included. |
| Dart formatting and analyzers | PASS | 340 non-generated Dart files checked; domain, data, UI, app, and CLI analyzers report no issues and every file is at most 500 lines. |
| Dart domain/data tests | PASS | 199 domain and 282 data tests passed, including Manifest V5 dispatch, multiplayer admission, full runtime-constraint propagation, deterministic package-inbox planning, runtime repair, multiplayer synchronized-file receipt parity, and receipt provenance/repair behavior. |
| Flutter UI/app tests | PASS | 3 shared-UI and 61 launcher tests passed, including BLoC lifecycle, package-inbox outcomes, scaling, contrast, focus, install/repair confirmation, safe mode, recovery, and Xcode payload/logging configuration. |
| CLI tests | PASS | All 174 CLI tests passed with Dart `3.12.2`, including V4-to-V5 migration, multiplayer scaffolding and contract locks, packaging, registry, Unity probing, UGC, release metadata, final-archive validation, and the relocated seven-template lifecycle. |
| C#/Dart contract parity | PASS | Manifest V5, V4 retirement, SemVer 2.0, build mapping, multiplayer admission, canonical fields, unknown fields, dependencies, pins, conflicts, load order, and state fixtures agree. |
| Sidecar install/runtime/security | PASS | Lockfile `npm ci`, syntax checks, 23 tests, production dependency tree, and audit passed with zero vulnerabilities. |
| Archive, UGC, diagnostic, repair, and process hardening | PASS | Adversarial traversal/link/collision/size/race/rollback/redaction/timeout regressions passed. |
| First-party mods | NEEDS RERUN | Repeat deterministic packing and managed-assembly validation for all 16 source mods, the 14-package normal non-DevTool output, and the 15-package release payload with Creator Tools added explicitly; UiGallery remains excluded. |
| C# author templates | PASS | All seven template families scaffolded from a release-like payload, restored, relocated, built, tested, packed, validated, installed with full receipt checks, and rebuilt after extraction removal; each real platform-archive job repeats that lifecycle. Defaults remain deliberately non-publishable. |
| VPM and canonical ecosystem payload | NEEDS RERUN | The retained ecosystem evidence predates Creator Content and Creator Tools. Rebuild and compare two independent three-VPM plus 15-mod release trees from the frozen candidate. |
| Exact-Unity TopiaForgeUi build | PASS | Unity `6000.0.23f1`; two builds matched SHA-256 `3cc6624f2a3a5fabc83c4fde49b32f859869e1d1e202afdaf91a888089f9fedb`. |
| Exact-Unity representative world build | PASS | Two current-tree builds matched SHA-256 `afa3e9195e8e03199b414f8a5c9002e9f89831041a63c7e1c9b8eef173d9057d`; manifests, editor provenance, and companion/VPM inputs matched. |
| Exact-Unity lifecycle smoke | NEEDS RERUN | A current-tree Unity `6000.0.23f1` run executed the managed validator and all 16 lifecycle cycles successfully with zero retained-resource delta. The protected workflow invokes and uploads the same evidence, but it must still be regenerated from the frozen candidate. |
| Robotopia compatibility | PASS | Build `2309`; 219 bindings, 198 verifiable offline, 21 explicitly uncheckable offline, zero errors, warnings, or indeterminate findings; safe GravityGun, OppositeDay, Sandbox, and Zombies have no native binding declarations. |
| Public build freshness | PASS | A fresh 2026-07-28 public probe confirms both platform records identify build `2309`; CI/release fail if the public latest manifest changes. |
| BepInEx/UnityDoorstop provenance | PASS | Pinned BepInEx `5.4.23.5` archives and extracted trees, UnityDoorstop commit/source, hashes, modes, and notices validate. |
| Local macOS package structure | NEEDS RERUN | The retained universal-package record predates the V1 CLI, SDK, runtime, and canonical 15-mod release payload. Rebuild and validate the frozen V1 archive on macOS. |
| Local macOS launch and Xcode development | NEEDS RERUN | A scrubbed debug build passed with Flutter `3.44.6`, Dart `3.12.2`, and CocoaPods `1.16.2`, with no tracked native-project drift. Launch and repeat the build from the frozen candidate before release. |
| Local macOS runtime repair | NEEDS RERUN | The recorded repair targeted the retired pre-V1 loader and package set. Repeat with loader `1.0.0-rc.1` and the canonical 15-mod release payload; package-inbox ingestion remains part of authorized Robotopia acceptance. |
| Release-policy/BOM/SBOM/checksum machinery | PASS | Policy validation in technical dry-run mode and metadata build/verify regression suites passed; strict mode correctly stops on the blocked catalog status and unresolved owner/legal license decision. |
| Repository and CI hygiene | PASS | actionlint `1.7.7`, PSScriptAnalyzer `1.25.0`, PowerShell/bash syntax, repository-owned shellcheck, 157 JSON/YAML files, 113 Markdown files, 1,706 built HTML links, LFS, action pins, conflict markers, LF policy, and the Dart line cap passed. |
| Credential exposure containment | BLOCKED | The affected workspace DerivedData and launcher build logs were removed, and a scrubbed exact-toolchain sentinel build passed; 13 newly produced Xcode activity logs contained no credential-shaped variable names. Credential owners must still rotate the previously exposed values and confirm revocation. See `P0-CRED-01`. |
| Strict distributable-release policy | FAIL | Correctly stops on `OWNER_DECISION_REQUIRED`, `NOASSERTION`, and blocked release status; see `P0-LIC-01`. |
| Production macOS trust | FAIL | Structural/ad-hoc validation passes, but Developer ID team identity, notarization, stapling, and Gatekeeper correctly fail; see `P0-MAC-01`. |
| Windows x64 signed package and clean-host run | BLOCKED | Requires a Windows runner, Authenticode identity, RFC 3161 timestamp service, and clean-machine QA; see `P0-WIN-01`. |
| Linux x64 package and Proton run | BLOCKED | Flutter desktop builds are host-specific; requires Linux/Proton runners and gameplay QA; see `P0-LINUX-01`. |
| Signed macOS arm64 and Intel clean-host runs | BLOCKED | Requires Apple credentials and quarantined clean hosts; see `P0-MAC-01`. |
| Authorized Robotopia build-2309 acceptance | BLOCKED | A local Windows startup smoke passed on 2026-07-28: BepInEx loaded TopiaForge, detected `0.0.2309`, consumed all 16 staged packages, loaded every enabled first-party mod, initialized the native prompt/performance/UI bridges, and left Robotopia responsive. The complete dynamic-binding, reload, recovery, multiplayer, and profiler matrix still requires retained evidence from the frozen candidate; see `P0-GAME-01`. |
| Native UX/accessibility acceptance | BLOCKED | Screen Recording permission prevented screenshot comparison; screen-reader and native-platform manual QA remain; see `P1-UX-01`. |
| Project license, IP, and OSS legal approval | BLOCKED | Project-owner/legal decisions cannot be inferred; see `P0-LIC-01`, `P0-IP-01`, and `P0-OSS-01`. |
| Privacy/backend authorization and package trust policy | BLOCKED | Remote features default off, but owner approval is still required; see `P0-PRIV-01` and `P0-TRUST-01`. |
| GitHub rulesets, environments, secrets, tag, and attestations | BLOCKED | Repository administration and credential owners must configure and prove the trusted path; see `P0-HOST-01`. |
| Frozen candidate hosted matrix and reviewed release record | BLOCKED | This audit intentionally leaves uncommitted changes and creates no tag/release; see `P0-CAND-01`. |
| Independent player/author clean-machine acceptance | BLOCKED | Requires external participants and supported native hosts; see `P1-E2E-01`. |

Matrix totals: **17 PASS, 2 FAIL, 8 NEEDS RERUN, 11 BLOCKED, 0 SKIP**.

The five informational exceptions are explained, not waived: `dotnet format` reports expected workspace-loader
diagnostics for the intentional Unity compile/reference split while finding no formatting changes; Flutter reports newer
packages outside current compatible constraints; Node lists optional non-host native packages; package inspection
warns that the intentionally unresolved project license blocks publication; and 21 GameCompat bindings are explicitly
uncheckable offline and therefore belong to the Robotopia acceptance gate.

## Build-2309 runtime adaptation (2026-07-28)

The build-2309 managed surface is additive relative to the retired build-2227 baseline: the strict 219-binding audit
found no removed or changed declared binding. Live startup did expose an independent loader defect: BepInEx 5 parses
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

- [ ] **P0-LIC-01 — Approve the project/inbound license and replace the release sentinel.**

  Owner: project owner and legal counsel.

  Current state: `release/release-policy.json` deliberately contains `OWNER_DECISION_REQUIRED`; first-party
  manifests use SPDX-standard `NOASSERTION`; default scaffolds contain a no-grant notice; and the SDK NuGet pack
  target currently defaults to MIT. [`ReleaseLicenseInventory.md`](ReleaseLicenseInventory.md) records the complete
  inconsistency and propagation checklist. These declarations are evidence to resolve, not publication permission.

  Exit criteria: identify copyright holders and inbound contribution terms; approve an outbound SPDX expression;
  add the canonical root license; update all first-party manifests/packages; confirm which independently distributed
  artifacts must carry which text; run the strict policy, package, registry, BOM, SBOM, and archive-notice gates with
  zero findings.

- [ ] **P0-IP-01 — Approve the rights basis and public naming for Robotopia integration and assets.**

  Owner: project owner, Robotopia owner, and IP/trademark counsel.

  Exit criteria: retain written authority or an approved clean-room/non-affiliation basis for the Robotopia and
  TopiaForge names, Robotopia injection, compatibility extraction/baselines, registry claims, web-derived art, adapted
  icons, fonts, and custom-world content. Remove or replace any item that lacks a distributable rights basis and
  record provenance, transformation, hash, license, and approver for retained assets.

- [ ] **P0-OSS-01 — Obtain legal acceptance of the completed third-party disposition.**

  Owner: open-source compliance/legal and release engineering.

  Current state: BepInEx, Harmony, MonoMod, Cecil, UnityDoorstop, .NET, MetadataLoadContext, Flutter/Dart, SPDX data,
  and font provenance/notices are mechanically verified. UnityDoorstop corresponding source and neutral renamed TMP
  derivatives are bundled.

  Exit criteria: legal review confirms the LGPL corresponding-source method, OFL derivative/font treatment, notice
  placement, and license compatibility for the exact final BOM/SBOM bytes.

- [ ] **P0-PRIV-01 — Approve remote AI, player-token, microphone, and speech-to-text behavior.**

  Owner: backend owner, Robotopia owner, privacy/legal, security, and product.

  Current state: canonical descriptive capabilities are present; `remote-ai` is the sole remote-inference label; Zombies live-brain
  and voice defaults are off; no token, remote-AI, microphone, or STT activity occurs without explicit configuration.

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

- [ ] **P0-MAC-01 — Produce a Developer ID-signed, notarized final macOS archive.**

  Owner: macOS signing owner and release QA.

  Exit criteria: sign every nested Mach-O with the approved Developer ID/Team ID and hardened runtime; notarize and
  staple the app; extract the final ZIP with quarantine metadata; pass deep/strict codesign, expected team identity,
  `stapler validate`, and Gatekeeper on clean Apple Silicon and Intel hosts; run install, repair, launch, diagnostics,
  and uninstall. Never weaken library validation or omit hardened runtime in a public candidate. An explicitly
  non-distributable ad-hoc technical dry run may omit hardened runtime, and publication must continue to reject it.

- [ ] **P0-WIN-01 — Produce and validate the signed Windows x64 archive.**

  Owner: Windows signing owner and release QA.

  Exit criteria: build from the frozen SHA on Windows; Authenticode-sign every required executable with SHA-256 and
  an HTTPS RFC 3161 timestamp; run `signtool verify /pa /all /tw` against the final extracted ZIP; inspect hashes,
  links, modes/notices, and runtime assets; exercise clean install/repair/profile/safe-mode/failure/diagnostics/update
  and uninstall journeys.

- [ ] **P0-LINUX-01 — Produce and validate Linux x64 and Proton behavior.**

  Owner: Linux/Proton release QA.

  Exit criteria: build on Linux from the frozen SHA; inspect final ZIP executable modes, links, checksums, notices,
  and bundled payloads; run native launcher/CLI flows; exercise discovery, path translation, process launch,
  runtime repair, custom-world, recovery, and uninstall paths for Robotopia's Windows build under Proton.

- [ ] **P0-GAME-01 — Complete authorized build-2309 runtime and first-party-mod acceptance.**

  Owner: runtime/mod QA with authorized Robotopia access.

  Exit criteria: on build `2309`, test startup/shutdown, repeated scenes, safe mode, reloads, enable/disable,
  dependency order, package inbox, collision isolation, partial failures, restart-required state, save compatibility,
  all 16 source-mod flows, TopiaForgeUi-only UI, dirty updates, and resource teardown. Verify every declared GameCompat binding and
  record profiler evidence of no steady-state allocation regressions or task/callback leaks.

- [ ] **P0-HOST-01 — Configure and prove the protected hosted release path.**

  Owner: GitHub administrator, security, and credential owners.

  Current state: the checked-in desired-state policy defines separate `main`, `dev`, `release/*`, and `v*` rulesets
  plus the four protected environments. The 2026-07-22 live read-only audit could not inspect collaborators because
  the available GitHub credential lacks push/admin access (HTTP 403); no hosted protection is therefore attested.

  Exit criteria: configure required aggregate contexts (`Required / CI validation`, `Required / Release packages`,
  `Required / Registry validation`, and trusted-candidate `Required / Unity validation`); protect release and Unity
  environments with reviewers; inventory and scope Apple, Windows, Unity, managed-reference, Pages, and attestation
  credentials; prove fork PRs are secretless; enable reviewed Pages and immutable-release policy; protect creation of
  the annotated `v1.0.0-rc.1` tag while forbidding mutation/deletion; retain an administrator-reviewed dry run.

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
  review hosted secrets for least privilege; and retain a sentinel build proving a sanitized Xcode launch does not
  expose credentials.

- [ ] **P0-CAND-01 — Freeze and attest one candidate SHA.**

  Owner: release manager.

  Current state: the original dirty worktree is preserved locally as recovery commit `5ed7e20` on
  `safety/pre-rc1-worktree-20260722`. Its delta was transplanted onto `feat/v1-rc1-candidate` from current `dev`;
  this register does not designate either commit as the frozen candidate. No tag, release, signature, or publication
  has been created.

  Exit criteria: integrate the topic through `dev`, cut and stabilize `release/1.0.0-rc.1`, merge it to `main`, and
  approve the release notes; create the protected annotated `v1.0.0-rc.1` tag on the exact verified `main` SHA through
  the authorized process; run every hosted/native/Unity gate without unexplained
  warnings or skips; generate and independently verify the candidate BOM, SPDX SBOM, `SHA256SUMS`, nested digests,
  sizes, signatures, provenance, and manual-release index. The workflow may prepare only a matching draft and must
  never create/mutate the tag, replace assets, or publish automatically.

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

- [x] **P2-UPDATE-01 — Launcher upgrades are manual-only.** `manual-releases.json` format 2 carries only HTTPS URLs,
  hashes, sizes, and `manualOnly: true`; no replacement strategy is advertised. A future automatic updater requires a
  separate signed-metadata, bounded-extraction, rollback, and recovery review.
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
