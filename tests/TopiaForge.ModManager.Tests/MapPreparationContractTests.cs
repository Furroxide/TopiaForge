using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Worlds.Maps;

namespace TopiaForge.ModManager.Tests
{
    internal static class MapPreparationContractTests
    {
        internal static void Run()
        {
            var failures = new List<string>();
            foreach (var published in new[] { false, true })
            {
                try
                {
                    LoadMap(published);
                    foreach (var phase in new[] { "prepare-cancel", "import-cancel", "owner-stop", "index-failure" })
                        FailedMap(published, phase);
                }
                catch (Exception error) { failures.Add((published ? "published: " : "packaged: ") + error.Message); }
            }
            if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
            foreach (var outcome in new[] { "cancel", "owner-stop", "reentrant-dispose", "late-failure" }) ContentDrain(outcome);
            DispatchBounds();
            PreparationBounds();
            FailedContentCannotFinish();
            RefusedContentCannotFinish();
            Console.WriteLine("Map preparation contract passed: packaged/published success and four failure/cancellation phases each; four content drain cases; single-use and readiness bounds.");
        }

        private static void FailedMap(bool published, string phase)
        {
            using var test = new Fixture(); using var cancellation = new CancellationTokenSource();
            var backend = new MapWorldServiceTests.Backend();
            using var service = test.Maps(backend);
            var prepared = new TaskCompletionSource<OperationResult<IMapImportTransaction>>();
            if (phase == "prepare-cancel") backend.PendingPreparation = prepared.Task;
            if (phase == "index-failure") backend.ThrowIndex = true;
            backend.OnImport = () =>
            {
                test.AssertExclusive();
                if (phase == "import-cancel") cancellation.Cancel();
                if (phase == "owner-stop")
                {
                    test.Lifetime.Dispose();
                    Assert(test.Load.Disposals == 0, "reentrant owner cleanup must await the content dispatch handoff and drain");
                    test.AssertExclusive();
                }
            };
            var loading = test.Dispatcher.InvokeCallbackAsync(() => service.LoadAsync(test.Context, Request(published), cancellation.Token));
            if (phase == "prepare-cancel")
            {
                test.Until(() => backend.Label != null);
                cancellation.Cancel(); test.Dispatcher.Drain();
                Assert(!loading.IsCompleted && test.Load.Disposals == 0, "pending map preparation must drain before cancellation completes");
                test.AssertExclusive();
                prepared.SetResult(OperationResult<IMapImportTransaction>.Success(backend));
            }
            test.Wait(loading);
            Assert(loading.Result.ErrorCode == (phase == "index-failure" ? ModErrorCode.External : ModErrorCode.Cancelled),
                "map phase " + phase + " must retain its failure/cancellation result: " + loading.Result.ErrorCode);
            test.Until(() => test.Load.Disposals == 1);
            Assert(backend.ContentDisposals == backend.Imported && backend.Spawned == null,
                "failed map disposes every imported root and never places the player");
            test.AssertReleased();
        }

        private static void ContentDrain(string outcome)
        {
            using var test = new Fixture(); using var cancellation = new CancellationTokenSource();
            var preparing = test.Context.WorldRuntime.PrepareAsync(NativeWorldLoadRequest.OpenSandbox(WorldLoadTransition.SceneReplacement), default);
            test.Wait(preparing);
            var preparation = (WorldPreparation)preparing.Result.Value!;
            IInternalNativeSceneCompletion? completion = null;
            var dispatched = preparation.TryDispatchContent(new DelegateNativeSceneDispatch(sink =>
            {
                completion = sink;
                if (outcome == "reentrant-dispose")
                {
                    preparation.Dispose();
                    Assert(test.Load.Disposals == 0, "cleanup cannot run ahead of the in-progress dispatch handoff");
                }
                return NativeSceneDispatchStatus.Dispatched;
            }), cancellation.Token);
            Assert(dispatched.Succeeded && !dispatched.Value!.NativeDrained.IsCompleted, "content dispatch owns an observable pending native operation");
            var finish = preparation.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null, null, default);
            test.Wait(finish);
            Assert(finish.Result.ErrorCode == (outcome == "reentrant-dispose" ? ModErrorCode.Cancelled : ModErrorCode.Conflict),
                "readiness cannot release admission before content drains");
            test.Coordinator.NotifySceneArrived(new SceneSnapshot("Scene", true, true));
            Assert(!dispatched.Value!.NativeDrained.IsCompleted, "a scene event cannot replace terminal content completion");
            if (outcome == "owner-stop") test.Lifetime.BeginStop(); else cancellation.Cancel();
            test.Until(() => dispatched.Value.Completion.IsCompleted);
            Assert(dispatched.Value.Completion.Result.ErrorCode == ModErrorCode.Cancelled, "content observes caller and owner cancellation");
            var closing = preparation.CloseAsync();
            Assert(!closing.IsCompleted && test.Load.Disposals == 0, "cancelled content retains native resources until drain");
            test.AssertExclusive();
            completion!.NativeCompleted(outcome == "late-failure"
                ? OperationResult<SceneSnapshot>.Failure(ModErrorCode.External, "Synthetic late content failure.")
                : OperationResult<SceneSnapshot>.Success(new SceneSnapshot("Scene", true, true)));
            if (outcome == "late-failure")
            {
                test.Until(() => closing.IsCompleted);
                Assert(closing.IsFaulted && closing.Exception!.ToString().Contains("Synthetic late content failure", StringComparison.Ordinal),
                    "cleanup preserves native failure after the caller's cancellation");
                // The owner's tracked disposer must observe that same terminal cleanup failure.
                try { test.Lifetime.Dispose(); throw new InvalidOperationException("owner lost late failure"); }
                catch (AggregateException error) { Assert(error.ToString().Contains("Synthetic late content failure", StringComparison.Ordinal), "owner cleanup retains late failure"); }
            }
            else test.Wait(closing);
            Assert(dispatched.Value.NativeDrained.IsCompleted && test.Load.Disposals == 1, "native drain precedes one preparation cleanup");
            test.AssertReleased();
        }

        private static void DispatchBounds()
        {
            using var test = new Fixture();
            var preparing = test.Context.WorldRuntime.PrepareAsync(NativeWorldLoadRequest.OpenSandbox(WorldLoadTransition.SceneReplacement), default);
            test.Wait(preparing);
            var preparation = preparing.Result.Value!;
            var calls = 0;
            var dispatch = new DelegateNativeSceneDispatch(sink =>
            {
                calls++; sink.NativeCompleted(OperationResult<SceneSnapshot>.Success(new SceneSnapshot("Scene", true, true)));
                return NativeSceneDispatchStatus.Dispatched;
            });
            Assert(preparation.TryDispatchContent(dispatch, default).Succeeded, "prepared content enters its retained route");
            Assert(preparation.TryDispatchContent(dispatch, default).ErrorCode == ModErrorCode.InvalidState && calls == 1,
                "a preparation never becomes a reusable transition route");
            test.Load.PlayerReady = true;
            var finish = preparation.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null, null, default);
            test.Wait(finish);
            Assert(finish.Result.Succeeded && preparation.TryDispatchContent(dispatch, default).ErrorCode == ModErrorCode.InvalidState,
                "content cannot enter after readiness released admission");
            test.AssertReleased();
        }

        private static void PreparationBounds()
        {
            using var test = new Fixture();
            var preparation = new WorldPreparation(test.Load, default, default, test.Clock, test.Dispatcher);
            preparation.Attach(test.Parent.Acquire("Scene", false, "preparation bounds").Value!);
            var calls = 0;
            var dispatch = new DelegateNativeSceneDispatch(_ => { calls++; return NativeSceneDispatchStatus.NotDispatched; });
            Assert(preparation.TryDispatchContent(dispatch, default).ErrorCode == ModErrorCode.InvalidState,
                "content cannot dispatch before scene capture");
            test.Load.DefaultReady = false;
            var preparing = test.Dispatcher.InvokeCallbackAsync(preparation.PrepareAsync);
            Assert(preparation.TryDispatchContent(dispatch, default).ErrorCode == ModErrorCode.InvalidState,
                "scene capture alone cannot admit content before default spawn readiness");
            test.Load.DefaultReady = true; test.Clock.Advance(); test.Wait(preparing);
            test.Load.SceneValid = false;
            Assert(preparation.TryDispatchContent(dispatch, default).ErrorCode == ModErrorCode.InvalidState && calls == 0,
                "a lost prepared scene cannot receive native content");
            test.Load.SceneValid = true; test.Load.PlayerReady = true;
            var finish = preparation.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null, null, default);
            test.Wait(finish);
            Assert(finish.Result.Succeeded && preparation.TryDispatchContent(dispatch, default).ErrorCode == ModErrorCode.InvalidState && calls == 0,
                "readiness closes an unused content route too");
            preparation.Dispose();
            Assert(preparation.TryDispatchContent(dispatch, default).ErrorCode == ModErrorCode.Cancelled,
                "disposed preparation cannot dispatch content");
            test.AssertReleased();
        }

        private static void FailedContentCannotFinish()
        {
            using var test = new Fixture();
            var preparing = test.Context.WorldRuntime.PrepareAsync(NativeWorldLoadRequest.OpenSandbox(WorldLoadTransition.SceneReplacement), default);
            test.Wait(preparing);
            var preparation = preparing.Result.Value!;
            var dispatched = preparation.TryDispatchContent(new DelegateNativeSceneDispatch(sink =>
            {
                sink.NativeCompleted(OperationResult<SceneSnapshot>.Failure(ModErrorCode.External, "Synthetic content failure."));
                return NativeSceneDispatchStatus.Dispatched;
            }), default);
            Assert(dispatched.Succeeded && dispatched.Value!.NativeDrained.IsCompleted, "failed native content drains its operation");
            test.Load.PlayerReady = true;
            var finish = preparation.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null, null, default);
            test.Wait(finish);
            Assert(finish.Result.ErrorCode == ModErrorCode.External, "failed content cannot be promoted to world readiness");
            test.AssertReleased();
        }

        private static void RefusedContentCannotFinish()
        {
            var failures = new List<string>();
            foreach (var initialDispatch in new[] { true, false })
            {
                try
                {
                    using var test = new Fixture();
                    test.Load.RequiresDispatch = initialDispatch;
                    var preparing = test.Context.WorldRuntime.PrepareAsync(NativeWorldLoadRequest.OpenSandbox(WorldLoadTransition.SceneReplacement), default);
                    test.Wait(preparing);
                    Assert(preparing.Result.Succeeded && test.Load.Begins == (initialDispatch ? 1 : 0),
                        "preparation must exercise the selected initial scene dispatch path");
                    var preparation = preparing.Result.Value!;
                    var calls = 0;
                    var dispatch = new DelegateNativeSceneDispatch(sink =>
                    {
                        calls++;
                        sink.FailCaller(ModErrorCode.External, "Synthetic refusal");
                        return NativeSceneDispatchStatus.NotDispatched;
                    });
                    var refused = preparation.TryDispatchContent(dispatch, default);
                    Assert(!refused.Succeeded && refused.ErrorCode == ModErrorCode.External && refused.ErrorMessage == "Synthetic refusal",
                        "content admission must return its exact refusal");
                    Assert(preparation.TryDispatchContent(dispatch, default).ErrorCode == ModErrorCode.InvalidState && calls == 1,
                        "a refused content attempt still consumes the single-use route");
                    test.AssertExclusive();
                    test.Load.PlayerReady = true;
                    var finish = preparation.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null, null, default);
                    test.Wait(finish);
                    Assert(!finish.Result.Succeeded && finish.Result.ErrorCode == refused.ErrorCode && finish.Result.ErrorMessage == refused.ErrorMessage,
                        "NotDispatched content must retain its exact refusal at readiness; actual=" + finish.Result.ErrorCode);
                    Assert(test.Load.SpawnApplications == 0, "refused content must fail before applying a ready player spawn");
                    test.AssertReleased();
                    var repeated = preparation.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null, null, default);
                    test.Wait(repeated);
                    Assert(repeated.Result.ErrorCode == ModErrorCode.InvalidState, "failed readiness is single-use too");
                    Console.WriteLine("NotDispatched readiness refusal passed: initial scene dispatch=" + initialDispatch);
                }
                catch (Exception error) { failures.Add("initial scene dispatch=" + initialDispatch + ": " + error.Message); }
            }
            if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
        }

        private static void LoadMap(bool published)
        {
            using var test = new Fixture();
            var backend = new MapWorldServiceTests.Backend();
            using var service = test.Maps(backend);
            backend.OnPrepare = test.AssertExclusive;
            backend.OnImport = test.AssertExclusive;
            backend.OnSpawn = test.AssertExclusive;
            var loading = test.Dispatcher.InvokeCallbackAsync(() => service.LoadAsync(test.Context, Request(published), default));
            test.Until(() => backend.Spawned != null || loading.IsCompleted);
            Assert(backend.Imported == 1 && backend.Spawned == "entry" && !loading.IsCompleted,
                "real preparation must admit its map import before readiness; imports=" + backend.Imported
                + (loading.IsCompleted ? "; result=" + loading.Result.ErrorCode : string.Empty));
            test.AssertExclusive();
            test.Load.PlayerReady = true; test.Clock.Advance(); test.Wait(loading);
            Assert(loading.Result.Succeeded && loading.Result.Value!.Readiness.Spawn == MapWorldServiceTests.Backend.Spawn,
                "the actual readiness service applies the map spawn");
            Assert(backend.SceneArgument == (published ? "scene" : null) && backend.Label == (published ? "map:example" : "map:package"),
                "source identity and requested scene survive the full composition");
            test.AssertReleased();
            loading.Result.Value!.Dispose(); test.Dispatcher.Drain();
            Assert(backend.ContentDisposals == 1 && test.Load.Disposals == 1, "world disposal releases imported content and preparation once");
        }

        private static InternalMapWorldRequest Request(bool published) => published
            ? new InternalMapWorldRequest("example", "scene", null)
            : new InternalMapWorldRequest(null, null, _ => Task.FromResult(OperationResult<byte[]>.Success(MapDocumentTests.Project())));

        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private sealed class Fixture : IDisposable
        {
            internal readonly HostDispatcher Dispatcher = new HostDispatcher();
            internal readonly OwnerModLifetime Lifetime = new OwnerModLifetime();
            internal readonly BuiltinWorldFixture Context = new BuiltinWorldFixture();
            internal readonly NativeLoad Load = new NativeLoad();
            internal readonly Clock Clock = new Clock();
            internal readonly SceneCoordinator Coordinator;
            internal readonly INativeTransitionReservation Reservation;
            internal readonly IInternalSceneTransitionService Parent;
            private readonly IInternalSceneTransitionService sibling;
            private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("map-contract-");
            internal Fixture()
            {
                Coordinator = new SceneCoordinator(dispatcher: Dispatcher);
                Reservation = Coordinator.TryReserve(new NativeTransitionOwner("world.owner", "map-session"), "map world").Value!;
                Parent = Reservation.BorrowFor("world.owner", "map-session").SceneTransitions;
                sibling = Reservation.BorrowFor("other.provider", "map-session").SceneTransitions;
                Context.LifetimeOverride = Lifetime;
                Context.SceneTransitions = Parent;
                Context.SpawnPolicy = new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault);
                Context.WorldRuntimeOverride = new OwnerWorldRuntimeService(Lifetime, Parent, new NativeBackend(Load), Clock, Dispatcher);
            }
            internal MapWorldService Maps(MapWorldServiceTests.Backend backend) => new MapWorldService(
                new MapCache(Path.Combine(directory.FullName, "cache")), new PublishedBytes(), () => backend);
            internal void AssertExclusive()
            {
                foreach (var route in new[] { Parent, sibling })
                {
                    Assert(route.Acquire("Scene", false, "competing route").ErrorCode == ModErrorCode.Conflict,
                        "parent and sibling routes cannot acquire admission while preparation owns it");
                    Assert(route.TryDispatch(new NativeSceneRequest("Scene", false, "competing dispatch"),
                        new DelegateNativeSceneDispatch(_ => throw new InvalidOperationException("competitor entered"))).ErrorCode == ModErrorCode.Conflict,
                        "parent and sibling routes cannot dispatch while preparation owns admission");
                }
                Assert(Coordinator.TryReserve(new NativeTransitionOwner("other", "other-session"), "competitor").ErrorCode == ModErrorCode.Conflict,
                    "independent owner cannot reserve the executor");
            }
            internal void AssertReleased()
            {
                var next = Parent.Acquire("Scene", false, "after completion");
                Assert(next.Succeeded, "completion or failure releases the child admission");
                next.Value!.Dispose();
            }
            internal void Wait(Task task) => HostDispatcherTests.Pump(Dispatcher, task);
            internal void Until(Func<bool> condition)
            {
                var end = DateTime.UtcNow.AddSeconds(10);
                while (!condition() && DateTime.UtcNow < end) { Dispatcher.Drain(); Thread.Sleep(1); }
                Dispatcher.Drain();
                Assert(condition(), "controlled map stage did not complete");
            }
            public void Dispose()
            {
                Lifetime.Dispose(); Dispatcher.Drain(); Reservation.Dispose(); Dispatcher.Drain();
                Context.Dispose(); Dispatcher.Dispose(); directory.Delete(true);
            }
        }

        private sealed class PublishedBytes : IMapTransport
        {
            public Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token)
            {
                var bytes = MapDocumentTests.Project();
                return Task.FromResult(new MapResponse(200, new MemoryStream(bytes), bytes.Length));
            }
        }
        private sealed class NativeBackend : IWorldRuntimeBackend
        {
            private readonly NativeLoad load;
            internal NativeBackend(NativeLoad load) { this.load = load; }
            public OperationResult<IWorldNativeLoad> CreateLoad(NativeWorldLoadRequest request) => OperationResult<IWorldNativeLoad>.Success(load);
            public OperationResult<IReadOnlyList<NativeWorldEntry>> Discover(NativeWorldSource source, int maximumResults) => throw new NotSupportedException();
        }
        private sealed class NativeLoad : IWorldNativeLoad
        {
            internal bool PlayerReady;
            internal bool DefaultReady = true, SceneValid = true;
            internal int Disposals;
            public string ExpectedSceneName => "Scene";
            public bool RequiresDispatch { get; set; } = true;
            internal int Begins, SpawnApplications;
            public NativeSceneDispatchStatus Begin(IInternalNativeSceneCompletion completion)
            {
                Begins++;
                completion.NativeCompleted(OperationResult<SceneSnapshot>.Success(new SceneSnapshot("Scene", true, true)));
                return NativeSceneDispatchStatus.Dispatched;
            }
            public OperationResult<WorldSceneIdentity> CaptureScene() => OperationResult<WorldSceneIdentity>.Success(new WorldSceneIdentity(42, "Scene"));
            public OperationResult<bool> ValidateScene() => SceneValid ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Synthetic scene loss.");
            public OperationResult<bool> ValidateContent(IEntity? root) => OperationResult<bool>.Success(true);
            public OperationResult<TransformState>? ReadDefaultSpawn() => DefaultReady ? OperationResult<TransformState>.Success(TransformState.Identity) : null;
            public OperationResult<IReadOnlyList<TransformState>> FindSpawnMarkers(IEntity? root, string marker) => throw new NotSupportedException();
            public OperationResult<TransformState>? ApplySpawn(TransformState spawn)
            {
                SpawnApplications++;
                return PlayerReady ? OperationResult<TransformState>.Success(spawn) : null;
            }
            public void Dispose() { Disposals++; }
        }
        private sealed class Clock : IWorldRuntimeClock
        {
            private TaskCompletionSource<OperationResult<bool>> frame = new TaskCompletionSource<OperationResult<bool>>();
            public DateTime UtcNow => DateTime.UtcNow;
            public Task<OperationResult<bool>> NextFrameAsync(CancellationToken token) => frame.Task;
            internal void Advance()
            {
                var pending = frame; frame = new TaskCompletionSource<OperationResult<bool>>();
                pending.SetResult(OperationResult<bool>.Success(true));
            }
        }
    }
}
