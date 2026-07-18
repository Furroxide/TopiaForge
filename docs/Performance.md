# TopiaForge Performance mod

`io.github.furroxide.topiaforge.performance` is a runtime, **fully-reversible** performance mod for Robotopia (Unity 6 / HDRP).
It exposes a small set of presets plus fine-grained per-effect overrides, and reverts everything it
touched when it unloads. Nothing is baked into Robotopia assets — every lever is applied at runtime via an
injected HDRP override Volume, reflection on the active render-pipeline asset, plain
`QualitySettings`/`Application`/`Time`/`Physics` calls, or guarded Harmony patches.

## Presets (`performance_mode`)

Set `performance_mode` in `config/topiaforge.performance.json` (created on first launch). A preset rewrites
the individual lever fields below unless you set `override_manual: true`.

| Mode | What it does | Fidelity cost |
|------|--------------|---------------|
| `off` | Mod loads but applies nothing (still a clean no-op). | none |
| `balanced` *(default)* | Motion blur off, depth-of-field off, vSync on, `reuseCollisionCallbacks`, log stack-trace stripping, Sentry quieted. All free/near-free. | cosmetic only (no blur/DoF) |
| `performance` | Balanced **plus** SSR / SSAO / volumetric-fog / contact-shadows / SSGI / volumetric-clouds / lens-flare off, reflection-probe pool → 0, dynamic resolution @ 70%, LOD bias 0.8. Keeps the high quality level so geometry/textures stay sharp; the expensive per-pixel passes die. | softer image, no reflections/AO/volumetrics |
| `potato` | Performance **plus** forces the low quality level, dynamic resolution @ 55%, fog off, bloom off, one mip drop, anisotropic off. | clearly lower fidelity |

The headline GPU wins are the screen-space-pass kills, dynamic resolution, forcing the low quality
level, and zeroing the reflection-probe pool. The CPU/GC tier (`reuseCollisionCallbacks`, stack-trace
strip, Sentry quiet) ships on by default because it is essentially free and reversible.

## Manual control

Set `override_manual: true` to freeze the preset and hand-tune every field yourself. Fields left at
their "leave" sentinel (`-1`, `0`, or `false`) are not touched.

### Safe levers (on in `balanced`)
| Field | Default | Effect |
|-------|---------|--------|
| `motion_blur_off` | `true` | Force motion blur off (Robotopia ships it on). |
| `depth_of_field_off` | `true` | Force depth-of-field off (overrides the per-frame DoF write during dialogue). |
| `vignette_off` | `false` | Force vignette off (cosmetic). |
| `vsync_count` | `1` | VSync interval. `-1` leaves the engine default. |
| `reuse_collision_callbacks` | `true` | Stops a per-collision allocation. |
| `strip_log_stack_traces` | `true` | Drops stack traces from `Log`/`Warning` output. |
| `sentry_quiet` | `true` | Turns off Sentry diagnostic `Debug` logging. |

### Aggressive levers (on in `performance` / `potato`)
| Field | Effect |
|-------|--------|
| `force_quality_level_1` | Force Robotopia's low quality level regardless of detected GPU. |
| `dynamic_resolution_enabled` / `dynamic_resolution_percent` | HDRP dynamic resolution + STP upscale (50–100%). |
| `reflection_probe_pool` | Player reflection-probe pool size. `0` kills them; `-1` leaves it. |
| `ssr_off` / `ssgi_off` / `ssao_off` / `volumetric_fog_off` / `fog_off` / `contact_shadows_off` / `volumetric_clouds_off` / `lens_flare_off` | Disable that effect via the override Volume. |
| `bloom_off` / `bloom_low_quality` | Disable bloom, or keep it at quarter-res without HQ filtering. |
| `lod_bias` / `global_mip_limit` / `anisotropic_disable` / `particle_raycast_budget` | Cheap `QualitySettings` fine-tuning. |
| `asset_rebuild_allowed` + `shadow_atlas_resolution` / `disable_volumetrics_ssgi_asset` | Pipeline-asset levers. These force a one-time pipeline recreate (a brief hitch), so they are gated behind `asset_rebuild_allowed`. |

### Risky / explicit opt-in (never set by a preset)
| Field | Effect / caveat |
|-------|------------------|
| `target_frame_rate` | Numeric fps cap. `-1` = uncapped. Ignored while vSync is on. |
| `render_frame_interval` | Render every Nth frame (`2` = every other). Causes visible judder. |
| `fixed_delta_time` | Physics step (e.g. `0.0333` = 30 Hz). Applied at load only — changes physics feel. |
| `solver_iterations` | Physics solver iterations (affects newly-spawned bodies). |
| `disable_posthog` | No-op the PostHog telemetry bridge (privacy + tiny perf). |
| `disable_sentry` | Reduce Sentry tracing/session overhead. **Reduces crash reporting.** |
| `stop_perf_logger` | Stop the per-frame frametime sampler. |
| `shadow_refresh_rate` | Spread managed-light shadow refreshes over more frames (`LightUpdater`). |
| `pathfind_budget_ms` | Cap per-frame pathfinding time to reduce spikes when many robots repath. |
| `gpu_occlusion_culling` | **GPU occlusion culling** via Unity 6's GPU Resident Drawer: skips `MeshRenderer`s fully hidden behind other geometry *before* shading (plain frustum culling does not — it only drops what's off-screen, not what's behind a wall). Needs `asset_rebuild_allowed` **and** `gpu_occlusion_allow_unverified` (see the hard caveat below — by itself it will not engage). |
| `gpu_small_mesh_screen_percentage` | Companion GRD lever: also cull meshes smaller than this % of the screen (`0` = off). Enables the GPU Resident Drawer too, so it also needs `asset_rebuild_allowed` + `gpu_occlusion_allow_unverified`. |
| `gpu_occlusion_allow_unverified` | Required confirmation to actually enable the GPU Resident Drawer: *"I am running a build compiled with BatchRendererGroup Variants = Keep All."* Without it the GRD levers stay an explained no-op (the safe default). |

## How it stays reversible
Each applier captures the original value of everything it touches before changing it, and restores it on
unload. The injected Volume's `GameObject` and `VolumeProfile`/component `ScriptableObject`s are destroyed
explicitly (Unity does not GC them). Harmony patches are removed with `UnpatchSelf()`. Robotopia interaction
goes through clean-room reflection (`AccessTools` / `Type.GetType("…, GameCode")`), so a future Robotopia
update that renames a member downgrades a single lever to an inert, logged no-op instead of breaking the
mod.

## Build & install
```
dotnet build mods/TopiaForge.Performance/TopiaForge.Performance.csproj -c Release
```
`topiaforge dev-install` packs every `mods/*` with a `topiaforge.mod.json` into Robotopia's
`package-inbox`; launch Robotopia once and install from the F10 overlay (or the main-menu Mod Manager
button) to apply.

## Caveats
- **`balanced` enables vSync** (`vsync_count: 1`), which caps frame rate to your monitor's refresh rate.
  This reduces wasted GPU work, tearing, and heat — but if you want uncapped FPS, set `vsync_count: -1`
  (and optionally `override_manual: true` so the preset doesn't re-enable it).
- **Sentry quieting is effective until restart.** It runs in the SDK's one-time boot configuration, so
  unloading the mod stops re-applying it but does not roll back the live options; they reset on next launch.
- **Forcing the low quality level** leaves the in-game quality dropdown showing your previous pick while
  the engine renders low. This is the intended override; the real quality level and your saved preference
  are restored when the mod unloads.
- **GPU occlusion culling is experimental, off by default, and will not engage on the current shipped
  build.** It enables Unity 6's GPU Resident Drawer, which hands plain `MeshRenderer` submission to a
  GPU-driven (DOTS-instancing) path.
  - **The hard prerequisite.** The drawer needs the DOTS-instancing shader variants present in the build.
    Unity only keeps them when Robotopia is built with *Graphics → Shader Stripping → "BatchRendererGroup
    Variants" = "Keep All"*. Robotopia shipped with the drawer **off** and ships **no Entities Graphics**
    package, so under Unity's default (`KeepIfEntitiesGraphics`) those variants were **stripped**. Routing
    meshes through the drawer would then render them **pink/invisible**, and that failure is **silent**
    (Unity logs nothing) — and there is **no runtime API** to detect the strip setting. So this cannot be
    safely auto-enabled or auto-detected at runtime.
  - **Safe default: it refuses to engage.** Setting `gpu_occlusion_culling` alone logs the reason above and
    does **nothing** — it cannot break rendering. To actually enable it you must *also* set
    `gpu_occlusion_allow_unverified = true`, confirming you are on a "Keep All" build. (Plus
    `asset_rebuild_allowed`, since it forces a one-time pipeline recreate.)
  - **Best-effort watchdog (not a guarantee).** When enabled, the mod watches the log briefly and
    auto-reverts *just* the GPU-occlusion settings if it sees `BatchRendererGroup`/DOTS instancing errors.
    This only covers cases Unity actually logs — it does **not** catch the silent stripped-variant case. If
    you see pink/missing geometry, set `gpu_occlusion_culling = false`.
  - **Other caveats when it *does* engage.** It may not honour every per-renderer `MaterialPropertyBlock`,
    so per-instance-tinted materials could look different. It does not manage `SkinnedMeshRenderer`s, but it
    flips the global `USE_LEGACY_LIGHTMAPS` keyword while active, which can subtly change lightmap appearance
    on all lightmapped geometry (reverted on unload). Fully reversed on unload.
  - **To make it actually work:** rebuild Robotopia with "BatchRendererGroup Variants = Keep All", then set
    `asset_rebuild_allowed`, `gpu_occlusion_culling`, and `gpu_occlusion_allow_unverified` all true.

## Notes to verify live
- Dynamic resolution needs the camera flag (`Camera.allowDynamicResolution` +
  `HDAdditionalCameraData.allowDynamicResolution`), which the mod sets on Game cameras every scene/frame.
  Confirm the main camera actually downscales (watch the render-target size) before relying on it.
- The override Volume runs at priority 1000 (above Robotopia's ~0 and the Worlds mod's 50). If a future
  level uses a higher-priority volume, bump it.
- GPU occlusion culling logs whether it actually engaged a few frames after apply: look for
  `GPU occlusion culling is ACTIVE` (engaged) or `did NOT engage` (unsupported — rendering unchanged).
  To confirm the GPU win, profile a scene with lots of geometry behind walls and watch draw calls /
  GPU frame time drop versus the same view with the lever off.
