using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Runs the authority routing suites (WS3.7).</summary>
    internal static class AuthorityTests
    {
        internal static void Run()
        {
            MultiplayerAuthorityRulesTests.Run();
            AuthorityWireTests.Run();
            SpawnAdoptionTests.Run();
            AuthorityAssetLoadTests.Run();
            SpawnChangeTests.Run();
            SpawnShadowTests.Run();
            SpawnAbuseTests.Run();
            ProxyParticipantTests.Run();
            AuthorityCostTests.Run();
            Console.WriteLine("Authority routing tests passed.");
        }
    }

    /// <summary>
    /// One mod's context on one peer, as ModContext composes it: its replication seam, the network decorators over the
    /// peer's fake engine, and a UI service that records toasts. A test acts as the mod's scripts by default, and as the
    /// player inside <see cref="AsPlayer{T}"/>. Its spawns are the peer's unless it brings an asset service of its own,
    /// made for its lifetime, as a scope's is.
    /// </summary>
    internal sealed class AuthorityMod
    {
        internal const string Bundle = "assets/world.bundle";

        internal AuthorityMod(WorldPeer peer, ModExecutionContext context = ModExecutionContext.Process, string modId = WorldPeer.ModId,
            ModNetworkClass networkClass = ModNetworkClass.Shared, Func<OwnerModLifetime, IAssetService>? assets = null)
        {
            Peer = peer;
            ModId = modId;
            Lifetime = new OwnerModLifetime();
            Seam = peer.Replication.Bind(modId, new ModNetProfile(networkClass, context), Lifetime);
            Seam.Ui = Ui;
            Assets = new NetAssetService(assets?.Invoke(Lifetime) ?? peer.Assets, Seam);
            Entities = new NetEntityService(FakeEntities, Seam);
        }

        internal WorldPeer Peer { get; }

        internal string ModId { get; }

        internal OwnerModLifetime Lifetime { get; }

        internal RuntimeReplication Seam { get; }

        internal RecordingUi Ui { get; } = new RecordingUi();

        internal NetAssetService Assets { get; }

        internal FakeEntities FakeEntities { get; } = new FakeEntities();

        internal NetEntityService Entities { get; }

        /// <summary>Loads a prefab through the mod's own asset service, as its code does; the fake loader lands at once.</summary>
        internal IPrefabAsset Load(string assetName = "Crate", string bundlePath = Bundle)
        {
            var bundle = Assets.LoadBundleAsync(bundlePath).Result.Value!;
            return Assets.LoadPrefabAsync(bundle, assetName).Result.Value!;
        }

        /// <summary>Spawns a prefab at x, as the mod's code acting without intent does.</summary>
        internal OperationResult<ISpawnedEntity> Spawn(IPrefabAsset prefab, float x) =>
            Assets.Spawn(new AssetSpawnRequest(prefab, new TransformState(new Vec3(x, 1, 0), Quat.Identity, new Vec3(1, 1, 1))));

        /// <summary>Spawns a prefab at x on the player's intent, as the mod's code run by the player's own action does.</summary>
        internal OperationResult<ISpawnedEntity> SpawnAsPlayer(IPrefabAsset prefab, float x) => AsPlayer(() => Spawn(prefab, x));

        /// <summary>Runs <paramref name="act"/> as code acting on the local player's intent: a command the player ran.</summary>
        internal T AsPlayer<T>(Func<T> act)
        {
            using (Peer.Runtime.Intents.EnterPlayerCommand(ModId)) return act();
        }

        internal static TransformState At(float x, float y = 1f) => new TransformState(new Vec3(x, y, 0), Quat.Identity, new Vec3(1, 1, 1));
    }

    /// <summary>An entity service over the fake engine: moves and destroys what it knows, as the game's does.</summary>
    internal sealed class FakeEntities : IEntityService
    {
        internal int Moves { get; private set; }

        internal int Destroys { get; private set; }

        public bool TryGetTransform(IEntity entity, out TransformState transform)
        {
            var target = ObjectOf(entity);
            transform = target == null ? TransformState.Identity : new TransformState(new Vec3(target.Position.X, target.Position.Y, target.Position.Z), Quat.Identity, new Vec3(1, 1, 1));
            return target != null && target.IsAlive;
        }

        public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform)
        {
            var target = ObjectOf(entity);
            if (target == null || !target.IsAlive) return OperationResult<TransformState>.Failure(ModErrorCode.NotFound, "The entity is no longer alive.");
            Moves++;
            target.Position = new NetVector3(transform.Position.X, transform.Position.Y, transform.Position.Z);
            return OperationResult<TransformState>.Success(transform);
        }

        public IReadOnlyList<IEntity> Query(EntityQuery query) => Array.Empty<IEntity>();

        public OperationResult<bool> Destroy(IEntity entity)
        {
            if (!(entity is FakeSpawnedEntity spawned)) return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Only entities spawned by this mod can be destroyed.");
            Destroys++;
            spawned.Dispose();
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<IEntityMotion> AcquireMotion(IEntity entity) => OperationResult<IEntityMotion>.Failure(ModErrorCode.Unavailable, "No motion here.");

        internal static FakeWorldObject? ObjectOf(IEntity entity) =>
            entity is FakeSpawnedEntity spawned ? spawned.Object : entity is FakeWorldEntity fake ? fake.Object : null;
    }

    /// <summary>A UI service that records the toasts it is asked to show.</summary>
    internal sealed class RecordingUi : IUiService
    {
        internal List<(string Message, UiTone Tone)> Toasts { get; } = new List<(string, UiTone)>();

        public UiAccessibilityPreferences Accessibility => UiAccessibilityPreferences.Default;

        public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences) =>
            OperationResult<UiAccessibilityPreferences>.Success(preferences);

        public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => OperationResult<IUiSurface>.Failure(ModErrorCode.Unavailable, "No surfaces.");

        public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => OperationResult<IUiModal>.Failure(ModErrorCode.Unavailable, "No modals.");

        public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral)
        {
            Toasts.Add((message, tone));
            return OperationResult<bool>.Success(true);
        }
    }

    /// <summary>A provider's spawn handler over the fake engine, which records whose intent each spawn ran on.</summary>
    internal sealed class RecordingSpawnHandler : IInternalSpawnHandler
    {
        private readonly FakeWorldScene scene;
        private readonly Func<RuntimeReplication?> seam;

        internal RecordingSpawnHandler(FakeWorldScene scene, Func<RuntimeReplication?> seam)
        {
            this.scene = scene;
            this.seam = seam;
        }

        internal List<InternalIntentRecord> SpawnedFor { get; } = new List<InternalIntentRecord>();

        internal List<IEntity> Despawned { get; } = new List<IEntity>();

        internal bool Refuse { get; set; }

        /// <summary>Gets or sets whether the handler makes its spawn through its seam's <c>RequestSpawn</c>, recording it itself, as a provider may.</summary>
        internal bool RequestsThroughSeam { get; set; }

        public OperationResult<IEntity> Spawn(InternalSpawnDescriptor descriptor)
        {
            SpawnedFor.Add(seam()?.CurrentIntent ?? default);
            if (Refuse) return OperationResult<IEntity>.Failure(ModErrorCode.Unavailable, "refused");
            if (RequestsThroughSeam) return seam()!.RequestSpawn(descriptor, () => MakeHere(descriptor.Transform, descriptor.Kind));
            return MakeHere(descriptor.Transform, descriptor.Kind);
        }

        public void Despawn(IEntity entity)
        {
            Despawned.Add(entity);
            FakeEntities.ObjectOf(entity)?.Destroy();
        }

        /// <summary>Makes a provider's entity here, as its own spawn would on a joined player.</summary>
        internal OperationResult<IEntity> MakeHere(TransformState at, string kind = "tests.box")
        {
            var target = scene.ActiveScene ?? scene.AddScene("Spawns");
            return OperationResult<IEntity>.Success(scene.Add(target, kind, null, true, false, new NetVector3(at.Position.X, at.Position.Y, at.Position.Z)).Entity);
        }
    }

    /// <summary>A host and its joined players over a yard of boxes, the test mod shared, as the authority suites start from.</summary>
    internal static class AuthorityRig
    {
        internal static WorldRig Create(int players = 2, bool shared = true, int boxes = 0)
        {
            var rig = new WorldRig();
            rig.UseScene(scene =>
            {
                var yard = scene.AddScene("Yard");
                for (var index = 0; index < boxes; index++) scene.Add(yard, "Box", null, rigidbody: true, position: new NetVector3(index * 2, 0, 0));
            });
            rig.Host.World.Proxies.IsSharedMod = modId => shared && string.Equals(modId, WorldPeer.ModId, StringComparison.OrdinalIgnoreCase);
            rig.OpenToMultiplayer();
            for (var index = 1; index <= players; index++) rig.JoinAndSynchronize(rig.AddPlayer("p" + index));
            return rig;
        }

        /// <summary>Gets the host's live entities that count among the objects of the player with <paramref name="slot"/>.</summary>
        internal static List<HostWorldEntity> OwnedBy(WorldPeer host, int slot)
        {
            var ids = new List<uint>();
            host.Host.CollectIds(ids);
            var owned = new List<HostWorldEntity>();
            foreach (var id in ids)
            {
                if (host.Host.TryGetHosted(new NetworkEntityId(id), out var hosted) && hosted.OwnerSlot == slot) owned.Add(hosted);
            }

            return owned;
        }

        internal static FakeWorldObject Object(IEntity entity) => FakeEntities.ObjectOf(entity) ?? throw new InvalidOperationException("Not a fake object.");

        /// <summary>Gets the live crates a peer's scene holds: what that player sees.</summary>
        internal static List<FakeWorldObject> Live(WorldPeer peer, string name = "Crate") =>
            peer.Scene.AllObjects().Where(item => item.IsAlive && item.Name == name).ToList();
    }
}
