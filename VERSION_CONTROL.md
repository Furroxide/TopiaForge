# Version Control

TopiaForge uses git with Git LFS and Unity Smart Merge support. This keeps the
launcher and C# source code normal git content while protecting bundled runtime
binaries, durable assets, and Unity YAML files from common Unity+git mistakes.

## What is configured

- Root `.gitignore` keeps generated build output out of source control without
  hiding source directories such as `apps/topiaforge_cli/bin/`.
- Root `.gitattributes` normalizes text, routes durable binary assets through
  Git LFS, and marks Unity YAML files for `UnityYAMLMerge`.
- `templates/TopiaForge.UnityWorldTemplate/.gitignore` and `.gitattributes`
  carry the same git hygiene into newly created Unity world projects.
- `templates/TopiaForge.UnityWorldTemplate/ProjectSettings` pins Unity asset
  serialization to Force Text and version control mode to Visible Meta Files.
- `.githooks/commit-msg` strips AI co-author trailers, and the other hooks
  forward to Git LFS because this repo uses `core.hooksPath=.githooks`.

`third_party/BepInEx` is intentionally tracked as a bundled runtime asset and is
covered by `THIRD_PARTY_NOTICES.md`. Generated packages and distributables under
`dist/` are ignored and can be rebuilt with the packaging scripts.

## One-time setup on a fresh clone

Run these from the repository root:

```sh
git lfs install --local
git config core.hooksPath .githooks
```

If you keep your own hooks directory, point `core.hooksPath` at it instead and have it forward to
`.githooks` so the Git LFS integration still runs. `tools/bootstrap-dev.ps1` leaves an existing
setting alone rather than replacing it.

Optional Unity Smart Merge setup, with the Unity editor path adjusted for your
installed version:

```sh
TOOL="C:/Program Files/Unity/Hub/Editor/6000.0.23f1/Editor/Data/Tools/UnityYAMLMerge.exe"
git config merge.tool unityyamlmerge
git config mergetool.unityyamlmerge.trustExitCode false
git config mergetool.unityyamlmerge.keepBackup false
git config mergetool.unityyamlmerge.cmd "\"$TOOL\" merge -p \"\$BASE\" \"\$REMOTE\" \"\$LOCAL\" \"\$MERGED\""
git config merge.unityyamlmerge.name "Unity SmartMerge (UnityYAMLMerge)"
git config merge.unityyamlmerge.driver "\"$TOOL\" merge -p %O %B %A %A"
git config merge.unityyamlmerge.recursive binary
```

## Working rules

- Commit Unity assets and their `.meta` files together.
- Add new binary extensions to `.gitattributes` before committing files of that
  type. LFS rules are not retroactive.
- Resolve scene, prefab, and Unity asset conflicts with `git mergetool` after
  configuring UnityYAMLMerge.
- Keep `security/` in `.git/info/exclude` for local-only research material; do
  not move that exclusion into tracked repo files.
