# Publishing Your Mod

End-to-end: from a working mod to a registry that players can add to the launcher. Community submissions to the
Robotopia official registry are **closed for the initial release** while namespace ownership, moderation, malware
response, revocation, appeals, and installed-user response are defined. A merge to `registry/**` does not publish a
community package. Do not describe a package as officially reviewed or endorsed.

Self-hosting uses the same format-version-1 contracts, HTTPS/integrity rules, and zero-finding publication bar planned
for the official service. You host both the package and static index; players add the index URL as a package source.
See [RegistryFormat.md](RegistryFormat.md) for the complete wire format.

## 1. Validate to zero findings

```sh
robotopia check package .
```

Fix every line it prints — warnings included. Typical last-mile fixes: a valid SPDX expression and declared license
file, only known [permission values](Modding.md#permissions), explicit author identity, and a real SemVer `version`.
If your mod has dependencies, add
`--resolve` to dry-run resolution against your configured sources.

## 2. Pack

```sh
robotopia pack
```

Builds the project and writes `dist/<id>-<version>.robotopiamod`
([ModPackaging.md](ModPackaging.md) documents exactly what goes in).

## 3. Verify the package

```sh
robotopia check package dist/yourname.firstmod-1.0.0.robotopiamod
```

Validates the packed manifest and prints `sha256=<hex> (<size> MB)` — the hash the registry will pin.

## 4. Host the file

Upload the `.robotopiamod` to a **stable https URL**. A GitHub Release asset on your own repo works well.

**Never replace a published file.** The registry pins its sha256; changed bytes fail every install (and the
registry CI). Ship fixes as a new version instead.

## 5. Build the self-hosted registry

```sh
robotopia registry index --dir dist --output site/registry --base-url https://example.invalid/robotopia/registry/
```

The builder revalidates each package, computes SHA-256 and size, rejects conflicting duplicate id/version pairs, and
writes a deterministic static index. Replace the example base URL with the HTTPS directory where package assets will
live. Local/LAN-only sources can omit `--base-url` and keep the generated relative URLs.

## 6. Upload and verify

Upload `site/registry` and the exact `.robotopiamod` bytes to a static HTTPS host. Fetch the published index and package
from a clean machine, then verify the package again using its public URL and expected hash.

Your publication gate should enforce:

- zero schema and semantic findings for every published version;
- absolute HTTPS URLs with no credentials, query, or fragment;
- hosted SHA-256, byte length, and inline/package manifest equality;
- dependency resolution against the complete index;
- immutable history: never delete or rewrite a published version;
- conflicting duplicate id/version bytes are rejected.

Validate the output locally before upload:

```sh
robotopia registry validate --offline site/registry/index.json
robotopia check package dist/yourname.firstmod-1.0.0.robotopiamod
```

Add the resulting HTTPS index under **Settings → Package Sources** and exercise install, update, dependency preview,
hash failure, and rollback before sharing it.

## Updating a published mod

Releases are immutable — an update is a new version:

```sh
robotopia mod bump minor        # or major | patch — validated increment
robotopia pack
robotopia check package dist/yourname.firstmod-1.1.0.robotopiamod
# host the new file at its own URL, then:
robotopia registry add-entry dist/yourname.firstmod-1.1.0.robotopiamod --url <new url> --changelog "..."
```

Rebuild and atomically replace the static index only after the new package is available. Keep all older version
records and bytes reachable; never overwrite an existing asset. (`mod bump` drops any prerelease/build suffix, with a
note.)

## Official submissions

The repository retains entry-validation and append-only history tooling so governance can be implemented without a
format migration. Until the project publishes an ownership/moderation policy and explicitly reopens submissions, do
not open a community registry PR; it will be rejected with a submissions-closed diagnostic.
