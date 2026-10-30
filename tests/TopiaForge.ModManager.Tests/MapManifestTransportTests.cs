using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class MapManifestTransportTests
    {
        private static readonly Uri Origin = new Uri("https://maps.example.invalid/");
        private static readonly Uri Manifest = new Uri(Origin, "map/example/manifest.json");
        private static readonly Uri ProjectUri = new Uri(Origin, "map/example/project.roboworld");
        internal static async Task RunAsync()
        {
            var context = new InlineContext(); var source = new Transport(); var calls = 0;
            Uri Resolve(Uri uri, string json)
            {
                calls++;
                Assert(SynchronizationContext.Current == context && uri == Manifest && json == "{}", "native parser runs on captured context with exact manifest JSON");
                return ProjectUri;
            }
            var wrapper = new MapManifestTransport(source, new MapEndpointPolicy(Origin), Resolve, context);
            var validators = new MapCacheRecord { MapId = "example", Etag = "project-tag" };
            using (var response = await wrapper.FetchAsync(Manifest, validators, default))
                Assert(response.Status == 200 && source.Requests.Count == 2 && source.Requests[0].Cached == null
                    && ReferenceEquals(source.Requests[1].Cached, validators) && source.Requests[1].Uri == ProjectUri && calls == 1,
                    "bounded manifest fetch precedes project; validators apply only to project");
            foreach (var foreign in new[] { "https://foreign.example.invalid/project", "http://maps.example.invalid/project", "https://user@maps.example.invalid/project", "https://maps.example.invalid/project#fragment" })
            {
                var unsafeSource = new Transport();
                var unsafeWrapper = new MapManifestTransport(unsafeSource, new MapEndpointPolicy(Origin), (_, __) => new Uri(foreign), context);
                await Refuses(unsafeWrapper, "unsafe native project origin");
                Assert(unsafeSource.Requests.Count == 1, "unsafe project never reaches transport");
            }
            foreach (var bytes in new[] { new byte[65537], new byte[] { 0xff }, Array.Empty<byte>() })
            {
                var invalid = new Transport { ManifestBytes = bytes };
                await Refuses(new MapManifestTransport(invalid, new MapEndpointPolicy(Origin), Resolve, context), "manifest size/UTF8/empty bound");
                Assert(invalid.Requests.Count == 1, "invalid manifest never requests project");
            }
            var redirected = new Transport { Status = 302 };
            using (var response = await new MapManifestTransport(redirected, new MapEndpointPolicy(Origin), Resolve, context).FetchAsync(Manifest, null, default))
                Assert(response.Status == 302 && redirected.Requests.Count == 1, "redirect is passed to existing refusal without following");
            await Refuses(new MapManifestTransport(new Transport { Status = 304 }, new MapEndpointPolicy(Origin), Resolve, context), "manifest cannot reuse project cache validators");
            var privateFault = new MapManifestTransport(new Transport(), new MapEndpointPolicy(Origin), (_, __) => throw new Exception("private native path/address"), context);
            await Refuses(privateFault, "native parser faults are sanitized");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var stopped = new Transport();
            try { await new MapManifestTransport(stopped, new MapEndpointPolicy(Origin), Resolve, context).FetchAsync(Manifest, null, cancelled.Token); throw new Exception("cancellation ignored"); }
            catch (OperationCanceledException) { Assert(stopped.Requests.Count == 0, "cancelled request makes no contact"); }
            using var waitingCancellation = new CancellationTokenSource();
            var queue = new QueueContext(); var waitingSource = new Transport(); var lateCalls = 0;
            var waiting = new MapManifestTransport(waitingSource, new MapEndpointPolicy(Origin), (_, __) => { lateCalls++; return ProjectUri; }, queue)
                .FetchAsync(Manifest, null, waitingCancellation.Token);
            Assert(queue.Pending != null && !waiting.IsCompleted, "native resolution waits for its host context");
            waitingCancellation.Cancel();
            try { await waiting; throw new Exception("queued native callback ignored cancellation"); }
            catch (OperationCanceledException) { }
            queue.Drain();
            Assert(lateCalls == 0 && waitingSource.Requests.Count == 1, "cancelled queued callback never parses or contacts project");
            var root = Directory.CreateTempSubdirectory("manifest-cache-");
            try
            {
                var cache = new MapCache(root.FullName); cache.Put("example", new MapDocument(Project()), null, null);
                var offline = new MapManifestTransport(new Transport { Offline = true }, new MapEndpointPolicy(Origin), Resolve, context);
                var result = await new MapStore(cache, new MapEndpointPolicy(Origin), source).GetAsync("example", Manifest, default, null, offline);
                Assert(result.Succeeded && result.Value!.Offline, "manifest network failure retains validated offline cache fallback");
            }
            finally { root.Delete(true); }
            Console.WriteLine("Manifest map transport passed: bounded consented transport, native context, no unsafe follow, project validators, cancellation and offline cache (native behavior unproved).");
        }
        private static async Task Refuses(MapManifestTransport transport, string reason)
        {
            try { using var response = await transport.FetchAsync(Manifest, null, default); throw new Exception(reason + " accepted"); }
            catch (FormatException error) { Assert(!error.Message.Contains("private native", StringComparison.Ordinal) && !error.Message.Contains("://", StringComparison.Ordinal), reason + " is private"); }
        }
        private sealed class InlineContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object? state)
            {
                var previous = Current; SetSynchronizationContext(this);
                try { callback(state); } finally { SetSynchronizationContext(previous); }
            }
        }
        private sealed class QueueContext : SynchronizationContext
        {
            internal Action? Pending;
            public override void Post(SendOrPostCallback callback, object? state) => Pending = () => callback(state);
            internal void Drain() { var next = Pending; Pending = null; next?.Invoke(); }
        }
        private sealed class Transport : IMapTransport
        {
            internal readonly List<(Uri Uri, MapCacheRecord? Cached)> Requests = new List<(Uri, MapCacheRecord?)>();
            internal byte[] ManifestBytes = Encoding.UTF8.GetBytes("{}");
            internal int Status = 200;
            internal bool Offline;
            public Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Requests.Add((uri, cached));
                if (Offline) throw new HttpRequestException();
                var bytes = uri == Manifest ? ManifestBytes : Project();
                return Task.FromResult(new MapResponse(uri == Manifest ? Status : 200, new MemoryStream(bytes), bytes.Length));
            }
        }
    }
}
