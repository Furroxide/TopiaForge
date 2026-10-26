using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class MapStoreTests
    {
        private static readonly Uri Origin = new Uri("https://maps.example.test/");
        private static readonly Uri Project = new Uri(Origin, "map/example/project.roboworld");
        internal static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "map-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                References();
                await RevalidationAndTrust(Path.Combine(root, "cache"));
                CacheBound(Path.Combine(root, "bound"));
                await DownloadFailures(Path.Combine(root, "failures"));
                await TransportHeaders();
                await Loopback(Path.Combine(root, "http"));
                Console.WriteLine("MapStoreTests passed (trust, cache, revalidation, offline, gone, cancellation and loopback bounds).");
            }
            finally { Directory.Delete(root, true); }
        }
        private static void References()
        {
            var policy = new MapEndpointPolicy(Origin);
            policy.Require(Project);
            foreach (var value in new[] { "http://maps.example.test/map/x", "http://maps.example.test:443/map/x", "https://other.example.test/map/x",
                "https://maps.example.test:8443/map/x", "https://name@maps.example.test/map/x", "https://maps.example.test/map/x#fragment" })
                Expect<FormatException>(() => policy.Require(new Uri(value)));
            Expect<FormatException>(() => new MapEndpointPolicy(new Uri("http://maps.example.test/"), true));
        }
        private static async Task RevalidationAndTrust(string root)
        {
            var cache = new MapCache(root);
            var transport = new Transport();
            var store = new MapStore(cache, new MapEndpointPolicy(Origin), transport);
            var first = await store.GetAsync("example", Project, default);
            Assert(first.Succeeded && !first.Value!.Offline && transport.Cached == null, "first fetch");
            var digest = first.Value!.Document.Sha256;
            Assert(cache.Read("example")?.Record.Etag == "\"one\"", "validator saved");
            transport.Status = 304;
            var unchanged = await store.GetAsync("example", Project, default);
            Assert(unchanged.Succeeded && unchanged.Value!.Document.Sha256 == digest && transport.Cached?.Etag == "\"one\"", "304 retains exact verified bytes");
            transport.Fail = true;
            var offline = await store.GetAsync("example", Project, default);
            Assert(offline.Succeeded && offline.Value!.Offline && offline.Value.SavedUtc.Length > 0, "network-only fallback is disclosed");
            Check(await store.GetAsync("unplayed", Project, default), ModErrorCode.Unavailable);
            transport.Fail = false; transport.Status = 200;
            File.WriteAllBytes(Path.Combine(root, digest + ".map"), Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(MapDocumentTests.Project()).Replace("Courtyard", "Tampered", StringComparison.Ordinal)));
            Assert((await store.GetAsync("example", Project, default)).Succeeded && transport.Cached == null, "corrupt cache cannot supply validators or play");
            Assert(cache.Read("example")?.Record.Sha256 == digest, "refetch repairs corrupt content");
            transport.Status = 302;
            Assert((await store.GetAsync("example", Project, default)).ErrorCode == ModErrorCode.InvalidArgument, "redirect cannot use offline fallback");
            transport.Status = 403;
            Assert((await store.GetAsync("example", Project, default)).ErrorCode == ModErrorCode.Unavailable, "permission refusal cannot use offline fallback");
            foreach (var status in new[] { 404, 410 })
            {
                cache.Put("example", new MapDocument(MapDocumentTests.Project()), null, null);
                transport.Status = status;
                Assert((await store.GetAsync("example", Project, default)).ErrorCode == ModErrorCode.NotFound && cache.Read("example") == null,
                    "gone maps drop their copy");
            }
            if (OperatingSystem.IsWindows())
            {
                cache.Put("example", new MapDocument(MapDocumentTests.Project()), null, null);
                var index = Path.Combine(root, "id-example.json");
                File.SetAttributes(index, FileAttributes.ReadOnly);
                try
                {
                    transport.Status = 410;
                    Assert((await store.GetAsync("example", Project, default)).ErrorCode == ModErrorCode.NotFound, "gone map is refused even if unlink fails");
                    Assert(new MapCache(root).Read("example") == null, "gone tombstone prevents offline replay after restart");
                }
                finally { File.SetAttributes(index, FileAttributes.Normal); }
                cache.Put("example", new MapDocument(MapDocumentTests.Project()), null, null);
                Assert(cache.Read("example") != null, "fresh validated publication clears its tombstone");
            }
            Assert(!Directory.EnumerateFiles(root).Any(p => p.Contains(".tmp-")), "atomic cache leaves no partial file");
        }
        private static void CacheBound(string root)
        {
            var first = new MapDocument(MapDocumentTests.Project());
            var second = new MapDocument(MapDocumentTests.Project("\"other\":{\"components\":{}}"));
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cache = new MapCache(root, Math.Max(first.Packed.Length, second.Packed.Length), () => clock);
            cache.Put("first", first, "unsafe\r\nvalidator", null);
            cache.Put("CON", first, null, null);
            Assert(cache.Read("CON") != null, "published ids cannot become Windows device paths");
            Assert(cache.Read("first")?.Record.Etag == null, "header injection never reaches a request");
            clock = clock.AddMinutes(1);
            cache.Put("second", second, null, null);
            Assert(cache.Read("first") == null && cache.Read("second") != null, "least recently played content is evicted");
            Assert(Directory.EnumerateFiles(root, "*.map").Sum(p => new FileInfo(p).Length) <= Math.Max(first.Packed.Length, second.Packed.Length), "cache byte bound");
        }
        private static async Task DownloadFailures(string root)
        {
            var transport = new Transport();
            var store = new MapStore(new MapCache(root), new MapEndpointPolicy(Origin), transport);
            transport.Length = MapDocument.MaximumPackedBytes + 1;
            Check(await store.GetAsync("example", Project, default), ModErrorCode.InvalidArgument);
            transport.Length = 1;
            Check(await store.GetAsync("example", Project, default), ModErrorCode.External);
            transport.Length = null; transport.Bytes = new byte[MapDocument.MaximumPackedBytes + 1];
            Check(await store.GetAsync("example", Project, default), ModErrorCode.InvalidArgument);
            transport.Bytes = Gzip(new byte[MapDocument.MaximumJsonBytes + 1]);
            Check(await store.GetAsync("example", Project, default), ModErrorCode.InvalidArgument);
            transport.Bytes = Encoding.UTF8.GetBytes("not JSON");
            Check(await store.GetAsync("example", Project, default), ModErrorCode.InvalidArgument);
            using var cancellation = new CancellationTokenSource();
            transport.Stream = new Drip(cancellation);
            Check(await store.GetAsync("example", Project, cancellation.Token), ModErrorCode.Cancelled);
            Assert(!Directory.Exists(root) || !Directory.EnumerateFiles(root).Any(), "failed or cancelled bytes never enter cache");
            var before = transport.Calls;
            Check(await store.GetAsync("example", new Uri("http://maps.example.test/map/x"), default), ModErrorCode.InvalidArgument);
            Assert(transport.Calls == before, "untrusted source is refused before network");
        }
        private static async Task TransportHeaders()
        {
            var handler = new Handler();
            using var transport = new MapHttpTransport(handler);
            using var response = await transport.FetchAsync(Project, new MapCacheRecord { Etag = "\"one\"", LastModified = "Thu, 01 Jan 2026 00:00:00 GMT" }, default);
            Assert(handler.Conditional && response.Status == 304, "production transport sends validators and returns status unchanged");
        }
        private static async Task Loopback(string root)
        {
            // Actual socket reads exercise HttpClient framing; only the internal test policy permits HTTP loopback.
            foreach (var kind in new[] { "redirect", "lying", "oversize", "cancel" })
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var origin = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
                var serve = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    await using var stream = client.GetStream();
                    var request = new byte[4096];
                    var received = await stream.ReadAsync(request, cancellation.Token);
                    Assert(received > 0, "loopback receives a request");
                    var header = kind == "redirect" ? "HTTP/1.1 302 Found\r\nLocation: https://other.example.test/map\r\nContent-Length: 0\r\n\r\n"
                        : "HTTP/1.1 200 OK\r\nContent-Length: " + (kind == "oversize" ? MapDocument.MaximumPackedBytes + 1 : 2000) + "\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellation.Token);
                    if (kind == "cancel") { cancellation.Cancel(); return; }
                    if (kind == "lying") await stream.WriteAsync(Encoding.UTF8.GetBytes("{}"), cancellation.Token);
                });
                try
                {
                    using var transport = new MapHttpTransport();
                    var store = new MapStore(new MapCache(Path.Combine(root, kind)), new MapEndpointPolicy(origin, true), transport);
                    var result = await store.GetAsync("example", new Uri(origin, "project.roboworld"), cancellation.Token);
                    Assert(!result.Succeeded, "loopback " + kind + " refuses incomplete content");
                    Assert(!result.ErrorMessage.Contains(origin.ToString()), "loopback address stays private");
                    await serve;
                }
                finally { listener.Stop(); }
            }
        }
        private static void Check(OperationResult<MapAcquisition> result, ModErrorCode code)
        {
            Assert(result.ErrorCode == code && !result.Succeeded, "expected " + code + ", got " + result.ErrorCode);
            Assert(!result.ErrorMessage.Contains("://") && !result.ErrorMessage.Contains("\\") && !result.ErrorMessage.Contains("/"), "public failure has no URL or path");
        }
        private sealed class Transport : IMapTransport
        {
            internal int Status = 200; internal bool Fail; internal int Calls;
            internal byte[] Bytes = MapDocumentTests.Project(); internal long? Length; internal Stream? Stream; internal MapCacheRecord? Cached;
            public Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token)
            {
                Calls++; Cached = cached;
                if (Fail) throw new HttpRequestException("private backend reply " + uri);
                return Task.FromResult(new MapResponse(Status, Stream ?? new MemoryStream(Bytes), Length, "\"one\""));
            }
        }
        private sealed class Handler : HttpMessageHandler
        {
            internal bool Conditional;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Conditional = request.Headers.Contains("If-None-Match") && request.Headers.Contains("If-Modified-Since");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified) { Content = new ByteArrayContent(Array.Empty<byte>()) });
            }
        }
        private sealed class Drip : MemoryStream
        {
            private readonly CancellationTokenSource cancellation;
            internal Drip(CancellationTokenSource cancellation) { this.cancellation = cancellation; }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            { cancellation.Cancel(); return Task.FromCanceled<int>(token); }
        }
    }
}
