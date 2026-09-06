using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class LegacyLaunchDiscoveryTests
    {
        internal static async Task RunAsync(string repositoryRoot, string? selected = null)
        {
            if (selected is not null and not ("remembered" or "failure" or "cancel" or "menu" or "target" or "queued-menu" or "queued-target" or "worker-explicit"))
                throw new ArgumentException("Unknown legacy launch discovery case.", nameof(selected));
            if (selected == null || selected == "remembered") await DelayedDiscoveryRetainsRememberedInstance(repositoryRoot);
            if (selected == null || selected == "failure") await FailedDiscoveryDoesNotStart();
            if (selected == null || selected == "cancel") await CancelledDiscoveryDoesNotStart();
            if (selected == null || selected == "menu") SupersededAutoloadCannotDispatch("main-menu", false);
            if (selected == null || selected == "target") SupersededAutoloadCannotDispatch("target-B", false);
            if (selected == null || selected == "queued-menu") SupersededAutoloadCannotDispatch("main-menu", true);
            if (selected == null || selected == "queued-target") SupersededAutoloadCannotDispatch("target-B", true);
            if (selected == null || selected == "worker-explicit") ExplicitCommandAdmissionBelongsToHost();
            Console.WriteLine("Legacy launch discovery tests passed (" + (selected ?? "8 cases") + ").");
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
            var preparation = new LegacyLaunchDiscovery(new InlineDiscoveryDispatcher(), async () =>
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
            var preparation = new LegacyLaunchDiscovery(new InlineDiscoveryDispatcher(), () => Task.FromException(new InvalidOperationException("discovery fault")));
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
            var preparation = new LegacyLaunchDiscovery(new InlineDiscoveryDispatcher(), () => Task.FromCanceled(new System.Threading.CancellationToken(true)));
            try
            {
                await preparation.AfterAsync(() => { launched = true; return Task.CompletedTask; });
                throw new Exception("Expected discovery cancellation.");
            }
            catch (OperationCanceledException) { }
            Assert(!launched, "A cancelled discovery attempt cannot start remembered gameplay.");
        }

        private static void SupersededAutoloadCannotDispatch(string explicitChoice, bool queuedLegacy)
        {
            using var host = new HostDispatcher();
            var discovery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var preparation = new LegacyLaunchDiscovery(host, () => discovery.Task);
            var actual = "idle";
            var legacyCalls = 0;
            var pending = preparation.AfterAsync(() =>
            {
                Interlocked.Increment(ref legacyCalls);
                actual = "remembered-A";
                return Task.CompletedTask;
            });
            if (queuedLegacy)
            {
                discovery.SetResult(true);
                WaitUntil(() => host.HasPendingWork || pending.IsCompleted,
                    "completed discovery must queue its final admission on the host");
                Assert(host.HasPendingWork && !pending.IsCompleted,
                    "the regression must hold legacy dispatch in the host queue before the explicit command wins");
            }
            var explicitCommand = preparation.ExplicitAsync(() =>
            {
                Assert(host.IsCurrent, "explicit commands must be admitted on the runtime host");
                actual = explicitChoice;
                return Task.FromResult(true);
            });
            Pump(host, explicitCommand);
            if (!queuedLegacy) discovery.SetResult(true);
            Pump(host, pending);
            Assert(actual == explicitChoice && legacyCalls == 0,
                "remembered autoload A must not run after explicit " + explicitChoice
                + (queuedLegacy ? ", even if its final host dispatch was already queued" : ", while discovery was pending"));
        }

        private static void ExplicitCommandAdmissionBelongsToHost()
        {
            using var host = new HostDispatcher();
            var preparation = new LegacyLaunchDiscovery(host, () => Task.CompletedTask);
            var commandCalls = 0;
            var pending = Task.Run(() => preparation.ExplicitAsync(() =>
            {
                Assert(host.IsCurrent, "worker commands must enter the same host queue before changing generation or dispatching runtime work");
                commandCalls++;
                return Task.FromResult(true);
            }));
            WaitUntil(() => host.HasPendingWork || pending.IsCompleted, "worker command must reach host admission");
            Assert(commandCalls == 0, "a worker callback cannot run before the host drains");
            Pump(host, pending);
            Assert(commandCalls == 1, "the host admits one explicit command");
        }

        private static void Pump(HostDispatcher host, Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!task.IsCompleted || host.HasPendingWork) && DateTime.UtcNow < deadline)
            { host.Drain(); Thread.Sleep(1); }
            Assert(task.IsCompleted, "host-owned legacy launch admission must finish");
            task.GetAwaiter().GetResult();
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(1);
            Assert(condition(), message);
        }

        private sealed class InlineDiscoveryDispatcher : IHostDispatcher
        {
            public bool IsCurrent => true;
            public void Post(Action action) => action();
            public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
            public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
            public Task<T> InvokeCallbackAsync<T>(Func<Task<T>> callback) => callback();
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
