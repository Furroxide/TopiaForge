using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.TestDoubles
{
    /// <summary>
    /// An engine's loaded scenes, as the network world sees them: objects with names, children, rigidbodies and native
    /// systems, network marks, poses, and an asset service that spawns into them. The ModRuntime and ModManager harnesses
    /// both compile it.
    /// </summary>
    internal sealed class FakeWorldScene : INetworkWorldBackend
    {
        private readonly List<FakeSceneData> scenes = new List<FakeSceneData>();
        private int nextHandle = 100;

        internal IReadOnlyList<FakeSceneData> Scenes => scenes;

        /// <summary>Gets or sets the local player's object, which no census touches.</summary>
        internal FakeWorldObject? Player { get; set; }

        /// <summary>Gets or sets the scene spawns land in; the first scene by default.</summary>
        internal FakeSceneData? ActiveScene { get; set; }

        /// <summary>Gets or sets a failure the native census throws after appending this many targets.</summary>
        internal int? FailNativeCensusAfter { get; set; }

        internal int Prepared { get; private set; }

        internal FakeSceneData AddScene(string name)
        {
            var scene = new FakeSceneData(name, nextHandle++);
            scenes.Add(scene);
            ActiveScene ??= scene;
            return scene;
        }

        internal void UnloadScene(FakeSceneData scene)
        {
            foreach (var root in scene.Roots) root.Destroy();
            scenes.Remove(scene);
            if (ReferenceEquals(ActiveScene, scene)) ActiveScene = scenes.Count > 0 ? scenes[0] : null;
        }

        /// <summary>Adds an object; one with a rigidbody starts kinematic unless told otherwise, as the game's props are.</summary>
        internal FakeWorldObject Add(FakeSceneData scene, string name, FakeWorldObject? parent = null, bool rigidbody = false, bool kinematic = true, NetVector3 position = default)
        {
            var created = new FakeWorldObject(this, scene, name, parent, rigidbody, kinematic) { Position = position };
            if (parent == null) scene.Roots.Add(created);
            else parent.Children.Add(created);
            return created;
        }

        /// <summary>Gets how many times a census keyed scene objects, so tests can see when keys are taken.</summary>
        internal int Censuses { get; private set; }

        /// <summary>Gets or sets a failure the scene census throws, as a broken engine side might.</summary>
        internal Exception? FailSceneCensus { get; set; }

        public void CollectSceneObjects(int? sceneHandle, List<NetworkSceneObject> into)
        {
            if (FailSceneCensus != null) throw FailSceneCensus;
            Censuses++;
            var found = new List<(NetworkSceneObject Object, SceneObjectDescriptor Descriptor)>();
            Collect(sceneHandle, found);
            foreach (var item in found) into.Add(item.Object);
        }

        /// <summary>Appends what the census would key, with each object's descriptor, so tests can key it the model's way.</summary>
        internal void CollectDescriptors(int? sceneHandle, List<SceneObjectDescriptor> into)
        {
            var found = new List<(NetworkSceneObject Object, SceneObjectDescriptor Descriptor)>();
            Collect(sceneHandle, found);
            foreach (var item in found) into.Add(item.Descriptor);
        }

        public void CollectLoadedScenes(List<int> into)
        {
            foreach (var scene in scenes) into.Add(scene.Handle);
        }

        private void Collect(int? sceneHandle, List<(NetworkSceneObject Object, SceneObjectDescriptor Descriptor)> into)
        {
            foreach (var scene in scenes)
            {
                if (sceneHandle.HasValue && scene.Handle != sceneHandle.Value) continue;
                var path = new List<SceneObjectPathSegment>();
                for (var index = 0; index < scene.Roots.Count; index++) Walk(scene, scene.Roots[index], index, path, into);
            }
        }

        public void CollectNativeAuthority(int? sceneHandle, List<NativeAuthorityTarget> into)
        {
            foreach (var scene in scenes)
            {
                if (sceneHandle.HasValue && scene.Handle != sceneHandle.Value) continue;
                foreach (var root in scene.Roots) CollectNatives(root, into);
            }
        }

        public INetworkWorldObject? Adopt(IEntity entity)
        {
            var target = ObjectOf(entity);
            return target != null && target.IsAlive ? target.Root : null;
        }

        public bool TryGetNetworkId(IEntity entity, out NetworkEntityId id)
        {
            id = NetworkEntityId.None;
            for (var current = ObjectOf(entity); current != null; current = current.Parent)
            {
                if (current.Mark != 0) return NetworkEntityId.TryCreate(current.Mark, out id);
            }

            return false;
        }

        public void PrepareReplica(INetworkWorldObject replica)
        {
            if (!(replica is FakeWorldObject target)) return;
            Prepared++;
            target.Visit(each => each.Kinematic = each.HasRigidbody || each.Kinematic);
        }

        public INativeAuthoritySwitch? PresenceSwitch(INetworkWorldObject sceneObject) =>
            sceneObject is FakeWorldObject target ? target.PresenceSwitch : null;

        /// <summary>Gets every object of every scene, depth first.</summary>
        internal List<FakeWorldObject> AllObjects()
        {
            var all = new List<FakeWorldObject>();
            foreach (var scene in scenes)
            {
                foreach (var root in scene.Roots) root.Visit(all.Add);
            }

            return all;
        }

        private static FakeWorldObject? ObjectOf(IEntity entity) =>
            entity is FakeWorldEntity fake ? fake.Object : entity is FakeSpawnedEntity spawned ? spawned.Object : null;

        private void Walk(FakeSceneData scene, FakeWorldObject current, int siblingIndex, List<SceneObjectPathSegment> path,
            List<(NetworkSceneObject Object, SceneObjectDescriptor Descriptor)> into)
        {
            if (!current.IsAlive) return;
            path.Add(new SceneObjectPathSegment(current.Name, siblingIndex));
            try
            {
                if (Player != null && (ReferenceEquals(current, Player) || Player.IsUnder(current) || current.IsUnder(Player))) return;
                if (current.Mark != 0) return;
                if (current.HasRigidbody)
                {
                    var descriptor = new SceneObjectDescriptor(scene.Name, path.ToArray(), "UnityEngine.Rigidbody");
                    into.Add((new NetworkSceneObject(scene.Handle, descriptor, current), descriptor));
                    return;
                }

                for (var index = 0; index < current.Children.Count; index++) Walk(scene, current.Children[index], index, path, into);
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        private void CollectNatives(FakeWorldObject current, List<NativeAuthorityTarget> into)
        {
            if (!current.IsAlive) return;
            if (Player != null && (ReferenceEquals(current, Player) || current.IsUnder(Player) || Player.IsUnder(current)))
            {
                foreach (var child in current.Children) CollectNatives(child, into);
                return;
            }

            if (current.HasRigidbody) Append(into, new NativeAuthorityTarget(current.KinematicSwitch, true));
            foreach (var native in current.Natives) Append(into, new NativeAuthorityTarget(native, native.SuppressedValue));
            foreach (var child in current.Children) CollectNatives(child, into);
        }

        private void Append(List<NativeAuthorityTarget> into, NativeAuthorityTarget target)
        {
            if (FailNativeCensusAfter.HasValue && into.Count >= FailNativeCensusAfter.Value) throw new InvalidOperationException("the native census failed part way");
            into.Add(target);
        }
    }

    /// <summary>One loaded scene: its name, the engine's handle, and its root objects in order.</summary>
    internal sealed class FakeSceneData
    {
        internal FakeSceneData(string name, int handle)
        {
            Name = name;
            Handle = handle;
        }

        internal string Name { get; }

        internal int Handle { get; }

        internal List<FakeWorldObject> Roots { get; } = new List<FakeWorldObject>();
    }

    /// <summary>One object in a fake scene: a network root candidate when it carries a rigidbody.</summary>
    internal sealed class FakeWorldObject : INetworkWorldObject
    {
        private readonly FakeWorldScene world;

        internal FakeWorldObject(FakeWorldScene world, FakeSceneData scene, string name, FakeWorldObject? parent, bool rigidbody, bool kinematic)
        {
            this.world = world;
            Scene = scene;
            Name = name;
            Parent = parent;
            HasRigidbody = rigidbody;
            Kinematic = kinematic;
            Entity = new FakeWorldEntity(this);
            KinematicSwitch = new FakeSwitch(this, NativeAuthorityKind.SceneBody, () => Kinematic, value => Kinematic = value, true);
            PresenceSwitch = new FakeSwitch(this, NativeAuthorityKind.SceneObjectPresence, () => Active, value => Active = value, false);
        }

        internal FakeSceneData Scene { get; }

        internal string Name { get; private set; }

        internal FakeWorldObject? Parent { get; private set; }

        internal List<FakeWorldObject> Children { get; } = new List<FakeWorldObject>();

        internal List<FakeSwitch> Natives { get; } = new List<FakeSwitch>();

        internal bool HasRigidbody { get; }

        internal bool Kinematic { get; set; }

        internal bool Active { get; set; } = true;

        internal bool Destroyed { get; private set; }

        internal NetVector3 Position { get; set; }

        internal NetQuaternion Rotation { get; set; } = NetQuaternion.Identity;

        internal NetVector3? Velocity { get; set; }

        internal uint Mark { get; private set; }

        internal int Writes { get; private set; }

        internal FakeSwitch KinematicSwitch { get; }

        internal FakeSwitch PresenceSwitch { get; }

        internal FakeWorldObject Root => Parent == null ? this : Parent.Root;

        public bool IsAlive => !Destroyed;

        public bool IsNetworkRoot => Mark != 0;

        public IEntity Entity { get; }

        /// <summary>Adds a native system to the object, enabled unless told otherwise.</summary>
        internal FakeSwitch AddNative(NativeAuthorityKind kind, bool on = true, bool suppressedValue = false)
        {
            var value = on;
            var native = new FakeSwitch(this, kind, () => value, written => value = written, suppressedValue);
            Natives.Add(native);
            return native;
        }

        internal bool IsUnder(FakeWorldObject ancestor)
        {
            for (var current = Parent; current != null; current = current.Parent)
            {
                if (ReferenceEquals(current, ancestor)) return true;
            }

            return false;
        }

        internal void Visit(Action<FakeWorldObject> visit)
        {
            visit(this);
            foreach (var child in Children) child.Visit(visit);
        }

        internal void Destroy() => Visit(each => each.Destroyed = true);

        /// <summary>
        /// Destroys the object and takes it out of the hierarchy, as the engine does by the end of the frame: every later
        /// sibling's index moves down by one.
        /// </summary>
        internal void Remove()
        {
            Destroy();
            Siblings().Remove(this);
        }

        /// <summary>Moves the object to the end of another parent's children, or of the scene's roots, as the engine's SetParent does.</summary>
        internal void Reparent(FakeWorldObject? parent)
        {
            Siblings().Remove(this);
            Parent = parent;
            Siblings().Add(this);
        }

        internal void Rename(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));

        private List<FakeWorldObject> Siblings() => Parent?.Children ?? Scene.Roots;

        public bool TryReadPose(out NetPose pose)
        {
            pose = Velocity.HasValue ? new NetPose(Position, Rotation, Velocity.Value) : new NetPose(Position, Rotation);
            return !Destroyed;
        }

        public void WritePose(in NetPose pose)
        {
            Position = pose.Position;
            Rotation = pose.Rotation;
            Writes++;
        }

        public void SetNetworkIdentity(NetworkEntityId id) => Mark = id.Value;

        public override string ToString() => Name;
    }

    /// <summary>The entity mods see for a fake object.</summary>
    internal sealed class FakeWorldEntity : IEntity
    {
        internal FakeWorldEntity(FakeWorldObject target)
        {
            Object = target;
            Id = "fake-" + Interlocked.Increment(ref next);
        }

        private static int next;

        internal FakeWorldObject Object { get; }

        public string Id { get; }

        public string Name => Object.Name;

        public bool IsAlive => Object.IsAlive;

        public Vec3 Position => new Vec3(Object.Position.X, Object.Position.Y, Object.Position.Z);
    }

    /// <summary>A native switch on a fake object, which a test can make throw.</summary>
    internal sealed class FakeSwitch : INativeAuthoritySwitch
    {
        private readonly Func<bool> read;
        private readonly Action<bool> write;

        internal FakeSwitch(FakeWorldObject owner, NativeAuthorityKind kind, Func<bool> read, Action<bool> write, bool suppressedValue)
        {
            Owner = owner;
            Kind = kind;
            this.read = read;
            this.write = write;
            SuppressedValue = suppressedValue;
        }

        internal FakeWorldObject Owner { get; }

        internal bool SuppressedValue { get; }

        internal bool ThrowOnRead { get; set; }

        internal bool ThrowOnWrite { get; set; }

        internal int WriteCount { get; private set; }

        public NativeAuthorityKind Kind { get; }

        public object Target => this;

        public bool IsAlive => Owner.IsAlive;

        internal bool Value => read();

        public bool Read() => ThrowOnRead ? throw new InvalidOperationException("the native read failed") : read();

        public void Write(bool value)
        {
            if (ThrowOnWrite) throw new InvalidOperationException("the native write failed");
            WriteCount++;
            write(value);
        }

        /// <summary>Sets the value as the game would, behind the suppressor's back.</summary>
        internal void SetNatively(bool value) => write(value);
    }

    /// <summary>An asset service that loads fake bundles and prefabs and spawns their objects into a fake scene.</summary>
    internal sealed class FakeWorldAssets : IAssetService
    {
        private readonly FakeWorldScene world;
        private readonly IModLifetime lifetime;

        internal FakeWorldAssets(FakeWorldScene world, IModLifetime lifetime)
        {
            this.world = world;
            this.lifetime = lifetime;
        }

        internal int BundleLoads { get; private set; }

        internal int PrefabLoads { get; private set; }

        internal List<FakeSpawnedEntity> Spawned { get; } = new List<FakeSpawnedEntity>();

        /// <summary>Gets or sets a bundle path whose load fails, as a missing file would.</summary>
        internal string? MissingBundle { get; set; }

        /// <summary>Gets or sets whether loads wait until <see cref="CompleteLoads"/>, as the engine's asynchronous loader does.</summary>
        internal bool HoldLoads { get; set; }

        private readonly List<Action> waiting = new List<Action>();

        /// <summary>Completes every load that waits.</summary>
        internal int CompleteLoads()
        {
            var due = waiting.ToArray();
            waiting.Clear();
            foreach (var complete in due) complete();
            return due.Length;
        }

        public Task<OperationResult<IAssetBundle>> LoadBundleAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            BundleLoads++;
            var result = relativePath == MissingBundle
                ? OperationResult<IAssetBundle>.Failure(ModErrorCode.NotFound, "no such bundle")
                : OperationResult<IAssetBundle>.Success(new FakeBundle(relativePath));
            return Later(result);
        }

        public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(IAssetBundle bundle, string assetName, CancellationToken cancellationToken = default)
        {
            PrefabLoads++;
            return Later(OperationResult<IPrefabAsset>.Success((IPrefabAsset)new FakePrefab(bundle.RelativePath, assetName)));
        }

        private Task<T> Later<T>(T result)
        {
            if (!HoldLoads) return Task.FromResult(result);
            var pending = new TaskCompletionSource<T>();
            waiting.Add(() => pending.SetResult(result));
            return pending.Task;
        }

        public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request)
        {
            var scene = world.ActiveScene ?? world.AddScene("Active");
            var position = request.Transform.Position;
            var spawned = world.Add(scene, request.Prefab.Name, null, true, false, new NetVector3(position.X, position.Y, position.Z));
            var entity = new FakeSpawnedEntity(spawned, request.Transform);
            Spawned.Add(entity);
            lifetime.Track(entity);
            return OperationResult<ISpawnedEntity>.Success(entity);
        }

        private sealed class FakeBundle : IAssetBundle
        {
            internal FakeBundle(string path)
            {
                RelativePath = path;
            }

            public string RelativePath { get; }

            public bool IsAlive { get; private set; } = true;

            public void Dispose() => IsAlive = false;
        }
    }

    /// <summary>A prefab as the runtime's asset service hands one out: it knows its bundle and its asset name.</summary>
    internal sealed class FakePrefab : IPrefabAsset, IPrefabAssetProvenance
    {
        internal FakePrefab(string bundlePath, string assetName)
        {
            BundlePath = bundlePath;
            AssetName = assetName;
        }

        public string BundlePath { get; }

        public string AssetName { get; }

        public string Name => AssetName;

        public bool IsAlive { get; private set; } = true;

        public void Dispose() => IsAlive = false;
    }

    /// <summary>A spawned fake object, as a mod holds it.</summary>
    internal sealed class FakeSpawnedEntity : ISpawnedEntity
    {
        internal FakeSpawnedEntity(FakeWorldObject target, TransformState initial)
        {
            Object = target;
            InitialTransform = initial;
        }

        internal FakeWorldObject Object { get; }

        public string Id => Object.Entity.Id;

        public string Name => Object.Name;

        public bool IsAlive => Object.IsAlive;

        public Vec3 Position => Object.Entity.Position;

        public TransformState InitialTransform { get; }

        public void Dispose() => Object.Destroy();
    }
}
