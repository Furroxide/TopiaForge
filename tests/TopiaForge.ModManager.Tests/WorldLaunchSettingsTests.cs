using System;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldLaunchSettingsTests
    {
        public static void Run()
        {
            TestDeserializationSeedsRuntimeDefaults();
            TestLoadModeReconciliation();
            TestMergePreservesProviderAndUnknownFields();
            TestMergeRejectsMalformedUnknownValues();
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
            Assert(settings.EndSessionOnMenuScene && settings.InterceptPauseMenu,
                "manager launch settings must preserve provider lifecycle defaults when fields are missing");
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

        private static void TestMergePreservesProviderAndUnknownFields()
        {
            const string existing = "{\"selectedWorldId\":\"old\",\"endSessionOnMenuScene\":false,"
                + "\"interceptPauseMenu\":false,\"retiredProviderState\":null,"
                + "\"futureProviderState\":{\"nested\":[1,{\"x\":true}]}}";
            var settings = new WorldLaunchSettings
            {
                SelectedWorldId = "new.world",
                SelectedGamemodeId = "new.mode",
                LoadMode = WorldLaunchSettings.SceneReplacement,
                AutoLoadOnStart = true,
                AllowAdditiveFallback = false,
            };

            var merged = settings.MergeIntoJson(existing);
            var round = JsonUtil.Deserialize<WorldLaunchSettings>(merged);
            Assert(round.SelectedWorldId == "new.world" && round.SelectedGamemodeId == "new.mode",
                "merge should replace manager-owned selection fields");
            Assert(round.LoadMode == WorldLaunchSettings.SceneReplacement && round.AutoLoadOnStart
                && !round.AllowAdditiveFallback, "merge should replace every launch preference");
            Assert(merged.Contains("\"endSessionOnMenuScene\":false")
                && merged.Contains("\"interceptPauseMenu\":false"),
                "merge must retain provider lifecycle preferences");
            Assert(merged.Contains("\"retiredProviderState\":null"),
                "merge must preserve legitimate null-valued provider fields");
            Assert(merged.Contains("\"futureProviderState\":{\"nested\":[1,{\"x\":true}]}"),
                "merge must retain unknown nested provider JSON");
        }

        private static void TestMergeRejectsMalformedUnknownValues()
        {
            var settings = new WorldLaunchSettings();
            AssertThrows<FormatException>(() => settings.MergeIntoJson("{\"future\":garbage}"),
                "merge must reject a malformed primitive instead of preserving invalid JSON");
            AssertThrows<FormatException>(() => settings.MergeIntoJson("{\"future\":\"bad" + '\u0001' + "value\"}"),
                "merge must reject an unescaped control character instead of preserving invalid JSON");
            AssertThrows<FormatException>(() => settings.MergeIntoJson("{\"future\":01}"),
                "merge must reject a leading-zero JSON number");
            AssertThrows<FormatException>(() => settings.MergeIntoJson("{\"future\":NaN}"),
                "merge must reject the non-standard NaN literal");
            AssertThrows<FormatException>(() => settings.MergeIntoJson("{\"future\":Infinity}"),
                "merge must reject the non-standard Infinity literal");
            AssertThrows<FormatException>(() => settings.MergeIntoJson("{\"future\":true\f}"),
                "merge must reject non-JSON whitespace between tokens");

            const string strictValid = "{\"exponent\":-1.25e+3,\"nothing\":null,\"yes\":true,\"no\":false}";
            var retained = JsonObjectMerge.Merge(
                strictValid,
                new System.Collections.Generic.Dictionary<string, string>());
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
