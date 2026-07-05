# Publishing Your Mod

End-to-end: from a working mod to an entry players install from the launcher. The official registry is a
folder of entry files in `furroxide/quantum-works` — **you host the package file**, the registry records
where it lives and what its bytes hash to. Prefer running your own source? See
[RegistryFormat.md](RegistryFormat.md) — both can coexist.

The bar to clear: the official registry requires a **zero-finding manifest** — errors *and warnings*.
`robotopia registry add-entry` refuses a package with any validation finding, and CI re-checks on the PR.
Ids with reserved prefixes (`robotopia.*`, `sample.*`, `quantumworks.*`) or colliding with a first-party id
are rejected.

## 1. Validate to zero findings

```sh
robotopia check package .
```

Fix every line it prints — warnings included. Typical last-mile fixes: an SPDX-style `license`, only known
[permission values](Modding.md#permissions), a real semver `version`. If your mod has dependencies, add
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

## 5. Create the registry entry

```sh
robotopia registry add-entry dist/yourname.firstmod-1.0.0.robotopiamod --url https://github.com/you/firstmod/releases/download/v1.0.0/yourname.firstmod-1.0.0.robotopiamod --changelog "Initial release."
```

(`--changelog @notes.md` reads from a file; `--output` overrides the target directory.) The command computes
the sha256, re-validates the manifest against the zero-finding bar, refuses a version that is already
published (releases are immutable — bump instead), prepends the version to `registry/<id>.json`, and prints
the fork/PR steps.

## 6. Open the PR

Fork `furroxide/quantum-works`, add your `registry/<id>.json`, and open a pull request against `main`.

What CI validates on the PR:

- filename equals the lowercase mod id;
- the id is not reserved (`robotopia.*`, `sample.*`, `quantumworks.*`) and doesn't collide with a
  first-party id;
- the manifest has zero validation findings; the version is semver; the download URL is https;
- the hosted file's sha256 matches `packageSha256` (downloads capped at 512 MB);
- the inline `manifest` equals the `robotopia.mod.json` inside the package;
- every required dependency id resolves in the merged registry.

Preflight the same checks locally:

```sh
robotopia registry validate --only registry/yourname.firstmod.json
robotopia check package dist/yourname.firstmod-1.0.0.robotopiamod --entry registry/yourname.firstmod.json
```

(`registry validate --offline` skips the download and checks structure only.)

## 7. Merge = live

A Pages deploy runs on every `registry/**` push to `main`, so your entry is live in the official index the
moment the PR merges — no release tag needed. Launcher users see the mod under the official source
(`robotopia.official`).

## Updating a published mod

Releases are immutable — an update is a new version:

```sh
robotopia mod bump minor        # or major | patch — validated increment
robotopia pack
robotopia check package dist/yourname.firstmod-1.1.0.robotopiamod
# host the new file at its own URL, then:
robotopia registry add-entry dist/yourname.firstmod-1.1.0.robotopiamod --url <new url> --changelog "..."
```

Open a PR with the updated `registry/<id>.json` — the new version is prepended and older versions remain in
the entry's history. (`mod bump` drops any pre-release/build suffix, with a note.)

## Self-hosting as an alternative (or in addition)

You don't need the official registry to distribute a mod: build an index with
`robotopia registry index --dir packages`, host it on any static host, and have players add the URL as a
package source. [RegistryFormat.md](RegistryFormat.md) has the walkthrough and the format spec. The
launcher merges all configured sources and keeps the highest version per mod id.
