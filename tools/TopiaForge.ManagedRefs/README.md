# Robotopia managed-reference tool

This independently buildable C# tool restores the proprietary Robotopia managed assemblies required to compile the
game-side TopiaForge projects. It has no Unity, GameCode, or solution-project dependency, so it can run before the
rest of `TopiaForge.slnx` is restored or built.

```powershell
dotnet run --project tools/TopiaForge.ManagedRefs/TopiaForge.ManagedRefs.csproj -c Release -- --help
```

The default `auto` source tries the public build archive pinned by
`.github/robotopia-game-build.json`, then uses the bundled source when both bundled variables are configured.
`--source public` and `--source bundled` disable that fallback. Use `--probe` for a non-mutating availability check,
`--cache-key-only` for an Actions cache key, and `--write-local-props` to write ignored
`Directory.Build.local.props` atomically.

`--require-latest` is always an online public release gate, even with the bundled source. It verifies the latest
manifest against both pinned platform archives and probes both endpoints before consulting a cache. Omit it for an
offline or bundled-only restore.

## Environment contract

| Variable | Purpose |
| --- | --- |
| `ROBOTOPIA_REFS_SOURCE` | Default source: `auto`, `public`, or `bundled`. An explicit option wins. |
| `ROBOTOPIA_REFS_SOURCE_PLATFORM` | Public archive platform override. |
| `ROBOTOPIA_REFS_CACHE` | Cache-root override. |
| `ROBOTOPIA_REFS_URL` | Credential-free HTTPS URL for the bundled ZIP. |
| `ROBOTOPIA_REFS_SHA256` | Exact bundled ZIP SHA-256. |
| `ROBOTOPIA_REFS_TOKEN` | Optional bearer token sent only to the bundled endpoint. |
| `RUNNER_TOOL_CACHE` | CI cache-root fallback. |
| `RUNNER_OS` | Platform segment used by `--cache-key-only`. |
| `GITHUB_OUTPUT` | Receives `key=<cache key>`. |
| `GITHUB_ENV` | Receives `RobotopiaManagedDir=<absolute path>` after restore. |

Public archives use 7-Zip; bundled archives use the .NET ZIP reader. Downloads reject redirects, credentials in
URLs, queries, and fragments. The tool checks SHA-256 before and after extraction, validates all 20 required PE
assembly identities, and promotes a complete staging directory into the cache under an interprocess lock. Failed or
interrupted extraction never becomes a visible cache entry.

Run its dependency-free regression harness with:

```powershell
dotnet run --project tests/TopiaForge.ManagedRefs.Tests/TopiaForge.ManagedRefs.Tests.csproj -c Release
```
