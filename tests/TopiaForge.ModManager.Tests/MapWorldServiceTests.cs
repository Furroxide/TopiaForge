using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class MapWorldServiceTests
    {
        internal static async Task AuthoredReadinessAsync()
        {
            foreach (var cancel in new[] { false, true })
            {
                using var failedFixture = Fixture(); using var cancellation = new CancellationTokenSource();
                var failedBackend = new Backend { ThrowIndex = !cancel, OnIndex = cancel ? cancellation.Cancel : null };
                using var failedService = Service(failedBackend);
                var failed = await failedService.LoadAsync(failedFixture, Request(), cancellation.Token);
                Assert(failed.ErrorCode == (cancel ? ModErrorCode.Cancelled : ModErrorCode.External),
                    "index failure and cancellation refuse readiness with their proper result");
                Assert(failedBackend.ContentDisposals == 1 && failedFixture.Native.DisposeCount == 1 && failedBackend.Spawned == null,
                    "index failure and cancellation clean up before player placement");
                if (cancel) Assert(!failedBackend.Map!.IsAlive, "cancelled index cannot stay live");
            }
            using var fixture = Fixture(); var backend = new Backend(); using var service = Service(backend);
            var pending = service.LoadAsync(fixture, Request(), default);
            Assert(!pending.IsCompleted && backend.Map != null, "authored index exists while native readiness is pending");
            backend.MapAlive = false;
            fixture.Native.Succeed();
            var result = await pending;
            Assert(!result.Succeeded, "a map lost during native readiness must not publish a successful world");
            Assert(backend.ContentDisposals == 1 && fixture.Native.DisposeCount == 1,
                "lost indexed root releases content and preparation exactly once");
            Assert(backend.Map!.ResolveAnchors(Array.Empty<MapAnchor>()).ErrorCode == ModErrorCode.InvalidState,
                "failed readiness leaves no usable authored map");
            Console.WriteLine("Authored map atomic readiness passed (3 phases: indexing failure, indexing cancellation, lost root).");
        }
        internal static async Task RunAsync()
        {
            var owned = Directory.CreateTempSubdirectory("map-service-");
            try
            {
                var root = owned.FullName;
                await ReadinessAndOwnership(root);
                foreach (var phase in new[] { "prepare", "read", "lore", "import", "spawn", "readiness" }) await CancelledPhase(root, phase);
                await InvalidSpawn(root);
                await PrivateNativeFailure(root);
                await CacheFailureRecovery();
                await PendingPreparationDrains(root);
                await CleanupFailureAfterCancellation(root);
                await LoreAwaiter();
                Console.WriteLine("Map world service passed: readiness, ownership, six cancellation phases, pending preparation drain, failed cleanup, cache recovery, ambiguous spawn and native failure privacy.");
            }
            finally { owned.Delete(true); }
        }
        private static async Task LoreAwaiter()
        {
            var pending = new LorePending();
            var drain = MapLorePreparation.DrainAsync(pending);
            Assert(!drain.IsCompleted, "native lore operation must drain before ownership can end");
            pending.Awaiter.Finish(); await drain;
            Assert(pending.Awaiter.Reads == 1, "native awaiter result is consumed once");
            pending = new LorePending(); pending.Awaiter.Failure = new Exception("private https://example.test/path");
            drain = MapLorePreparation.DrainAsync(pending); pending.Awaiter.Finish();
            try { await drain; throw new InvalidOperationException("lore failure was ignored"); }
            catch (MapProblem error) { Assert(error.Code == ModErrorCode.Unavailable && !error.Message.Contains("private"), "lore failures are fixed and private"); }
        }
        private sealed class LorePending
        {
            internal readonly LoreAwaiterFixture Awaiter = new LoreAwaiterFixture();
            public LoreAwaiterFixture GetAwaiter() => Awaiter;
        }
        private sealed class LoreAwaiterFixture
        {
            private Action? continuation;
            internal int Reads; internal Exception? Failure;
            public bool IsCompleted { get; private set; }
            public Exception? GetResult() { Reads++; return Failure; }
            public void OnCompleted(Action value) { continuation = value; }
            internal void Finish() { IsCompleted = true; continuation?.Invoke(); }
        }
        private static BuiltinWorldFixture Fixture() => new BuiltinWorldFixture
        {
            SpawnPolicy = new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault),
            SceneTransitions = new Transitions()
        };
        private static MapWorldService Service(string root, Backend backend) => new MapWorldService(new MapCache(Path.Combine(root, "cache")),
            new NoNetwork(), () => backend);
        private static InternalMapWorldRequest Request(byte[]? bytes = null, Action? read = null) => new InternalMapWorldRequest(null, null, _ =>
        { read?.Invoke(); return Task.FromResult(OperationResult<byte[]>.Success(bytes ?? Project())); });
        private static async Task ReadinessAndOwnership(string root)
        {
            using var fixture = Fixture(); var backend = new Backend(); using var service = Service(root, backend);
            var original = fixture.Fake.Lifetime.TrackedResourceCount;
            var task = service.LoadAsync(fixture, Request(), default);
            Assert(!task.IsCompleted && backend.Imported == 1 && backend.Spawned == "entry", "import and spawn precede readiness");
            Assert(backend.Label == "map:package" && backend.SceneArgument == null && fixture.Native.Request?.Kind == NativeWorldLoadKind.OpenSandbox,
                "native default scene and no-op sandbox are retained; native parser never sees a path");
            Assert(fixture.Native.ProviderSpawn == Backend.Spawn && fixture.Native.Policy?.Kind == WorldSpawnKind.ProviderDefault,
                "authored map spawn reaches existing readiness, without generic marker search");
            fixture.Native.Succeed();
            var result = await task;
            Assert(result.Succeeded && result.Value!.Readiness.Scene.InstanceId == 42 && backend.ContentDisposals == 0, "Running uses actual native readiness");
            result.Value!.Dispose(); result.Value.Dispose();
            Assert(backend.ContentDisposals == 1 && fixture.Native.DisposeCount == 1 && fixture.Fake.Lifetime.TrackedResourceCount == original,
                "the returned world owns and releases map content and native preparation exactly once");
        }
        private static async Task CancelledPhase(string root, string phase)
        {
            using var fixture = Fixture(); using var cancellation = new CancellationTokenSource();
            var backend = new Backend(); using var service = Service(root, backend);
            if (phase == "prepare") fixture.Native.OnPrepare = cancellation.Cancel;
            if (phase == "lore") backend.OnPrepare = cancellation.Cancel;
            if (phase == "import") backend.OnImport = cancellation.Cancel;
            if (phase == "spawn") backend.OnSpawn = cancellation.Cancel;
            var pending = service.LoadAsync(fixture, Request(read: phase == "read" ? cancellation.Cancel : null), cancellation.Token);
            if (phase == "readiness") { cancellation.Cancel(); fixture.Native.Succeed(); }
            var result = await pending;
            Assert(result.ErrorCode == ModErrorCode.Cancelled && fixture.Native.DisposeCount == 1, "cancel " + phase + " never publishes readiness");
            Assert(backend.ContentDisposals == backend.Imported, "cancel " + phase + " releases every returned root");
        }
        private static async Task InvalidSpawn(string root)
        {
            using var fixture = Fixture(); var backend = new Backend(); using var service = Service(root, backend);
            var bytes = Project("\"a\":{\"components\":{\"spawn-location\":{}}},\"b\":{\"components\":{\"spawn-location\":{}}}");
            var result = await service.LoadAsync(fixture, Request(bytes), default);
            Assert(result.ErrorCode == ModErrorCode.InvalidState && backend.Imported == 0 && fixture.Native.DisposeCount == 1,
                "ambiguous spawn refuses before import and cleans preparation");
        }
        private static async Task PrivateNativeFailure(string root)
        {
            using var fixture = Fixture(); var backend = new Backend { Throw = true }; using var service = Service(root, backend);
            var result = await service.LoadAsync(fixture, Request(), default);
            Assert(!result.Succeeded && !result.ErrorMessage.Contains("private") && !result.ErrorMessage.Contains("://"), "native details stay private");
            Assert(fixture.Native.DisposeCount == 1, "native prepare fault cleans scene");
        }
        private static async Task CacheFailureRecovery()
        {
            var directory = Directory.CreateTempSubdirectory("map-recovery-");
            try
            {
                var cachePath = Path.Combine(directory.FullName, "cache");
                File.WriteAllText(cachePath, "A file cannot be used as a cache directory.");
                foreach (var cleanupFails in new[] { false, true })
                {
                    using var fixture = Fixture(); var backend = new Backend();
                    fixture.Native.ThrowOnDispose = cleanupFails;
                    using var service = new MapWorldService(new MapCache(cachePath), new PublishedBytes(), () => backend);
                    var result = await service.LoadAsync(fixture, new InternalMapWorldRequest("example", "scene", null), default);
                    Assert(result.ErrorCode == ModErrorCode.External && backend.Imported == 0 && fixture.Native.DisposeCount == 1,
                        "cache failure refuses before import and attempts cleanup");
                    Assert(cleanupFails
                        ? result.ErrorMessage.Contains("cleaned up") && result.ErrorMessage.Contains("restart")
                        : result.ErrorMessage.Contains("disk space"),
                        "cache failure retains disk recovery unless cleanup also fails");
                    Assert(!result.ErrorMessage.Contains("native cleanup failed") && !result.ErrorMessage.Contains(cachePath),
                        "recovery guidance hides native and local filesystem details");
                }
            }
            finally { directory.Delete(true); }
        }
        private static async Task PendingPreparationDrains(string root)
        {
            using var fixture = Fixture(); using var cancellation = new CancellationTokenSource();
            var completion = new TaskCompletionSource<OperationResult<IMapImportTransaction>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new Backend { PendingPreparation = completion.Task }; using var service = Service(root, backend);
            var pending = service.LoadAsync(fixture, Request(), cancellation.Token);
            cancellation.Cancel();
            Assert(!pending.IsCompleted && fixture.Native.DisposeCount == 0, "cancellation waits for pending native preparation before cleanup");
            completion.SetResult(OperationResult<IMapImportTransaction>.Success(backend));
            var result = await pending;
            Assert(result.ErrorCode == ModErrorCode.Cancelled && fixture.Native.DisposeCount == 1 && backend.Imported == 0,
                "drained preparation cannot import or publish a cancelled map");
        }
        private static async Task CleanupFailureAfterCancellation(string root)
        {
            using var fixture = Fixture(); using var cancellation = new CancellationTokenSource();
            var backend = new Backend { OnImport = cancellation.Cancel, ThrowContentCleanup = true };
            using var service = Service(root, backend);
            var result = await service.LoadAsync(fixture, Request(), cancellation.Token);
            Assert(result.ErrorCode == ModErrorCode.External && result.ErrorMessage.Contains("restart"),
                "a failed import cleanup must not be reported as ordinary cancellation");
            Assert(backend.ContentDisposals == 1 && fixture.Native.DisposeCount == 1 && !result.ErrorMessage.Contains("private"),
                "failed cleanup attempts every owned resource and hides native details");
        }
        private sealed class PublishedBytes : IMapTransport
        {
            public Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token)
            {
                var bytes = Project();
                return Task.FromResult(new MapResponse(200, new MemoryStream(bytes), bytes.Length));
            }
        }
        private sealed class Backend : IMapNativeBackend, IMapImportTransaction
        {
            internal static readonly TransformState Spawn = new TransformState(new Vec3(7, 2, 9), Quat.Identity, new Vec3(1, 1, 1));
            internal int Imported, ContentDisposals; internal string? Spawned, SceneArgument, Label;
            internal Action? OnPrepare, OnImport, OnSpawn; internal bool Throw;
            internal bool ThrowContentCleanup;
            internal bool MapAlive = true;
            internal AuthoredMapIndex? Map;
            internal bool ThrowIndex;
            internal Action? OnIndex;
            internal Task<OperationResult<IMapImportTransaction>>? PendingPreparation;
            public Uri PublicBase => new Uri("https://maps.example.test/");
            public Uri ResolvePublished(string id) => new Uri(PublicBase, id);
            public Task<OperationResult<IMapImportTransaction>> PrepareAsync(MapDocument document, string? sceneId, string label,
                WorldSceneIdentity scene, CancellationToken token)
            {
                if (Throw) throw new InvalidOperationException("private native reply https://maps.example.test/secret");
                OnPrepare?.Invoke(); SceneArgument = sceneId; Label = label;
                return PendingPreparation ?? Task.FromResult(OperationResult<IMapImportTransaction>.Success(this));
            }
            public string SceneId => "scene";
            public AuthoredMapIndex CreateMap(MapDocument document, string mapId, IModContext context)
            {
                if (ThrowIndex) throw new InvalidOperationException("Synthetic indexing failure.");
                Map = new AuthoredMapIndex(mapId, document.Authored.Scenes[SceneId], new MapRevision(document.Sha256, document.Packed.Length, document.Modified),
                    Array.Empty<AuthoredMarker>(), value => value, value => TransformState.Identity, () => MapAlive);
                OnIndex?.Invoke();
                return Map;
            }
            public bool NativeEntered => Imported != 0;
            public bool NativeReturned => Imported != 0;
            public OperationResult<IDisposable> Import()
            {
                Imported++; OnImport?.Invoke();
                return OperationResult<IDisposable>.Success(new Owned(() =>
                {
                    ContentDisposals++;
                    if (ThrowContentCleanup) throw new IOException("private native cleanup diagnostic");
                }));
            }
            public OperationResult<TransformState> PlaceRespawn(string entityId)
            { OnSpawn?.Invoke(); Spawned = entityId; return OperationResult<TransformState>.Success(Spawn); }
            public void Dispose() { }
        }
        private sealed class Owned : IDisposable
        { private readonly Action dispose; internal Owned(Action dispose) { this.dispose = dispose; } public void Dispose() => dispose(); }
        private sealed class NoNetwork : IMapTransport
        { public Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token) => throw new InvalidOperationException("Packaged maps must not fetch."); }
        internal sealed class Transitions : IInternalSceneTransitionService
        {
            public bool IsBusy => false;
            public OperationResult<IInternalSceneTransitionLease> Acquire(string name, bool automatic, string reason) => throw new NotSupportedException();
            public OperationResult<IInternalNativeSceneOperation> TryDispatch(NativeSceneRequest request, IInternalNativeSceneDispatch dispatch, CancellationToken token = default)
            {
                Assert(!request.ObserveSceneArrival, "import operates in its already prepared scene");
                var operation = new Operation(); operation.DispatchStatus = dispatch.Begin(operation);
                return OperationResult<IInternalNativeSceneOperation>.Success(operation);
            }
        }
        private sealed class Operation : IInternalNativeSceneOperation, IInternalNativeSceneCompletion
        {
            private readonly TaskCompletionSource<OperationResult<SceneSnapshot>> done = new TaskCompletionSource<OperationResult<SceneSnapshot>>();
            public Task<OperationResult<SceneSnapshot>> Completion => done.Task;
            public Task<OperationResult<SceneSnapshot>> NativeCompletion => done.Task;
            public Task NativeDrained => done.Task;
            public NativeSceneDispatchStatus DispatchStatus { get; set; }
            public void FailCaller(ModErrorCode code, string message) => done.TrySetResult(OperationResult<SceneSnapshot>.Failure(code, message));
            public void NativeCompleted(OperationResult<SceneSnapshot> result) => done.TrySetResult(result);
            public void RequireManagedCompletion() { }
            public void ManagedCompleted(OperationResult<bool> result) { }
        }
    }
}
