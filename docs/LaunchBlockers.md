# Initial release blocker register

Last audited: 2026-07-31. Reconciled against the 2409 tree on 2026-08-27; see
[Reconciliation](#reconciliation-2026-08-27). Reconciled again on 2026-08-28 after a
working session that closed two advisory gates and moved engineering work on four
others; see [Second reconciliation](#second-reconciliation-2026-08-28).
Product candidate: `0.1.0-rc.1`. Recommendation: **NO-SHIP**.
Governance relaxed for the `0.x` line on 2026-08-22; see
[What blocks a `0.x` release](#what-blocks-a-0x-release).

Scope change on 2026-08-12: **Linux is out of `0.1.0-rc.1`** and returns in `0.1.0-rc.2`.
The administrator host cannot reach a GPU Vulkan implementation inside WSL2, and
Robotopia's Direct3D 12 renderer requires it through VKD3D, so no credible Proton
acceptance evidence was obtainable. RC1 ships Windows x64 only. See `P0-LINUX-01`.

A first-party mod audit on 2026-07-27 found and fixed one critical and two high-severity engineering defects that
the prior remediation had missed (see [First-party mod audit](#first-party-mod-audit-2026-07-27) below). No further
local critical- or high-severity engineering *product* defect is known as of that audit. The release remains blocked by
decisions, credentials, protected-host configuration, and native Robotopia-runtime acceptance that cannot be supplied
by source changes. The strict publication gates intentionally continue to reject the candidate until those items are
closed.

`P0-CREATOR-01` was retired on 2026-08-22 rather than closed. It existed to attest the native CreatorTools evidence
collector from an interactive session, and the standalone package it collected evidence for no longer ships: the
workbench moved into Sandbox. Its *release* machinery — the challenge-bound acceptance runner, the
`release-windows-creator-evidence-v2` descriptor and bundle, the generator, and the three `Assert-WindowsCreator*`
verifiers — was deleted with it, because a verifier nothing produces evidence for is not a gate, it is a hard stop
nobody can pass. The workbench checklist survives as manual QA in
[`LiveGameAcceptance.md`](LiveGameAcceptance.md).

Corrected 2026-08-24: the collector itself was **not** deleted. `CreatorAcceptanceRecorder` and
`CreatorAcceptanceCases` moved into the shipping Sandbox mod with the rest of the workbench and are still constructed
by `CreatorWorkbench.TryCreate`. They are inert without a provisioned 64-hex challenge, and nothing can provision one
any more, so this is dormant instrumentation rather than a live code path — but it does ship, and its consumer does
not exist. Removing it from `mods/TopiaForge.Sandbox/CreatorTools/` is follow-up work, not a release blocker.

More broadly: "Creator Tools is retired" describes the *package*, not the *code*. Roughly 5,400 lines moved into
Sandbox, which also gained an optional `io.github.furroxide.topiaforge.multiplayer` dependency. The payload count
drops 15 → 13; the shipped code surface does not shrink proportionally.

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

## What blocks a `0.x` release

Priority says how serious a gate is. **Enforcement** says whether an unmet one stops the release, and on a `0.x` line
those are no longer the same question.

TopiaForge has never shipped. Eight of the twelve recorded gates wait on organizational evidence — IP counsel
sign-off, a paid code-signing certificate, GitHub organization administration, external QA participants — that an
alpha with no users cannot obtain, and a register where every gate is fatal is a register that says nothing about
which gate matters. So five gates stay **blocking** and seven become **advisory**:

| Gate | Enforcement | Why |
| --- | --- | --- |
| `P0-IP-01` | blocking | No rights basis, no distribution, at any version. |
| `P0-OSS-01` | blocking | Redistributing an unlicensed third-party asset is release-fatal at `0.0.1`. |
| `P0-PRIV-01` | blocking | `RoboApiClient` posts to an unapproved third-party backend reusing the player's token. |
| `P0-CRED-01` | blocking | Exposed credentials stay exposed regardless of version number. |
| `P0-GAME-01` | blocking | Obtainable by the maintainer alone, and it is the claim the product *is*. |
| `P0-WIN-01` | advisory | `0.x` ships unsigned with a documented SmartScreen warning; see the gate. |
| `P0-TRUST-01` | advisory | The trust model is disclosed, not enforced; approving it is a `1.0` question. |
| `P0-HOST-01` | advisory | Protected-host configuration is org administration, not product state. |
| `P0-CAND-01` | advisory | Freeze discipline is process; a `0.x` prerelease is not immutable-forever. |
| `P1-UX-01` | advisory | Was already dispositionable; it is now dispositionable by default. |
| `P1-E2E-01` | advisory | Needs external participants an unshipped alpha has none of. |
| `P1-SUPPORT-01` | advisory | One named interim owner is honest for `0.x`. |

Advisory does not mean removed. Every gate keeps its entry in
[`release/release-readiness.json`](../release/release-readiness.json) with its status and reason code,
`topiaforge release validate-readiness` prints unmet advisory gates as warnings, and the published BOM carries the
whole summary. Only the *computed status* changes: an advisory gate cannot by itself hold a candidate.

Enforcement is pinned per gate id in `apps/topiaforge_cli/lib/src/release_readiness.dart`, so the decision file cannot
declare itself advisory. Restoring the `1.0` posture means moving each value back to `blocking` in that contract and in
the decision file together.

## Reconciliation (2026-08-27)

The 2026-07-31 audit predates the 2409 cutover ([#66](https://github.com/Furroxide/TopiaForge/pull/66),
landed 2026-08-26). That line retired two subsystems, so parts of this register described a repository
that no longer exists. This pass corrects only what the retirement falsified. **No gate changed state
and no new evidence was recorded** — a developer workstation cannot supply a matrix row, and nothing
here was rerun on the release host.

| Row | Was | Now | Why |
| --- | --- | --- | --- |
| Sidecar install/runtime/security | PASS | RETIRED | `tools/ugc-automerge-sidecar` was deleted; the row attested a subsystem that is gone. |
| CLI tests | PASS | NEEDS RERUN | Counted 190 cases and credited UGC coverage; the `ugc` command family was deleted and the suite is now 215 cases. |

Two rows were checked and are already correct: **First-party mods** (14 source mods, 13-package
payload) and **VPM and canonical ecosystem payload**, which already records that its evidence
predates the retirement.

`P0-OSS-01`'s structural limitation — the inventory verifies that *listed* licence files exist rather
than that every redistributed asset *has* one — has a fix in review at
[#79](https://github.com/Furroxide/TopiaForge/pull/79). It enumerates the redistributed assets and
fails on any the notices do not cover, and it found twelve first-party assets that nothing recorded.
That closes the engineering half only; the gate still needs the compliance review in its exit
criteria and stays **blocking**.

The `docs.topiaforge.dev` custom domain still returns 404 with the Pages API reporting `status: null`,
unchanged since it was first noted. Rechecked 2026-08-28, with one detail worth adding so the next
audit does not start from the wrong end: the CNAME resolves and `https_certificate` is `approved`, so
DNS and TLS are both fine. `status: null` means no Pages build has ever succeeded for the domain,
which is where to look. It is not a gate.

## Second reconciliation (2026-08-28)

A working session on 2026-08-28 acted on everything in this register that could be acted on
without authority the project does not have. **The ship decision does not move**: five blocking
gates were open at the start and five are open at the end. Two *advisory* gates close on a
recorded owner decision, and four gates advanced on their engineering half without closing.

### Gates that changed state

| Gate | Change | Enforcement |
| --- | --- | --- |
| `P0-TRUST-01` | **Approved** by the project owner on 2026-08-28. The disclosed-not-enforced package trust model is accepted for the `0.x` line. Recorded as `EVID-P0-TRUST-01-0001`. | advisory |
| `P1-SUPPORT-01` | **Approved** by the project owner on 2026-08-28. `@furroxide` confirms the `SUPPORT.md` / `SECURITY.md` channels are monitored and accepts the best-effort response expectations for `0.x`. Recorded as `EVID-P1-SUPPORT-01-0001`. | advisory |

Both must be revisited before `1.0`: `P0-TRUST-01`'s exit criteria still describe a `1.0`-grade
revocation and recovery story, and one named interim owner is honest for `0.x` and not for a
shipped product with users.

### Gates that advanced without closing

- **`P0-OSS-01`** — the engineering half is closed. [#79](https://github.com/Furroxide/TopiaForge/pull/79)
  landed, so the audit now enumerates the redistributed assets and fails on any the notices do not
  cover, rather than checking that listed licence files exist. The structural limitation this
  register described is gone. The gate **stays blocking**: its exit criteria still require the LGPL
  corresponding-source method, OFL derivative/font treatment, and notice-placement review by
  open-source compliance, and none of that is an engineering task.
- **`P0-WIN-01`** — the trust-envelope work this register called "the last move of that work" is
  done. [#87](https://github.com/Furroxide/TopiaForge/pull/87) made the detached CMS handoff
  signature conditional across the hosted-verification evidence, the final public asset inventory,
  the attestation subject, and `publish-release-draft.sh`, and deleted the guard step that rejected
  an unsigned distribution outright. The signature field is now *absent* for an unsigned build
  rather than present-and-empty, and verification fails closed in both directions. The gate
  **stays advisory and open**: shipping unsigned is a decision nobody has recorded, and a signed
  distribution still needs a purchased certificate.
- **`P0-PRIV-01`** — part of the evidence half advanced. The RoboAPI client's offline, timeout,
  cancellation, response-cap, request-cap, and log-redaction paths now have regressions behind
  them, driven against loopback sockets. HTTP 401/429/5xx, redirect refusal, and TLS failure remain
  uncovered: they need the client to reach a server it trusts, and the backend root must be HTTPS.
  The gate **stays blocking and open**, and the owner explicitly declined to record a `0.x`
  disposition for it on 2026-08-28. Its approval half — destination, retention, training use, cost,
  deletion, abuse limits, jurisdiction — is untouched.
- **`P0-GAME-01`** — all three exit criteria were met on the current tree against the maintainer's
  build-`2409` install: the loader reported `0.0.2409`, thirteen mods reached `Loaded` including all
  eight `GameCode`-coupled ones, and `gamecompat verify` exited 0 over 206 bindings. The full result
  is in the gate entry below. It **stays blocking**, because the criteria bind their evidence to a
  frozen candidate SHA and `P0-CAND-01` is open, so there is nothing to bind them to. What changed
  is that the criteria are now known to be reachable, and the void build-`2309` evidence is retired.

### Gates that did not change

`P0-CRED-01` is unchanged and **blocking**. The exposed credentials have **not** been rotated —
confirmed with the owner on 2026-08-28. No amount of source work closes it.

`P0-IP-01` is unchanged. A canonical trademark and non-affiliation notice with a CI drift audit is in
review at [#83](https://github.com/Furroxide/TopiaForge/pull/83), which improves the disclosure. It
does not supply counsel, and the naming, injection, and extraction questions are exactly as open as
they were.

`P0-HOST-01`, `P0-CAND-01`, `P1-UX-01`, and `P1-E2E-01` were out of this session's scope and are
unchanged.

### Corrections to this register

Two claims here were wrong and are corrected below and in the matrix. A third — the
"three-VPM plus 13-mod" ecosystem composition — was already corrected by
[#84](https://github.com/Furroxide/TopiaForge/pull/84); `release/catalog.json` carries exactly two
VPM packages, `vpm-resolver` and `world-companion`, and that is now confirmed against the file.

| Claim | Was | Is |
| --- | --- | --- |
| GameCompat binding counts | 182 bindings, 161 verifiable | **206** bindings, **185** verifiable, 21 uncheckable offline. The tree grew after the 2409 cutover. |
| CreatorTools removal size | "roughly 25 workbench call sites" | **42 references across 5 files**, and removing them exposes a second dead layer (three `HashSet`s, two predicates, two counters) that only the recorder ever populated. |

### Follow-up work completed

[#85](https://github.com/Furroxide/TopiaForge/pull/85) landed, removing `CreatorAcceptanceRecorder`
and `CreatorAcceptanceCases` from the shipping Sandbox mod. This register described them as inert;
they were in fact **unreachable**. `SandboxController` is the only caller of
`CreatorWorkbenchOptions` and never passed an `acceptanceChallenge`, so the optional parameter always
took its empty default and `TryCreate` always returned `null`. Not a release blocker, as recorded —
but it did ship, and it no longer does.

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
| CLI tests | CURRENT TREE | Rerun 2026-08-28 on the pinned Dart `3.12.2`: **211 passed, 4 skipped, 0 failed** across 215 cases. Two failures on a first cold run were environmental and are explained: `mod set and mod add edit the manifest with validation` and `packaged CLI owns the relocated seven-template lifecycle` both need a Release build of the C# solution *and* a resolvable `RobotopiaManagedDir`, which falls through to a developer's untracked `Directory.Build.local.props`. Both pass once that exists. This retires the void 190-case result; it is **not** the release row, which still requires the frozen candidate on the release host. |
| C#/Dart contract parity | PASS | Manifest V5, V4 retirement, SemVer 2.0, build mapping, multiplayer admission, canonical fields, unknown fields, dependencies, pins, conflicts, load order, and state fixtures agree. |
| Sidecar install/runtime/security | RETIRED | `tools/ugc-automerge-sidecar` was deleted with UGC live sync in the 2409 cutover. The row is kept rather than removed so the matrix does not silently lose a line; there is no sidecar left to test, and nothing replaced it. |
| Archive, UGC, diagnostic, repair, and process hardening | PASS | Adversarial traversal/link/collision/size/race/rollback/redaction/timeout regressions passed. Transaction recovery passes interruptions before and after every phase on all three layouts; the real Windows archive also passed a locally signed `rc.1` → synthetic `rc.2` swap and forced-health-failure rollback. |
| First-party mods | CURRENT TREE | Rerun 2026-08-28: `pack --all` emitted exactly 13 packages from 14 source mods with UiGallery the one skipped DevTool, twice, into separate trees — **all 13 byte-identical by SHA-256 across both runs**. All 13 pass `check package`, reporting valid managed assembly metadata. Repeat from the frozen candidate for the release row. |
| C# author templates | PASS | All seven template families scaffolded from a release-like payload, restored, relocated, built, tested, packed, validated, installed with full receipt checks, and rebuilt after extraction removal; each real platform-archive job repeats that lifecycle. Defaults remain deliberately non-publishable. |
| VPM and canonical ecosystem payload | NEEDS RERUN | The retained ecosystem evidence predates Creator Content and the UgcLiveSync/CreatorTools retirement. Rebuild and compare two independent release trees, each holding two VPM packages plus 13 mods, from the frozen candidate. Confirmed 2026-08-28 against `release/catalog.json`: two VPM packages, 13 mods. The 13-mod half is proven deterministic on the current tree (see First-party mods); the full `ecosystem-dist` comparison needs a platform-archive build on the release host and remains outstanding. |
| Exact-Unity TopiaForgeUi build | PASS | Unity `6000.0.23f1`; two builds matched SHA-256 `3cc6624f2a3a5fabc83c4fde49b32f859869e1d1e202afdaf91a888089f9fedb`. |
| Exact-Unity representative world build | PASS | Two current-tree builds matched SHA-256 `afa3e9195e8e03199b414f8a5c9002e9f89831041a63c7e1c9b8eef173d9057d`; manifests, editor provenance, and companion/VPM inputs matched. |
| Exact-Unity lifecycle smoke | NEEDS RERUN | A current-tree Unity `6000.0.23f1` run executed the managed validator and all 16 lifecycle cycles successfully with zero retained-resource delta. The administrator-controlled Windows release flow must regenerate and scrub that evidence from the frozen candidate. |
| Robotopia compatibility | PASS | Build `2409`; **206 bindings across 8 mods, 185 verifiable offline**, 21 explicitly uncheckable offline, zero errors, warnings, or indeterminate findings. Re-verified 2026-08-28 against the live install's `Managed` directory: `gamecompat verify` exits 0. The 8 declaring mods are exactly the ones pinned to `0.0.2409` — Chronos, CreatorContent, NoFeedbackUrl, PerfFixes, Performance, Prompts, RobotKit, Worlds; safe GravityGun, Multiplayer, OppositeDay, Sandbox, UiGallery, and Zombies declare the bounded range and have no native binding declarations. |
| Public build freshness | PASS | The public latest manifest identifies build `2409`, matching the pin; CI/release fail if it changes. |
| BepInEx/UnityDoorstop provenance | PASS | Pinned BepInEx `5.4.23.5` archives and extracted trees, UnityDoorstop commit/source, hashes, modes, and notices validate. |
| macOS release package | OUT OF RC1 | Generic packaging remains in source, but macOS is not in RC1 policy, catalog, update metadata, handoff, or public assets. It requires a separately reviewed future release. |
| Release-policy/BOM/SBOM/checksum machinery | PASS | Strict policy and metadata regressions cover AGPL-3.0-or-later, actual platform trust, signed update metadata/sidecar, checksums, BOM, SBOM, and immutable asset inventory. |
| Repository and CI hygiene | PASS | actionlint `1.7.7`, PowerShell/bash parsing, 164 JSON/YAML files, 118 Markdown files, 1,943 built HTML links, action pins, conflict markers, LF policy, and the 381-file non-generated Dart line cap passed. PSScriptAnalyzer `1.25.0` is rerun after every release-script edit. |
| Credential exposure containment | BLOCKED | The affected workspace DerivedData and launcher build logs were removed, and a scrubbed exact-toolchain sentinel build passed; 13 newly produced Xcode activity logs contained no credential-shaped variable names. Credential owners must still rotate the previously exposed values and confirm revocation. See `P0-CRED-01`. |
| Strict distributable-release policy | NEEDS RERUN | RC1 policy is scoped to Windows x64 only, forbids signing exceptions, and requires an exact nonzero Windows certificate SHA-256 pin plus an authenticated detached CMS handoff. |
| Windows x64 RC1 package and clean-host run | BLOCKED (unsigned archive built) | An unsigned 63.4 MB `TopiaForge-windows-x64.zip` was produced from `dev` on 2026-08-28 and passes `release test-package --platform windows --zip <archive> --require-windows-unsigned --run-embedded-cli`; doing it found and fixed two defects on the package-construction path, which CI never exercises. Still requires a reviewed code-signing certificate/PFX, RFC 3161 timestamp service, a frozen clean candidate, exact timestamped-signature verification, Unity/Robotopia evidence, and clean-machine QA; see `P0-WIN-01`. |
| Linux x64 package and Proton run | OUT OF RC1 | The WSL2 builder is fully provisioned and every pinned Linux toolchain verifies, but no GPU-backed Vulkan implementation is reachable there: NVIDIA ships no Vulkan ICD for WSL2 and Ubuntu does not package Mesa's Dozen driver, leaving only software lavapipe. Robotopia's Direct3D 12 renderer reaches Proton through VKD3D, which requires Vulkan, so the working OpenGL-over-d3d12 path cannot serve it. RC1 is therefore Windows-only; see `P0-LINUX-01`. |
| Authorized Robotopia acceptance on the pinned build | BLOCKED (current-tree evidence recorded) | The 2026-07-28 build-`2309` evidence stays void. All three re-scoped criteria were met on the current tree on 2026-08-28 — see `P0-GAME-01` for the captured log lines — but the gate binds its evidence to a frozen candidate SHA, and `P0-CAND-01` is open, so this remains **BLOCKED**. |
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
release-fatal here but invisible to the machine decision. All twelve are now
recorded, each with the `enforcement` value from
[What blocks a `0.x` release](#what-blocks-a-0x-release), so the computed status
cannot reach `ready` while any *blocking* gate is unresolved and an unmet
advisory gate is still visible in the published summary.

The decision carries **twelve** gates for RC1. `P0-LINUX-01` is deliberately absent
because Linux is out of this candidate; restoring it belongs to `0.1.0-rc.2`
alongside the policy archive entry and both schema gate contracts. `P0-OSS-01` is
present: it was re-opened on 2026-08-06 and the readiness contract must be able to
carry it rather than infer it from the legal inventory passing.

`P0-CREATOR-01` was never a readiness entry and is now retired outright; see the
note at the top of this register.

## Dependency and code-scanning alerts (recorded 2026-08-24)

Four open Dependabot alerts, three high: `nanoid`, `js-yaml`, and `fast-uri`, plus medium `postcss`. All
four are in `website/package-lock.json`, which builds the documentation site. `website/` is not copied
into any release payload — `release_package_payload.dart` never references it — so none of them reaches
a distributed artifact. They execute during the Pages build over repository-owned content, not
attacker-supplied input. **Disposition: not release-blocking for `0.1.0-rc.1`; carry them into the Pages
work and take the upstream fixes when they land.** GitHub reports three of the four as `runtime` scope,
which is scope *within the website package*, not within the product.

Three CodeQL alerts, all high, all `actions/cache-poisoning/poisonable-step`: two in
`release-package-build.yml` and one in `deploy-pages.yml`. **Fixed 2026-08-25** by making all three
restore-only (`actions/cache/restore`). Both workflows check out a caller-supplied ref, so cache *write*
access there let that ref plant an entry the default branch would later restore. Entries are populated by
`ci.yml` on trusted refs, which runs on push to `main`, `dev`, and `release/**`, so nothing is lost but
the occasional cold download.

Worth recording why this was a real finding rather than a theoretical one: `ManagedRefsRestore` accepts a
cache hit on a *structural* check (`validator.IsValid`) and returns before re-asserting the pinned archive
SHA-256, which it only checks on the download path. A poisoned entry would therefore have been used as the
managed reference assemblies the whole C# build compiles against. There is no sound way to verify a mutable
cache against itself — an attacker who can write the tree can write any marker beside it — so write access
*is* the trust boundary, which is exactly what this change closes.

Note for the freeze: `codeql-high-critical` is active with `bypass_actors: []`, so a `release/*` → `main`
promotion that introduces any *new* high alert cannot be merged by anyone, including the owner.

The remaining informational exceptions are explained, not waived:
`dotnet format` reports expected workspace-loader
diagnostics for the intentional Unity compile/reference split while finding no formatting changes; Flutter reports newer
packages outside current compatible constraints; Node lists optional non-host
native packages; and 21 GameCompat bindings are explicitly uncheckable offline
and therefore belong to the Robotopia acceptance gate.

## Build-2309 runtime adaptation (2026-07-28)

The strict build-2309 audit found no removed or changed declared binding. Live
startup did expose an independent loader defect: BepInEx 5 parses
`BepInPlugin.Version` as `System.Version`, so the semantic prerelease value `0.1.0-rc.1` caused it to skip the
TopiaForge plugin as invalid before `Awake`. The plugin now advertises the numeric core `1.0.0` to BepInEx while the
runtime, package manifests, and compatibility engine retain the full `0.1.0-rc.1` SemVer. `VersionUtilTests` locks the
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

- [ ] **P0-IP-01 — Approve the rights basis and public naming for Robotopia integration and assets.** *(blocking)*

  Owner: project owner, Robotopia owner, and IP/trademark counsel.

  Current state, web-derived art (recorded 2026-08-24): the three Robotopia web brand rasters
  (`topiaforge-city-header.webp`, `baby-stitch.webp`, `sheriff.webp`) are now attributed in
  [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md) as the property of Tomato Cake, outside this
  project's AGPL grant, bundled with attribution only while a written grant remains outstanding. All
  three are still referenced by the launcher and no first-party replacement exists for them; the
  precedent for retiring one is `packages/launcher_ui/lib/src/pixel_robot.dart`, which replaced
  `robot.webp`. The project owner has accepted this as a **non-blocking** risk for the `0.x` line. It
  must be revisited before `1.0`, and the files are removed or replaced on request from Tomato Cake.

  Exit criteria: retain written authority or an approved clean-room/non-affiliation basis for the Robotopia and
  TopiaForge names, Robotopia injection, compatibility extraction/baselines, registry claims, web-derived art, adapted
  icons, fonts, and custom-world content. Remove or replace any item that lacks a distributable rights basis and
  record provenance, transformation, hash, license, and approver for retained assets. The web-derived art
  sub-item carries the dated disposition above for `0.x`; the naming, injection, and extraction
  questions are unchanged and still need counsel.

- [ ] **P0-OSS-01 — Complete the third-party redistribution audit.** *(re-opened 2026-08-06)* *(blocking)*

  Owner: open-source compliance/legal and release engineering.

  Current state: BepInEx, Harmony, MonoMod, Cecil, UnityDoorstop, .NET, MetadataLoadContext, Flutter/Dart, and SPDX
  data remain mechanically verified. UnityDoorstop corresponding source is vendored in the repository
  **and, as of 2026-08-24, copied into the release archive**. It previously was not: package payload
  copying took only `third_party/BepInEx/LICENSES/`, so the LGPL-2.1 binary shipped with its notice but
  without its source. The payload writer now resolves the archive list from `provenance.json` and throws
  on a declared-but-missing file, and package validation rejects an archive that lacks it.

  This gate was previously marked complete on the strength of "font provenance/notices are mechanically verified".
  That claim did not hold. The legal inventory in `release_metadata_inventory.dart` is a fixed allowlist of licence
  texts, and it named only the two launcher fonts. It therefore never reached
  `tools/unity-ui-bundle/Assets/TextMesh Pro/`, and three defects shipped in the release archives undetected:
  the EmojiOne sprite sheet with no redistribution grant, Liberation Sans with no notice entry at all, and a
  Quicksand copy taken from the Robotopia web bundle that was not byte-identical to upstream. All three are fixed,
  and the Liberation licence text is now in the inventory.

  **The structural limitation is closed as of 2026-08-28.**
  [#79](https://github.com/Furroxide/TopiaForge/pull/79) landed
  `check_asset_licence_coverage.py`, which enumerates the redistributed non-source files and requires each to be
  accounted for by `THIRD_PARTY_NOTICES.md` or a blanket-licensed tree, failing on anything it cannot place. Coverage
  is matched from exact Markdown code spans, and a bare filename is deliberately not enough — honouring one would let
  a new file inherit a retired entry's coverage, which is how the original substring matcher could be walked past. It
  found twelve first-party assets nothing recorded; all are now listed by path. It runs in the hygiene job, so a new
  asset fails when it is added rather than at release time.

  What remains is not an engineering task. The gate **stays blocking** on the compliance review in its exit
  criteria.

  Exit criteria: the source inventory verifies the LGPL corresponding-source
  method, OFL derivative/font treatment, notice placement, and original license
  terms. Exact final BOM/SBOM/archive bytes are rechecked at publication.

- [ ] **P0-PRIV-01 — Approve remote AI, player-token, microphone, and speech-to-text behavior.** *(blocking)*

  Owner: backend owner, Robotopia owner, privacy/legal, security, and product.

  Current state: canonical descriptive capabilities are present; `remote-ai` is the sole remote-inference label; Zombies live-brain
  and voice defaults are off; no request is sent to the RoboAPI backend, no token value is read, and no audio is
  captured without explicit configuration. One qualification, corrected on 2026-08-13: RobotKit's availability probe
  runs on every mod load and scene change, and it tests for the presence of the credential file and enumerates
  microphone device names. It no longer parses or caches the token — that happens only on the request path, behind the
  consumer opt-in — and enumerating device names starts no capture.

  Evidence progress 2026-08-28: the offline, timeout, caller-cancellation, response-cap, request-cap, and
  log-redaction rows of the acceptance matrix in
  [`PrivacyAndCapabilities.md`](PrivacyAndCapabilities.md) now have regressions behind them, driven against loopback
  sockets so nothing depends on name resolution or an external host. The redaction check searches every line the
  client logged across all of those paths for the bearer token, the session identifier, and `Bearer`. Still
  uncovered, and not claimed: HTTP 401/429/5xx handling, redirect refusal, and TLS failure, each of which needs the
  client to reach a server it trusts while the backend root is required to be HTTPS.

  **No `0.x` disposition is recorded.** The owner was asked on 2026-08-28 and declined; this gate is not softened for
  the alpha line. It stays blocking, and the approval half below is untouched by the tests above.

  Exit criteria: authorize the backend use; document destination, purpose, authentication, consent, cost, retention,
  deletion, abuse/rate limits, transcript/history handling, incident response, and jurisdictional requirements; review
  launcher disclosures; test signed-out, denied, offline, rate-limited, timeout, cancellation, and revocation paths.

- [x] **P0-TRUST-01 — Approve the package trust and first-party publication model.** *(advisory)*
  *(approved 2026-08-28)*

  Owner: security, product, registry, and release owners.

  Current state: the launcher discloses source, digest, aggregate capabilities, and arbitrary-code risk. Permissions
  are explicitly descriptive, not a sandbox. Official community submissions/deployment are closed; self-hosted
  registries remain supported.

  **Approved 2026-08-28 by the project owner** for the `0.x` line, recorded as `EVID-P0-TRUST-01-0001` in
  [`release/release-readiness.json`](../release/release-readiness.json). The decision is that a disclosed —
  not enforced — trust model is the right posture for an alpha: the launcher states source, digest, aggregate
  capabilities, and arbitrary-code risk, official submissions stay closed so the only official payload is
  first-party, and capability declarations continue to be presented as disclosure rather than containment.

  This is an approval of the `0.x` posture, not of a `1.0` one. Revisit before `1.0`, when there are installed
  users to warn and recover and the revocation story has to be real rather than described.

  Exit criteria: approve how first-party keys/digests and download origins are trusted, how a compromised package is
  revoked, how installed users are warned/recovered, and who may authorize an official payload. Do not market
  capability declarations as containment.

- [ ] **P0-WIN-01 — Produce and validate the Windows x64 archive.** *(advisory)*

  Owner: Windows release QA.

  Downgraded 2026-08-22, with one thing said plainly that the downgrade does **not** change. A code-signing
  certificate is a purchase decision, not a code defect, so it should not be what a `0.x` alpha's readiness register
  hangs on; the intended `0.x` disposition is to ship unsigned behind a documented SmartScreen warning.

  Progress 2026-08-24: the explicit recorded mode now exists.
  `signingIdentities.windowsDistribution` is `signed` (the default when the key is absent) or `unsigned`, so a
  missing certificate can no longer be mistaken for a decision to ship without one. `release validate-policy`
  enforces the guard rails — an unsigned distribution may not also pin a certificate, must be a prerelease, and
  must be on a `0.x` line — and `tools/release/build-windows.ps1` drops `--require-windows-signing` while adding
  `--require-windows-unsigned`, so the artifacts are *proved* unsigned rather than merely unchecked. Preflight and
  handoff staging in `tools/release-admin.ps1` skip the certificate and the detached CMS signature, and the two
  `release.yml` handoff-verification steps expect the P7S to be absent.

  **Finished 2026-08-28.** [#87](https://github.com/Furroxide/TopiaForge/pull/87) reshaped the published trust
  envelope, which was the remaining piece: the hosted-verification evidence, the final public asset inventory, the
  attestation subject, and `tools/publish-release-draft.sh` all read `signingIdentities.windowsDistribution` from
  the policy at the verified target SHA, so the mode is a property of the reviewed candidate. The signature field is
  **omitted** for an unsigned build rather than emitted empty — an absent field cannot be mistaken for a signature
  that was present and went unverified, while `""` reads like both — and Windows `trustCheck` becomes `unsigned`
  instead of asserting an Authenticode check that did not run. Verification asserts the shape in both directions, so
  evidence and mode disagreeing fails closed either way. The guard step is deleted, which was the last move of that
  work rather than the first.

  Selecting `unsigned` now produces a publishable candidate. **The gate stays open**: nobody has recorded the
  decision to ship unsigned, and the signed path below still needs a purchased, reviewed certificate.

  **First unsigned archive produced and validated, 2026-08-28.** `release build-package --platform windows`
  completed from the `dev` tree and emitted a 63.4 MB `TopiaForge-windows-x64.zip` containing the launcher, the
  CLI, the GameCompat extractor, 13 mod packages, and both VPM packages. `release test-package --platform windows
  --zip <archive> --require-windows-unsigned --run-embedded-cli` passes against it, and all three executables
  report Authenticode `NotSigned` with no certificate.

  This is the first time that command has been run to completion in this repository, and it found two defects that
  stopped it, both fixed in [#90](https://github.com/Furroxide/TopiaForge/pull/90):

  1. `_resolveFlutterCommand` and `_resolveDartCommand` asked `commandExists` and then invoked the bare name.
     On Windows `where flutter` succeeds because `flutter.bat` is on PATH, but `Process.start` will not run a
     `.bat` from the bare name, so the build died on `ProcessException: The system cannot find the file
     specified` — a message naming neither Flutter nor PATH. Resolution now returns a real path and prefers an
     extension in `PATHEXT`, because `where` lists the extensionless POSIX script first when both are present.
  2. `_verifyUnsignedExecutable` encoded its verdict in an exit code, so every failure became "signed or has an
     invalid signature". `Get-AuthenticodeSignature` returns `UnknownError` for a file it could not read, and a
     freshly extracted executable is routinely held open by an antivirus scanner, so a genuinely unsigned package
     intermittently failed as a signing violation. The probe now reports status and certificates, and the caller
     separates signed, unsigned, and could-not-tell — treating any certificate as conclusive and retrying only a
     read that did not happen.

  **Why CI could not have caught either.** `release build-package` is never run by CI: GitHub verifies
  admin-staged bytes rather than producing them, by design. Every defect on the package-construction path is
  therefore found by a human building a package, or not at all. Both of these sat on the unsigned path that #87
  had just made load-bearing.

  **None of this closes the gate.** The archive was built from `dev` rather than a frozen candidate SHA, it is
  unsigned by necessity rather than by a recorded decision, and no clean-host QA journey has been run against it.
  What changed is that the Windows package path is now known to work end to end.

  One environment constraint worth recording, because the failure it produces names the wrong component: MSBuild's
  FileTracker refuses to create its `.tlog` files under the system temporary directory, so a build tree under
  `%TEMP%` fails with `No CMAKE_CXX_COMPILER could be found` while the toolchain is entirely healthy. Only
  `CMakeConfigureLog.yaml` names the real cause, `MSB8029` / `FTK1011`. Build from an ordinary path.

  Exit criteria for signed distribution: build from the frozen SHA on the administrator Windows
  workstation. Require the CLI, GameCompat extractor, and launcher to have
  valid Authenticode signatures from the exact reviewed leaf-certificate
  SHA-256 pin and valid HTTPS RFC 3161 timestamps; reject unsigned, partly
  signed, untimestamped, expired-at-signing, mismatched, or invalid output.
  Inspect hashes, links, modes/notices, and runtime assets;
  exercise clean install/repair/profile/safe-mode/failure/diagnostics/confirmed
  update, forced rollback, and uninstall journeys.

- **P0-LINUX-01 — Produce and validate Linux x64 and Proton behavior.** *(OUT OF RC1;
  deferred to `0.1.0-rc.2` on 2026-08-12)*

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

  Exit criteria for `0.1.0-rc.2`: restore the Linux archive to the policy, the gate to both
  schemas, and `P0-LINUX-01` to the readiness decision, then build Linux x64 from the frozen
  SHA and inspect final ZIP executable modes, links, checksums, notices, and bundled
  payloads. Run the actual Robotopia matrix for the pinned build through Proton with
  `WINEDLLOVERRIDES=winhttp=n,b` **on a host that can reach a GPU Vulkan implementation**,
  exercise the native Linux launcher/CLI, discovery, path translation, process launch,
  runtime repair, custom-world, recovery, and uninstall flows, and return a scrubbed
  evidence bundle tied to the exact archive digest. A build-only run still does not pass.
  If that host is not the Windows administrator workstation, the evidence contract's
  `wsl2-wslg` execution environment and the orchestrator's WSL-driven collection must be
  reworked first.

- [ ] **P0-GAME-01 — Complete authorized runtime and first-party-mod acceptance on the pinned build.**
  *(blocking)*

  Owner: runtime/mod QA with authorized Robotopia access.

  Re-scoped 2026-08-22. The gate previously named build `2309` and demanded the complete dynamic-binding, reload,
  recovery, multiplayer, and profiler matrix across all sixteen source mods. Its 2026-07-28 evidence is void — the
  pinned build is now `2409` — and re-earning that matrix is not what a `0.x` alpha needs from this gate. It needs to
  know the thing runs.

  The gate is anchored to **the build pinned in [`release/release-policy.json`](../release/release-policy.json)**
  (`gameBuild.id`), not to a literal written here, so `topiaforge compat bump` retargets it without a documentation
  edit.

  Exit criteria, all three on that pinned build, from the frozen candidate SHA:

  1. **Startup smoke.** BepInEx loads TopiaForge, the loader reports the detected game version as `0.0.<pinned id>`,
     and Robotopia reaches an interactive state and shuts down cleanly.
  2. **One mod load.** At least one first-party mod reaches `Loaded` — a `GameCode`-coupled one, since an exact pin is
     a claim about exactly those.
  3. **`gamecompat verify` exits 0** against that install's `Managed` directory, with no critical binding error.

  Anything beyond those three is a `1.0` concern and belongs in [`LiveGameAcceptance.md`](LiveGameAcceptance.md) as
  manual QA, not here. Record the three results as the replacement evidence.

  **Current-tree run, 2026-08-28.** All three criteria were met against the maintainer's build-`2409` install. This
  is recorded as preliminary evidence; it is **not** the gate.

  1. *Startup smoke* — BepInEx loaded TopiaForge, the manager log reported
     `Detected Robotopia game version 0.0.2409`, the game reached the `TestCityStartMenu` scene, and shutdown
     unloaded all 13 mods in reverse order. `last-run.json` records an empty `rootError`, an empty `recovery`, and a
     280 ms → 5,035 ms startup across five stages.
  2. *Mod load* — 14 packages installed from the inbox and **13 reached `Loaded`**, UiGallery excluded as the one
     DevTool. Eight of those are the `GameCode`-coupled mods pinned to exactly `0.0.2409` (Chronos, CreatorContent,
     NoFeedbackUrl, PerfFixes, Performance, Prompts, RobotKit, Worlds), so the criterion's "a `GameCode`-coupled
     one" is satisfied eight times over. Zero packages reported errors.
  3. *`gamecompat verify`* — **exit 0** against that install's `Managed` directory: 206 declared bindings, 185
     verifiable, 21 uncheckable offline, 0 errors, 0 warnings, 0 indeterminate.

  No `[ERROR]` or `[WARN]` line appeared in the session, and scene events dispatched normally — the
  `Gravity Gun scene refresh: TestCityStartMenu` line is the first live confirmation that the scene-handle fix in
  [#81](https://github.com/Furroxide/TopiaForge/pull/81) works on 2409.

  **This does not close the gate.** The exit criteria bind these three results to the frozen candidate SHA, and
  `P0-CAND-01` is open, so there is no frozen candidate to bind them to. The run retires the void build-`2309`
  evidence and shows the three criteria are reachable; it must be repeated against the frozen candidate before this
  gate can be signed. `P0-GAME-01` **stays blocking**.

- **P0-CREATOR-01 — Attest the native CreatorTools evidence collector from a live run.**
  *(RETIRED 2026-08-22)*

  Retired, not closed. The gate attested an evidence collector for the standalone CreatorTools package, and that
  package no longer ships — the workbench moved into Sandbox. Keeping a gate that only an interactive session against
  a deleted package could pass is a permanent hard stop wearing a checkbox.

  Removed with it: `apps/topiaforge_cli/lib/src/creator_acceptance_{models,evidence,runner}.dart` and
  `creator_persistence_probe.dart`, `topiaforge acceptance creator`, the `creatorAcceptance` inventory in
  `tests/live-game-acceptance.json`, `tools/release/new-windows-creator-evidence.ps1`, the three
  `Assert-WindowsCreator*` verifiers and the `-WindowsCreatorEvidence`/`-WindowsCreatorEvidenceBundle` inputs in
  `tools/release-admin.ps1`, and the `release-windows-creator-evidence-v2` branch of the handoff QA contract.

  `CreatorAcceptanceRecorder` and `CreatorAcceptanceCases` were removed from
  `mods/TopiaForge.Sandbox/CreatorTools` by [#85](https://github.com/Furroxide/TopiaForge/pull/85) on 2026-08-28.
  Two corrections to what this register said about them, both found while doing it. They were not merely inert but
  **unreachable**: `SandboxController` is the only
  caller of `CreatorWorkbenchOptions` and never passes an `acceptanceChallenge`, so the optional parameter always
  takes its empty default and `TryCreate` always returns `null`. And they are woven through **42 references across
  five files**, not roughly 25 — and removing those exposes a second dead layer beneath, because the three
  `HashSet`s, two predicates, and two counters that survive are populated only by methods that return early when the
  recorder is null.

  What the workbench still owes is manual QA, recorded in
  [`LiveGameAcceptance.md`](LiveGameAcceptance.md). Live-run coverage of the shipped product belongs to `P0-GAME-01`.

- [ ] **P0-HOST-01 — Configure and prove the protected verifier/publisher path.** *(advisory)*

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
  `v0.1.0-rc.1` tag while forbidding mutation/deletion; retain an administrator-reviewed dry run. Complete one
  non-publishing two-platform rehearsal before deleting the obsolete live `unity-validation` and
  `game-acceptance` environments.

- [ ] **P0-CRED-01 — Rotate credentials exposed through the local Xcode build log.** *(blocking)*

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

- [ ] **P0-CAND-01 — Freeze and attest one candidate SHA.** *(advisory)*

  Owner: release manager.

  Current state: the original dirty worktree is preserved locally as recovery commit `5ed7e20` on
  `safety/pre-rc1-worktree-20260722`. Its delta was transplanted onto `feat/v1-rc1-candidate` from current `dev`;
  this register does not designate either commit as the frozen candidate. No tag, release, signature, or publication
  has been created.

  Exit criteria: integrate the topic through `dev`, cut and stabilize `release/0.1.0-rc.1`, merge it to `main`, and
  approve the release notes; create the protected annotated `v0.1.0-rc.1` tag on the exact verified `main` SHA through
  the authorized process; run every local/native/Unity gate without unexplained
  warnings or skips; generate and independently verify the candidate BOM, SPDX SBOM, `SHA256SUMS`, nested digests,
  sizes, signatures, provenance, platform manifests, handoff manifest, and manual-release index. The administrator
  may stage only a matching draft, and GitHub may publish it automatically only after protected release-environment
  approval and exact-byte verification. Neither path may create/mutate the tag or replace mismatched assets.

## P1 acceptance gates

- [ ] **P1-UX-01 — Complete native visual and accessibility acceptance.** *(advisory)*

  Owner: product/accessibility QA.

  Exit criteria: capture and review Home, Setup, Mods, Browse, Profiles, Diagnostics, Settings, and Developer flows on
  every supported native host at 800x600 and larger; cover empty/loading/warning/error/destructive/recovery states,
  keyboard-only navigation, focus restoration, 100–200% text scaling, high contrast, reduced motion, screen readers,
  long paths, and no-overflow behavior. Local automated coverage is green, but macOS denied Screen Recording to this
  audit, so screenshot comparison was not fabricated.

- [ ] **P1-E2E-01 — Run independent clean-machine player and author journeys.** *(advisory)*

  Owner: release/community QA.

  Exit criteria: a player discovers Robotopia, installs/repairs BepInEx, installs the canonical package set, previews
  capabilities/dependencies, launches normally and in safe mode, diagnoses a failure, updates manually, and recovers.
  Separately, a new author uses only published docs to install prerequisites, scaffold with explicit author/license,
  build/test/package/validate, publish to a self-hosted registry, install through the launcher, diagnose, and update.

- [x] **P1-SUPPORT-01 — Name public support and incident owners.** *(advisory)*
  *(approved 2026-08-28)*

  Owner: project/community/security owners.

  Current state: [`ReleaseOperations.md`](ReleaseOperations.md), `SUPPORT.md`, and `SECURITY.md` name `@furroxide` as
  interim support, security-intake, release, incident, revocation, and rollback owner with a best-effort support model.

  **Approved 2026-08-28 by the project owner**, recorded as `EVID-P1-SUPPORT-01-0001` in
  [`release/release-readiness.json`](../release/release-readiness.json). `@furroxide` confirms the channels above are
  monitored and accepts the best-effort response expectations for the `0.x` line as the documented model, with no
  delegates named. One interim owner covering support, security intake, release, incident, revocation, and rollback
  is an honest description of an alpha with no users; it is not a model for a shipped product, and this must be
  revisited before `1.0`.

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
- [x] **P2-COMPAT-01 — Build `2409` is the supported Robotopia build.** Numeric build `N` maps to SemVer `0.0.N`.
  Compatibility is declared per mod: mods with native `GameCode` bindings pin `0.0.2409`; SDK-only mods declare the
  bounded range `>=0.0.2409 <0.0.2600`. See [the compatibility policy](CompatibilityPolicy.md).
  Any change in the public latest manifest stops release for a new compatibility audit; unknown constrained versions
  block mods but never block an empty safe-mode launch.

## Ship decision

**NO-SHIP**, unchanged by the 2026-08-28 session. All five blocking gates — `P0-IP-01`, `P0-OSS-01`, `P0-PRIV-01`,
`P0-CRED-01`, `P0-GAME-01` — are open, so the computed readiness status is `blocked` on its own terms. Separately,
`release validate-policy` still fails on the unset Windows signing identity and the deliberately `blocked` catalog
status.

What that session did change is the shape of what is left. Two advisory gates are closed on a recorded owner
decision, and of the five blocking gates, three now have nothing further an engineer can do: `P0-OSS-01` needs
compliance review, `P0-CRED-01` needs credentials rotated, and `P0-IP-01` needs counsel. `P0-GAME-01` needs a frozen
candidate rather than any new capability, and `P0-PRIV-01` needs backend authorisation plus the three HTTP-status
rows a trusted local certificate fixture would unlock. **None of the remaining blocking work is a code defect.**

The recommendation may change once every blocking gate is closed with evidence from the frozen candidate SHA, the
final matrix is rerun against it, and no new critical/high finding or unexplained warning remains. Advisory gates
should still be closed or given an explicit dated disposition — they are advice, not absolution — but an open one no
longer holds the release.
