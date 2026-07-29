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
            AssertSceneLoadCancellationOwnership();
            AssertSceneLoadPublicationOrder();
            AssertUiCreationAbortPaths();
            AssertAudioResourceReuse();

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

        private static void AssertSceneLoadCancellationOwnership()
        {
            var source = ReadManagerSource("UnitySceneService.cs");
            AssertContains(
                source,
                "stoppingRegistration = stoppingToken.Register(Cancel);",
                "scene loads must complete with cancellation when their owner stops");
            AssertContains(
                source,
                "callerRegistration = callerToken.CanBeCanceled ? callerToken.Register(Cancel) : default;",
                "scene loads must complete with cancellation when their caller cancels");
            AssertContains(
                source,
                "stoppingRegistration.Dispose();",
                "scene-load cancellation registrations must be released after result completion");
            AssertContains(
                source,
                "operation?.DisposeFromBackend();",
                "backend disposal must settle the result and release native completion tracking");
            if (source.Contains("lifetime.Track(operation);", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Native scene-load state must remain backend-owned rather than lifetime-tracked as a cancellable resource.");
            }
        }

        private static void AssertSceneLoadPublicationOrder()
        {
            var source = ReadManagerSource("UnitySceneService.cs");
            var beginLoadStart = source.IndexOf(
                "public OperationResult<SceneLoadState> BeginLoad(",
                StringComparison.Ordinal);
            if (beginLoadStart < 0)
            {
                throw new InvalidOperationException("UnitySceneBackend.BeginLoad could not be inspected.");
            }

            var beginLoadEnd = source.IndexOf("public void Dispose()", beginLoadStart, StringComparison.Ordinal);
            if (beginLoadEnd < 0)
            {
                throw new InvalidOperationException("UnitySceneBackend.BeginLoad could not be inspected.");
            }

            var beginLoad = source.Substring(beginLoadStart, beginLoadEnd - beginLoadStart);
            AssertOrdered(
                beginLoad,
                "var state = new SceneLoadState(",
                "activeLoad = state;",
                "state.Arm(stoppingToken, callerToken);");

            var stateStart = source.IndexOf("internal sealed class SceneLoadState", StringComparison.Ordinal);
            if (stateStart < 0)
            {
                throw new InvalidOperationException("SceneLoadState's two-phase construction could not be inspected.");
            }

            var armStart = source.IndexOf(
                "public void Arm(CancellationToken stoppingToken, CancellationToken callerToken)",
                stateStart,
                StringComparison.Ordinal);
            if (armStart < 0)
            {
                throw new InvalidOperationException("SceneLoadState's two-phase construction could not be inspected.");
            }

            var construction = source.Substring(stateStart, armStart - stateStart);
            if (construction.Contains("operation.completed += OnCompleted;", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "SceneLoadState must not subscribe to native completion before it is published as activeLoad.");
            }

            var armEnd = source.IndexOf("public void AbortArming()", armStart, StringComparison.Ordinal);
            if (armEnd < 0)
            {
                throw new InvalidOperationException("SceneLoadState.Arm could not be inspected.");
            }

            var arm = source.Substring(armStart, armEnd - armStart);
            AssertOrdered(
                arm,
                "operation.completed += OnCompleted;",
                "stoppingRegistration = stoppingToken.Register(Cancel);",
                "callerRegistration = callerToken.CanBeCanceled ? callerToken.Register(Cancel) : default;");
            AssertContains(
                beginLoad,
                "state.AbortArming();",
                "failed scene-load arming must clean up its partially initialized state");
        }

        private static void AssertUiCreationAbortPaths()
        {
            var source = ReadManagerSource("UnityUiService.cs");
            AssertContains(
                source,
                "AbortSurfaceCreation(surface, nativeRoot, request.Id);",
                "surface creation failures must tear down partially created native UI");
            AssertContains(
                source,
                "state?.Abort();",
                "modal creation failures must tear down state without reporting user cancellation");
        }

        private static void AssertAudioResourceReuse()
        {
            var source = ReadManagerSource("UnityAudioService.cs");
            AssertContains(
                source,
                "private readonly Dictionary<AudioCueKey, AudioClip> clipCache",
                "owner audio must cache synthesized clips");
            AssertContains(
                source,
                "private readonly Stack<PooledAudioSource> availableSources",
                "owner audio must reuse native playback hosts");
            AssertContains(
                source,
                "lifetime.Defer(Dispose);",
                "owner audio caches and playback hosts must be released with the mod lifetime");
            AssertContains(
                source,
                "source.spatialBlend = request.Position.HasValue ? 1f : 0f;",
                "rented playback hosts must apply positional audio state");
            AssertContains(
                source,
                "Source.spatialBlend = 0f;",
                "returned playback hosts must clear positional audio state");
            AssertContains(
                source,
                "currentOwner.ReturnSource(current);",
                "stopped and completed playback must return its host to the owner pool");
            AssertContains(
                source,
                "Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();",
                "completed playback must release its lifetime lease immediately");

            var cacheStart = source.IndexOf(
                "private OperationResult<AudioClip> GetOrCreateClip",
                StringComparison.Ordinal);
            var poolStart = source.IndexOf(
                "private PooledAudioSource RentSource()",
                StringComparison.Ordinal);
            if (cacheStart < 0 || poolStart <= cacheStart)
            {
                throw new InvalidOperationException("The synthesized audio cue cache could not be inspected.");
            }

            var cache = source.Substring(cacheStart, poolStart - cacheStart);
            AssertOrdered(
                cache,
                "var key = new AudioCueKey(cueId, loop);",
                "clipCache.TryGetValue(key, out var cached)",
                "return OperationResult<AudioClip>.Success(cached);",
                "AudioClip.Create(",
                "clipCache.Add(key, clip);");
        }

        private static string ReadManagerSource(string fileName) => File.ReadAllText(Path.Combine(
            Program.FindRepoRoot(),
            "src",
            "TopiaForge.ModManager",
            fileName));

        private static void AssertContains(string source, string expected, string diagnostic)
        {
            if (!source.Contains(expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(diagnostic + ".");
            }
        }

        private static void AssertOrdered(string source, params string[] expected)
        {
            var previous = -1;
            foreach (var value in expected)
            {
                var index = source.IndexOf(value, previous + 1, StringComparison.Ordinal);
                if (index < 0)
                {
                    throw new InvalidOperationException(
                        "Expected ordered implementation step was not found: " + value + ".");
                }

                previous = index;
            }
        }
    }
}
