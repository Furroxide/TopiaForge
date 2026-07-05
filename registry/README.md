# Community Mod Registry

This folder is the official QuantumWorks mod registry for community mods. Each
file is one mod: `registry/<id>.json`, where `<id>` is the lowercase mod id.
CI merges these entries with the first-party packages into the public index at
`https://furroxide.github.io/quantum-works/registry/index.json`, which the
launcher reads out of the box.

## Submitting your mod

1. Pack it: `robotopia pack` (see `docs/PublishingYourMod.md` for the full
   walkthrough).
2. Host the `.robotopiamod` file at a stable https URL — a GitHub Release
   asset on your own repository works well. Never replace a published file;
   the registry pins its SHA-256.
3. Generate your entry:

   ```
   robotopia registry add-entry dist/your.mod-1.0.0.robotopiamod --url <download url> --changelog "First release"
   ```

4. Fork this repository, add the generated `registry/<id>.json`, and open a
   pull request.

## What CI checks

`robotopia registry validate` runs on every PR (you can run it locally too):

- the file name matches the lowercase mod id, and the id is not reserved
  (`robotopia.*`, `sample.*`, `quantumworks.*`) or taken by a first-party mod;
- the manifest passes validation with **zero** findings (warnings included);
- the download URL is https and serves bytes matching `packageSha256`
  (512 MB limit);
- the inlined manifest matches `robotopia.mod.json` inside the hosted package;
- required dependencies exist in the merged registry.

## Updating a released mod

Bump the version (`robotopia mod bump`), repack, host the new file, re-run
`registry add-entry` (it prepends the new version and keeps your history), and
open a PR. Entries go live on merge — no release tag needed.
