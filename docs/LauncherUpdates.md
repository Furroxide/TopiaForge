# Launcher updates

TopiaForge `1.0.0-rc.1` introduces signed whole-package updates so a later
candidate can validate an installed `rc.1` to `rc.2` upgrade. Updates are
explicitly confirmed, never silent, and never elevate privileges.

## Discovery and trust

The launcher reads only the public
`furroxide/TopiaForge` GitHub Releases API. Draft releases are ignored. Stable
channel users accept stable releases; beta users accept stable or prerelease
releases. Prerelease builds default to beta checks. Startup checks use a
persisted cooldown, and Settings provides a manual **Check now** action.

Every update release contains:

- `topiaforge-update-v1.json`, the exact UTF-8 signed payload; and
- `topiaforge-update-v1.json.sig`, an Ed25519 signature sidecar.

The payload identifies the product, SemVer, tag, channel, minimum updater
version, release URL, and the exact immutable platform set in release policy
(Windows x64 and Linux x64 for RC1). Each archive
record includes its GitHub URL, SHA-256, byte size, entry count, expanded size,
and install layout. The sidecar names its public key by SHA-256-derived key ID.
The initial embedded key is `ed25519:26229e3d2b54e81c`.

Signature verification happens before JSON payload parsing. The verified
version, tag, channel, release URL, asset URL, and asset size must then agree
with GitHub. Only a strictly newer compatible SemVer is offered. A persisted
highest-seen version rejects a signed replay to an older candidate.

`manual-releases.json` remains stable-only, format 2, and `manualOnly: true`.
Prerelease discovery does not modify the Pages feed.

## Download and staging

The launcher streams the selected archive to a new bounded partial file. It
uses fixed HTTPS GitHub endpoints, bounded redirects, response and total
timeouts, a signed size limit, and incremental SHA-256 verification. Failures
remove the partial file and redact CDN query strings from user-visible errors.

Extraction rejects traversal, absolute and device paths, links outside the
archive root, nested links, special files, duplicate paths, case and Unicode
collisions, privileged modes, excessive entries, excessive individual files,
and excessive expansion. Only sanitized executable bits are preserved.
Windows and Linux require a complete portable root. macOS requires one
`TopiaForge.app` bundle with the embedded runtime payload. A layout the launcher
cannot safely replace falls back to the verified release page.

## Installation transaction

The packaged CLI acts as an external helper:

1. Wait for the launcher process to exit.
2. Journal a pre-mutation phase.
3. Rename the current complete installation to a same-volume backup.
4. Rename the staged complete installation into place.
5. Launch the new launcher with a one-time health nonce and marker path.
6. Commit only after the new launcher renders its first frame and writes the
   matching marker.

The journal is written atomically with a previous-copy fallback. Pre-mutation
states let recovery distinguish an operation that did not start from a rename
that completed before its next journal write. If startup fails, the helper
terminates the launched process when known, preserves the failed installation,
restores the backup, and relaunches the prior launcher. Startup recovery is
idempotent and does not launch a duplicate process.

The helper replaces only user-writable portable installs and app bundles. It
does not request administrator privileges. Package-manager, read-only, or
otherwise unsupported layouts remain manual.

## Key custody

Only `release/update-keys.json` and the matching embedded public key are
committed. The private seed is stored as
`TOPIAFORGE_UPDATE_ED25519_PRIVATE_KEY_B64` in the protected GitHub `release`
environment. Release jobs never print the seed.

The owner must retain an encrypted recovery copy before temporary key material
is removed. Loss of every private-key copy prevents installed `rc.1` launchers
from trusting `rc.2`; a replacement key cannot silently recover that trust.
Compromise requires an incident response, release halt, and a recovery path
appropriate to the already-installed trust root.

## `rc.2` validation procedure

Before creating `1.0.0-rc.2`:

1. Reuse the existing Ed25519 update key; do not rotate it for this test.
2. Confirm release policy still forbids every unsigned/ad-hoc code-signing
   exception.
3. Require Authenticode signing and timestamping for Windows.
4. If macOS is added to that release, require Developer ID signing,
   notarization, and stapling before adding its archive to policy.
5. Bump every catalogued product/component/package version and the launcher
   build constant.
6. Build all archives from the exact protected release SHA.
7. Generate and independently verify the signed update payload and sidecar.
8. Install the immutable public `rc.1` archive on clean Windows and Linux
   hosts.
9. Use the in-app beta check, download, confirmation, helper swap, relaunch,
   and health handshake to reach `rc.2`.
10. Repeat with an injected startup failure and retain evidence that `rc.1`
    was restored with the failed `rc.2` package preserved.

The `rc.2` release is blocked if any platform uses the `rc.1` exception, the
signature is not accepted by an installed `rc.1`, the helper elevates, rollback
fails, or the final package differs from the signed inventory.
