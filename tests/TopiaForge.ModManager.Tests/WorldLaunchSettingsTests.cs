using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldLaunchSettingsTests
    {
        public static void Run()
        {
            TestDeserializationSeedsRuntimeDefaults();
            TestLoadModeReconciliation();
            TestManagerStateAlwaysCarriesWorldLaunch();
            TestJsonObjectMergeRejectsMalformedValues();
        }

        private static void TestDeserializationSeedsRuntimeDefaults()
        {
            var settings = JsonUtil.Deserialize<WorldLaunchSettings>("{}");

            Assert(settings.LoadMode == WorldLaunchSettings.AdditiveArena,
                "world launch settings should default to additiveArena");
            Assert(settings.AllowAdditiveFallback,
                "world launch settings should preserve allowAdditiveFallback default true when missing");
            Assert(!settings.AutoLoadOnStart,
                "world launch settings should default autoLoadOnStart to false");
        }

        private static void TestLoadModeReconciliation()
        {
            Assert(WorldLaunchSettings.ReconcileLoadMode(
                    supportsSceneReplacement: true,
                    supportsAdditiveArena: false,
                    requestedMode: WorldLaunchSettings.AdditiveArena) == WorldLaunchSettings.SceneReplacement,
                "scene-only worlds should snap additiveArena to sceneReplacement");

            Assert(WorldLaunchSettings.ReconcileLoadMode(
                    supportsSceneReplacement: false,
                    supportsAdditiveArena: true,
                    requestedMode: WorldLaunchSettings.SceneReplacement) == WorldLaunchSettings.AdditiveArena,
                "additive-only worlds should snap sceneReplacement to additiveArena");

            Assert(WorldLaunchSettings.ReconcileLoadMode(
                    supportsSceneReplacement: true,
                    supportsAdditiveArena: true,
                    requestedMode: WorldLaunchSettings.SceneReplacement) == WorldLaunchSettings.SceneReplacement,
                "worlds that support both modes should keep a valid requested mode");

            Assert(WorldLaunchSettings.NormalizeLoadMode("bogus") == WorldLaunchSettings.AdditiveArena,
                "unknown load modes should normalize to additiveArena");
        }

        /// <summary>
        /// The remembered selection is manager-owned state now, rather than keys merged into the Worlds
        /// mod's own config document — that shared file is what silently discarded every launcher choice.
        /// DataContractJsonSerializer builds state with GetUninitializedObject, so an absent member
        /// arrives null and Normalize has to seed it; callers must never have to null-check it.
        /// </summary>
        private static void TestManagerStateAlwaysCarriesWorldLaunch()
        {
            var fresh = JsonUtil.Deserialize<ManagerState>("{\"schemaVersion\":1,\"mods\":[]}");
            fresh.Normalize();
            Assert(fresh.WorldLaunch != null, "a state document with no worldLaunch must still expose one");
            Assert(fresh.WorldLaunch!.LoadMode == WorldLaunchSettings.AdditiveArena,
                "a seeded selection must carry a usable load mode");
            Assert(!fresh.WorldLaunch.AutoLoadOnStart,
                "a fresh install must boot to the game's own menu, not into a gamemode nobody chose");

            var stored = JsonUtil.Deserialize<ManagerState>(
                "{\"schemaVersion\":1,\"mods\":[],\"worldLaunch\":{\"selectedGamemodeId\":\"a.b.c\","
                + "\"loadMode\":\"bogus\",\"autoLoadOnStart\":true}}");
            stored.Normalize();
            Assert(stored.WorldLaunch!.SelectedGamemodeId == "a.b.c", "a stored selection must survive");
            Assert(stored.WorldLaunch.AutoLoadOnStart, "a stored auto-load choice must survive");
            Assert(stored.WorldLaunch.LoadMode == WorldLaunchSettings.AdditiveArena,
                "an unusable stored load mode must be clamped, not carried into the runtime");
        }

        private static void TestJsonObjectMergeRejectsMalformedValues()
        {
            var noChanges = new Dictionary<string, string>();
            AssertThrows<FormatException>(() => JsonObjectMerge.Merge("{\"future\":garbage}", noChanges),
                "merge must reject a malformed primitive instead of preserving invalid JSON");
            AssertThrows<FormatException>(
                () => JsonObjectMerge.Merge("{\"future\":\"bad" + '\u0001' + "value\"}", noChanges),
                "merge must reject an unescaped control character instead of preserving invalid JSON");
            AssertThrows<FormatException>(() => JsonObjectMerge.Merge("{\"future\":01}", noChanges),
                "merge must reject a leading-zero JSON number");
            AssertThrows<FormatException>(() => JsonObjectMerge.Merge("{\"future\":NaN}", noChanges),
                "merge must reject the non-standard NaN literal");
            AssertThrows<FormatException>(() => JsonObjectMerge.Merge("{\"future\":Infinity}", noChanges),
                "merge must reject the non-standard Infinity literal");
            AssertThrows<FormatException>(() => JsonObjectMerge.Merge("{\"future\":true\f}", noChanges),
                "merge must reject non-JSON whitespace between tokens");

            const string strictValid = "{\"exponent\":-1.25e+3,\"nothing\":null,\"yes\":true,\"no\":false}";
            var retained = JsonObjectMerge.Merge(strictValid, noChanges);
            Assert(retained.Contains("\"exponent\":-1.25e+3")
                && retained.Contains("\"nothing\":null")
                && retained.Contains("\"yes\":true")
                && retained.Contains("\"no\":false"),
                "strict validation must preserve valid exponent, null, and boolean values");
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
