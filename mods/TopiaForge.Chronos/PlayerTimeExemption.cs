using System;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Chronos
{
    // Owns all reflection into the native player so Chronos needs no GameCode reference and no RobotKit dependency.
    // Exempts the player for Superhot by scaling move speed UP by 1/worldScale so it stays full-speed while the
    // world crawls. Hard-freeze suspension goes through IPlayerService.AcquireControl so it composes safely with
    // every other mod's control lease. Verified from the GameCode decompile: the player is
    // PlayerController.FindPlayer().FPSController (a FirstPersonController); move uses private groundSpeed/airSpeed
    // and is scaled by Time.* (so 1/scale compensates), while LOOK reads raw per-frame mouse delta × mouseSensitivity
    // with no dt — already full-speed at any timeScale, so it needs no compensation. Everything is guarded: if a
    // member can't be resolved (build drift), the op degrades to a no-op and logs once, never throws.
    internal sealed class PlayerTimeExemption
    {
        private const float ExemptScaleFloor = 0.05f; // bound the speed-up so a near-zero scale can't divide to absurd

        private readonly IModLogger logger;

        private bool typesResolved;
        private MethodInfo? findPlayer;
        private PropertyInfo? fpsControllerProp;
        private FieldInfo? groundSpeedField;
        private FieldInfo? airSpeedField;
        private bool resolveFailedLogged;

        // Exemption state (move-speed scaling).
        private Behaviour? exemptFps;
        private float baseGround;
        private float baseAir;
        private bool exemptActive;

        public PlayerTimeExemption(IModLogger logger)
        {
            this.logger = logger;
        }

        // --- EXEMPT (Superhot: keep the player full-speed while the world is slow) -------------------------------

        // Apply each frame while an ExemptPlayer lease is active. Scales move speed up by 1/scale (bounded). Captures
        // the native baseline the first time (or when the player instance changes, e.g. respawn).
        public void ApplyExemption(float worldScale)
        {
            var fps = ResolveFps();
            if (fps == null)
            {
                return;
            }

            if (!ReferenceEquals(fps, exemptFps))
            {
                // New player instance (or first time): capture its native speeds as the baseline.
                RestoreExemption(); // restore the old instance first, if any
                exemptFps = fps;
                if (!TryGetSpeeds(fps, out baseGround, out baseAir))
                {
                    exemptFps = null;
                    return;
                }

                exemptActive = true;
            }

            var divisor = worldScale < ExemptScaleFloor ? ExemptScaleFloor : worldScale;
            var factor = 1f / divisor;
            TrySetSpeeds(fps, baseGround * factor, baseAir * factor);
        }

        // Restore native move speeds (on ExemptPlayer release / teardown).
        public void RestoreExemption()
        {
            if (exemptActive && exemptFps != null)
            {
                TrySetSpeeds(exemptFps, baseGround, baseAir);
            }

            exemptFps = null;
            exemptActive = false;
        }

        // --- reflection plumbing ---------------------------------------------------------------------------------

        private Behaviour? ResolveFps()
        {
            EnsureTypes();
            if (findPlayer == null || fpsControllerProp == null)
            {
                return null;
            }

            try
            {
                var player = findPlayer.Invoke(null, Array.Empty<object>());
                if (player == null)
                {
                    return null; // no player in this scene
                }

                return fpsControllerProp.GetValue(player) as Behaviour;
            }
            catch (Exception ex)
            {
                LogResolveFailureOnce("resolve player: " + ex.Message);
                return null;
            }
        }

        private bool TryGetSpeeds(Behaviour fps, out float ground, out float air)
        {
            ground = 0f;
            air = 0f;
            if (groundSpeedField == null)
            {
                return false;
            }

            try
            {
                ground = (float)groundSpeedField.GetValue(fps);
                air = airSpeedField != null ? (float)airSpeedField.GetValue(fps) : ground;
                return true;
            }
            catch (Exception ex)
            {
                LogResolveFailureOnce("read player speed: " + ex.Message);
                return false;
            }
        }

        private void TrySetSpeeds(Behaviour fps, float ground, float air)
        {
            if (groundSpeedField == null)
            {
                return;
            }

            try
            {
                groundSpeedField.SetValue(fps, ground);
                airSpeedField?.SetValue(fps, air);
            }
            catch (Exception ex)
            {
                LogResolveFailureOnce("write player speed: " + ex.Message);
            }
        }

        private void EnsureTypes()
        {
            if (typesResolved)
            {
                return;
            }

            typesResolved = true;
            try
            {
                var playerControllerType = FindType("PlayerController");
                var fpsType = FindType("FirstPersonController");
                if (playerControllerType == null || fpsType == null)
                {
                    LogResolveFailureOnce("PlayerController/FirstPersonController types not found");
                    return;
                }

                findPlayer = playerControllerType.GetMethod("FindPlayer", BindingFlags.Public | BindingFlags.Static);
                fpsControllerProp = playerControllerType.GetProperty("FPSController", BindingFlags.Public | BindingFlags.Instance);
                groundSpeedField = fpsType.GetField("groundSpeed", BindingFlags.NonPublic | BindingFlags.Instance);
                airSpeedField = fpsType.GetField("airSpeed", BindingFlags.NonPublic | BindingFlags.Instance);
                if (findPlayer == null || fpsControllerProp == null || groundSpeedField == null)
                {
                    LogResolveFailureOnce("player time hooks (FindPlayer/FPSController/groundSpeed) not all resolved");
                }
            }
            catch (Exception ex)
            {
                LogResolveFailureOnce("reflect player types: " + ex.Message);
            }
        }

        private static Type? FindType(string name)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var index = 0; index < assemblies.Length; index++)
            {
                Type? t;
                try
                {
                    t = assemblies[index].GetType(name, false);
                }
                catch
                {
                    t = null;
                }

                if (t != null)
                {
                    return t;
                }
            }

            return null;
        }

        private void LogResolveFailureOnce(string message)
        {
            if (resolveFailedLogged)
            {
                return;
            }

            resolveFailedLogged = true;
            logger.Warn("Chronos player time-hook unavailable (" + message + "); player exemption degrades to no-op.");
        }
    }
}
