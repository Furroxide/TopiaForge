using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods.Internal;
using TopiaForge.Worlds;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class PublishedMapDiscoveryTests
    {
        internal static async Task RunAsync(string repo)
        {
            var root = Directory.CreateTempSubdirectory("published-maps-");
            try
            {
                Corpus(repo);
                await Discovery(root.FullName);
                await SharedStore(root.FullName);
                HostRestriction(root.FullName);
                await OnlineRefusal(root.FullName);
                await PublishedMapCacheIdentityTests.RunAsync(root.FullName);
                Console.WriteLine("Published map discovery passed: shared corpus, bounded identities/listing, safe refusal, shared static/discovered cache, offline reuse, cancellation, ownership and host restriction.");
            }
            finally { root.Delete(true); }
        }
        private static void Corpus(string repo)
        {
            using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "tests/fixtures/maps/published-source-cases.json")));
            var count = 0;
            foreach (var test in corpus.RootElement.EnumerateArray())
            {
                var accepted = false;
                try { PublishedMapLibrary.Parse(Encoding.UTF8.GetBytes(test.GetProperty("json").GetString()!), default); accepted = true; }
                catch (FormatException) { }
                Assert(accepted == test.GetProperty("valid").GetBoolean(), "source corpus " + test.GetProperty("name").GetString()); count++;
            }
            Expect<FormatException>(() => PublishedMapLibrary.Parse(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"sources\":[]}" + new string(' ', PublishedMapLibrary.MaximumBytes)), default));
            Expect<FormatException>(() => PublishedMapLibrary.Parse(Bytes(Enumerable.Range(0, 4097).Select(i => new { map = "map" + i, scene = "scene", name = "Map" }).ToArray()), default));
            var normal = new PublishedMapSource("Example", "scene", "Map");
            Assert(normal.WorldId.EndsWith(".Example", StringComparison.Ordinal), "readable discovery ids end in the canonical map id");
            Assert(!string.Equals(normal.WorldId, new PublishedMapSource("example", "scene", "Other").WorldId, StringComparison.OrdinalIgnoreCase), "case-sensitive source ids cannot collide in case-insensitive declarations");
            Assert(normal.WorldId != new PublishedMapSource("Example", "other", "Map").WorldId, "scene identity is stable and distinct");
            Assert(normal.WorldId == new PublishedMapSource("Example", "scene", "Renamed").WorldId, "renaming does not change identity");
            Assert(new PublishedMapSource(new string('x', 64), new string('s', 128), "Map").WorldId.Length <= 96, "long source ids fit declaration bounds without truncating the cache id");
            Console.WriteLine("Published source corpus passed (" + count + " shared cases).");
        }
        private static byte[] Bytes(object sources) => JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, sources });
        private static async Task Discovery(string root)
        {
            using var fixture = new BuiltinWorldFixture { PublishedMapLibraryPath = Path.Combine(root, "sources.json"), WorldFamilyId = PublishedMapSource.Family };
            var provider = new PublishedMapDiscoverySource();
            Assert((await provider.DiscoverAsync(fixture, default)).Value!.Count == 0, "a missing list is empty without network");
            File.WriteAllBytes(fixture.PublishedMapLibraryPath, Bytes(new[] { new { map = "example", scene = "scene", name = "Courtyard" }, new { map = "another", scene = "scene", name = "Workshop" } }));
            fixture.MaximumResults = 1;
            var first = await provider.DiscoverAsync(fixture, default);
            Assert(first.Succeeded && first.Value!.Count == 1 && first.Value[0].Description!.Contains("downloaded when you play", StringComparison.Ordinal), "bounded listing discloses deferred download");
            fixture.WorldId = first.Value![0].Id;
            var capture = new Capture(); fixture.Fake.Extensions.Register<IInternalMapWorldService>(capture);
            await provider.LoadAsync(fixture, default);
            Assert(capture.Calls == 1 && capture.Request!.PublishedId != null && capture.Request.SceneId == "scene" && capture.Request.ReadFile == null, "discovery forwards published content to the shared map service");
            fixture.Transition = WorldLoadTransition.AdditiveArena;
            Assert((await provider.LoadAsync(fixture, default)).ErrorCode == ModErrorCode.InvalidArgument && capture.Calls == 1, "additive loading refuses before map access");
            fixture.Transition = WorldLoadTransition.SceneReplacement;
            File.WriteAllBytes(fixture.PublishedMapLibraryPath, Bytes(Array.Empty<object>()));
            Assert((await provider.LoadAsync(fixture, default)).ErrorCode == ModErrorCode.NotFound && capture.Calls == 1, "removed sources are revalidated on load");
            File.WriteAllText(fixture.PublishedMapLibraryPath, "{\"secret\":\"https://example.test/private\"}");
            var invalid = await provider.DiscoverAsync(fixture, default);
            Assert(!invalid.Succeeded && !invalid.ErrorMessage.Contains("private", StringComparison.Ordinal) && !invalid.ErrorMessage.Contains(root, StringComparison.Ordinal), "malformed list errors hide payloads and paths");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Assert((await provider.DiscoverAsync(fixture, cancel.Token)).ErrorCode == ModErrorCode.Cancelled, "pre-cancelled discovery never reads its source");
            fixture.FamilyId = "example.wrong.family";
            Assert((await provider.DiscoverAsync(fixture, default)).ErrorCode == ModErrorCode.InvalidArgument, "wrong family refuses");
            fixture.FamilyId = PublishedMapSource.Family;
            fixture.MaximumResults = 0;
            Assert((await provider.DiscoverAsync(fixture, default)).ErrorCode == ModErrorCode.InvalidArgument, "invalid result budget refuses");
            fixture.MaximumResults = 1;
            var duplicated = new { map = "same", scene = "scene", name = "Map" };
            File.WriteAllBytes(fixture.PublishedMapLibraryPath, Bytes(new[] { duplicated, duplicated }));
            Assert(!(await provider.DiscoverAsync(fixture, default)).Succeeded, "duplicates beyond the requested page still refuse");
            fixture.Fake.Dispose();
            Assert((await provider.DiscoverAsync(fixture, default)).ErrorCode == ModErrorCode.Cancelled, "owner cancellation stops discovery");
        }
        private static async Task SharedStore(string root)
        {
            var cache = new MapCache(Path.Combine(root, "cache")); var transport = new Transport();
            var backend = new MapWorldServiceTests.Backend();
            using var service = new MapWorldService(cache, transport, () => backend);
            using var staticWorld = Fixture(root);
            var initial = service.LoadAsync(staticWorld, new InternalMapWorldRequest("example", "scene", null), default);
            staticWorld.Native.Succeed(); var first = await initial;
            Assert(first.Succeeded && first.Value is IInternalMapWorldInstance && cache.Read("example") != null, "static maps publish a nonhostable instance and canonical cache entry");
            first.Value!.Dispose();
            transport.Fail = true;
            using var discovered = Fixture(root);
            discovered.WorldFamilyId = PublishedMapSource.Family;
            discovered.PublishedMapLibraryPath = Path.Combine(root, "shared.json");
            File.WriteAllBytes(discovered.PublishedMapLibraryPath, Bytes(new[] { new { map = "example", scene = "scene", name = "Courtyard" } }));
            discovered.WorldId = new PublishedMapSource("example", "scene", "Courtyard").WorldId;
            discovered.Fake.Extensions.Register<IInternalMapWorldService>(service);
            var pending = new PublishedMapDiscoverySource().LoadAsync(discovered, default);
            discovered.Native.Succeed(); var second = await pending;
            Assert(second.Succeeded && second.Value is IInternalMapWorldInstance && transport.Cached != null, "discovered maps replay the static world's verified cached bytes offline");
            Assert(backend.SceneArgument == "scene" && backend.Label == "map:example", "shared native seam uses canonical map id and selected scene, never discovery id");
            second.Value!.Dispose();
            Assert(backend.ContentDisposals == 2, "both returned worlds own and dispose their imported content");
        }
        private static BuiltinWorldFixture Fixture(string root) => new BuiltinWorldFixture
        { SpawnPolicy = new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), SceneTransitions = new MapWorldServiceTests.Transitions(), MapCacheDirectory = root };
        private static void HostRestriction(string root)
        {
            using var fixture = new SessionFixture(Path.Combine(root, "host"));
            var parentLibrary = ((IInternalMapStorageContext)fixture.Parent).PublishedMapLibraryPath;
            File.WriteAllBytes(parentLibrary, Bytes(new[] { new { map = "example", scene = "scene", name = "Map" } }));
            SessionFixture.Load = (context, _) =>
            {
                var childLibrary = ((IInternalMapStorageContext)context.Context).PublishedMapLibraryPath;
                Assert(childLibrary == parentLibrary && PublishedMapLibrary.Read(childLibrary, default).Count == 1,
                    "production child scope retains its installation's map library");
                return Task.FromResult(OperationResult<IWorldInstance>.Success(new RestrictedWorld()));
            };
            fixture.Launch();
            Assert(!fixture.Hosted.CanHostCurrentWorld, "a nonstatic map instance cannot become a multiplayer host");
        }
        private static async Task OnlineRefusal(string root)
        {
            var net = new LoopbackNetPair(); var log = new RecordingRuntimeLog();
            var runtime = new MultiplayerRuntime(() => net.Host, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
            try
            {
                Assert(runtime.Connect() && net.Host.StartHosting(new InternalHostSettings("Map fixture", 4)).Succeeded, "loopback host starts");
                runtime.BeforeModsUpdate(0.016f); runtime.AfterModsUpdate(0.016f);
                using var fixture = new SessionFixture(Path.Combine(root, "online"), replication: new MultiplayerReplication(runtime));
                var backend = new MapWorldServiceTests.Backend(); using var service = new MapWorldService(new MapCache(Path.Combine(root, "never")), new Transport(), () => backend);
                var result = await service.LoadAsync(new LoadContext(fixture.Parent), new InternalMapWorldRequest("example", "scene", null), default);
                Assert(result.ErrorCode == ModErrorCode.Unavailable && result.ErrorMessage.Contains("single player", StringComparison.Ordinal)
                    && backend.Imported == 0, "the shared service refuses multiplayer before native or download work");
            }
            finally { runtime.Disconnect(); }
        }
        private sealed class LoadContext : IWorldLoadContext
        {
            internal LoadContext(IModContext context) { ContextValue = context; }
            private IModContext ContextValue { get; }
            public IModContext Context => ContextValue;
            public string SessionId => "fixture";
            public string TargetId => "example.target";
            public string WorldId => "example.world";
            public string? WorldFamilyId => PublishedMapSource.Family;
            public WorldLoadTransition Transition => WorldLoadTransition.SceneReplacement;
            public WorldSpawnPolicy SpawnPolicy => new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault);
        }
        private sealed class RestrictedWorld : IInternalMapWorldInstance
        { public WorldReadiness Readiness { get; } = new WorldReadiness(new WorldSceneIdentity(-12, "World"), TransformState.Identity); public void Dispose() { } }
        private sealed class Capture : IInternalMapWorldService
        {
            internal int Calls; internal InternalMapWorldRequest? Request;
            public Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, InternalMapWorldRequest request, CancellationToken token)
            { Calls++; Request = request; return Task.FromResult(OperationResult<IWorldInstance>.Failure(ModErrorCode.Unavailable, "Native seam reached.")); }
        }
        private sealed class Transport : IMapTransport
        {
            internal bool Fail; internal MapCacheRecord? Cached;
            public Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token)
            {
                Cached = cached;
                if (Fail) throw new HttpRequestException();
                var bytes = Project();
                return Task.FromResult(new MapResponse(200, new MemoryStream(bytes), bytes.Length, "etag", null));
            }
        }
    }
}
