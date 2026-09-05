using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class ScopedAssetOwnershipTests
    {
        internal static void Run(string root)
        {
            var parentIdentity = new AssetScopeOwnership("package");
            var childIdentity = new AssetScopeOwnership("package");
            var siblingIdentity = new AssetScopeOwnership("package");
            childIdentity.AttachParent(parentIdentity); siblingIdentity.AttachParent(parentIdentity);
            Assert(childIdentity.AllowsSpawn(parentIdentity), "an explicit child scope must accept a parent-owned prefab for spawning");
            Assert(!childIdentity.AllowsSpawn(siblingIdentity) && !parentIdentity.AllowsSpawn(childIdentity),
                "the parent bridge must not grant sibling or upward asset access");
            Throws(() => new AssetScopeOwnership("foreign").AttachParent(parentIdentity));
            Throws(() => new AssetScopeOwnership("package").AttachParent(childIdentity));
            Throws(() => childIdentity.AttachParent(parentIdentity));

            using var dispatcher = new HostDispatcher();
            var paths = new ManagerPaths(Path.Combine(root, "scoped-assets")); paths.EnsureCreated();
            var factory = new Factory();
            var parent = new ModContext(new ModManifest { Id = "assets.mod", Name = "Assets", Version = "1.0.0" },
                paths, "package", new OwnerFacadeStoppingTests.Logger(), new ModServiceRegistry(), null, factory);
            var creation = parent.CreateChildScopeAsync("assets-session", CancellationToken.None, () => { },
                new NativeTransitionAccessSlot("assets-session:assets.mod", "assets-session", () => true), dispatcher);
            HostDispatcherTests.Pump(dispatcher, creation);
            var scope = creation.Result;
            var parentAssets = (Assets)parent.Assets;
            var childAssets = (Assets)scope.Context.Assets;
            var prefab = parentAssets.MakePrefab();
            var result = scope.Context.Assets.Spawn(new AssetSpawnRequest(prefab, TransformState.Identity));
            Assert(result.Succeeded && prefab.IsAlive && parentAssets.Spawned == 0 && childAssets.Spawned == 1,
                "parent-prefab spawning must allocate through the child service and child lifetime");
            scope.BeginStop(); scope.Dispose();
            Assert(prefab.IsAlive && !result.Value!.IsAlive,
                "session cleanup destroys its instance while preserving the parent prefab/bundle ownership");
            parent.DisposeLifetime();
            Assert(!prefab.IsAlive, "only package cleanup releases package asset handles");
            Console.WriteLine("ScopedAssetOwnershipTests passed.");
        }
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private static void Throws(Action action)
        { try { action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException("Expected invalid parent binding"); }
        private sealed class Factory : IGameplayContextFactory
        {
            public GameplayContextServices Create(string owner, string package, string data, IModLifetime lifetime,
                IModLogger logger, NativeTransitionAccessSlot? slot = null)
            {
                var other = GameplayContextServices.Unavailable(lifetime);
                return new GameplayContextServices(other.Input, other.LocalPlayer, other.Entities, other.Physics,
                    other.Time, other.Scheduler, other.Scenes, other.Interactions, other.Items, new Assets(package, lifetime),
                    other.Audio, other.Ui, other.UnityInterop, other.SceneTransitions);
            }
        }
        private sealed class Assets : IAssetService, IParentAssetScope
        {
            private readonly AssetScopeOwnership ownership;
            private readonly IModLifetime lifetime;
            internal int Spawned;
            internal Assets(string package, IModLifetime lifetime) { ownership = new AssetScopeOwnership(package); this.lifetime = lifetime; }
            public void AttachParent(IAssetService source) => ownership.AttachParent(((Assets)source).ownership);
            internal Prefab MakePrefab() { var prefab = new Prefab(ownership); lifetime.Track(prefab); return prefab; }
            public Task<OperationResult<IAssetBundle>> LoadBundleAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
            public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(IAssetBundle bundle, string name, CancellationToken token = default) => throw new NotSupportedException();
            public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request)
            {
                if (request.Prefab is not Prefab prefab || !ownership.AllowsSpawn(prefab.Owner))
                    return OperationResult<ISpawnedEntity>.Failure(ModErrorCode.InvalidArgument, "foreign prefab");
                var instance = new Instance(); lifetime.Track(instance); Spawned++;
                return OperationResult<ISpawnedEntity>.Success(instance);
            }
        }
        private sealed class Prefab : IPrefabAsset
        {
            internal Prefab(AssetScopeOwnership owner) { Owner = owner; }
            internal AssetScopeOwnership Owner { get; }
            public string Name => "prefab";
            public bool IsAlive { get; private set; } = true;
            public void Dispose() => IsAlive = false;
        }
        private sealed class Instance : ISpawnedEntity
        {
            public string Id => "instance";
            public string Name => "instance";
            public bool IsAlive { get; private set; } = true;
            public Vec3 Position => Vec3.Zero;
            public TransformState InitialTransform => TransformState.Identity;
            public void Dispose() => IsAlive = false;
        }
    }
}
