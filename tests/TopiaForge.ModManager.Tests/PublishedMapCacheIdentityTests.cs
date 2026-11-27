using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Worlds;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class PublishedMapCacheIdentityTests
    {
        internal static async Task RunAsync(string root)
        {
            var cache = new MapCache(Path.Combine(root, "case-cache"));
            var transport = new Transport();
            var backend = new Backend();
            using var service = new MapWorldService(cache, transport, () => backend);
            var library = Path.Combine(root, "case-sources.json");
            File.WriteAllBytes(library, JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                sources = new[] { new { map = "example", scene = "scene", name = "Lower" }, new { map = "Example", scene = "scene", name = "Upper" } }
            }));
            async Task<OperationResult<IWorldInstance>> Play(string id)
            {
                using var context = new BuiltinWorldFixture
                {
                    PublishedMapLibraryPath = library,
                    WorldFamilyId = PublishedMapSource.Family,
                    WorldId = new PublishedMapSource(id, "scene", "Map").WorldId,
                    SpawnPolicy = new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault),
                    SceneTransitions = new MapWorldServiceTests.Transitions()
                };
                context.Fake.Extensions.Register<IInternalMapWorldService>(service);
                var pending = new PublishedMapDiscoverySource().LoadAsync(context, default);
                context.Native.Succeed();
                var result = await pending;
                if (result.Succeeded) result.Value!.Dispose();
                return result;
            }
            var firstHash = new MapDocument(transport.Bytes).Sha256;
            Assert((await Play("example")).Succeeded, "first discovered source loads through the production map service");
            transport.Fail = true;
            var missing = await Play("Example");
            Assert(missing.ErrorCode == ModErrorCode.Unavailable,
                "an unplayed case-distinct discovered source must not borrow another map's offline bytes");
            Assert(transport.Cached == null, "case-distinct maps must not share conditional request validators");
            transport.Fail = false;
            transport.Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Project()).Replace("Courtyard", "Workshop", StringComparison.Ordinal));
            var secondHash = new MapDocument(transport.Bytes).Sha256;
            Assert((await Play("Example")).Succeeded, "second discovered source downloads its own bytes");
            transport.Fail = true;
            Assert((await Play("example")).Succeeded && backend.Hash == firstHash, "lowercase source retains its exact offline import");
            Assert((await Play("Example")).Succeeded && backend.Hash == secondHash, "uppercase source retains its exact offline import");
            if (OperatingSystem.IsWindows())
            {
                var lowerIndex = Array.Find(Directory.GetFiles(Path.Combine(root, "case-cache"), "id-*.json"),
                    path => File.ReadAllText(path).Contains("\"mapId\":\"example\"", StringComparison.Ordinal))!;
                File.SetAttributes(lowerIndex, FileAttributes.ReadOnly);
                try
                {
                    try { cache.Remove("example"); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
                    Assert(cache.Read("example") == null, "failed index deletion still invalidates only its own source");
                    Assert((await Play("Example")).Succeeded && backend.Hash == secondHash,
                        "one source's persisted removal marker must not block a case-distinct consumer");
                }
                finally { File.SetAttributes(lowerIndex, FileAttributes.Normal); }
            }
            cache.Remove("example");
            Assert((await Play("example")).ErrorCode == ModErrorCode.Unavailable, "removed source no longer plays offline");
            Assert((await Play("Example")).Succeeded && backend.Hash == secondHash, "removing one case-distinct map preserves the other consumer");
            var index = Directory.GetFiles(Path.Combine(root, "case-cache"), "id-*.json")[0];
            var saved = File.ReadAllText(index);
            try
            {
                File.WriteAllText(index, saved.Replace("\"mapId\":\"Example\"", "\"mapId\":\"example\"", StringComparison.Ordinal));
                Assert((await Play("Example")).ErrorCode == ModErrorCode.Unavailable,
                    "a cache index with another exact source identity cannot supply offline bytes");
                File.WriteAllText(index, saved.Replace("\"mapId\":\"Example\"", "\"legacy\":\"Example\"", StringComparison.Ordinal));
                Assert((await Play("Example")).ErrorCode == ModErrorCode.Unavailable,
                    "a cache index without source identity cannot supply offline bytes");
            }
            finally { File.WriteAllText(index, saved); }
            Assert(backend.Inner.Imported == backend.Inner.ContentDisposals, "all discovered imports release their content");
            Console.WriteLine("Published cache identity passed: unplayed refusal, validators, two exact offline imports, independent removal and disposal.");
        }
        private sealed class Backend : IMapNativeBackend
        {
            internal readonly MapWorldServiceTests.Backend Inner = new MapWorldServiceTests.Backend();
            internal string? Hash;
            public Uri PublicBase => Inner.PublicBase;
            public Uri ResolvePublished(string id) => Inner.ResolvePublished(id);
            public Task<OperationResult<IMapImportTransaction>> PrepareAsync(MapDocument document, string? sceneId, string label, WorldSceneIdentity scene, CancellationToken token)
            { Hash = document.Sha256; return Inner.PrepareAsync(document, sceneId, label, scene, token); }
            public void Dispose() => Inner.Dispose();
        }
        private sealed class Transport : IMapTransport
        {
            internal byte[] Bytes = Project();
            internal bool Fail;
            internal MapCacheRecord? Cached;
            public Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token)
            {
                Cached = cached;
                if (Fail) throw new System.Net.Http.HttpRequestException();
                return Task.FromResult(new MapResponse(200, new MemoryStream(Bytes), Bytes.Length, "validator"));
            }
        }
    }
}
