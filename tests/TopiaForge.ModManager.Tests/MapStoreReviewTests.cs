using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class MapStoreReviewTests
    {
        internal static async Task RunAsync(string root)
        {
            Directory.CreateDirectory(root);
            // Exercise the UI wrapper on the calling main thread before network awaits can resume on a worker.
            PlainOfflineNotices();
            await HeaderDeadlineStillBounded(Path.Combine(root, "headers"));
            await ChangedBodyGetsFullDeadline(Path.Combine(root, "deadline"));
            await ValidatedMemorySurvivesCacheFailure(Path.Combine(root, "blocked"));
            await TransientStatusPolicy(Path.Combine(root, "statuses"));
            Console.WriteLine("Map-store review passed: changed-body deadline, in-memory cache failure, transient status policy, bounded plain notices.");
        }
        private static async Task HeaderDeadlineStillBounded(string root)
        {
            var cache = new MapCache(root);
            cache.Put("example", new MapDocument(Project()), null, null);
            var reply = new Reply { WaitHeaders = true };
            var store = new MapStore(cache, new MapEndpointPolicy(Reply.Origin), reply);
            using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await store.GetAsync("example", Reply.Origin, caller.Token);
            Assert(result.Succeeded && result.Value!.Offline && result.Value.OfflineReason == MapOfflineReason.Slow
                && !caller.IsCancellationRequested, "cached revalidation still expires its own five-second header wait before caller cancellation");
        }
        private static async Task ChangedBodyGetsFullDeadline(string root)
        {
            var cache = new MapCache(root);
            var original = new MapDocument(Project());
            cache.Put("example", original, null, null);
            var changed = new MapDocument(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Project()).Replace("Courtyard", "Republished")));
            var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var origin = new Uri("http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + "/");
            var serving = Task.Run(async () =>
            {
                using var client = await server.AcceptTcpClientAsync(stop.Token);
                await using var stream = client.GetStream();
                var request = new byte[4096];
                var used = 0;
                var completeHeaders = false;
                while (used < request.Length)
                {
                    await stream.ReadExactlyAsync(request.AsMemory(used, 1), stop.Token);
                    used++;
                    if (used >= 4 && request[used - 4] == 13 && request[used - 3] == 10
                        && request[used - 2] == 13 && request[used - 1] == 10)
                    { completeHeaders = true; break; }
                }
                if (!completeHeaders) throw new InvalidOperationException("Loopback request headers exceeded their test bound.");
                var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + changed.Packed.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, stop.Token);
                // Real production HttpClient response headers arrive promptly; changed bytes arrive past the unchanged5s revalidation bound.
                await Task.Delay(TimeSpan.FromMilliseconds(5500), stop.Token);
                await stream.WriteAsync(changed.Packed, stop.Token);
            });
            try
            {
                using var transport = new MapHttpTransport();
                var store = new MapStore(cache, new MapEndpointPolicy(origin, true), transport);
                var acquired = await store.GetAsync("example", origin, stop.Token);
                Assert(acquired.Succeeded && !acquired.Value!.Offline && acquired.Value.Document.Sha256 == changed.Sha256,
                    "changed cached publication completes beyond revalidation deadline under its separate body budget");
                await serving;
                Assert(cache.Read("example")!.Record.Sha256 == changed.Sha256, "the changed revision becomes the verified offline copy");
            }
            finally { stop.Cancel(); server.Stop(); try { await serving; } catch (Exception) when (stop.IsCancellationRequested) { } }
        }
        private static async Task ValidatedMemorySurvivesCacheFailure(string root)
        {
            File.WriteAllText(root, "A file cannot serve as the cache directory.");
            var bytes = Project();
            var transport = new Reply { Bytes = bytes };
            var store = new MapStore(new MapCache(root), new MapEndpointPolicy(Reply.Origin), transport);
            var acquired = await store.GetAsync("example", Reply.Origin, default);
            Assert(acquired.Succeeded && !acquired.Value!.Offline && acquired.Value.CacheWriteFailed
                && acquired.Value.Document.Sha256 == MapDocument.Hash(bytes), "validated downloaded map survives an unwritable cache in memory");
            var expected = SessionMapStoreTests.Identity(bytes);
            var exact = await store.GetExactAsync(expected, Reply.Origin, null, default);
            Assert(exact.Succeeded && exact.Value!.CacheWriteFailed && expected.Matches(exact.Value.Document.Packed),
                "exact revision keeps cache-save diagnostics without weakening hash validation");
            transport.Status = 503;
            var hostCopy = await store.GetExactAsync(expected, Reply.Origin,
                (_, __) => Task.FromResult(OperationResult<byte[]>.Success(bytes)), default);
            Assert(hostCopy.Succeeded && hostCopy.Value!.CacheWriteFailed && expected.Matches(hostCopy.Value.Document.Packed),
                "validated admitted host copy survives cache publication failure in memory");
            transport.Status = 200;
            transport.Bytes = Encoding.UTF8.GetBytes("not a map");
            Assert(!(await store.GetAsync("example", Reply.Origin, default)).Succeeded, "cache failure never makes invalid downloaded bytes playable");
        }
        private static async Task TransientStatusPolicy(string root)
        {
            var cache = new MapCache(root);
            var document = new MapDocument(Project());
            cache.Put("example", document, null, null);
            var reply = new Reply();
            var store = new MapStore(cache, new MapEndpointPolicy(Reply.Origin), reply);
            foreach (var status in new[] { 408, 429, 500, 503, 599 })
            {
                reply.Status = status;
                var result = await store.GetAsync("example", Reply.Origin, default);
                Assert(result.Succeeded && result.Value!.Offline && result.Value.Document.Sha256 == document.Sha256,
                    "recoverable service status replays only a verified cached map");
                Assert(!(await store.GetAsync("unplayed", Reply.Origin, default)).Succeeded, "transient status cannot invent an uncached map");
            }
            foreach (var status in new[] { 400, 401, 403, 451, 302 })
            {
                reply.Status = status;
                Assert(!(await store.GetAsync("example", Reply.Origin, default)).Succeeded, "authorization and other non-transient refusals stay fail-closed");
            }
        }
        private static void PlainOfflineNotices()
        {
            var ui = new PlainProbe();
            var name = "<size=300>Declared world</size>" + new string('x', 256);
            MapNotices.Offline(ui, name, "1 January 2026", MapOfflineReason.Slow);
            Assert(ui.Plain == 1 && ui.Rich == 0 && ui.Message.Contains("<size=300>") && ui.Message.Length <= 512
                && ui.Message.Contains("too slow") && !ui.Message.Contains("could not be reached"),
                "bounded world name uses literal plain toast with accurate slow-service wording");
            MapNotices.Offline(ui, "", "an earlier play", MapOfflineReason.ServiceBusy);
            Assert(ui.Message.Contains("this map") && ui.Message.Contains("temporarily unavailable"), "missing display name has a usable bounded fallback");
            UnityMainThreadGuard.CaptureCurrentThread();
            var rig = new PresenceRig();
            using var lifetime = new OwnerModLifetime();
            var replication = rig.Host.Replication.Bind("io.example.maps", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), lifetime);
            var wrapped = new NetUiService(ui, replication);
            MapNotices.Offline(wrapped, name, "an earlier play", MapOfflineReason.Unreachable);
            Assert(ui.Plain == 3 && ui.Rich == 0, "production network UI decorator preserves the plain map-notice capability");
            var absent = new RichProbe();
            MapNotices.Offline(new NetUiService(absent, replication), name, "an earlier play", MapOfflineReason.Unreachable);
            Assert(absent.Rich == 1 && !absent.Message.Contains("<") && !absent.Message.Contains("Declared world"),
                "absent inner plain capability never falls back to rich rendering of an untrusted map name");
        }
        private sealed class Reply : IMapTransport
        {
            internal static readonly Uri Origin = new Uri("https://maps.example.test/");
            internal int Status = 200;
            internal bool WaitHeaders;
            internal byte[] Bytes = Project();
            public async Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token)
            {
                if (WaitHeaders) await Task.Delay(Timeout.Infinite, token);
                return new MapResponse(Status, new MemoryStream(Bytes), Status == 200 ? Bytes.Length : 0);
            }
        }
        private sealed class PlainProbe : RichProbe, IInternalMapNoticeUi
        {
            internal int Plain;
            public OperationResult<bool> ShowMapNotice(string text, UiTone tone) { Plain++; Message = text; return OperationResult<bool>.Success(true); }
        }
        private class RichProbe : IUiService
        {
            internal int Rich;
            internal string Message = string.Empty;
            public OperationResult<bool> ShowToast(string text, UiTone tone = UiTone.Neutral) { Rich++; Message = text; return OperationResult<bool>.Success(true); }
            public UiAccessibilityPreferences Accessibility => throw new NotSupportedException();
            public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences value) => throw new NotSupportedException();
            public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => throw new NotSupportedException();
            public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => throw new NotSupportedException();
        }
    }
}
