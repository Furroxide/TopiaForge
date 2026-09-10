using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class LaunchTargetPreviewTests
    {
        internal static void Run()
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "tests", "fixtures", "gamemode-v6", "resolution", "player-choice-honours-an-offered-transition.json")));
            var manifest = ModManifestJson.Deserialize(fixture.RootElement.GetProperty("profile").GetProperty("packages")[0].GetProperty("manifest").GetRawText());
            var targets = manifest.Contributions!.LaunchTargets; var worlds = manifest.Contributions.Worlds;
            var extra = JsonUtil.Clone(worlds[0]); extra.Id += ".extra"; worlds.Add(extra);
            var forbidden = JsonUtil.Clone(extra); forbidden.Id += ".no-consent"; forbidden.OpenToAnyCompatible = null; worlds.Add(forbidden);
            EffectiveProfile Profile() => new EffectiveProfile("preview", 2, new[] { new ResolvedPackage(manifest.Id, manifest.Version, manifest) });
            var preview = LaunchTargetPreviewBuilder.Build(Profile()).Single();
            Assert(preview.Choices.Any(choice => choice.Request.WorldOverride == extra.Id && choice.Transition == ModTransitions.AdditiveArena), "Open world choices must include compatible consenting worlds and offered transitions.");
            Assert(preview.Choices.All(choice => choice.WorldId != forbidden.Id), "Worlds without consent cannot appear as offered choices.");
            foreach (var permission in new bool?[] { null, false })
            {
                targets[0].World!.AllowPlayerOverride = permission;
                preview = LaunchTargetPreviewBuilder.Build(Profile()).Single();
                Assert(preview.Choices.All(choice => choice.Request.WorldOverride == null), "Absent and false override permission must never offer another world.");
            }
            targets[0].World!.AllowPlayerOverride = true; targets[0].World!.Policy = ModWorldPolicy.FixedPolicy;
            targets[0].Transition = ModLaunchTargetDeclaration.AutoTransition;
            preview = LaunchTargetPreviewBuilder.Build(Profile()).Single();
            Assert(preview.Choices.Count == 1 && preview.Choices[0].IsDeclaredDefault, "Fixed/auto targets cannot invent player controls.");
            targets[0].World!.Policy = ModWorldPolicy.OpenPolicy; worlds[0].OpenToAnyCompatible = null;
            preview = LaunchTargetPreviewBuilder.Build(Profile()).Single();
            Assert(!preview.Declared.Resolved && preview.Choices.Count == 1 && preview.Choices[0].WorldId == extra.Id && !preview.Choices[0].IsDeclaredDefault,
                "A blocked default must remain blocked even when an alternative is available; the player must choose it explicitly.");
            Console.WriteLine("Manager target policy choices: PASS");
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
