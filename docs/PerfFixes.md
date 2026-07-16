# TopiaForge Performance Fixes mod

`io.github.furroxide.topiaforge.perffixes` fixes **root causes** of stutter and wasted CPU in Robotopia **without changing
anything you can see or play**. Unlike the [Performance](Performance.md) mod (which trades fidelity for
FPS by lowering settings), every fix here is **behavior-identical** — Robotopia does exactly the same work,
just more cheaply. It is safe to run on its own, on top of vanilla, with no graphics change.

All fixes default **on**; each can be toggled independently in `config/topiaforge.perffixes.json`.

## The fixes

### 1. `reuse_collision_callbacks` — stop per-collision GC garbage
Unity allocates a fresh managed `Collision` object for *every* `OnCollisionEnter`/`OnCollisionExit`
callback unless `Physics.reuseCollisionCallbacks` is set. Robotopia never sets it. Every Robotopia collision handler
consumes the `Collision` synchronously (there is no `OnCollisionStay`, nothing retains
`.contacts` past the callback, and the one deferred path copies a `ContactPoint` *struct* before going
async), so reusing the buffer changes nothing observable — it just removes a steady stream of GC garbage
during ragdoll/prop pile-ups. **One line, provably safe.**

### 2. `camera_main_cache` — resolve `Camera.main` once per frame
`Camera.main` is **not** cached by Unity: each read does a native `FindGameObjectsWithTag("MainCamera")`
scan. Robotopia reads it through `CameraUtils.TryGetMainCamera` from several per-frame systems (the light
manager, depth-of-field, every look-at-camera billboard, the reflection-probe manager). A Harmony prefix
memoizes the result keyed on the frame number. The main camera is invariant within a frame (Robotopia never
switches cameras mid-frame), so every caller gets the identical reference — the native scan just runs once
per frame instead of many times. A Unity fake-null re-check honours a destroyed/swapped camera.

### 3. `collision_proxy_pooled` — pooled collision-proxy dispatch
`CollisionEventProxy.OnCollisionEnter/Exit` (on robot body parts) dispatch through `IterReceivers()`, which
allocates a fresh `ICollisionEventReceiver[]` array *and* a `yield` iterator state-machine on **every**
collision callback — two heap allocations per body-part hit, concentrated exactly when combat/ragdolls
create GC pressure. The fix reimplements the dispatch with a pooled `List` and the non-allocating
`GetComponentsInParent(false, list)` overload. The component set, order, the `includeInactive:false`
default, the self-GameObject skip, and the `enabled` guard are all preserved, so the dispatch is identical —
the garbage is gone. If a future Robotopia update ever makes the reimplementation structurally invalid, the fix
self-disables and falls back to the original method.

## Reversibility
The single global flag (`reuseCollisionCallbacks`) is captured and restored. The two Harmony patches are
removed with `UnpatchSelf()` on unload, and their caches/gates are cleared. Targets are resolved by name
(`AccessTools`), so a renamed Robotopia member downgrades that one fix to a logged no-op rather than breaking
the mod.

## Build & install
```
dotnet build mods/TopiaForge.PerfFixes/TopiaForge.PerfFixes.csproj -c Release
```
`topiaforge dev-install` stages it to Robotopia's `package-inbox`; install from the F10 overlay on the next Robotopia
launch.

## Deferred (vetted but not shipped)
A fourth candidate — removing the `yield`-iterator allocations in `LightUpdater.Update` and
`ActivityGroupManager.UpdateActivityGroups` (the BVH query enumerators) — was verified as real but requires
a full reimplementation of both methods (private fields, exact traversal order, the shadow-refresh budget
math) for only a few small allocations per frame. The risk-to-reward of a faithful port that can't be
unit-tested against live Robotopia wasn't worth shipping by default; it's documented here as a future option.

## What was rejected (and why)
The hunt evaluated 25 candidates and kept only the provably-identical, genuinely-hot ones. Notably
rejected: a "LightUpdater allocates a sort delegate every frame" claim (false — the lambda is
non-capturing, so the C# compiler caches it, zero per-frame allocation); enabling incremental GC (already
the build default via `boot.config gc-max-time-slice`); and various single-instance/sparse allocations
below the cost-of-patching bar.
