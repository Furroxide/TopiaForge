using System;
using System.Reflection;
using HarmonyLib;
using TopiaForge.Mods;

namespace TopiaForge.PerfFixes
{
    /// <summary>
    /// Clean-room Harmony helper: resolves a game method by name via <see cref="AccessTools"/> (so a
    /// renamed/removed target downgrades that one fix to a logged no-op instead of breaking the mod) and
    /// patches it. Mirrors the resolution style of the TopiaForge.Performance mod.
    /// </summary>
    internal static class PatchUtil
    {
        public static bool TryPatchPrefix(Harmony harmony, IModLogger logger, string typeName, string methodName,
            Type[]? args, MethodInfo prefix)
        {
            var target = Resolve(logger, typeName, methodName, args);
            if (target == null)
            {
                return false;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                logger.Debug($"PerfFixes: patched {typeName}.{methodName}.");
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn($"PerfFixes: failed to patch {typeName}.{methodName}: {ex.Message}");
                return false;
            }
        }

        public static MethodInfo Own(Type owner, string name)
        {
            return owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        }

        private static MethodBase? Resolve(IModLogger logger, string typeName, string methodName, Type[]? args)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    logger.Warn($"PerfFixes: game type '{typeName}' not found; that fix is inactive.");
                    return null;
                }

                var method = args == null
                    ? AccessTools.Method(type, methodName)
                    : AccessTools.Method(type, methodName, args);
                if (method == null)
                {
                    logger.Warn($"PerfFixes: method '{typeName}.{methodName}' not found; that fix is inactive.");
                }

                return method;
            }
            catch (Exception ex)
            {
                logger.Warn($"PerfFixes: could not resolve {typeName}.{methodName}: {ex.Message}");
                return null;
            }
        }
    }
}
