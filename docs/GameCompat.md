# Game Compatibility — detecting breaking changes in game updates

TopiaForge mods don't compile against `GameCode.dll`. Every hook into the game is **reflection by name** —
`Type.GetType("RobotBody, GameCode")`, `GetMethod("Damage")`, `Enum.ToObject(DamageType, (int)x)`,
`component.GetType().GetField("initialState")`. There are ~200 such bindings across the mods. Each is guarded
(`throwOnError:false` + `try/catch`), so when a game update renames, removes, re-signs, or **re-orders** one of
those symbols, the binding silently returns `null` and the feature **quietly stops working** — no crash, no build
error, no CI failure, no signal to the player. This subsystem turns that silent runtime rot into a loud, offline,
reviewable signal.

It is deliberately **separate from `SdkSurfaceTests`**, which only guards the SDK's *own* Unity-free contract
(the interfaces and enums in `TopiaForge.Mods.Abstractions`). That test never touches the game. This one does.

## The pieces

| Piece | Path | What it is |
| --- | --- | --- |
| Binding manifests | `bindings/<mod-id>.gamebindings.json` | One per mod. The declarative single source of truth for every game symbol that mod reflectively depends on. |
| Surface library | `src/TopiaForge.GameCompat.Surface` | Unity-free, GameCode-free core: manifest + snapshot models, canonical JSON, and the pure differ. Referenced by the extractor **and** the test harness. |
| Extractor tool | `src/TopiaForge.GameCompat.Extractor` | net10.0 console tool. Reads the real `GameCode.dll` via `MetadataLoadContext` (metadata only — no Unity, no code execution) and produces/verifies a surface snapshot. |
| Surface baseline | `baselines/gamecode.surface.baseline.json` | The checked-in, known-good snapshot of the exact game surface the mods use, captured from a real install. |
| Offline CI gate | `tests/TopiaForge.ModManager.Tests/GameCompatTests.cs` | Runs in the existing hand-rolled harness. Deterministic, no DLL needed. |

## A binding, and its match modes

Each binding declares how the mod *actually* resolves the symbol, because that decides what can honestly be
verified offline. Green-washing an unverifiable binding is worse than not having the tool.

| `matchMode` | Runtime pattern | What the tool checks |
| --- | --- | --- |
| `StaticFullName` | `Type.GetType("X, GameCode")`, then a member on that type | Full: type + member + signature exist. |
| `SimpleNameWalk` | `IsNamed`/`FindComponent` matching `Type.Name` up the base chain | Some type with that simple name still exists (cannot prove *which*). |
| `PredicateOverload` | `GetMethods().First(m => m.Name==… && arity && some params)` | An overload matches the *pinned* parameter positions (unconstrained ones ignored, exactly like the runtime predicate). |
| `DynamicInstance` | member off a runtime `instance.GetType()` | Soft signal: the declaring type is author-*inferred*, so a miss is a **warning**, never a hard failure. |
| `ValueContract` | a magic string written into a game field (`Mode = "SelectedFile"`) | If the field is an enum, the token is still a valid member; otherwise recorded as an uncheckable contract. |
| `Uncheckable` | dynamically-built type strings, invoke-only contracts | Nothing — but it is **counted** so coverage is honest. |

Enum bindings come in two flavours:
- `Enum.Parse(type, "Standby")` resolves by **name** → omit `expectedOrdinal`; only the name's presence is checked.
- `Enum.ToObject(type, (int)modEnum)` resolves by **ordinal** → set `expectedOrdinal`; the differ asserts the game
  enum still has that member **at that ordinal**. This is the crux of ordinal-drift: a reordered enum silently
  corrupts every `(int)` cast, and this is the one thing a one-sided "does the name exist" check would miss.

`criticality` (`critical` / `degraded` / `optional`) drives severity: a broken **critical** binding is an Error
(fails the gate and `verify`), **degraded** is a Warning, **optional** is Info.

## Why the offline gate is not circular

The subtle failure mode of a scheme like this is a gate that checks the manifest against a baseline that was itself
generated *from* the manifest — proving nothing. We avoid that: the extractor snapshots the **complete member
surface** of every referenced type (all of `Health`'s methods, not just `Damage`), captured independently from the
real DLL. So when `GameCompatTests` asks "does `Health.Damage(Single, DamageType, String)` exist?", the answer comes
from the game's own metadata, not from an echo of the manifest. A manifest that declares a critical symbol the real
surface never had fails the build.

## Two modes: always-on offline gate, best-effort live check

**Offline gate (CI, every test run, no game install).** `GameCompatTests.Run()` loads the checked-in manifests +
baseline and asserts: every manifest is valid; the baseline is canonical (re-serializes to itself — catches
hand-edits) and complete (nothing left `unreadable`); every declared verifiable binding resolves against the
baseline surface; and the game `DamageType` ordinals still line up with the SDK's `RobotDamageType`. This catches
**manifest drift** and **SDK↔game enum divergence** with zero external inputs.

**Live check (where a game install exists).** `gamecompat verify` extracts a fresh snapshot from the *installed*
`GameCode.dll` and (a) resolves the manifests against it — catching a **new build that dropped a symbol** — and
(b) diffs it against the baseline to show exactly what changed. It exits non-zero when a critical binding is broken,
and skips cleanly (exit 0, "no game install detected") where there is no install. This is the mode that catches a
brand-new game update; the offline gate cannot, because CI has no DLL.

## CLI

```
# resolve every binding against the installed game (+ diff vs baseline); exit 1 if a critical binding is broken
dotnet run --project src/TopiaForge.GameCompat.Extractor -- verify

# offline source-vs-manifest drift check (no DLL): every "X, GameCode" literal must be a declared binding
dotnet run --project src/TopiaForge.GameCompat.Extractor -- audit

# snapshot the current surface to a file
dotnet run --project src/TopiaForge.GameCompat.Extractor -- extract --out surface.json
```

Managed-dir resolution order: `--managed <dir>`, `$RobotopiaManagedDir`, `$RobotopiaGameDir\Robotopia_Data\Managed`,
then the default launcher install path.

## The baseline-refresh ritual (after an intentional game adaptation)

A baseline bump means "we accept this new game surface as the known-good". It is a **reviewed act**, never a
rubber stamp:

1. The game updated and you adapted the affected mods (new symbol names/signatures in the bridges).
2. Update the affected `bindings/<mod-id>.gamebindings.json` to match.
3. Run `gamecompat baseline`. It **prints the surface diff vs the previous baseline** — review it as the changelog
   of what the game changed — then writes the new baseline. It **refuses** to write a partial capture (any
   `unreadable` type), so a baseline poisoned by an incomplete Managed dir can't be committed.
4. Re-run the tests (`dotnet run --project tests/TopiaForge.ModManager.Tests`) — the gate should be green.
5. Commit the manifest changes **and** the baseline **and** the mod code together. The old baseline stays in
   history, so `git diff` of two baselines is itself a record of how the game's reflected surface moved.

## Adding a binding when a mod takes a new game dependency

1. Add the `Type.GetType`/`GetMethod`/… call in the mod as usual.
2. Add a binding to that mod's `bindings/<mod-id>.gamebindings.json` (pick the right `matchMode`, `criticality`,
   and — for callable members — the parameter discriminator).
3. Run `gamecompat audit` — it fails if a `", GameCode"` literal has no matching manifest binding, so you can't
   forget. (Genuinely-dynamic bindings the scanner can't see go in `bindings/<mod-id>.audit-allow.json`.)
4. Refresh the baseline (ritual above) so the new binding has a known-good entry.

## Known limits (by construction, stated honestly)

- Members resolved off a runtime instance type (`DynamicInstance`) can only be checked against an *inferred*
  declaring type, so they are warnings, not hard failures.
- Plain-string value contracts (a discriminator that isn't an enum) are `Uncheckable` offline — counted, not proven.
- `MetadataLoadContext` reads metadata only; it cannot prove a constructor is actually invocable beyond
  shape/accessibility, and cannot follow a runtime `SampleAt()→Sample→Hit` chain. Those are declared explicitly by
  the manifest author, not machine-derived.
- The live check needs both a game install and this tool present, so for most end users the update-time signal
  arrives through the launcher (see the launcher Diagnostics integration), not the raw CLI.
