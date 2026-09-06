using System;
using System.IO;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class LegacyWorldLaunchAdapterTests
    {
        internal static void Run()
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Program.FindRepoRoot(),
                "tests", "fixtures", "gamemode-v6", "resolution", "auto-transition-prefers-scene-replacement.json")));
            var manifest = ModManifestJson.Deserialize(fixture.RootElement.GetProperty("profile").GetProperty("packages")[0]
                .GetProperty("manifest").GetRawText());
            EffectiveProfile Profile() => new EffectiveProfile("legacy", 0, new[] { new ResolvedPackage(manifest.Id, manifest.Version, manifest) });
            var target = manifest.Contributions!.LaunchTargets[0];
            var plan = LaunchResolver.Resolve(Profile(), new LaunchRequest(target.Id)).Plan!;
            var intent = new WorldLaunchIntent
            {
                GamemodeId = plan.GamemodeId,
                WorldId = plan.WorldId,
                LoadMode = plan.Transition == ModTransitions.SceneReplacement ? WorldLaunchSettings.SceneReplacement : WorldLaunchSettings.AdditiveArena
            };
            var translated = LegacyWorldLaunchAdapter.Resolve(Profile(), RuntimeObservation.None, intent);
            Assert(translated.Succeeded && translated.Value!.TargetId == target.Id && translated.Value.WorldOverride == null,
                "a unique matching legacy selection maps to its actual target without inventing an override");
            intent.WorldId = "example.missing.world";
            var missing = LegacyWorldLaunchAdapter.Resolve(Profile(), RuntimeObservation.None, intent);
            Assert(!missing.Succeeded && intent.WorldId == "example.missing.world", "a missing legacy world remains unavailable without fallback or saved-value mutation");
            intent.WorldId = plan.WorldId;
            var duplicate = JsonUtil.Clone(target); duplicate.Id = target.Id + ".alternate";
            manifest.Contributions.LaunchTargets.Add(duplicate);
            Assert(!LegacyWorldLaunchAdapter.Resolve(Profile(), RuntimeObservation.None, intent).Succeeded, "several valid targets require explicit repair");
            manifest.Contributions.LaunchTargets.Remove(duplicate);
            intent.GamemodeId = "io.github.furroxide.topiaforge.worlds.sandbox";
            Assert(!LegacyWorldLaunchAdapter.Resolve(Profile(), RuntimeObservation.None, intent).Succeeded,
                "the retired Sandbox identifier cannot silently select Free Play or creator mode");
            Console.WriteLine("Legacy launch adapter tests passed.");
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
