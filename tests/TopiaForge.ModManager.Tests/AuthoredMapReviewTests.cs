using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class AuthoredMapReviewTests
    {
        internal static void Run(string test)
        {
            if (test == "package" || test == "all") SnapshotPackageOwnership();
            if (test == "scene" || test == "all") LongSceneIdentity();
            if (test == "bounds" || test == "all") AnchorBounds();
            Console.WriteLine("Authored map review checks passed: " + test + ".");
        }

        private static void SnapshotPackageOwnership()
        {
            var folder = Directory.CreateTempSubdirectory("map-package-ownership-");
            try
            {
                using var fixture = new SessionFixture(folder.FullName);
                fixture.Parent.ConfigureSessions(fixture.Hosted);
                var package = Directory.CreateDirectory(Path.Combine(folder.FullName, "package"));
                File.WriteAllText(Path.Combine(package.FullName, "anchors.json"), Anchors("consumer"));
                using var source = IndexedMap();
                var map = source.ForPackage(_ => OperationResult<byte[]>.Success(Encoding.UTF8.GetBytes(Anchors("provider"))));
                SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(new MapWorld(map)));
                IAuthoredMap? notification = null;
                WorldReadiness? notifiedWorld = null;
                fixture.Parent.Sessions.StateChanged += snapshot =>
                {
                    if (snapshot.Phase == WorldSessionPhase.Running) { notification = snapshot.World!.RequireMap(); notifiedWorld = snapshot.World; }
                };
                fixture.Launch();
                var session = SessionFixture.ModeSession!;
                Assert(session.World.Spawn == MapWorld.Settled && session.World.RequestedSpawn == TransformState.Identity,
                    "the direct package projection preserves the accepted pose and requested marker separately");
                Assert(session.World.RequireMap().ResolveAnchors("anchors.json")["choice"].Id == "consumer", "direct session uses consumer package");
                IWorldSessionService sessions = ((IInternalWorldSessionContext)session.Context).Sessions;
                var retained = sessions.Current.World!.RequireMap();
                Assert(sessions.Current.World!.Spawn == MapWorld.Settled && sessions.Current.World!.RequestedSpawn == TransformState.Identity
                    && notifiedWorld!.Spawn == MapWorld.Settled && notifiedWorld.RequestedSpawn == TransformState.Identity,
                    "snapshot and notification projections retain both spawn values");
                Assert(retained.ResolveAnchors("anchors.json")["choice"].Id == "consumer", "snapshot anchors must use the consuming package, not the provider");
                Assert(notification!.ResolveAnchors("anchors.json")["choice"].Id == "consumer", "notification anchors use the consuming package");
                var stop = session.StopAsync(); fixture.Wait(stop);
                Assert(!retained.IsAlive && !retained.TryGetEntity("consumer", out _), "retained snapshot cannot access ended map");
                Assert(retained.ResolveAnchors("anchors.json").ErrorCode == ModErrorCode.InvalidState, "ended snapshot refuses anchor reads");
                using var successor = IndexedMap();
                SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(new MapWorld(successor, knownRequest: false)));
                fixture.Launch("next-map");
                Assert(SessionFixture.ModeSession!.World.RequestedSpawn == null && notifiedWorld!.RequestedSpawn == null,
                    "replacement projections must not inherit the previous world's known request");
                Assert(!retained.IsAlive && SessionFixture.ModeSession!.World.RequireMap().Entity("consumer").Entity.IsAlive,
                    "old snapshot cannot become a handle into the next generation");
            }
            finally { folder.Delete(true); }
        }

        private static void LongSceneIdentity()
        {
            var scene = new string('s', 128);
            var json = "{\"scenes\":{\"" + scene + "\":{\"entities\":{\"entry\":{\"components\":{}}}}}}";
            Assert(AuthoredProject.Read(Encoding.UTF8.GetBytes(json)).Scenes.ContainsKey(scene), "runtime accepts 128-character scene ids");
            using var map = FakeAuthoredMap.FromProject(json);
            Assert(map.SceneId == scene && map.Entity("entry").SceneId == scene, "testing kit accepts every runtime scene identity");
            Expect<ArgumentException>(() => new FakeAuthoredMap(sceneId: scene + "s"));
        }

        private static void AnchorBounds()
        {
            using var fake = new FakeAuthoredMap().AddEntity("entry", "Entry");
            IAuthoredMap map = fake;
            var anchors = new System.Collections.Generic.List<MapAnchor>();
            for (var i = 0; i < 1024; i++) anchors.Add(new MapAnchor("key" + i, "entry"));
            Assert(map.ResolveAnchors(anchors).IsComplete, "1024 anchors are accepted");
            anchors.Add(new MapAnchor("overflow", "entry"));
            Assert(map.ResolveAnchors(anchors).ErrorCode == ModErrorCode.InvalidArgument, "1025 anchors are refused");
            fake.WithAnchorFile("large.json", new string(' ', 256 * 1024 + 1));
            Assert(map.ResolveAnchors("large.json").ErrorCode == ModErrorCode.InvalidArgument, "oversized anchor file is refused");
        }

        private static string Anchors(string id) => "{\"schemaVersion\":1,\"anchors\":[{\"key\":\"choice\",\"entityId\":\"" + id + "\"}]}";
        private static AuthoredMapIndex IndexedMap()
        {
            var project = AuthoredProject.Read(Project("\"provider\":{\"components\":{}},\"consumer\":{\"components\":{}}"));
            return new AuthoredMapIndex("example-map", project.Scenes["scene"], new MapRevision(new string('a', 64), 100, ""),
                new[] { new AuthoredMarker("provider", new FakeEntity("provider", "Provider", Vec3.Zero), true),
                    new AuthoredMarker("consumer", new FakeEntity("consumer", "Consumer", Vec3.Zero), true) },
                AuthoredAffine.Compose(project.Scenes["scene"].Records, _ => AuthoredAffine.Identity, AuthoredAffine.Identity),
                p => p, _ => TransformState.Identity);
        }
        private sealed class MapWorld : IWorldInstance
        {
            private readonly AuthoredMapIndex map;
            internal static readonly TransformState Settled = new TransformState(new Vec3(0, 0.13f, 0), Quat.Identity, new Vec3(1, 1, 1));
            internal MapWorld(AuthoredMapIndex map, bool knownRequest = true)
            { this.map = map; Readiness = new WorldReadiness(new WorldSceneIdentity(-12, "World"), Settled, knownRequest ? TransformState.Identity : (TransformState?)null, map); }
            public WorldReadiness Readiness { get; }
            public void Dispose() => map.Dispose();
        }
    }
}
