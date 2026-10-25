---
title: Diagnostics
description: Resolve stable TopiaForge SDK, restore, and development-loop diagnostics.
---

# Diagnostics

TopiaForge errors include a stable code, a plain-language cause, a remediation, and a link to this
reference. Preserve the code when reporting a problem; wording may improve between releases.

## SDK and build diagnostics

### TF1001

**Native API in a safe project.**

The project directly references a Robotopia implementation, Unity implementation, or patching API.
Use the matching `IModContext` service or specialist module. Only an intentionally advanced package
should add `interop-unity` and accept its unstable compatibility policy.

### TF1002

**Unsupported target framework or missing SDK feed.**

Safe mods target `netstandard2.1`. Keep the generated project settings and run
`topiaforge restore` to seed the release-local NuGet source. If the feed is missing, restore it from
the same TopiaForge release rather than adding a source-checkout path.

### TF1003

**Copied SDK assembly or restore failure.**

Framework contract assemblies are reference-only and supplied by the loader. Remove copied SDK
DLLs from the project/package. Keep exact `PackageReference` entries, then rerun
`topiaforge restore` and inspect the first NuGet error if restore still fails.

### TF1004

**Module dependency missing.**

The project references a specialist contract assembly without its manifest/runtime pair. Run
`topiaforge mod add <module>`, then `topiaforge restore`. This adds both the exact contract package
and its root `dependencies` entry; an intentional nonblocking integration may instead use the
root `optionalDependencies` map. Text in descriptions or `x-*` metadata does not count. Commit
`packages.lock.json` and `topiaforge.sdk.lock.json` after restore. Multiplayer projects must also run
`topiaforge mod sync multiplayer` and commit `topiaforge.multiplayer.lock.json`.

### TF1005

**Retired pre-V1 API.**

The project uses an API removed by the V1 reset. Follow the replacement in the analyzer message:
derive from `TopiaForgeMod`, use typed context properties, and use dependency-scoped extensions.
There is no compatibility shim.

### TF1006

**Required capability missing.**

The project references an API whose risk must be disclosed by a canonical root manifest
capability. `TopiaForge.Mods.Interop.Unity` requires `unsafe-native` in the root `capabilities`
array. Remove the interop package and use safe SDK services when possible; otherwise add the
capability and review the unstable compatibility and full-process-trust implications. Matching
text in descriptions or `x-*` metadata does not count.

### TF1007

**Loader-owned renderer referenced by a safe project.**

`TopiaForge.Mods.UnityUi` is an internal Robotopia-runtime renderer, not an authoring package.
Remove the reference and express HUDs, windows, fullscreen tools, modals, toasts, and accessibility
preferences through `Context.Ui`. Declaring `unsafe-native` does not authorize coupling a mod to
the loader-owned renderer.

### TF1008

**Blocking wait on an SDK task.**

The code calls `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` on a task. Asynchronous SDK work —
asset bundles, prefabs, custom-world content, scene loads, multiplayer commands — is driven by the
game's own frame loop and completes on the main thread. Blocking that thread stops the frame loop that
would have completed the task, so the task never finishes and the game hangs with no recovery but
ending the process.

For a multiplayer command, call the generated `Submit...` method instead of waiting on
`Submit...Async`. It hands the host's answer to a callback on the main thread, never once the mod has
stopped, and for the player whose action submitted the command, as the code after an `await` acts:

```csharp
SubmitAddScore(new AddScoreRequest { Amount = 5 }, answer =>
{
    if (!answer.Result.Succeeded) ShowRefusal(answer.Result.ErrorMessage);
});
```

The message names the method to call, and the IDE's quick fix rewrites the wait for you: the
statements that read the confirmation move into the callback. It offers no fix where moving them would
change what they do, such as a `return` or an `await` after the wait, or a local they assign that code
after them reads. See
[Multiplayer](Multiplayer.md#submit-a-command-and-read-its-answer) for the answer's rules.

For other SDK work, use `PendingOperation<T>` — the SDK primitive for exactly this. It starts the work,
polls it, and never waits. It also surfaces a result that arrives *after* you cancelled or timed out, so
game objects the SDK still handed back are released on the main thread instead of leaking:

```csharp
private readonly PendingOperation<IAssetBundle> load = new PendingOperation<IAssetBundle>();

protected override void OnLoad()
{
    Context.Events.SubscribeUpdate(OnUpdate);
    load.Begin(
        token => Context.Assets.LoadBundleAsync("content/level.bundle", token),
        Context.Lifetime.StoppingToken,
        (float)Context.Time.Frame.ElapsedTime);
}

private void OnUpdate(float deltaTime)
{
    switch (load.Poll((float)Context.Time.Frame.ElapsedTime, timeoutSeconds: 30f, out var result))
    {
        case PendingOperationState.Completed when result.TryGetValue(out var bundle):
            Use(bundle);
            break;
        case PendingOperationState.Abandoned when result.TryGetValue(out var orphan):
            orphan.Dispose();
            break;
        case PendingOperationState.TimedOut:
            ShowLoadFailed();
            break;
    }
}
```

`Context.Time.Frame.ElapsedTime` counts unscaled seconds since TopiaForge's gameplay services started, so a
paused or slowed game still times out; it is a `double`, and the operation's clock is a `float`.

A hand-rolled `IsCompleted` poll is still accepted by this rule, but `PendingOperation<T>` is the
supported shape: cancellation, timeout, late-result release, and restart-while-draining are all
already handled and covered by tests.

Awaiting the task never blocks, so `await` is fine too. In multiplayer it is the better choice for work
that a player's own action starts, such as a click that loads a prefab and then spawns it: the code after
the `await` keeps the player's action, while a result polled in a later Update counts as a script. See
[The player's own actions](Multiplayer.md#the-players-own-actions).

This rule applies to every mod project, including one that sets `TopiaForgeSafeProject` to `false`:
opting into the unstable interop package does not move the code off the game loop. Test projects
(`IsTestProject`) are exempt.

### TF1009

**Input edge read in FixedUpdate.**

The code reads `WasPressed` or `WasReleased` in a handler passed to `SubscribeFixedUpdate`. The input
service samples input once a frame, before Update, and an edge holds for that one frame. FixedUpdate
runs zero or more times a frame on its own clock, so it misses presses when the game runs fast and
repeats them when it runs slow. In multiplayer only a mod's Update and LateUpdate carry the player's
input as the player's own action, so what the handler does counts as a script there.

Act on the edge in Update or LateUpdate. When the response has to run in FixedUpdate, such as a force
on the local player, note the press in Update and use it there:

```csharp
private IInputAction jump = null!;
private bool jumpRequested;

protected override void OnLoad()
{
    jump = Context.Input.RegisterAction(
        new InputActionDefinition("jump", "Jump", new[] { InputBinding.Key("Space") })).Value!;
    Context.Events.SubscribeUpdate(_ =>
    {
        if (jump.WasPressed) jumpRequested = true;
    });
    Context.Events.SubscribeFixedUpdate(_ =>
    {
        if (!jumpRequested) return;
        jumpRequested = false;
        ApplyJumpForce();
    });
}
```

Held input (`IsHeld`) and analog values (`Value`) hold for the whole frame, so reading them in
FixedUpdate is fine and is not reported. Nor is an edge read in code the handler only hands on to run
later, such as an Update or scene subscription or a `Context.Scheduler` callback, written in place,
kept in a local or written as a local function, since that runs on the frame's own clock. A spawn, a
transform change or a destroy that answers the press belongs in Update, where it counts as the player's
own action.

The rule follows a handler written in place or kept in a local first, a handler method wherever it is
declared, and the local functions either one calls. It does not follow the handler into other methods
it calls, or into a handler kept in a field. The rule is a warning, applies to every mod project like
TF1008, and exempts test projects.

### TF1010

**SDK API needs a newer loader (error).**

An API marked `[RequiresTopiaForge("0.2.0")]` needs a manifest whose
`supportedLoaderVersionRange` has a lower bound of at least `0.2.0`. The diagnostic names the API,
its required version and the admitted range. Raise that field and `supportedSdkVersionRange` to
the releases the mod actually supports. SemVer prereleases sort before the corresponding release;
`>=0.2.0-rc.1` still admits loaders older than `0.2.0`. An unbounded or unreadable floor cannot prove
compatibility and reports the error when a marked API is used. Ordinary manifest validation still
reports invalid or missing fields.

The IDE fix updates both fields only when each is a simple `>=A <B` range and the required version
stays below both ceilings. It preserves a stricter existing floor. Exact pins, wildcards, unbounded,
reordered, repeated and other comparator shapes receive no fix; edit those intentionally. The fix
does not widen a ceiling or invent missing fields. When the range's ceiling is below the API too, as
in `>=0.1.0-rc.1 <0.2.0`, raising the floor alone would leave an empty range, so the message names
both fields and bounds that admit the API, such as `">=0.2.0 <0.3.0"`.

Checks read attributes from referenced assembly metadata, including a containing type's requirement.
They cover method calls and method groups, helper bodies in this compilation, property reads and
writes (including marked accessors, null-conditional assignments and attribute named arguments),
indexers (including `?[]`), events, fields and constants, constructors, attributes and their
constant arguments, generic arguments and `nameof`. An override or an explicit interface
implementation of a marked member is checked too, because it only runs on a loader that has the
member. `nameof` and constant uses are intentionally checked even when they emit no runtime call: the
declared SDK floor also describes source support. Documentation comments are not code, so a `cref`
is not checked. Reflection, dynamic binding and the bodies of precompiled third-party helpers are not
inspected; their authors must propagate their required loader floor. Generated source and test
projects retain the analyzer's existing exemption. The check also applies to interop projects.

A project without its own `topiaforge.mod.json`, such as a service mod's contracts project, has no
floor to raise, so TF1010 does not run there. When such a library uses a newer API, raise the floor
of the mod that ships it.

Compiler-inserted calls without an explicit symbol reference, such as an omitted base-constructor
initializer, operators, conversions and `foreach`/`await` protocols, are outside this syntax check,
as is a method that implements a newer interface member implicitly, without naming it. Raise the
manifest floor manually when relying on a newer member through one of these forms.

SDK authors: see [how to mark additions](CompatibilityPolicy.md#version-gated-sdk-additions).

### TF1101

**Interop references unavailable.**

The explicitly unstable interop package requires Robotopia managed reference assemblies. Install
Robotopia and run `topiaforge restore`, or remove the package and use the safe SDK.

### TF1102

**NuGet resolved another SDK version than the pin.**

`topiaforge restore` and `topiaforge pack` pin each SDK `PackageReference` in a mod project to the SDK's
version, then read the lock file that NuGet wrote. The lock file says which version NuGet resolved, or
that it resolved none. When that differs from the pin, something outside the project's own item changed
it, such as:

- an `Update` or `Remove` item in a `Directory.Build.props` or `Directory.Build.targets` above the
  project, which MSBuild imports by name alone;
- a `PackageVersion` in a `Directory.Packages.props` above the project;
- a property or item expression in the project, such as `Include="$(SdkId)"`;
- another imported `.props` or `.targets` file, or a target that edits `PackageReference` items before
  restore.

The message names the package, both versions, and any of those three files above the project that names
the package in plain text; otherwise it lists the causes above, those files first. Make that use the
SDK's version, or remove it, then run `topiaforge restore` again. `topiaforge pack` packs nothing until
the versions agree.

### TF1103

**No lock file to check.**

`topiaforge restore` runs NuGet with lock files on. It reads the lock file where NuGet puts it: where
`NuGetLockFilePath` says, as the restore records in `obj/project.assets.json`; otherwise where the
project file itself declares `NuGetLockFilePath` in a plain `<PropertyGroup>`, as a literal path;
otherwise `packages.<project name>.lock.json` when there is one; otherwise `packages.lock.json` beside the
project. When none is there, it cannot be read, or the restore did not write it, which SDK versions NuGet
resolved is unknown, so the restore fails. NuGet rewrites the lock file it uses on every restore, so one the
restore left as it was is left from an earlier one, from before `NuGetLockFilePath` or `obj` moved, and it
is never checked in place of the lock file NuGet wrote. The message names the files it looked for, or the
one it found and why it is left over.

Keep `RestorePackagesWithLockFile` on, as the templates set it, and run `topiaforge restore` again. If
`NuGetLockFilePath` moves the lock file, keep the project's `obj` folder beside it, where the restore
records that path, or declare `NuGetLockFilePath` in the project file itself as a literal path: the check
does not evaluate MSBuild, so a path set in an imported file with `obj` moved elsewhere, or set through a
property expression, cannot be followed. If an `obj` folder left beside the project from before it moved
names an old path, delete that folder. If the project file declares `NuGetLockFilePath` but NuGet put the
lock file elsewhere, a later setting, a condition or an import sets another path, or none: make the project
file's declaration the one that applies.

## Multiplayer contract diagnostics

The multiplayer generator reports these while it builds a `[MultiplayerContract]`. Errors stop the build;
warnings mark contracts that build but will behave badly in a session.

### TFMP001

**Multiplayer contract must be partial.**

Mark the `[MultiplayerContract]` class `partial` and keep it top-level and non-generic; the generator adds the descriptor, codecs, binding, and typed proxies to it.

### TFMP002

**Invalid multiplayer command.**

A `[MultiplayerCommand]` handler is a non-static, non-async, non-generic method shaped `OperationResult<TResponse> Handle(MultiplayerCommandContext command, TRequest request)`. The request and response are classes, and no parameter is `ref`, `in`, or `out`. Out-of-range settings report TFMP016 instead.

### TFMP003

**Unsupported multiplayer payload type.**

Payloads are concrete classes with a compiler-generated parameterless constructor and deterministic primitive, enum, string, array, or nested payload members. Native Unity or game objects, interfaces, polymorphic and recursive graphs, and constructors or initializers with side effects are rejected.

### TFMP004

**Unbounded multiplayer payload.**

Give every string and array member `[NetworkBound(maximum)]`, so the generated codec has a hard size limit. A bound the compiler cannot resolve, usually for want of `using TopiaForge.Mods;`, is the compiler's error to report, so it raises CS0246 and no TFMP004.

### TFMP005

**Predicted handler is nondeterministic.**

An owner-predicted handler runs on the sender and again on the server, and both runs must agree. Draw random numbers from `command.CreateRandom()` and never read clocks, `System.Random`, or new GUIDs.

### TFMP006

**Predicted handler performs a process-local side effect.**

An owner-predicted handler runs on the sender and again on the server, so it may only touch what both runs share. It may update generated replicated state, read its request, its state, its locals and arrays, copy and resize arrays with `Array.Copy`, `CopyTo`, `Clone` and `Array.Resize`, call this mod's own non-virtual methods, whose bodies answer to the same rules, and emit buffered presentation events. Move UI, audio, file, and service calls into a `[PresentationEvent]` handler, or set `Prediction = PredictionMode.None` when the presser can wait for the host. A virtual or async method, one from another assembly, a static constructor the call would run first, `new T()`, an interface's or overridable property, and an event are refused, because the generator cannot read what would run. So is handing `this` on to a helper, a local or a return value, which would let the helper reach the mod's fields. One expression reports once, at its outermost offending call.

### TFMP007

**Duplicate multiplayer wire id.**

Every state, command, object, and event id in one contract must be unique.

### TFMP008

**Multiplayer payload exceeds its limit.**

The generated codec can produce more bytes than `MaximumPayloadBytes` allows. Raise the limit or tighten the `[NetworkBound]` values it adds up from.

### TFMP009

**Invalid multiplayer wire id.**

Ids are non-empty, contain no control characters, and stay at most 128 characters after the contract id prefix is added.

### TFMP010

**Invalid replicated object handler.**

A `[ReplicatedObject]` handler is a non-async, non-generic method shaped `OperationResult<TState> Apply(ReplicatedObjectCommandContext command, TState state, TInput input)`, with class state and input types and no `ref`, `in`, or `out` parameters.

### TFMP011

**Multiplayer payload exceeds the transport limit.**

Replicated state and presentation-event codecs must stay within the 1 MiB transport limit.

### TFMP012

**Generated multiplayer member name collision.**

A declaration would generate a member, such as `Submit...`, `Submit...Async` or `Emit...To`, that already exists. Rename the handler or the conflicting member.

### TFMP013

**Multiplayer contract id is required.**

Set `[MultiplayerContract(Id = "...")]`. The id is a permanent wire identity, so class and namespace names are never used.

### TFMP014

**Contract exceeds the per-connection inbound budget.**

A warning: the declared worst-case client-to-server rate exceeds 32 KiB/s. Lower `MaximumPerSecond` or `MaximumPayloadBytes` to what the commands really need.

### TFMP015

**Server-only handler uses local presentation.**

A warning: a handler with `Prediction = None` runs only on the session authority, so UI, input, local-player, audio, or Unity calls there affect the host rather than the player who acted. Publish a presentation event, or address the player through `IPlayer.Ui`.

### TFMP016

**Invalid multiplayer setting.**

A setting is outside its range: `Prediction` is `None` or `Owner`, `MaximumPerSecond` is 1 to 1000, `MaximumPayloadBytes` is 64 to 1048576, `Delivery` is `Coalesced` or `Immediate`, and `MaximumUpdatesPerSecond` is 1 to 60, or 0 for the provider's snapshot rate.

## Runtime diagnostics

Use `Context.Runtime.UnavailableCapabilities` to explain why a Robotopia binding or provider feature is
not active. Use `Context.Diagnostics.Report(...)` for structured, bounded reports that belong in a
diagnostic bundle; use `Context.Logger` for ordinary attributed operational messages.

After a run, `last-run.json` records exact packages and hashes, authoritative load order, stage
timings, compatibility decisions, outcomes, root exception chains, and recovery state. Installation
receipts detect modified package bytes before execution. Startup recovery quarantines a mod only
when the journal proves the process stopped inside that mod's load callback. Any other unclean exit
uses one-shot safe mode without assigning blame; only an explicit clean-exit record skips recovery.
Unity runs no frame once a quit starts destroying objects, so the manager holds the game's quit, for
at most 10 seconds, while its teardown unloads every mod and writes that record. On the first quit
request the manager moves its quit handler behind every handler subscribed until then, the game's own
among them, so the game's own quit steps, its shutdown feedback page among them, run while every mod
and patch is still in place. `manager.log` marks the request with `Quit requested.`

## Development-loop diagnostics

The `topiaforge dev` command stops at the first failed stage and never installs an unvalidated
package. Each code below names exactly one stage.

### TFDEV100

**Restore failed.** Run `topiaforge restore --project <path>` and resolve every reported lock, feed,
or SDK issue.

### TFDEV105

**Pinned toolchain unavailable.** Install the exact SDK in `global.json`, then restore again.

### TFDEV110

**Build failed.** Fix the first compiler/analyzer diagnostic, then run `dotnet build --no-restore`.

### TFDEV120

**Tests failed.** Run `dotnet test --no-restore` for the complete test-host and assertion output.

### TFDEV130

**Pack failed.** Check the manifest, entry assembly, declared package files, and Release output.

### TFDEV140

**Package validation failed.** Run `topiaforge check package <archive>` and correct every blocking
manifest, managed assembly, or archive finding.

### TFDEV150

**Install failed.** Pass `--game-dir <Robotopia folder>` or set `ROBOTOPIA_GAME_DIR`, then run
`topiaforge doctor`.

### TFDEV160

**Launch failed.** Run `topiaforge doctor`, repair the runtime in the launcher, and retry with
`--no-tail` while diagnosing.

### TFDEV170

**Log tail failed.** Inspect `BepInEx/TopiaForge/logs` and the launcher diagnostic bundle, or rerun
with `--no-tail`.

See [Development loop](CliDevLoop.md) for flags, interactive defaults, and generated project state.

## Release scaffold diagnostics

### TFSCF170

**The scaffold or its installed package evidence is not release-portable.** Run
`topiaforge restore --project <path>`, then rerun `topiaforge check scaffold <path>`. Correct every
reported SDK lock, project reference, exact package version, generated props, install receipt, or
manager state mismatch. A project must remain buildable after the release extraction is removed.

### TFSCF171

**The independent scaffold validator could not complete.** Repair or re-extract the matching
TopiaForge release so the CLI, pinned `global.json`, and `tools/package-validator` directory remain
together. Retry the command after confirming the pinned .NET SDK is installed.

### TFPKG150 / TFPKG151

**The trusted metadata validator or its pinned `global.json` is missing.** Repair or re-extract the
TopiaForge release; do not copy the CLI executable away from its `tools/package-validator` support
directory.

### TFPKG160 / TFPKG161

**Managed package metadata is invalid or could not be inspected.** Run `topiaforge check package
<archive>` and fix the reported PE, identity, target-framework, entry-type/constructor, SDK, API, or
bundled-framework finding. Validation reads metadata only and never loads mod code.

### TFPKG170

**Downloaded package metadata differs from the manifest approved during planning.** Refresh the
package source and retry. Source maintainers must publish immutable bytes whose canonical V6
manifest exactly matches the indexed manifest; TopiaForge does not stage mismatched packages.

### TFPKG180

**Manifest feature needs a newer loader.**

`topiaforge pack` and `topiaforge check package` compare each used manifest feature with the lower
bound of `supportedLoaderVersionRange`. Raise that floor to the required release. The shared table
is `ManifestFeatureRequirements.current` in
`packages/launcher_domain/lib/src/manifest_feature_requirements.dart`. It is empty for the 0.1
contract; tests inject a feature without adding a pretend production feature. When the `map` world
content kind is introduced, add its predicate and first loader release (`0.2.0`) to that table alongside
the schema and reader changes, whether it lands in V7 or a pre-release V6 amendment. An unknown kind
still fails the existing schema validation.

### TFINBOX100–TFINBOX130

**A package-inbox safety or installability check failed.** The affected archive is retained rather
than executed or silently deleted. Fix the reported Robotopia-installation, size, manifest, runtime,
dependency, metadata, hash, or filesystem issue and process the inbox again. Files changed during
processing remain in a `.topiaforge-consume-*` quarantine directory under the inbox for inspection.

### TFLOG001

**A completed operation could not be recorded in the launcher file log.** The durable operation is
still successful. Check that the launcher data `logs` directory is writable, is not a symbolic link,
and has free disk space; the bounded fallback diagnostic is written to the launcher's standard error
stream.
