using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace TopiaForge.ModManager.Tests
{
    internal static class MainThreadGuardTests
    {
        public static void Run()
        {
            UnityMainThreadGuard.CaptureCurrentThread();
            UnityMainThreadGuard.AssertCurrent("main-thread probe");

            var failure = Task.Run(() =>
            {
                try
                {
                    UnityMainThreadGuard.AssertCurrent("background probe");
                    return string.Empty;
                }
                catch (InvalidOperationException exception)
                {
                    return exception.Message;
                }
            }).GetAwaiter().GetResult();
            if (!failure.StartsWith("TFSDK100:", StringComparison.Ordinal)
                || !failure.Contains("Context.Scheduler.NextFrame", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The SDK must reject engine access away from the game thread.");
            }

            AssertAdapterEntryGuards();

            Console.WriteLine("MainThreadGuardTests passed.");
        }

        private static void AssertAdapterEntryGuards()
        {
            var managerRoot = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.ModManager");
            var entries = new Dictionary<string, string[]>
            {
                ["UnityInputService.cs"] = new[] { "public OperationResult<IInputAction> RegisterAction" },
                ["UnityPlayerService.cs"] = new[] { "public bool TryGetSnapshot", "public OperationResult<PlayerHealthSnapshot> Damage" },
                ["UnityWorldServices.cs"] = new[] { "public bool TryRaycast", "public OperationResult<IEntityMotion> AcquireMotion" },
                ["UnitySceneService.cs"] = new[] { "public Task<OperationResult<SceneSnapshot>> LoadAsync" },
                ["UnityInteractionAndItemServices.cs"] = new[]
                {
                    "public OperationResult<IInteractableRegistration> Register",
                    "public async Task<OperationResult<HeldItemSnapshot>> GiveAsync",
                    "public async Task<OperationResult<IEntity>> DropHeldAsync"
                },
                ["UnityAssetService.cs"] = new[]
                {
                    "public Task<OperationResult<IAssetBundle>> LoadBundleAsync",
                    "public OperationResult<ISpawnedEntity> Spawn"
                },
                ["UnityAudioService.cs"] = new[] { "public OperationResult<IAudioPlayback> Play" },
                ["UnityUiService.cs"] = new[] { "public OperationResult<IUiSurface> CreateSurface" },
                ["OwnerUnityInteropService.cs"] = new[] { "public OperationResult<IEntity> Wrap" },
                ["UnityTimingServices.cs"] = new[] { "public void Tick" }
            };

            foreach (var entry in entries)
            {
                var source = File.ReadAllText(Path.Combine(managerRoot, entry.Key));
                foreach (var signature in entry.Value)
                {
                    var start = source.IndexOf(signature, StringComparison.Ordinal);
                    if (start < 0)
                    {
                        throw new InvalidOperationException(entry.Key + " is missing adapter entry " + signature + ".");
                    }

                    var length = Math.Min(600, source.Length - start);
                    var bodyStart = source.Substring(start, length);
                    if (!bodyStart.Contains("UnityMainThreadGuard.AssertCurrent();", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(entry.Key + " does not guard " + signature + ".");
                    }
                }
            }
        }
    }
}
