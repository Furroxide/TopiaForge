using System;
using System.Reflection;
using HarmonyLib;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;
using UnityEngine;

namespace TopiaForge.Performance.Appliers
{
    /// <summary>
    /// Harmony patches against game methods, all resolved by name via <see cref="AccessTools"/> so a
    /// renamed/removed target downgrades a single lever to an inert no-op (logged) instead of breaking
    /// the mod. Covers: forcing the low quality level (and re-forcing it after the in-game quality
    /// dropdown), quieting/disabling Sentry, no-op'ing the PostHog telemetry bridge, and stopping the
    /// per-frame frametime sampler.
    /// </summary>
    internal sealed class PatchApplier : PerfApplierBase
    {
        private static bool forceLow;
        private static bool sentryQuiet;
        private static bool sentryDisable;
        private static IModLogger? log;
        private static bool forcedLogged;

        private readonly PerformanceConfig config;
        private readonly IModLogger logger;
        private readonly IHarmonyLease harmony;

        private int origQualityLevel = -1;
        private bool capturedQuality;

        public PatchApplier(PerformanceConfig config, IModLogger logger, IHarmonyLease harmony)
        {
            this.config = config;
            this.logger = logger;
            this.harmony = harmony;
        }

        public override string Name => "Patch";

        public override void Apply()
        {
            log = logger;

            if (config.ForceQualityLevel1)
            {
                forceLow = true;
                CaptureQuality();
                PatchPostfix("LevelSettingsApplier", "SetInitialQualityLevel", Array.Empty<Type>(),
                    nameof(ForceQualityPostfix));
                PatchPostfix("SettingsScreen", "OnQualityLevelChanged", new[] { typeof(int) },
                    nameof(ForceQualityPostfix));
                // The boot-time SetInitialQualityLevel may already have run before this mod loaded; force now.
                GameReflectionLite.ForceLowQuality();
            }

            if (config.SentryQuiet || config.DisableSentry)
            {
                sentryQuiet = config.SentryQuiet || config.DisableSentry;
                sentryDisable = config.DisableSentry;
                PatchPostfix("SentryRuntimeConfiguration", "Configure", null, nameof(SentryConfigurePostfix));
            }

            if (config.DisablePosthog)
            {
                PatchPrefix("PostHogUtils", "OnSessionLogEntry", null, nameof(SkipPrefix));
            }

            if (config.StopPerfLogger)
            {
                PatchPrefix("PerformanceLogger", "StartFrametimeTracking", null, nameof(SkipPrefix));
            }
        }

        public override void Revert()
        {
            forceLow = false;
            sentryQuiet = false;
            sentryDisable = false;
            forcedLogged = false;

            if (config.ForceQualityLevel1 && capturedQuality)
            {
                try
                {
                    QualitySettings.SetQualityLevel(origQualityLevel);
                    var t = GameReflectionLite.GameType("LevelSettingsApplier");
                    if (t != null)
                    {
                        GameReflectionLite.CallStaticVoid(t, "ApplySettings");
                    }
                }
                catch
                {
                    // Best-effort.
                }
            }

            // Harmony unpatch is performed once by the mod's owner-scoped lease.
            log = null;
        }

        private void CaptureQuality()
        {
            if (capturedQuality)
            {
                return;
            }

            try
            {
                origQualityLevel = QualitySettings.GetQualityLevel();
            }
            catch
            {
                origQualityLevel = -1;
            }

            capturedQuality = true;
        }

        private void PatchPostfix(string typeName, string methodName, Type[]? args, string patchMethod)
        {
            var target = ResolveTarget(typeName, methodName, args);
            if (target == null)
            {
                return;
            }

            try
            {
                harmony.Patch(target, postfix: Own(patchMethod));
                logger.Debug($"Performance: patched {typeName}.{methodName} (postfix).");
            }
            catch (Exception ex)
            {
                logger.Warn($"Performance: failed to patch {typeName}.{methodName}: {ex.Message}");
            }
        }

        private void PatchPrefix(string typeName, string methodName, Type[]? args, string patchMethod)
        {
            var target = ResolveTarget(typeName, methodName, args);
            if (target == null)
            {
                return;
            }

            try
            {
                harmony.Patch(target, prefix: Own(patchMethod));
                logger.Debug($"Performance: patched {typeName}.{methodName} (prefix).");
            }
            catch (Exception ex)
            {
                logger.Warn($"Performance: failed to patch {typeName}.{methodName}: {ex.Message}");
            }
        }

        private MethodBase? ResolveTarget(string typeName, string methodName, Type[]? args)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    logger.Warn($"Performance: game type '{typeName}' not found; that lever is inactive.");
                    return null;
                }

                var method = args == null
                    ? AccessTools.Method(type, methodName)
                    : AccessTools.Method(type, methodName, args);
                if (method == null)
                {
                    logger.Warn($"Performance: method '{typeName}.{methodName}' not found; that lever is inactive.");
                }

                return method;
            }
            catch (Exception ex)
            {
                logger.Warn($"Performance: could not resolve {typeName}.{methodName}: {ex.Message}");
                return null;
            }
        }

        private static MethodInfo Own(string name)
        {
            return typeof(PatchApplier).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        }

        // ----- Harmony patch bodies (static) -----

        private static void ForceQualityPostfix()
        {
            if (!forceLow)
            {
                return;
            }

            GameReflectionLite.ForceLowQuality();
            if (!forcedLogged)
            {
                forcedLogged = true;
                log?.Info("Performance: forced low quality level (1).");
            }
        }

        // Returning false skips the original method body.
        private static bool SkipPrefix()
        {
            return false;
        }

        // Harmony binds 'options' by parameter name from Configure(SentryUnityOptions options).
        // NOTE: Configure runs once at boot, so this postfix typically fires only at startup. Revert just
        // stops re-application (flips the gate flags) — it does not roll the live SentryUnityOptions values
        // back. In practice the option object isn't reconfigured after load, so the quieting persists until
        // the game restarts. This lever is therefore "effective until restart" rather than live-reversible.
        private static void SentryConfigurePostfix(object options)
        {
            if (options == null)
            {
                return;
            }

            try
            {
                if (sentryQuiet)
                {
                    GameReflectionLite.SetProperty(options, "Debug", false);
                    SetEnumProperty(options, "DiagnosticLevel", "Error");
                }

                if (sentryDisable)
                {
                    GameReflectionLite.SetProperty(options, "AutoSessionTracking", false);
                    GameReflectionLite.SetProperty(options, "TracesSampleRate", 0.0);
                    GameReflectionLite.SetProperty(options, "CaptureFailedRequests", false);
                }
            }
            catch
            {
                // Non-fatal.
            }
        }

        private static void SetEnumProperty(object target, string propertyName, string enumMemberName)
        {
            try
            {
                var prop = target.GetType().GetProperty(propertyName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite)
                {
                    return;
                }

                var value = Enum.Parse(prop.PropertyType, enumMemberName);
                prop.SetValue(target, value);
            }
            catch
            {
                // Non-fatal.
            }
        }
    }
}
