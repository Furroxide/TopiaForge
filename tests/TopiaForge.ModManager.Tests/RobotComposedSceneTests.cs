using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotComposedSceneTests
    {
        internal static void Run()
        {
            WorldObservation();
            // Synthetic structure only: no native character data or asset identifiers are reproduced.
            const string json = "{\"id\":\"test\",\"entities\":{\"actor\":{\"components\":{\"transform\":{},\"agent\":{},\"humanoid-body-parts\":{\"Head\":\"test-part\"}}}}}";
            Check(RobotComposedSceneShape.TryRead(json, out var scene) && scene != null, "one native composed actor is eligible");
            Check(!RobotComposedSceneShape.TryRead(json.Replace("\"agent\":{}", "\"model-renderer\":{}"), out _), "visual-only data cannot qualify as an agent");
            Check(!RobotComposedSceneShape.TryRead(json.Replace("\"transform\":{}", "\"prefab-instance\":{}"), out _), "nested scene imports do not enter this bounded factory");
            Check(!RobotComposedSceneShape.TryRead(json.Replace("\"components\":", "\"parent\":\"another\",\"components\":"), out _), "external parents are refused");
            Check(!RobotComposedSceneShape.TryRead(json.Replace("\"test-part\"", "null"), out _), "unknown native reference shapes are refused");
            Check(!RobotComposedSceneShape.TryRead("{\"scenes\":{},\"entities\":{}}", out _), "project and scene containers are not conflated");
            Check(!RobotComposedSceneShape.TryRead(new string('x', 65537), out _), "oversized scene input is refused");
            Check(!RobotComposedSceneShape.TryRead(new string('[', 80) + "0" + new string(']', 80), out _), "deep input is rejected before native deserialization");
            Check(RobotComposedSceneShape.IsMunicipalPath("characters/MuniciPal.json"), "identity derives from the complete native entry path");
            Check(RobotComposedSceneShape.IsMunicipalPath("characters\\MuniciPal.json"), "native path separators normalize consistently");
            Check(!RobotComposedSceneShape.IsMunicipalPath("characters/Robot01.json"), "other characters are never substituted");
            Check(RobotComposedSceneShape.IsMunicipalPath("MuniciPal/actor.json"), "a native folder token is sufficient without a filename assumption");
            Check(RobotComposedSceneShape.IsMunicipalPath("characters/municipal.json"), "native inventory classification ignores case");
            Check(!RobotComposedSceneShape.IsMunicipalPath(null), "missing identity is unavailable");
        }

        private static void WorldObservation()
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.RobotKit", "topiaforge.mod.json")));
            const string worlds = "io.github.furroxide.topiaforge.worlds";
            Check(manifest.RootElement.GetProperty("optionalDependencies").TryGetProperty(worlds, out _),
                "RobotKit declares visibility of the existing world-session extension provider");
            Check(manifest.RootElement.GetProperty("loadAfter").EnumerateArray().Any(value => value.GetString() == worlds),
                "the world-session extension is registered before RobotKit binds it once");
            var source = new Sessions();
            var notifications = 0;
            using var observation = new RobotWorldObservation(source, () => notifications++);
            Check(source.Reads == 1 && source.Subscribers == 1, "provider observation binds once");
            for (var index = 0; index < 1000; index++)
                Check(ReferenceEquals(observation.Current, source.State), "actor reads reuse the committed snapshot");
            Check(source.Reads == 1, "hot-path reads do not allocate new context-bound observations");
            var session = new Session();
            source.Publish(new WorldSessionSnapshot(WorldSessionPhase.LoadingWorld, session, null, 1));
            var ready = new WorldReadiness(new WorldSceneIdentity(1, "test.world"), TransformState.Identity);
            source.Publish(new WorldSessionSnapshot(WorldSessionPhase.StartingMode, session, ready, 2));
            Check(observation.Current!.Phase == WorldSessionPhase.StartingMode && ReferenceEquals(observation.Current.World, ready)
                && notifications == 2, "committed readiness invalidates discovery synchronously before factory startup, without advancing a clock");
            observation.Dispose(); observation.Dispose();
            source.Publish(new WorldSessionSnapshot(WorldSessionPhase.Running, session, ready, 3));
            Check(source.Subscribers == 0 && observation.Current == null && notifications == 2, "provider stop unsubscribes idempotently");
            using var missing = new RobotWorldObservation(null, () => throw new InvalidOperationException());
            Check(missing.Current == null, "missing optional Worlds context stays honestly unavailable");
        }

        private sealed class Sessions : IWorldSessionService
        {
            internal int Reads, Subscribers;
            internal WorldSessionSnapshot State = new WorldSessionSnapshot(WorldSessionPhase.Idle, null, null, 0);
            private Action<WorldSessionSnapshot>? changed;
            public WorldSessionSnapshot Current { get { Reads++; return State; } }
            public event Action<WorldSessionSnapshot> StateChanged
            {
                add { changed += value; Subscribers++; }
                remove { changed -= value; Subscribers--; }
            }
            internal void Publish(WorldSessionSnapshot snapshot) { State = snapshot; changed?.Invoke(snapshot); }
        }

        private sealed class Session : IWorldSession
        {
            public string SessionId => "test.session";
            public string TargetId => "test.target";
            public string GamemodeId => "test.mode";
            public string WorldId => "test.world";
            public string? WorldFamilyId => null;
            public Task<OperationResult<bool>> StopAsync(CancellationToken token = default) => throw new NotSupportedException();
            public Task<OperationResult<bool>> RestartAsync(CancellationToken token = default) => throw new NotSupportedException();
            public Task<OperationResult<bool>> ReturnToMainMenuAsync(CancellationToken token = default) => throw new NotSupportedException();
        }
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
