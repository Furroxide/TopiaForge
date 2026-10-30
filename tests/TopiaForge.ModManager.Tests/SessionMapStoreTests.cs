using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class SessionMapStoreTests
    {
        internal static async Task RunAsync()
        {
            NativePublishedMapBindingsTests.Run();
            await MapManifestTransportTests.RunAsync();
            var root = Directory.CreateTempSubdirectory("exact-maps-");
            try
            {
                var original = new MapDocument(Project());
                var identity = Identity(original.Packed);
                var live = new MapDocument(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Project()).Replace("Courtyard", "Updated")));
                var transport = new Transport { Bytes = live.Packed };
                var cache = new MapCache(root.FullName);
                var store = new MapStore(cache, new MapEndpointPolicy(Transport.Origin), transport);
                var copies = 0;
                Task<OperationResult<byte[]>> Copy(CancellationToken token, IProgress<long>? progress)
                { copies++; return Task.FromResult(OperationResult<byte[]>.Success(original.Packed)); }
                var first = await store.GetExactAsync(identity, Transport.Origin, Copy, default);
                Assert(first.Succeeded && first.Value!.Document.Sha256 == identity.Sha256 && copies == 1, "changed publication falls back to exact host revision");
                Assert(cache.Read("example")!.Record.Sha256 == live.Sha256, "host copy never replaces current publication");
                var fetches = transport.Calls;
                Assert((await store.GetExactAsync(identity, Transport.Origin, Copy, default)).Succeeded && copies == 1 && transport.Calls == fetches,
                    "reconnect uses hash cache without network or host");
                File.WriteAllBytes(Path.Combine(root.FullName, identity.Sha256 + ".map"), live.Packed);
                Assert((await store.GetExactAsync(identity, Transport.Origin, Copy, default)).Succeeded && copies == 2, "corrupt hash cache falls through and is repaired");
                var uncached = new MapStore(new MapCache(Path.Combine(root.FullName, "uncached")), new MapEndpointPolicy(Transport.Origin), transport);
                var bad = await uncached.GetExactAsync(identity, Transport.Origin,
                    (_, __) => Task.FromResult(OperationResult<byte[]>.Success(live.Packed)), default);
                Assert(bad.ErrorCode == ModErrorCode.Conflict, "host bytes with wrong digest cannot import");
                var hostile = Encoding.UTF8.GetBytes("{\"scenes\":{},\"scenes\":{}}");
                Assert((await uncached.GetExactAsync(Identity(hostile), Transport.Origin,
                    (_, __) => Task.FromResult(OperationResult<byte[]>.Success(hostile)), default)).ErrorCode == ModErrorCode.InvalidArgument,
                    "correct hash does not bypass strict document validation");
                var missing = new InternalMapIdentity("example", "missing", identity.Sha256, identity.Size, identity.Modified);
                Assert((await uncached.GetExactAsync(missing, Transport.Origin, Copy, default)).ErrorCode == ModErrorCode.NotFound,
                    "selected scene must exist before publishing host copy");
                var tooLarge = new InternalMapIdentity("example", "scene", identity.Sha256,
                    InternalMapIdentity.MaximumTransferBytes + 1, string.Empty);
                var beforeLarge = copies;
                Assert((await uncached.GetExactAsync(tooLarge, Transport.Origin, Copy, default)).ErrorCode == ModErrorCode.Unavailable && copies == beforeLarge,
                    "fallback over 4 MiB never requests host bytes");
                transport.Bytes = original.Packed;
                var before = copies;
                Assert((await uncached.GetExactAsync(identity, Transport.Origin, Copy, default)).Succeeded && copies == before, "matching live publication needs no host transfer");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                Assert((await store.GetExactAsync(identity, Transport.Origin, Copy, cancelled.Token)).ErrorCode == ModErrorCode.Cancelled,
                    "even a cached revision respects cancellation");
                Assert(!Directory.EnumerateFiles(root.FullName, "*.tmp-*", SearchOption.AllDirectories).Any(), "no partial cache files");
                await NativeBoundary(root.FullName, identity, original.Packed, live.Packed);
                Console.WriteLine("SessionMapStoreTests passed (11 exact acquisition/cache/validation checks; 3 native boundary cases).");
            }
            finally { root.Delete(true); }
        }

        private static async Task NativeBoundary(string root, InternalMapIdentity expected, byte[] exact, byte[] wrong)
        {
            foreach (var kind in new[] { "exact", "wrong", "cancel" })
            {
                using var fixture = new BuiltinWorldFixture
                {
                    ExpectedMap = expected,
                    SpawnPolicy = new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault),
                    SceneTransitions = new MapWorldServiceTests.Transitions()
                };
                using var cancellation = new CancellationTokenSource();
                fixture.ReadHostMap = (_, __) =>
                {
                    if (kind == "cancel") cancellation.Cancel();
                    return Task.FromResult(OperationResult<byte[]>.Success(kind == "wrong" ? wrong : exact));
                };
                var began = false;
                fixture.BeginMapHost = () => { began = true; return OperationResult<bool>.Success(true); };
                var backend = new MapWorldServiceTests.Backend { OnPrepare = () => Assert(began, "map stream replacement runs before native parsing") };
                using var service = new MapWorldService(new MapCache(Path.Combine(root, kind)), new Transport { Bytes = wrong }, () => backend);
                var pending = service.LoadAsync(fixture, new InternalMapWorldRequest("example", null, null), cancellation.Token);
                fixture.Native.Succeed();
                var result = await pending;
                if (kind == "exact")
                {
                    Assert(result.Succeeded && backend.SceneArgument == "scene" && backend.Imported == 1, "host scene is forced even when declaration infers its scene");
                    var map = (IInternalMapWorldInstance)result.Value!;
                    Assert(map.Identity!.Sha256 == expected.Sha256 && map.PackedMap.Span.SequenceEqual(exact), "running instance retains exact identity and host bytes");
                    map.Dispose();
                }
                else Assert(result.ErrorCode == (kind == "wrong" ? ModErrorCode.Conflict : ModErrorCode.Cancelled) && backend.Imported == 0,
                    "invalid or cancelled transfer never enters native import");
            }
        }

        internal static InternalMapIdentity Identity(byte[] bytes) => new InternalMapIdentity("example", "scene", MapDocument.Hash(bytes), bytes.Length, string.Empty);
        private sealed class Transport : IMapTransport
        {
            internal static readonly Uri Origin = new Uri("https://maps.example.test/");
            internal byte[] Bytes = Project();
            internal int Calls;
            public Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token)
            { Calls++; return Task.FromResult(new MapResponse(200, new MemoryStream(Bytes), Bytes.Length)); }
        }
    }
}
