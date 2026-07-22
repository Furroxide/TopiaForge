# Changelog

## 1.0.0-rc.1

- Migrated to the owner-scoped TopiaForge V1 lifecycle, package, and SDK contracts.
- Rebuilt combat around stable RobotKit/physics entity identity, custom archetype health, headshots, charged piercing shots, and combo scoring.
- Added reachable horde spawning, moving-target pursuit, stranded-enemy recovery, ally combat and loyalty, uplink charges, deterministic stand-down, and between-wave requisitions.
- Added explicit opt-in live JACK IN text/voice conversations with engine-owned persuasion gates and fully offline deterministic fallback.
- Added scene-readiness gating, unscaled control timers, native health synchronization, retained game-over actions, confirmed menu return, accessibility settings, and dirty-checked HUD updates.
- Migrated legacy `overrideKey` bindings to `jackInKey` and removed serialized visual/bark/input knobs that had no safe-SDK runtime effect.

## 0.12.1

- Initial public release.
