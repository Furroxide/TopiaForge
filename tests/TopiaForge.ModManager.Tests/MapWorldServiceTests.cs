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
        internal static async Task RunAsync()
        {
            await ReadinessAndOwnership();
            foreach (var phase in new[] { "prepare", "read", "lore", "import", "spawn", "readiness" }) await CancelledPhase(phase);
            await InvalidSpawn();
            await PrivateNativeFailure();
            await LoreAwaiter();
            Console.WriteLine("Map world service passed: readiness, ownership, six cancellation phases, ambiguous spawn and native failure privacy.");
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
        private static MapWorldService Service(Backend backend) => new MapWorldService(new MapCache(Path.Combine(Path.GetTempPath(), "unused-map-cache")),
            new NoNetwork(), () => backend);
        private static InternalMapWorldRequest Request(byte[]? bytes = null, Action? read = null) => new InternalMapWorldRequest(null, null, _ =>
        { read?.Invoke(); return Task.FromResult(OperationResult<byte[]>.Success(bytes ?? Project())); });
        private static async Task ReadinessAndOwnership()
        {
            using var fixture = Fixture(); var backend = new Backend(); using var service = Service(backend);
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
        private static async Task CancelledPhase(string phase)
        {
            using var fixture = Fixture(); using var cancellation = new CancellationTokenSource();
            var backend = new Backend(); using var service = Service(backend);
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
        private static async Task InvalidSpawn()
        {
            using var fixture = Fixture(); var backend = new Backend(); using var service = Service(backend);
            var bytes = Project("\"a\":{\"components\":{\"spawn-location\":{}}},\"b\":{\"components\":{\"spawn-location\":{}}}");
            var result = await service.LoadAsync(fixture, Request(bytes), default);
            Assert(result.ErrorCode == ModErrorCode.InvalidState && backend.Imported == 0 && fixture.Native.DisposeCount == 1,
                "ambiguous spawn refuses before import and cleans preparation");
        }
        private static async Task PrivateNativeFailure()
        {
            using var fixture = Fixture(); var backend = new Backend { Throw = true }; using var service = Service(backend);
            var result = await service.LoadAsync(fixture, Request(), default);
            Assert(!result.Succeeded && !result.ErrorMessage.Contains("private") && !result.ErrorMessage.Contains("://"), "native details stay private");
            Assert(fixture.Native.DisposeCount == 1, "native prepare fault cleans scene");
        }
        private sealed class Backend : IMapNativeBackend, IMapImportTransaction
        {
            internal static readonly TransformState Spawn = new TransformState(new Vec3(7, 2, 9), Quat.Identity, new Vec3(1, 1, 1));
            internal int Imported, ContentDisposals; internal string? Spawned, SceneArgument, Label;
            internal Action? OnPrepare, OnImport, OnSpawn; internal bool Throw;
            public Uri PublicBase => new Uri("https://maps.example.test/");
            public Uri ResolvePublished(string id) => new Uri(PublicBase, id);
            public Task<OperationResult<IMapImportTransaction>> PrepareAsync(MapDocument document, string? sceneId, string label,
                WorldSceneIdentity scene, CancellationToken token)
            {
                if (Throw) throw new InvalidOperationException("private native reply https://maps.example.test/secret");
                OnPrepare?.Invoke(); SceneArgument = sceneId; Label = label;
                return Task.FromResult(OperationResult<IMapImportTransaction>.Success(this));
            }
            public string SceneId => "scene";
            public bool NativeEntered => Imported != 0;
            public bool NativeReturned => Imported != 0;
            public OperationResult<IDisposable> Import()
            { Imported++; OnImport?.Invoke(); return OperationResult<IDisposable>.Success(new Owned(() => ContentDisposals++)); }
            public OperationResult<TransformState> PlaceRespawn(string entityId)
            { OnSpawn?.Invoke(); Spawned = entityId; return OperationResult<TransformState>.Success(Spawn); }
            public void Dispose() { }
        }
        private sealed class Owned : IDisposable
        { private readonly Action dispose; internal Owned(Action dispose) { this.dispose = dispose; } public void Dispose() => dispose(); }
        private sealed class NoNetwork : IMapTransport
        { public Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token) => throw new InvalidOperationException("Packaged maps must not fetch."); }
        private sealed class Transitions : IInternalSceneTransitionService
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
