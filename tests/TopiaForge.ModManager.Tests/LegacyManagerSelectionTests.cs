using System;
using System.IO;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class LegacyManagerSelectionTests
    {
        internal static void Run()
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "tests", "fixtures", "gamemode-v6",
                "resolution", "auto-transition-prefers-scene-replacement.json")));
            var manifest = ModManifestJson.Deserialize(fixture.RootElement.GetProperty("profile").GetProperty("packages")[0].GetProperty("manifest").GetRawText());
            EffectiveProfile Profile() => new EffectiveProfile("manager", 1, new[] { new ResolvedPackage(manifest.Id, manifest.Version, manifest) });
            var target = manifest.Contributions!.LaunchTargets[0];
            var plan = LaunchResolver.Resolve(Profile(), new LaunchRequest(target.Id)).Plan!;
            var legacy = new WorldLaunchSettings
            {
                SelectedGamemodeId = plan.GamemodeId,
                SelectedWorldId = plan.WorldId,
                LoadMode = plan.Transition == ModTransitions.SceneReplacement ? WorldLaunchSettings.SceneReplacement : WorldLaunchSettings.AdditiveArena
            };
            LaunchSelection Saved() => LaunchSelection.UnresolvedLegacy("{\"worldLaunch\":" + JsonUtil.Serialize(legacy) + "}");
            var original = Saved();
            var resolved = LaunchSelectionResolver.Resolve(original, Profile());
            Assert(resolved.Available && resolved.Request!.TargetId == target.Id && resolved.Request.WorldOverride == null,
                "Exactly one fully valid legacy mapping must select its declared target without inventing an override.");
            Assert(original.Kind == "unresolved-legacy", "Derived resolution must not destroy the original saved JSON.");
            var duplicate = JsonUtil.Clone(target); duplicate.Id += ".alternate"; manifest.Contributions.LaunchTargets.Add(duplicate);
            Assert(!LaunchSelectionResolver.Resolve(Saved(), Profile()).Available, "Ambiguous valid mappings require explicit repair.");
            manifest.Contributions.LaunchTargets.Remove(duplicate);
            legacy.SelectedGamemodeId = "io.github.furroxide.topiaforge.worlds.sandbox";
            Assert(!LaunchSelectionResolver.Resolve(Saved(), Profile()).Available, "The retired Sandbox ID must never map to another mode.");
            legacy.SelectedGamemodeId = plan.GamemodeId;
            legacy.LoadMode = "future-transition";
            Assert(!LaunchSelectionResolver.Resolve(Saved(), Profile()).Available, "Unknown legacy transition values require repair.");
            foreach (var missing in new[] { "{}", "{\"worldLaunch\":null}", "{\"worldLaunch\":{\"selectedGamemodeId\":\"" + plan.GamemodeId + "\"}}" })
                Assert(!LaunchSelectionResolver.Resolve(LaunchSelection.UnresolvedLegacy(missing), Profile()).Available,
                    "Missing legacy worlds or transitions must not be inferred from target defaults.");
            Console.WriteLine("Legacy manager selection resolution: PASS");
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
