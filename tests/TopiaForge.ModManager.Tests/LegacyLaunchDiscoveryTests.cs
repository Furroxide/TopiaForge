using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class LegacyLaunchDiscoveryTests
    {
        internal static async Task RunAsync(string repositoryRoot)
        {
            await DelayedDiscoveryRetainsRememberedInstance(repositoryRoot);
            await FailedDiscoveryDoesNotStart();
            await CancelledDiscoveryDoesNotStart();
            Console.WriteLine("Legacy launch discovery tests passed (3 cases).");
        }

        private static async Task DelayedDiscoveryRetainsRememberedInstance(string repositoryRoot)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot,
                "tests", "fixtures", "gamemode-v6", "resolution", "discovery-longest-family-accepts-correct-attribution.json")));
            var root = fixture.RootElement;
            var manifest = ModManifestJson.Deserialize(root.GetProperty("profile").GetProperty("packages")[0]
                .GetProperty("manifest").GetRawText());
            var profile = new EffectiveProfile("fixture", 1, new[] { new ResolvedPackage(manifest.Id, manifest.Version, manifest) });
            var committed = RuntimeObservation.FromEnvelopes(profile, new[] {
                LaunchTransportJson.ReadObservation(root.GetProperty("observation").GetProperty("envelopes")[0].GetRawText()) });
            var intent = new WorldLaunchIntent
            {
                GamemodeId = manifest.Contributions!.Gamemodes[0].Id,
                WorldId = root.GetProperty("request").GetProperty("worldOverride").GetString()!,
                LoadMode = WorldLaunchSettings.SceneReplacement
            };
            Assert(!LegacyWorldLaunchAdapter.Resolve(profile, RuntimeObservation.None, intent).Succeeded,
                "The fixture must actually require a discovered instance before translating its remembered selection.");
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observation = RuntimeObservation.None;
            var discoveryCalls = 0; var translations = 0;
            var preparation = new LegacyLaunchDiscovery(async () =>
            {
                discoveryCalls++;
                await release.Task;
                observation = committed;
            });
            _ = preparation.Start(); // The menu starts the discovery that autoload must share.
            OperationResult<LaunchRequest>? selected = null;
            var pending = preparation.AfterAsync(() =>
            {
                translations++;
                selected = LegacyWorldLaunchAdapter.Resolve(profile, observation, intent);
                return Task.CompletedTask;
            });
            Assert(!pending.IsCompleted && translations == 0,
                "Remembered discovered content must wait without consuming its sole translation attempt.");
            release.SetResult(true);
            await pending;
            Assert(discoveryCalls == 1 && translations == 1 && selected?.Succeeded == true
                && selected.Value!.WorldOverride == intent.WorldId,
                "Launch must translate once against the committed observation from the same discovery attempt.");
        }

        private static async Task FailedDiscoveryDoesNotStart()
        {
            var launched = false;
            var preparation = new LegacyLaunchDiscovery(() => Task.FromException(new InvalidOperationException("discovery fault")));
            try
            {
                await preparation.AfterAsync(() => { launched = true; return Task.CompletedTask; });
                throw new Exception("Expected the discovery fault.");
            }
            catch (InvalidOperationException error) { Assert(error.Message == "discovery fault", "The primary discovery failure must survive."); }
            Assert(!launched, "A failed discovery attempt cannot launch against an incomplete observation.");
        }

        private static async Task CancelledDiscoveryDoesNotStart()
        {
            var launched = false;
            var preparation = new LegacyLaunchDiscovery(() => Task.FromCanceled(new System.Threading.CancellationToken(true)));
            try
            {
                await preparation.AfterAsync(() => { launched = true; return Task.CompletedTask; });
                throw new Exception("Expected discovery cancellation.");
            }
            catch (OperationCanceledException) { }
            Assert(!launched, "A cancelled discovery attempt cannot start remembered gameplay.");
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
