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
            RobotCatalogDiscoveryTests.Run();
            RobotComposedDiscoveryTests.Run();
            RobotComposedCleanupTests.Run();
            DiscoveryDiagnostics();
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

        private static void DiscoveryDiagnostics()
        {
            var logger = new DiscoveryLogger();
            RobotKitDiagnostics.Configure(logger);
            try
            {
                var session = new Session();
                var ready = new WorldReadiness(new WorldSceneIdentity(7, "private-world"), TransformState.Identity);
                var state = new WorldSessionSnapshot(WorldSessionPhase.Running, session, ready, 1);
                foreach (var reason in Enum.GetValues<RobotComposedRefusal>())
                {
                    for (var i = 0; i < 1000; i++) RobotKitDiagnostics.ReportComposedDiscovery(state, reason);
                    Check(logger.Messages.Last() == (reason == RobotComposedRefusal.CharacterUnprepared
                            ? "RobotKit composed discovery admitted the body only: " : "RobotKit composed discovery refused: ") + reason + ".",
                        "only a static refusal code reaches Info diagnostics");
                    Check(RobotKitDiagnostics.LastComposedDiscovery(() => state) == reason,
                        "the latest discovery reason is kept for a refused spawn");
                    var text = RobotComposedReferences.Explain(reason);
                    Check(text.Length > 20 + reason.ToString().Length && text.EndsWith(" Code: " + reason + ".", StringComparison.Ordinal),
                        "every discovery code has a fixed sentence for the author and the player");
                }
                var count = Enum.GetValues<RobotComposedRefusal>().Length;
                Check(logger.Messages.Count == count, "each refusal is bounded per world/session");
                var rebound = new WorldSessionSnapshot(WorldSessionPhase.Running, session,
                    new WorldReadiness(ready.Scene, TransformState.Identity), 2);
                RobotKitDiagnostics.ReportComposedDiscovery(rebound, RobotComposedRefusal.HostCount);
                Check(logger.Messages.Count == count, "snapshot/readiness rebinding does not reset diagnostics");
                Check(RobotKitDiagnostics.LastComposedDiscovery(() => rebound) == RobotComposedRefusal.HostCount,
                    "a repeated code is still the latest reason");
                var next = new WorldSessionSnapshot(WorldSessionPhase.Running, session,
                    new WorldReadiness(new WorldSceneIdentity(7, "private-world"), TransformState.Identity), 3);
                Check(RobotKitDiagnostics.LastComposedDiscovery(() => next) == null
                    && RobotKitDiagnostics.LastComposedDiscovery(() => null) == null
                    && RobotKitDiagnostics.LastComposedDiscovery(() => throw new InvalidOperationException()) == null,
                    "a reason never crosses into another world or an unreadable observation");
                RobotKitDiagnostics.ReportComposedDiscovery(next, RobotComposedRefusal.HostCount);
                Check(logger.Messages.Count == count + 1, "real world lifetime replacement reports again even with reused numeric scene handle");
                RobotKitDiagnostics.ReportComposedDiscovery(null, RobotComposedRefusal.HostCount);
                Check(logger.Messages.Count == count + 1, "missing observation cannot reset a real world diagnostic budget");
                var replacement = new WorldSessionSnapshot(WorldSessionPhase.Running, new Session("private-replacement"), next.World, 4);
                Check(RobotKitDiagnostics.LastComposedDiscovery(() => replacement) == null, "a reason never crosses into another session");
                RobotKitDiagnostics.ReportComposedDiscovery(replacement, RobotComposedRefusal.HostCount);
                Check(logger.Messages.Count == count + 2, "actual session replacement reports again with the same world identity");

                const string timedOut = "RobotKit composed context retired: VerificationTimedOut.";
                const string readFailed = "RobotKit composed context retired: NativeIdentityReadFailed.";
                RobotKitDiagnostics.ReportComposedRetirement(state, RobotComposedRetirement.VerificationTimedOut);
                RobotKitDiagnostics.ReportComposedRetirement(next, RobotComposedRetirement.VerificationTimedOut);
                RobotKitDiagnostics.ReportComposedRetirement(replacement, RobotComposedRetirement.VerificationTimedOut);
                RobotKitDiagnostics.ReportComposedRetirement(state, RobotComposedRetirement.NativeIdentityReadFailed);
                for (var i = 0; i < 1000; i++)
                {
                    RobotKitDiagnostics.ReportComposedRetirement(rebound, RobotComposedRetirement.VerificationTimedOut);
                    RobotKitDiagnostics.ReportComposedRetirement(next, RobotComposedRetirement.VerificationTimedOut);
                    RobotKitDiagnostics.ReportComposedRetirement(replacement, RobotComposedRetirement.VerificationTimedOut);
                    RobotKitDiagnostics.ReportComposedRetirement(state, RobotComposedRetirement.NativeIdentityReadFailed);
                    RobotKitDiagnostics.ReportComposedDiscovery(state, RobotComposedRefusal.HostCount);
                    RobotKitDiagnostics.ReportComposedDiscovery(next, RobotComposedRefusal.HostCount);
                    RobotKitDiagnostics.ReportComposedDiscovery(replacement, RobotComposedRefusal.HostCount);
                }
                Check(logger.Messages.Skip(count + 2).SequenceEqual(new[] { timedOut, timedOut, timedOut, readFailed }),
                    "interleaved retirement and discovery retain each world's reason budget across publications and sessions");
            }
            finally { RobotKitDiagnostics.Clear(logger); }
        }

        private sealed class DiscoveryLogger : IModLogger
        {
            internal readonly System.Collections.Generic.List<string> Messages = new();
            public void Info(string message) => Messages.Add(message);
            public void Debug(string message) => throw new InvalidOperationException("Discovery must log at Info.");
            public void Warn(string message) => throw new InvalidOperationException("Unexpected warning.");
            public void Error(string message) => throw new InvalidOperationException("Unexpected error.");
            public void Error(Exception exception, string message) => throw new InvalidOperationException("Raw exception escaped.");
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
            private readonly string id;
            internal Session(string id = "test.session") { this.id = id; }
            public string SessionId => id;
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
