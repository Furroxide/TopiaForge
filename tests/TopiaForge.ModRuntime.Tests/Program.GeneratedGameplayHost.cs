using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        // Only the native engine boundary is synthetic. Package loading, source
        // binding, child contexts, built-in providers and orchestration are real.
        private sealed class GeneratedGameplayHost : IRuntimeGameplayHost
        {
            public event Action<GameTimeSample>? FixedUpdate { add { } remove { } }
            public event Action<GameTimeSample>? LateUpdate { add { } remove { } }
            internal int ActiveResources;
            internal int NativeDispatches;
            internal int BundleLoads;
            internal int PrefabLoads;
            internal int Spawns;
            internal bool Disposed;
            internal WorldSpawnPolicy? LastSpawnPolicy;
            internal TaskCompletionSource<OperationResult<WorldReadiness>>? PendingReadiness;
            private readonly WorldSceneIdentity scene = new WorldSceneIdentity(451, "GeneratedScene");
            internal void CompleteReadiness() => PendingReadiness!.SetResult(OperationResult<WorldReadiness>.Success(
                new WorldReadiness(scene, new TransformState(new Vec3(2, 3, 4), Quat.Identity, new Vec3(1, 1, 1)))));
            public GameplayContextServices Create(string ownerModId, string packagePath, string dataPath,
                IModLifetime lifetime, IModLogger logger, NativeTransitionAccessSlot? transitionAccess = null)
            {
                var unavailable = GameplayContextServices.Unavailable(lifetime);
                return new GameplayContextServices(unavailable.Input, unavailable.LocalPlayer, unavailable.Entities,
                    unavailable.Physics, unavailable.Time, unavailable.Scheduler, unavailable.Scenes, unavailable.Interactions,
                    unavailable.Items, new Assets(this, lifetime, packagePath), unavailable.Audio, unavailable.Ui, null,
                    unavailable.SceneTransitions, new Native(this, lifetime, transitionAccess));
            }
            public GameTimeSample BeginFrame(float deltaTime) => new GameTimeSample(GameLoopPhase.Frame, deltaTime, deltaTime, 0d, 0);
            public void Dispose() => Disposed = true;

            private sealed class Native : IInternalWorldRuntimeService
            {
                private readonly GeneratedGameplayHost owner;
                private readonly IModLifetime lifetime;
                private readonly NativeTransitionAccessSlot? access;
                internal Native(GeneratedGameplayHost owner, IModLifetime lifetime, NativeTransitionAccessSlot? access)
                { this.owner = owner; this.lifetime = lifetime; this.access = access; }
                public Task<OperationResult<IReadOnlyList<NativeWorldEntry>>> DiscoverAsync(NativeWorldSource source, int maximumResults, CancellationToken token) =>
                    throw new InvalidOperationException("Generated static worlds do not discover content.");
                public async Task<OperationResult<IInternalWorldPreparation>> PrepareAsync(NativeWorldLoadRequest request, CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    Assert(access?.Borrowed != null && !lifetime.IsStopping, "native preparation requires the real scoped provider grant");
                    var started = access!.Borrowed!.TryDispatch(new NativeSceneRequest(owner.scene.Name, false, "generated engine fixture", false),
                        new DelegateNativeSceneDispatch(completion =>
                        {
                            owner.NativeDispatches++;
                            completion.NativeCompleted(OperationResult<SceneSnapshot>.Success(new SceneSnapshot(owner.scene.Name, true, true)));
                            return NativeSceneDispatchStatus.Dispatched;
                        }), token);
                    Assert(started.TryGetValue(out var operation), "the shared native executor admits generated world preparation");
                    var result = await operation!.Completion;
                    await operation.NativeDrained;
                    Assert(result.Succeeded, "the synthetic native operation completed successfully");
                    return OperationResult<IInternalWorldPreparation>.Success(new Preparation(owner, lifetime));
                }
            }

            private abstract class Resource : IDisposable
            {
                private readonly GeneratedGameplayHost owner;
                private IDisposable? lease;
                internal bool IsDisposed;
                protected Resource(GeneratedGameplayHost owner) { this.owner = owner; owner.ActiveResources++; }
                internal void Own(IModLifetime lifetime)
                {
                    try { lease = lifetime.Track(this); }
                    catch { Dispose(); throw; }
                    if (IsDisposed) lease.Dispose();
                }
                public void Dispose()
                {
                    if (IsDisposed) return;
                    IsDisposed = true;
                    try { Release(); }
                    finally { owner.ActiveResources--; lease?.Dispose(); }
                }
                protected virtual void Release() { }
            }

            private sealed class Preparation : Resource, IInternalWorldPreparation
            {
                private readonly GeneratedGameplayHost owner;
                private CancellationTokenRegistration cancellation;
                internal Preparation(GeneratedGameplayHost owner, IModLifetime lifetime) : base(owner) { this.owner = owner; Own(lifetime); }
                public WorldSceneIdentity Scene => owner.scene;
                public TransformState? NativeDefaultSpawn => TransformState.Identity;
                public Task<OperationResult<WorldReadiness>> FinishAsync(WorldSpawnPolicy policy, IEntity? contentRoot,
                    TransformState? providerSpawn, CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    Assert(!IsDisposed, "native preparation is still owned");
                    if (policy.Kind == WorldSpawnKind.AuthoredMarker)
                        Assert(policy.MarkerName == "SpawnPoint" && contentRoot?.IsAlive == true, "the declared marker is resolved only against live bundle content");
                    owner.LastSpawnPolicy = policy;
                    var pending = new TaskCompletionSource<OperationResult<WorldReadiness>>();
                    owner.PendingReadiness = pending;
                    cancellation = token.Register(() => pending.TrySetCanceled(token));
                    return pending.Task;
                }
                protected override void Release() => cancellation.Dispose();
            }

            private sealed class Assets : IAssetService
            {
                private readonly GeneratedGameplayHost owner;
                private readonly IModLifetime lifetime;
                private readonly string package;
                internal Assets(GeneratedGameplayHost owner, IModLifetime lifetime, string package)
                { this.owner = owner; this.lifetime = lifetime; this.package = package; }
                public Task<OperationResult<IAssetBundle>> LoadBundleAsync(string relativePath, CancellationToken token = default)
                {
                    token.ThrowIfCancellationRequested();
                    Assert(File.Exists(Path.Combine(package, relativePath)), "the installed generated package contains its declared bundle bytes");
                    owner.BundleLoads++;
                    var bundle = new Bundle(owner, relativePath); bundle.Own(lifetime);
                    return Task.FromResult(OperationResult<IAssetBundle>.Success(bundle));
                }
                public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(IAssetBundle bundle, string name, CancellationToken token = default)
                {
                    token.ThrowIfCancellationRequested();
                    Assert(bundle.IsAlive && name == "assets/world/world.prefab", "the generated prefab path reaches the owned bundle facade unchanged");
                    owner.PrefabLoads++;
                    var prefab = new Prefab(owner, name); prefab.Own(lifetime);
                    return Task.FromResult(OperationResult<IPrefabAsset>.Success(prefab));
                }
                public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request)
                {
                    Assert(request.Prefab.IsAlive, "spawning requires a live owned prefab");
                    owner.Spawns++;
                    var entity = new Entity(owner, request.Transform); entity.Own(lifetime);
                    return OperationResult<ISpawnedEntity>.Success(entity);
                }
            }
            private sealed class Bundle : Resource, IAssetBundle
            {
                internal Bundle(GeneratedGameplayHost owner, string path) : base(owner) { RelativePath = path; }
                public string RelativePath { get; }
                public bool IsAlive => !IsDisposed;
            }
            private sealed class Prefab : Resource, IPrefabAsset
            {
                internal Prefab(GeneratedGameplayHost owner, string name) : base(owner) { Name = name; }
                public string Name { get; }
                public bool IsAlive => !IsDisposed;
            }
            private sealed class Entity : Resource, ISpawnedEntity
            {
                internal Entity(GeneratedGameplayHost owner, TransformState transform) : base(owner) { InitialTransform = transform; }
                public string Id => "generated.entity";
                public string Name => "Generated content";
                public bool IsAlive => !IsDisposed;
                public Vec3 Position => InitialTransform.Position;
                public TransformState InitialTransform { get; }
            }
        }
    }
}
