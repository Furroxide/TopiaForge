using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotBodyTests
    {
        internal static void RunViews()
        { ViewTargets(); Console.WriteLine("Persistent body view assertions: " + assertions); }

        private static void ViewTargets()
        {
            var rig = NewRig();
            using var hostWorld = new BodyReadContext();
            using var host = new Bodies(rig.Host, hostWorld);
            var owner = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            using var ownerWorld = hostWorld.For(owner.Lifetime);
            var body = host.Bind(owner, ownerWorld).Spawn(Request()).Value!;
            using var hostView = new BodyRenderer(rig.Host, hostWorld);
            rig.Host.Replication.Views.Activate(hostView);
            using var local = owner.Seam.PlayerWorldView(hostView,
                rig.Host.Control.SessionSnapshot.LocalParticipantId?.Value ?? "local").Hide(body).Value!;
            Check(local != null && hostView.Hidden.Count == 1, "local returned body hides outside the loaded scene");
            local!.Dispose();
            Check(hostView.Hidden.Count == 0, "local hide restores on lease disposal");
            rig.OpenToMultiplayer();
            var player = rig.AddPlayer("guest");
            using var clientWorld = new BodyReadContext();
            using var guest = new Bodies(player, clientWorld);
            using var clientView = new BodyRenderer(player, clientWorld);
            player.Replication.Views.Activate(clientView);
            rig.JoinAndSynchronize(player);
            using var audience = owner.Seam.PlayerWorldView(hostView, player.Control.SessionSnapshot.LocalParticipantId!.Value.Value).Hide(body).Value!;
            Check(audience != null && rig.Run(() => clientView.Hidden.Count == 1), "remote audience hides actual provider replica");
            Check(hostView.Hidden.Count == 0, "remote hide does not affect the host audience");
            var original = guest.Native.Single();
            original.Object.Destroy();
            rig.Clock.Advance(5_000_001);
            Check(rig.Run(() => guest.Native.Count == 2 && clientView.Hidden.Contains(guest.Native[1].Object)), "canonical hide follows provider recovery");
            Check(!clientView.Hidden.Contains(original.Object), "recovery releases stale native hold");
            Check(!clientView.Admits(original.Object.Entity), "retired replica refuses a new native hide");
            Check(!clientView.Admits(body), "another process's returned body refuses");
            Check(!hostView.Admits(new FakeEntity(body.Id, "Foreign", Vec3.Zero)), "same-id foreign entity refuses");
            using (var foreignRenderer = new BodyRenderer(rig.Host, hostWorld))
                Check(!foreignRenderer.Admits(body), "foreign renderer cannot borrow active world admission");
            var saved = hostWorld.World;
            hostWorld.World = new WorldReadiness(saved.Scene, TransformState.Identity);
            Check(!hostView.Admits(body), "same-scene world replacement refuses old body");
            hostWorld.World = saved;
            using var participant = new FakeModLifetime();
            var participantSeam = rig.Host.Replication.Bind(WorldPeer.ModId,
                new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Participant), participant);
            Check(participantSeam.PlayerWorldView(hostView, "local").Hide(body).ErrorCode == ModErrorCode.NotAuthoritative,
                "participant hide authority remains refused");
            clientWorld.WorldLifetime.Dispose();
            Check(!clientView.Admits(guest.Native[1].Object.Entity), "ended client world refuses surviving registry root");
            owner.Lifetime.Dispose();
            Check(!body.IsAlive && !hostView.Admits(body), "scope retirement refuses returned body");
            rig.Frames(30);
            Check(clientView.Hidden.Count == 0, "scope retirement restores remote native holds");
            Check(!audience!.IsActive, "scope retirement revokes audience lease");
        }

        // World readiness and lifetime are inputs; admission, canonical lookup, replica recovery, leases and
        // transport projection execute production code. Engine roots deliberately fail the scene predicate.
        private sealed class BodyReadContext : IInternalNativeEntityReadContext, IInternalWorldViewContext, IDisposable
        {
            internal WorldReadiness World = new WorldReadiness(new WorldSceneIdentity(7, "World"), TransformState.Identity);
            internal FakeModLifetime WorldLifetime = new FakeModLifetime();
            private readonly IModLifetime lifetime;
            private readonly BodyReadContext root;
            internal BodyReadContext() { root = this; lifetime = WorldLifetime; }
            private BodyReadContext(BodyReadContext root, IModLifetime lifetime) { this.root = root; this.lifetime = lifetime; }
            internal BodyReadContext For(IModLifetime caller) => new BodyReadContext(root, caller);
            public bool TryGetViewWorld(out WorldReadiness? world, out IModLifetime? scope)
            { world = root.World; scope = root.WorldLifetime; return !lifetime.IsStopping && !scope.IsStopping; }
            public IInternalNativeEntityReadScope CaptureNativeEntityReadScope() => new ReadScope(this, root.World, root.WorldLifetime);
            public void Dispose() { if (ReferenceEquals(root, this)) WorldLifetime.Dispose(); }
            private sealed class ReadScope : IInternalNativeEntityReadScope
            {
                private readonly BodyReadContext context;
                private readonly WorldReadiness world;
                private readonly IModLifetime lifetime;
                internal ReadScope(BodyReadContext context, WorldReadiness world, IModLifetime lifetime)
                { this.context = context; this.world = world; this.lifetime = lifetime; }
                public int SceneInstanceId => world.Scene.InstanceId;
                public string SceneName => world.Scene.Name;
                public bool IsCurrent(IModLifetime caller) => ReferenceEquals(caller, context.lifetime) && !caller.IsStopping
                    && !lifetime.IsStopping && ReferenceEquals(context.root.World, world) && ReferenceEquals(context.root.WorldLifetime, lifetime);
                public bool IsCurrentWorld(IInternalWorldViewContext caller) => IsCurrent(context.lifetime)
                    && caller.TryGetViewWorld(out var current, out var scope) && ReferenceEquals(current, world) && ReferenceEquals(scope, lifetime);
            }
        }

        private sealed class BodyRenderer : IInternalWorldViewRenderer, IInternalWorldViewBinding, IInternalViewEntityContext
        {
            private readonly WorldPeer peer;
            private readonly BodyReadContext world;
            private readonly IInternalNativeEntityReadScope readScope;
            private IDisposable? tracking;
            internal readonly HashSet<FakeWorldObject> Hidden = new HashSet<FakeWorldObject>();
            internal BodyRenderer(WorldPeer peer, BodyReadContext world)
            {
                this.peer = peer; this.world = world; readScope = world.CaptureNativeEntityReadScope();
                context.UnwrapProvider = UnwrapViewProviderEntity; tracking = world.WorldLifetime.Track(this);
            }
            public IInternalWorldViewRenderer Renderer => this;
            public string WorldId => "world";
            public bool TryNameAuthored(IEntity entity, out string scene, out string id) { scene = id = ""; return false; }
            public IEntity? ResolveAuthored(string scene, string id) => null;
            public object? UnwrapViewEntity(IEntity entity) => null;
            public bool IsCurrentViewAvatar(IEntity entity, IInternalWorldViewRenderer renderer) => false;
            public object? UnwrapViewProviderEntity(IEntity entity, IInternalWorldViewRenderer renderer) =>
                NativeViewProviderTarget.Resolve(world, peer.Replication.Views.ResolveProviderEntity(entity, renderer));
            // NativeViewTarget requires a public context; the narrow adapter delegates only the internal bridge.
            private readonly WorldViewTests.ViewFixture context = new WorldViewTests.ViewFixture();
            internal bool Admits(IEntity entity) => Resolve(entity) != null;
            private FakeWorldObject? Resolve(IEntity entity) => NativeViewTarget.Resolve<FakeWorldObject>(context, this, entity, _ => false);
            private bool disposed;
            public bool IsAlive => !disposed && readScope.IsCurrentWorld(world);
            public bool EnvironmentAvailable => false;
            public bool FogAvailable => false;
            public IReadOnlyList<string> Presets => Array.Empty<string>();
            public event Action Stopped = delegate { };
            public bool HasPreset(string preset) => false;
            public OperationResult<bool> SetEnvironment(string? preset) => OperationResult<bool>.Success(true);
            public OperationResult<bool> SetFog(VisionFog? fog) => OperationResult<bool>.Success(true);
            public OperationResult<IDisposable> Hide(IEntity entity)
            {
                var target = Resolve(entity);
                if (target == null) return OperationResult<IDisposable>.Failure(ModErrorCode.InvalidArgument, "Foreign body target.");
                Hidden.Add(target);
                return OperationResult<IDisposable>.Success(new Hold(() => Hidden.Remove(target)));
            }
            public void Dispose()
            {
                if (disposed) return; disposed = true; Stopped(); context.Dispose();
                var hold = tracking; tracking = null; hold?.Dispose();
            }
            private sealed class Hold : IDisposable
            {
                private Action? end;
                internal Hold(Action end) { this.end = end; }
                public void Dispose() { var action = end; end = null; action?.Invoke(); }
            }
        }
    }
}
