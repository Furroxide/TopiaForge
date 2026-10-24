using System;
using System.IO;
using TopiaForge.ModManager;
using TopiaForge.Mods;
namespace TopiaForge.ModManager.Tests
{
    internal static class NativeWorldReflectionTests
    {
        internal static void Run()
        {
            var failures = new System.Collections.Generic.List<Exception>();
            foreach (var test in new Action[] { CheckpointUsesItsDeclaredAssetType,
                AwaiterUsesOnlyExactNonGenericMethods, UnsupportedCompletionShapeFailsBeforeInvocation,
                OpenGenericAwaiterIsUnavailable, BuildPathReachesTheGameLoaderAsItsSceneName,
                SceneNameReachesTheGameLoaderUnchanged, SharedSceneNameIsRefusedBeforeLoading,
                UnlistedBuildPathIsUnavailable, GameLoaderIsGivenTheSceneName })
            {
                try { test(); } catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count > 0) throw new AggregateException(failures);
            Console.WriteLine("Native world reflection tests passed (9 cases).");
        }
        private static void CheckpointUsesItsDeclaredAssetType()
        {
            var method = NativeWorldReflection.CheckpointLoader(typeof(Loader), typeof(Checkpoint));
            Assert(method != null && method.GetParameters()[1].ParameterType == typeof(Checkpoint),
                "A derived checkpoint must not change the declared native overload.");
            var result = method!.Invoke(null, new object[] { false, new SpecializedCheckpoint() }) as Awaitable;
            Assert(result != null && result.IsDeclaredLoader, "The loader selected a runtime-subtype overload.");
        }
        private static void AwaiterUsesOnlyExactNonGenericMethods()
        {
            var methods = NativeWorldReflection.Awaiter(typeof(Awaitable));
            Assert(methods != null, "Unrelated overloads must not make the supported native awaiter unavailable.");
            var awaiter = methods!.GetAwaiter.Invoke(new Awaitable(true), null);
            methods.GetResult.Invoke(awaiter, null);
            Assert(((Awaiter)awaiter!).Calls == 1, "The exact parameterless GetResult must run once.");
        }
        private static void UnsupportedCompletionShapeFailsBeforeInvocation()
        {
            Assert(NativeWorldReflection.Awaiter(typeof(WrongAwaitable)) == null,
                "An awaiter with a non-boolean IsCompleted must fail preflight.");
        }
        private static void OpenGenericAwaiterIsUnavailable()
        {
            Assert(NativeWorldReflection.Awaiter(typeof(GenericAwaitable)) == null,
                "An unbound generic GetAwaiter cannot be invoked after native effects.");
        }
        private static readonly string[] BuildScenes =
        {
            "Assets/Scenes/TestCityStartMenu.unity", "Assets/Levels/Sample/SampleScene.unity",
            "Assets/Levels/02 City Streets/City Streets.unity", "Assets/Ugc/UgcPlay.unity",
            "Assets/Arenas/A/Arena.unity", "Assets/Arenas/B/arena.unity"
        };
        private static void BuildPathReachesTheGameLoaderAsItsSceneName()
        {
            // The game's loader looks the scene up again by the string it was given: a build path loaded the scene,
            // then failed with "Scene <path> could not be loaded", and the session never reached Running.
            foreach (var (path, name) in new[] { ("Assets/Levels/Sample/SampleScene.unity", "SampleScene"),
                ("Assets/Levels/02 City Streets/City Streets.unity", "City Streets") })
            {
                var named = NativeSceneNames.ForGameLoader(path, BuildScenes);
                Assert(named.Succeeded && named.Value == name, "A build path must reach the game's loader as its scene name: " + path);
            }
            var windows = NativeSceneNames.ForGameLoader("Assets\\Levels\\Sample\\SampleScene.unity", new[] { "Assets\\Levels\\Sample\\SampleScene.unity" });
            Assert(windows.Succeeded && windows.Value == "SampleScene", "A path with Windows separators names its scene too.");
        }
        private static void SceneNameReachesTheGameLoaderUnchanged()
        {
            var sandbox = NativeSceneNames.ForGameLoader("UgcPlay", BuildScenes);
            Assert(sandbox.Succeeded && sandbox.Value == "UgcPlay", "A scene name reaches the game's loader as it is.");
            var extension = NativeSceneNames.ForGameLoader("SampleScene.unity", BuildScenes);
            Assert(extension.Succeeded && extension.Value == "SampleScene", "A scene name never carries the scene file's extension to the loader.");
        }
        private static void SharedSceneNameIsRefusedBeforeLoading()
        {
            foreach (var scene in new[] { "Assets/Arenas/A/Arena.unity", "Arena", "ARENA" })
            {
                var named = NativeSceneNames.ForGameLoader(scene, BuildScenes);
                Assert(!named.Succeeded && named.ErrorCode == ModErrorCode.Conflict,
                    "A name two build scenes share, in any case, could load the other scene and must be refused: " + scene);
            }
        }
        private static void UnlistedBuildPathIsUnavailable()
        {
            var named = NativeSceneNames.ForGameLoader("Assets/Levels/Gone/SampleScene.unity", BuildScenes);
            Assert(!named.Succeeded && named.ErrorCode == ModErrorCode.NotFound,
                "A path that is not the build scene of that name must not load the build scene that is.");
        }
        private static void GameLoaderIsGivenTheSceneName()
        {
            // The adapter needs Unity, so its wiring is pinned in its source: the scene loader takes the resolved name.
            var root = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.ModManager");
            var loading = File.ReadAllText(Path.Combine(root, "UnityWorldRuntimeBackend.Loading.cs"));
            var backend = File.ReadAllText(Path.Combine(root, "UnityWorldRuntimeBackend.cs"));
            Assert(loading.Contains("new object[] { loaderScene, CancellationToken.None }", StringComparison.Ordinal)
                && !loading.Contains("new object[] { scenePath, CancellationToken.None }", StringComparison.Ordinal),
                "The game's scene loader must be given the resolved scene name, never the build path.");
            Assert(backend.Contains("NativeSceneNames.ForGameLoader(scenePath, BuildScenePaths())", StringComparison.Ordinal)
                && backend.Contains("loaderScene = name;", StringComparison.Ordinal),
                "Scene-replacement loads must resolve the loader's scene name from the build scenes.");
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        public class Checkpoint { }
        public sealed class SpecializedCheckpoint : Checkpoint { }
        public static class Loader
        {
            public static Awaitable LoadSceneImpl(bool automatic, Checkpoint checkpoint) => new Awaitable(true);
            public static Awaitable LoadSceneImpl(bool automatic, SpecializedCheckpoint checkpoint) => new Awaitable(false);
        }
        public sealed class Awaitable
        {
            public Awaitable(bool declared) { IsDeclaredLoader = declared; }
            public bool IsDeclaredLoader { get; }
            public Awaiter GetAwaiter() => new Awaiter();
            public Awaiter GetAwaiter(int unrelated) => throw new Exception("wrong overload");
        }
        public sealed class Awaiter
        {
            public int Calls;
            public bool IsCompleted => true;
            public void GetResult() { Calls++; }
            public void GetResult(int unrelated) => throw new Exception("wrong overload");
            public void GetResult<T>() => throw new Exception("unbound generic");
            public void OnCompleted(Action callback) => callback();
        }
        public sealed class WrongAwaitable { public WrongAwaiter GetAwaiter() => throw new Exception("preflight must not invoke"); }
        public sealed class WrongAwaiter
        {
            public string IsCompleted => "yes";
            public void GetResult() { }
            public void OnCompleted(Action callback) { }
        }
        public sealed class GenericAwaitable { public Awaiter GetAwaiter<T>() => throw new Exception("unbound generic"); }
    }
}
