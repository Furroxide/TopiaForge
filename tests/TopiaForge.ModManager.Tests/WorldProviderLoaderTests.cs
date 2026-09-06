using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldProviderLoaderTests
    {
        public static void Run()
        {
            FailureAfterAllocationCleansEverything().GetAwaiter().GetResult();
            CancellationDisposesLatePreparation().GetAwaiter().GetResult();
            OwnerStopDisposesLatePreparation().GetAwaiter().GetResult();
            SuccessRetainsCancellationAndOwnsItsResources().GetAwaiter().GetResult();
            DiscoverySourcesRemainIndependentAndReloadStableKeys().GetAwaiter().GetResult();
            Console.WriteLine("All world provider loader tests passed.");
        }
        private static async Task FailureAfterAllocationCleansEverything()
        {
            using var context = new NativeContext();
            var trace = new List<string>();
            context.Native.Preparation.OnDispose = () => { trace.Add("native"); throw new InvalidOperationException("native-cleanup"); };
            var loaded = await WorldProviderLoader.LoadAsync(new LoadContext(context), Request(), default, (_, resources) =>
            {
                resources.Add(new Resource(() => { trace.Add("content"); throw new InvalidOperationException("content-cleanup"); }));
                throw new InvalidOperationException("content-construction");
            });
            Assert(!loaded.Succeeded && loaded.ErrorMessage.Contains("content-construction") && loaded.ErrorMessage.Contains("content-cleanup")
                && loaded.ErrorMessage.Contains("native-cleanup"), "startup and every independent cleanup error must remain visible");
            Assert(string.Join(",", trace) == "content,native" && context.Inner.Lifetime.TrackedResourceCount == context.BaselineResources, "failed world construction must drain every owned resource; trace=" + string.Join(",", trace) + "; tracked=" + context.Inner.Lifetime.TrackedResourceCount);
        }
        private static async Task CancellationDisposesLatePreparation()
        {
            using var context = new NativeContext();
            using var cancellation = new CancellationTokenSource();
            var pending = new TaskCompletionSource<OperationResult<IInternalWorldPreparation>>();
            context.Native.Pending = pending.Task;
            var load = WorldProviderLoader.LoadAsync(new LoadContext(context), Request(), cancellation.Token);
            cancellation.Cancel();
            pending.SetResult(OperationResult<IInternalWorldPreparation>.Success(context.Native.Preparation));
            var result = await load;
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.Cancelled, "cancellation must not turn into successful late readiness");
            Assert(context.Native.Preparation.Disposals == 1 && context.Native.Preparation.Finishes == 0
                && context.Inner.Lifetime.TrackedResourceCount == context.BaselineResources, "a late preparation must be disposed without starting content");
        }
        private static async Task OwnerStopDisposesLatePreparation()
        {
            using var context = new NativeContext();
            var pending = new TaskCompletionSource<OperationResult<IInternalWorldPreparation>>();
            context.Native.Pending = pending.Task;
            var load = WorldProviderLoader.LoadAsync(new LoadContext(context), Request(), default);
            context.Inner.Lifetime.Dispose();
            pending.SetResult(OperationResult<IInternalWorldPreparation>.Success(context.Native.Preparation));
            var result = await load;
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.Cancelled && context.Native.Preparation.Disposals == 1,
                "owner unload must cancel and release a late native result");
        }
        private static async Task SuccessRetainsCancellationAndOwnsItsResources()
        {
            using var context = new NativeContext();
            using var cancellation = new CancellationTokenSource();
            var loaded = await WorldProviderLoader.LoadAsync(new LoadContext(context), Request(), cancellation.Token);
            Assert(loaded.Succeeded && context.Native.Preparation.Finishes == 1 && context.Native.Preparation.Disposals == 0,
                "load succeeds only after readiness and transfers an owned instance");
            Assert(context.Inner.Lifetime.TrackedResourceCount == context.BaselineResources + 1, "successful world resources must stay owned after the async method returns");
            cancellation.Cancel();
            Assert(context.Native.Preparation.FinishToken.IsCancellationRequested, "the returned world must retain its session cancellation link");
            loaded.Value!.Dispose();
            loaded.Value.Dispose();
            Assert(context.Native.Preparation.Disposals == 1 && context.Inner.Lifetime.TrackedResourceCount == context.BaselineResources, "instance teardown must be exactly once");
        }
        private static async Task DiscoverySourcesRemainIndependentAndReloadStableKeys()
        {
            using var context = new NativeContext();
            var curated = await new CuratedLevelDiscoverySource().DiscoverAsync(new DiscoveryContext(context, "example.curated"), default);
            var scenes = await new BuildSceneDiscoverySource().DiscoverAsync(new DiscoveryContext(context, "example.scenes"), default);
            Assert(curated.Succeeded && scenes.Succeeded && curated.Value!.Count == 1 && scenes.Value!.Count == 1,
                "curated levels cannot suppress build-settings worlds");
            var selected = scenes.Value![0].Id;
            var loaded = await new BuildSceneDiscoverySource().LoadAsync(new LoadContext(context, selected, "example.scenes"), default);
            Assert(loaded.Succeeded && context.Native.LastRequest!.Kind == NativeWorldLoadKind.Scene
                && context.Native.LastRequest.Key == "Assets/Scenes/City.unity", "a fresh provider must recover the full scene-path key, not its display name");
            loaded.Value!.Dispose();
            context.Native.EntriesRemoved = true;
            var missing = await new BuildSceneDiscoverySource().LoadAsync(new LoadContext(context, selected, "example.scenes"), default);
            Assert(!missing.Succeeded && missing.ErrorCode == ModErrorCode.NotFound && context.Native.Prepares == 1,
                "stale observations must fail before dispatching a removed native source");
        }
        private static NativeWorldLoadRequest Request() => NativeWorldLoadRequest.Scene("Assets/City.unity", WorldLoadTransition.SceneReplacement);
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private sealed class Resource : IDisposable
        {
            private readonly Action action;
            public Resource(Action action) { this.action = action; }
            public void Dispose() => action();
        }
        private sealed class Preparation : IInternalWorldPreparation
        {
            public WorldSceneIdentity Scene { get; } = new WorldSceneIdentity(23, "City");
            public TransformState? NativeDefaultSpawn => TransformState.Identity;
            public Action? OnDispose { get; set; }
            public int Disposals { get; private set; }
            public int Finishes { get; private set; }
            public CancellationToken FinishToken { get; private set; }
            public Task<OperationResult<WorldReadiness>> FinishAsync(WorldSpawnPolicy policy, IEntity? contentRoot,
                TransformState? providerSpawn, CancellationToken cancellationToken)
            {
                Finishes++;
                FinishToken = cancellationToken;
                return Task.FromResult(OperationResult<WorldReadiness>.Success(new WorldReadiness(Scene, TransformState.Identity)));
            }
            public void Dispose() { Disposals++; OnDispose?.Invoke(); }
        }
        private sealed class Runtime : IInternalWorldRuntimeService
        {
            public Preparation Preparation { get; } = new Preparation();
            public Task<OperationResult<IInternalWorldPreparation>>? Pending { get; set; }
            public NativeWorldLoadRequest? LastRequest { get; private set; }
            public int Prepares { get; private set; }
            public bool EntriesRemoved { get; set; }
            public Task<OperationResult<IInternalWorldPreparation>> PrepareAsync(NativeWorldLoadRequest request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                Prepares++;
                return Pending ?? Task.FromResult(OperationResult<IInternalWorldPreparation>.Success(Preparation));
            }
            public Task<OperationResult<IReadOnlyList<NativeWorldEntry>>> DiscoverAsync(NativeWorldSource source, int maximumResults, CancellationToken cancellationToken) =>
                Task.FromResult(OperationResult<IReadOnlyList<NativeWorldEntry>>.Success(EntriesRemoved ? Array.Empty<NativeWorldEntry>() : new[]
                {
                    new NativeWorldEntry(source, source == NativeWorldSource.BuildScenes ? "Assets/Scenes/City.unity" : "checkpoint:city", "City", "", "City")
                }));
        }
        private sealed class DiscoveryContext : IWorldDiscoveryContext
        {
            public DiscoveryContext(IModContext context, string family) { Context = context; FamilyId = family; }
            public string FamilyId { get; }
            public int MaximumResults => 2;
            public IModContext Context { get; }
        }
        private sealed class LoadContext : IWorldLoadContext
        {
            public LoadContext(IModContext context, string world = "example.world", string? family = null) { Context = context; WorldId = world; WorldFamilyId = family; }
            public string SessionId => "session";
            public string TargetId => "example.target";
            public string WorldId { get; }
            public string? WorldFamilyId { get; }
            public WorldLoadTransition Transition => WorldLoadTransition.SceneReplacement;
            public WorldSpawnPolicy SpawnPolicy => new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault);
            public IModContext Context { get; }
        }
        private sealed class NativeContext : IModContext, IInternalWorldRuntimeContext, IDisposable
        {
            public FakeModContext Inner { get; } = new FakeModContext();
            public Runtime Native { get; } = new Runtime();
            public int BaselineResources { get; }
            public NativeContext() { BaselineResources = Inner.Lifetime.TrackedResourceCount; }
            public IInternalWorldRuntimeService WorldRuntime => Native;
            private IModContext Context => Inner;
            public ModIdentity Identity => Context.Identity;
            public IRuntimeInfo Runtime => Context.Runtime;
            public IModLogger Logger => Context.Logger;
            public IModLifetime Lifetime => Context.Lifetime;
            public IModEvents Events => Context.Events;
            public IModFiles Files => Context.Files;
            public IModConfigService Config => Context.Config;
            public ILocalModStorageService LocalStorage => Context.LocalStorage;
            public IInputService Input => Context.Input;
            public IGameTime Time => Context.Time;
            public IModScheduler Scheduler => Context.Scheduler;
            public ILocalPlayerService LocalPlayer => Context.LocalPlayer;
            public ISceneService Scenes => Context.Scenes;
            public IEntityService Entities => Context.Entities;
            public IPhysicsService Physics => Context.Physics;
            public IInteractionService Interactions => Context.Interactions;
            public IItemService Items => Context.Items;
            public IAssetService Assets => Context.Assets;
            public IAudioService Audio => Context.Audio;
            public IUiService Ui => Context.Ui;
            public ILocalizationService Localization => Context.Localization;
            public ICommandService Commands => Context.Commands;
            public IDiagnosticsService Diagnostics => Context.Diagnostics;
            public IExtensionService Extensions => Context.Extensions;
            public void Dispose() => Inner.Dispose();
        }
    }
}
