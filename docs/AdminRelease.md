# Admin-orchestrated release

Production archives are built only on maintainer-controlled machines. GitHub
Actions verifies the exact staged bytes, attests what it verified, creates the
signed update metadata, and publishes after approval of the protected
`release` environment. It does not rebuild production packages.

The entry point is:

```powershell
./tools/release-admin.ps1 preflight
./tools/release-admin.ps1 build
```

**`build` is expected to stop on its first pass**, and this is by design rather
than a failure. The Creator evidence descriptor is bound to the Windows archive
by digest, so it cannot be produced until `build` has created that archive.
`build` therefore runs to `platforms-built`, retains everything, and then stops
at the Creator-evidence check with "Builds are retained; provide
-WindowsCreatorEvidence and run resume."

Note also that `build` contains a mandatory interactive window: the live
`TF-ACCEPT` acceptance run needs roughly 30 minutes at the keyboard with a
gamepad and microphone. See [`LiveGameAcceptance.md`](LiveGameAcceptance.md).

At that point run the Creator session, convert its result, and continue:

```powershell
topiaforge acceptance creator --creator-package <CreatorTools .topiaforgemod> --all

./tools/release/new-windows-creator-evidence.ps1 `
  -AcceptanceResult <result.json> -StateDirectory <output>/state `
  -CaseEvidenceDirectory <case artifacts> -SourceSha <sha> -Version 0.1.0-rc.1 `
  -WindowsArchive .release-local\0.1.0-rc.1\assets\TopiaForge-windows-x64.zip `
  -CanonicalEcosystemSha256 <sha> `
  -OutputBundle C:\release-qa\windows-creator-evidence.zip `
  -OutputDescriptor C:\release-qa\windows-creator-evidence.json

./tools/release-admin.ps1 resume `
  -WindowsCreatorEvidence C:\release-qa\windows-creator-evidence.json `
  -WindowsCreatorEvidenceBundle C:\release-qa\windows-creator-evidence.zip
./tools/release-admin.ps1 stage
./tools/release-admin.ps1 dispatch
```

`resume` continues from the durable local state and `all` runs every remaining
phase. Passing the two evidence flags to `build` or `all` up front works only
when a valid descriptor for that exact archive already exists — on a fresh
candidate it will not, so expect the two-step sequence above.
Add `-Rehearsal` to `all` for a verified, non-publishing two-platform
rehearsal. Local state and raw evidence live under `.release-local/`, which is
ignored by Git.

Each canonical ecosystem pass runs in its own detached clean worktree. The
SHA-256 of the normalized, sorted tree manifest is the ecosystem identity; the
single tar sent to WSL has a separate transport digest. Every
platform validation summary binds both values.

The machine-readable ship decision is
`release/release-readiness.json`. It is read from the exact target commit,
validated against the schema from that same commit, and bound into the BOM by
digest and gate summary. It carries one entry for each of the twelve
release-fatal gates in [`LaunchBlockers.md`](LaunchBlockers.md), so no gate that
can stop a release is tracked only in prose. Every P0 gate must be `approved`.
Each P1 gate must be either `approved` or `accepted-risk` with an allowed RC1
scope and evidence identifier. A blocked, missing, malformed, working-tree-only, or wrong-version
decision stops preflight, staging, and the protected finalizer. The catalog
must remain `blocked` until the reviewed decision is committed. The CLI treats
the `evidenceIds` as manually reviewed attestation references: it validates
their exact syntax, uniqueness, gate binding, and exact-commit digest, but does
not resolve an external evidence registry or prove reviewer identity. Evidence
existence and reviewer authorization are therefore part of the protected
`release` environment's final human approval.

## Admin Windows machine

The checkout must be a clean `main` exactly equal to `origin/main`. Configure:

- GitHub CLI authentication for a repository administrator.
- live GitHub governance matching the checked-in release controls: immutable
  releases enabled, the non-bypassable `release` environment reviewed only by
  `furroxide` for `v*` tags, and the active release-branch/version-tag
  lifecycle and immutability rulesets;
- a Git tag-signing key through `user.signingKey`, with its matching public key
  registered as an SSH signing key (or GPG key, as applicable) for the
  authenticated GitHub administrator;
- .NET `10.0.301`, Flutter `3.44.6`, Dart `3.12.2`, Node `24.18.0`,
  Unity `6000.0.23f1`, MSVC `14.51.36231`, Windows SDK
  `10.0.26100.0`, Python 3.11 or newer, Git LFS, 7-Zip, tar, `jq`, `bash`
  (Git for Windows), WSL, and GitHub CLI;
- an activated local Unity license and the Robotopia build-2409 installation.

On Windows systems where `python` resolves to the nonfunctional Microsoft
Store alias, set `TOPIAFORGE_PYTHON` (or pass `-PythonPath`) to an absolute,
working Python 3.11-or-newer executable. Preflight executes a version probe; a
path existing on disk is not sufficient.

The shell release verifiers parse GitHub API responses with `jq` and stop
without it. `release-admin.ps1` resolves `jq` before any transaction phase
begins, so a missing `jq` fails immediately instead of part-way through a
release. If `jq` is not on `PATH`, the installation directories used by
`winget`, Chocolatey, and `%ProgramFiles%\jq` are searched, and the resolved
directory is prepended to `PATH` for the shell verifiers only. Install it with:

```powershell
winget install jqlang.jq
```

RC1 and every later production release require Authenticode. Before freezing
the release commit, pin the reviewed leaf certificate SHA-256 in
`signingIdentities.windowsCertificateSha256` and supply
`WINDOWS_CERTIFICATE_PFX`, `WINDOWS_CERTIFICATE_PASSWORD`, and an HTTPS
`WINDOWS_TIMESTAMP_URL`. The pinned value is the leaf certificate fingerprint,
not the PFX digest. Preflight rejects missing, zero, expired, non-code-signing,
or mismatched credentials. The launcher, CLI, and GameCompat extractor must
all carry valid RFC 3161-timestamped signatures from that exact certificate.
The detached handoff CMS also carries exactly one RFC 3161 timestamp token.
Verification binds its message imprint to the CMS signer, requires trusted
code-signing and TSA chains with the appropriate EKUs, and evaluates both
leaf certificates at the timestamp instant. A `VerifyOnly` resumption reads
only the frozen handoff, P7S, policy pin, and platform trust store; it does not
require the PFX, its password, or the timestamp endpoint.

The protected `release` environment has two narrowly separated secrets:

- `TOPIAFORGE_UPDATE_ED25519_PRIVATE_KEY_B64` is the release update-signing
  seed. After testing recovery, remove every plaintext local duplicate and
  confirm the protected secret independently before staging.
- `TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN` is a dedicated fine-grained PAT used
  only by the protected live-governance verification steps. Scope it to this
  repository with `Administration: read` and `Actions: read`; fine-grained
  tokens receive the required Metadata read permission implicitly. These cover
  the environments, immutable-release, and security-feature endpoints checked
  by `tools/verify-release-governance.sh` and
  `.github/scripts/audit_repository_governance.py`. It must have no write
  permission of any kind. Do not store a short-lived GitHub App installation
  token as this long-lived environment secret; an App-based design must instead
  store the App identity/key and mint a fresh installation token in-workflow.

The workflow continues to use its short-lived `GITHUB_TOKEN` for the
candidate-verification, attestation, and publication operations that actually
need workflow authority. Do not substitute the maintainer's broad interactive
GitHub CLI token for the audit secret.

The preflight opens the exact Unity project in batch mode to prove that the
local activation is usable. It never needs Unity email/password credentials.

> **Out of `0.1.0-rc.1`.** Linux is descoped from RC1 and returns in `0.1.0-rc.2`, so
> everything from here to the end of *Same-host WSL2/WSLg Proton evidence* is inert for
> this candidate. `release-admin.ps1` reads `release/release-policy.json` and skips the
> WSL build, the Proton acceptance, and their preflight checks whenever
> `TopiaForge-linux-x64.zip` is absent from `platformArchives`; re-adding it turns the
> whole path back on unchanged. The instructions are retained for rc.2.
>
> Note for rc.2: WSL2 on this host cannot reach a GPU Vulkan implementation. NVIDIA
> ships no Vulkan ICD for WSL2 and Ubuntu does not package Mesa's Dozen driver, so only
> software lavapipe is available, while Robotopia's Direct3D 12 renderer needs Vulkan
> through VKD3D. A venue that provides real Vulkan is required before Linux acceptance
> can be recorded; if it is not this Windows workstation, the `wsl2-wslg` execution
> environment in the evidence contract and the WSL-driven collection below must be
> reworked first.

Firmware virtualization must be enabled manually. Then install WSL2 and the
named distribution from an elevated terminal, rebooting when Windows asks:

```powershell
wsl --install --no-distribution
wsl --install --distribution Ubuntu-24.04
wsl --set-version Ubuntu-24.04 2
```

Install the pinned Linux toolchains, Git LFS, Flutter desktop prerequisites,
Steam, and Proton `10.0-4` inside that distribution: clang `18.1.3`, CMake
`3.28.3`, Ninja `1.11.1`, and GTK `3.24.41`. WSLg and a working virtual GPU
are required for the real Robotopia run. Preflight runs `git lfs fsck` and
rejects pointer-file checkouts on both Windows and WSL. All platform pins live
in `release/platform-toolchains.json`.

### Obtaining the exact pinned Proton build

`Proton 10.0-4` is pinned by Steam provenance, not by display name.
`tools/release/test-proton.sh` requires all of the following, and the display
name alone satisfies none of them:

| Pin | Value | Where it is checked |
| --- | --- | --- |
| Steam app id | `3658110` | `appid` in `steamapps/appmanifest_3658110.acf` |
| Steam build id | `21617411` | `buildid` in the same appmanifest |
| Depot id | `3658111` | depot block in the same appmanifest |
| Depot manifest id | `5413949673798237105` | `manifest` inside that depot block |
| Reported version | contains `10.0-4` | `proton --version` |
| Runtime tree | canonical SHA-256 | hashed before and after acceptance; it must not change |

Each appmanifest key must appear exactly once, the file must be a regular file
rather than a symbolic link, and the Proton executable must be the launcher at
the root of `steamapps/common/<installdir>` named by that same appmanifest.

The deterministic procedure is:

1. In the WSLg Steam client, enable Steam Play compatibility tools and install
   `Proton 10.0`. Installing through Steam is what writes the appmanifest; a
   bare depot download does not produce one.
2. Before using it, read `steamapps/appmanifest_3658110.acf` and confirm every
   value in the table above.
3. Run the preflight from inside the WSL distribution. It checks the same values,
   plus the WSL2/WSLg environment, without launching the game:

```bash
tools/release/test-proton.sh --preflight-only --repo "$repo" --source-sha "$sha" --version 0.1.0-rc.1 --game-dir "$gameDir" --game-build-id 2409 --proton-executable "$proton" --steam-root "$steamRoot" --compat-data-root "$compatData"
```

If Steam has moved `Proton 10.0` to a newer build, the appmanifest will carry a
different `buildid` and depot `manifest`. That is a stop condition: report it and
do not proceed. A newer runtime under the same display name is not equivalent,
because the recorded evidence claims this exact tree.

To recover the exact historical depot for comparison, use `steamcmd`:

```bash
steamcmd +login <account> +download_depot 3658110 3658111 5413949673798237105 +quit
```

That yields the pinned depot content under
`steamapps/content/app_3658110/depot_3658111` for a canonical-digest comparison
against the installed tree. It does not create an appmanifest, so it supplements
the Steam install rather than replacing it.

Never hand-edit the appmanifest to make the check pass. The `.acf` is plain text,
so an edited `buildid` or `manifest` would satisfy every assertion above while the
installed bytes remain a different runtime — which defeats the entire pin and
makes the recorded Linux evidence false.

`protonSourceCommit` (`e2becb87430ca3ff510d949d9e75fa9b401da489`) is verified as a
policy pin, not against the installed tree; it identifies the reviewed
Proton source revision for audit.

### WSL paths

Configure absolute paths inside the WSL distribution:

```powershell
$env:TOPIAFORGE_PROTON_EXECUTABLE = "/home/release/.steam/root/steamapps/common/Proton 10.0/proton"
$env:TOPIAFORGE_STEAM_ROOT = "/home/release/.steam/root"
$env:TOPIAFORGE_COMPAT_DATA_ROOT = "/home/release/.local/share/topiaforge/compatdata"
```

The equivalent command parameters are `-ProtonExecutable`, `-SteamRoot`, and
`-CompatDataRoot`. `-WslDistribution` defaults to `Ubuntu-24.04`;
`-GameDirectory` identifies the current Windows Robotopia installation and is
translated into its WSL path by the orchestrator.

macOS packaging remains source-tested as future-platform capability, but RC1
has no macOS archive, remote builder, Apple identity, notarization step, or
macOS handoff manifest.

## Windows Creator evidence

The scripted Windows run verifies the installed build-2409 marker, Unity
lifecycle, canonical live markers, and packaged `new mod` to `dev` journey.
The nine interactive Creator workbench cases cannot be inferred from those
markers, screenshots, artifact-directory presence, a manually supplied cycle
number, or two identical arbitrary files. They require an authorized
interactive build-2409 session driven by the acceptance harness.

### Producing the evidence

Run the harness from the frozen candidate checkout, then perform the nine
workbench workflows in-game while it tails the manager log:

```
topiaforge acceptance creator --creator-package <CreatorTools .topiaforgemod> --all
```

The harness issues an unpredictable 64-hex one-run challenge and writes it into
the CreatorTools config before launch. `CreatorAcceptanceRecorder` is inert
unless that challenge is present, so an ordinary play session cannot emit
evidence. Each case emits `TF-CREATOR|PASS|<challenge>|<case>` only when every
required workbench transition was actually observed; a case whose transitions
did not all occur simply never passes. The harness digests the real
`player_data.json.gz` save document before and after End Session — decompressed
first, because a gzip header embeds an mtime that would otherwise read as a
change — and digests the checkpoint cursor and `<id>_reached` flags separately.

Convert the resulting `creator-acceptance-result.json` and retained state
pre-images into the release descriptor and bundle:

```
tools/release/new-windows-creator-evidence.ps1 -AcceptanceResult <result.json> `
  -StateDirectory <output>/state -CaseEvidenceDirectory <case artifacts> `
  -SourceSha <sha> -Version <version> -WindowsArchive <zip> `
  -CanonicalEcosystemSha256 <sha> -OutputBundle <bundle> -OutputDescriptor <json>
```

The generator derives every claim from the acceptance result rather than from
artifact presence, and refuses any result that is incomplete, short of the
required cycles, or shows changed save or checkpoint bytes.

### What the release validator enforces

`release-admin.ps1` accepts only `release-windows-creator-evidence-v2` and
re-derives the embedded acceptance result's claims rather than trusting the
public descriptor. A descriptor is rejected unless it binds:

- the challenge and exact `lastRunSessionId`;
- the CreatorTools package `sourceSha256` and ordered critical-file receipts;
- the `acceptanceResultSha256` of the acceptance result embedded in the bundle;
- the source SHA, version, Windows archive digest/size, canonical ecosystem
  digest, Robotopia build, and exact case-inventory digest;
- typed per-case evidence produced by the native action, not file presence;
- the measured lifecycle cycle count and unchanged before/after save and
  checkpoint receipts from that same session.

The bundle additionally carries the declared persistence layout, so a future
game build that relocates or renames its persisted state fails closed instead of
silently reporting "unchanged". `tools/test-release-admin.ps1` fails the run if
spoofed-challenge, spoofed-session, spoofed-result-digest, wrong-package-receipt,
replayed prior-run, missing-case, extra-case, short-cycle, mutated-save, or
mutated-checkpoint evidence is accepted.

## Same-host WSL2/WSLg Proton evidence

The orchestrator, rather than an imported external descriptor, runs the exact
Linux archive through pinned Proton in the same workstation's WSL2
distribution. Configure the environment-backed absolute Linux paths documented
by `release-admin.ps1` for the Proton executable, Steam root, and dedicated
compat-data root. The runner creates a private wrapper that sets
`STEAM_COMPAT_DATA_PATH`, `STEAM_COMPAT_CLIENT_INSTALL_PATH`, and
`WINEDLLOVERRIDES=winhttp=n,b`, then uses only the packaged Linux CLI for the
release journey.

The generated descriptor is deterministic JSON and binds at least:

```json
{
  "schema": "release-proton-evidence-v1",
  "version": "0.1.0-rc.1",
  "targetSha": "0123456789abcdef0123456789abcdef01234567",
  "platform": "linux-proton",
  "executionEnvironment": "wsl2-wslg",
  "independentQa": false,
  "archiveSha256": "64-lowercase-hex",
  "archiveSize": 123456,
  "canonicalEcosystemSha256": "64-lowercase-hex",
  "gameBuildId": 2409,
  "protonVersion": "10.0-4",
  "protonRuntimeSha256": "64-lowercase-hex",
  "wineDllOverrides": "winhttp=n,b",
  "result": "pass",
  "suite": "full",
  "caseInventorySha256": "sha256-of-the-source-SHA-case-inventory-blob",
  "requiredCasesSha256": "sha256-of-the-sorted-required-case-set",
  "passedCasesSha256": "sha256-of-the-sorted-passed-case-set",
  "evidenceSha256": "sha256-of-the-retained-bundle",
  "evidenceSize": 654321
}
```

The runner hashes the complete canonical Proton runtime tree and the raw case
inventory blob from `targetSha`; it verifies every required case appears
exactly once, every result passes, the release journey package loads, and the
evidence bundle bytes match the descriptor. Do not put usernames, hostnames,
paths, timestamps,
credentials, or raw game logs in the deterministic descriptor. Both QA bundles
remain private local evidence; only their scrubbed digests enter the public
handoff. This is intentionally recorded as same-host, non-independent RC1
evidence.

## Staging and publication

Only a fully verified local build can enter `stage`. That phase creates or
verifies the signed annotated version tag, creates or resumes the exact draft
release, and uploads all catalog assets plus the two platform manifests, the
aggregate handoff manifest, and its detached
`release-handoff-v1.json.p7s` CMS signature.
The release author and every draft asset uploader must be the governance-pinned
`furroxide` user at immutable actor ID `221987073`; matching a mutable login
without the actor ID and `User` type is insufficient.
Existing assets are downloaded and byte-compared; replacement is forbidden.
An interrupted draft upload reported by GitHub as `state=starter` is the sole
exception: while the durable phase is still `built`, the orchestrator deletes
that exact asset ID and retries it. An `uploaded` byte mismatch always fails
closed.

`dispatch` first records a unique `release-admin-<32 lowercase hex>` request
ID in durable phase `dispatch-requested`. Immediately before every remote
workflow-dispatch call, it increments and durably writes
`finalizerDispatchAttempt`; there is no interval in which GitHub can accept an
unjournaled attempt. It then passes the same request ID to the protected
finalizer and binds the state to the one workflow run whose run name, version
tag, event, tag branch, and head SHA all match. Before trusting that run ID,
the orchestrator reads the repository-scoped Actions REST record and requires
the exact repository plus workflow path `.github/workflows/release.yml`; a
same-name workflow cannot substitute. The exact GitHub run ID is persisted
before the orchestrator waits for completion.

If an interruption leaves a journaled attempt without a run ID, `resume`
searches and waits through a bounded registration grace period. A run that
appears is bound without another dispatch. If no exact run becomes visible,
the command fails closed and instructs the administrator to resume later.
It never automatically redispatches a journaled request because GitHub's
`workflow_dispatch` API has no idempotency key. Never dispatch a replacement
run manually.

Approval of the GitHub `release` environment is the final human checkpoint.
While approval or execution is pending, `resume` finds and watches that exact
run. If it completed with `failure` or `cancelled`, `resume` uses GitHub's
rerun operation on the same run ID and verifies the rerun; it does not create
a second workflow-dispatch run. After approval, GitHub rechecks live release
governance, then verifies the tag, source, exact workflow/run/job provenance
for every required hosted check, draft, pinned CMS handoff signature, exact
timestamped Windows trust state, and QA evidence; generates update metadata, BOM,
SBOM, and checksums; creates a custom verifier attestation; publishes
automatically; and re-verifies the immutable release. Governance and candidate
identity are checked again immediately before publication.
The generated metadata names are the only assets permitted to identify
`github-actions[bot]` (actor ID `41898282`, type `Bot`) as uploader. If GitHub
returns performing-App metadata, it must identify GitHub Actions integration
ID `15368`. Catalog bytes, handoff manifests, and any local detached handoff
signature must continue to identify the pinned human staging principal.
Each hosted platform-verification record includes both the handoff digest and
the exact detached-P7S digest. The protected finalizer rehashes both draft
assets and rejects evidence from any other signature bytes.

GitHub does not support conditional requests for unsafe `PATCH` operations
unless an endpoint explicitly documents them, and the release-update endpoint
does not. The finalizer therefore holds the repository-wide publication
concurrency group, requires the exact sole-administrator governance policy, and
re-fetches the draft metadata and every asset immediately before its single
`draft:false` transition. Do not manually edit the draft after approving the
environment; this is the residual GitHub API limitation, not a recoverable
release step. See GitHub's
[conditional-request guidance](https://docs.github.com/rest/using-the-rest-api/best-practices-for-using-the-rest-api#use-conditional-requests-if-appropriate).

If a protected finalizer uploads policy-declared generated metadata and stops
before the single `draft:false` transition, the draft remains resumable.
Fetcher and local verification accept those exact generated names from the
pinned GitHub Actions bot, plus the pinned Actions App identity when GitHub
exposes it, while every admin-staged asset must still identify the pinned
human. Complete generated bytes are recomputed and compared; only an
incomplete Actions-owned generated upload may be repaired by the protected
publisher. No admin-staged starter or mismatched byte is replaceable.

The local phase changes to `published` only after the recorded run has
conclusion `success` and a second local verification proves that the exact
source SHA is now an immutable, non-draft prerelease with byte-identical
handoff assets. Running `dispatch`, `resume`, or `all` again from `published`
only re-verifies that run and release; it does not dispatch, rerun, upload,
rewrite state, or replace bytes. Any identity, byte, metadata, run, or release
mismatch fails closed.
