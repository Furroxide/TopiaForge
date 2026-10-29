using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class WorldViewTests
    {
        private static void PresenceTargets()
        {
            CanonicalPlayerTargets();
            var rig = new PresenceRig(); rig.HostWith(2); rig.Frames(20);
            var host = rig.Host; var viewer = rig.Players[0]; var subject = rig.Players[1];
            var renderers = new Dictionary<PresenceNode, AvatarRenderer>();
            foreach (var node in rig.All)
            {
                // Use the actual production reference source, which deliberately cannot name presence avatars.
                var world = new NetworkWorld(node.Runtime, node.Replication, new FakeWorldScene(), rig.Clock.Read, _ => null);
                node.Replication.References = world;
                var renderer = new AvatarRenderer(node); renderers.Add(node, renderer);
                node.Replication.Views.Activate(new AvatarBinding(node.World!, renderer));
            }
            using var scope = new FakeModLifetime();
            var seam = host.Replication.Bind("example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), scope);
            using var fake = new FakeModContext();
            var players = MultiplayerPlayers.Install(host.Runtime, host.Presence);
            using var backend = players.Bind("example.mode", true, fake.Logger, scope, fake.LocalPlayer, fake.Ui, new ModInputActivity());
            using var directory = new PlayerDirectory(() => host.Control.SessionSnapshot, () => 1, backend);
            Check(directory.TryGet(subject.Id, out var player), "production directory finds subject");
            var original = player!.Entity!;
            var rawWorld = (NetworkWorld)host.Replication.References!;
            Check(!rawWorld.TryCreateRef(original, out _), "old production naming source cannot name a player entity");
            Check(host.Replication.TryCreateRef(original, out var reference) && reference.Kind == InternalNetRefKind.Player,
                "production seam names the actual IPlayer.Entity");
            Check(!rawWorld.TryResolveRef(reference, out _), "old production source independently cannot resolve a player reference");
            Check(viewer.Replication.TryResolveRef(reference, out var remote) && ReferenceEquals(remote, viewer.AvatarOf(subject)!.Entity),
                "canonical player lifetime resolves to each process's own current avatar");
            using var audience = Lease(seam.PlayerWorldView(renderers[host], viewer.Id.Value).Hide(original));
            using var local = Lease(seam.PlayerWorldView(renderers[host], host.Id.Value).Hide(original));
            rig.Frames(40);
            Check(renderers[host].Hidden.Contains(original) && renderers[viewer].Hidden.Contains(remote!) && renderers[subject].Hidden.Count == 0,
                "host local and remote audiences hide only the current presence avatar");
            Check(renderers[host].OldSceneGuardWouldReject, "old native scene guard rejects the validated persistent avatar");
            using var participantScope = new FakeModLifetime();
            var participant = host.Replication.Bind("example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Participant), participantScope);
            Check(participant.PlayerWorldView(renderers[host], viewer.Id.Value).Hide(original).ErrorCode == ModErrorCode.NotAuthoritative,
                "participant authority still rejects avatar hide");
            rig.Network.LoseLink(viewer.Control); rig.Frames(24);
            Check(renderers[viewer].Hidden.Count == 0 && audience.IsActive, "lost audience link restores native holds while grace preserves authority lease");
            rig.Network.Restore(viewer.Control); rig.Frames(60);
            Check(renderers[viewer].Hidden.Contains(viewer.AvatarOf(subject)!.Entity), "reconnect reapplies the production avatar reference baseline");
            var hostOld = host.AvatarOf(subject)!; var viewerOld = viewer.AvatarOf(subject)!;
            hostOld.Dispose(); viewerOld.Dispose(); rig.Frames(40);
            var replacement = player.Entity!; var viewerReplacement = viewer.AvatarOf(subject)!.Entity;
            Check(!ReferenceEquals(original, replacement) && host.Replication.TryResolveRef(reference, out var resolved)
                && ReferenceEquals(resolved, replacement), "lifetime reference follows actual backend avatar replacement");
            Check(!renderers[host].Hidden.Contains(original) && renderers[host].Hidden.Contains(replacement)
                && !renderers[viewer].Hidden.Contains(remote!) && renderers[viewer].Hidden.Contains(viewerReplacement),
                "replacement disposes both old native holds and hides new avatars");
            Check(!host.Replication.TryCreateRef(original, out _) && !renderers[host].Admits(original), "stale avatar handles cannot be named or admitted");
            Check(seam.PlayerWorldView(renderers[host], viewer.Id.Value).Hide(original).ErrorCode == ModErrorCode.InvalidArgument,
                "new hide call cannot reuse a cached stale player entity");
            using (var foreignRenderer = new AvatarRenderer(host))
                Check(!foreignRenderer.Admits(replacement), "valid avatar cannot escape the active renderer binding");
            var foreign = new TopiaForge.Mods.Testing.FakeEntity(replacement.Id, "Foreign", Vec3.Zero);
            Check(!host.Replication.TryCreateRef(foreign, out _) && !renderers[host].Admits(foreign), "same-id foreign runtime object rejected");
            Check(!renderers[host].Admits(viewerReplacement), "another process's current avatar rejected");
            var currentWorld = host.World; host.World = "foreign-world";
            Check(!renderers[host].Admits(replacement), "current avatar outside active world rejected"); host.World = currentWorld;
            host.SceneVersion++;
            Check(!renderers[host].Admits(replacement), "avatar from previous native scene generation rejected"); host.SceneVersion--;
            var replacementObject = host.AvatarOf(subject)!;
            var saved = host.Control.SessionSnapshot;
            host.Control.SnapshotOverride = Snapshot(saved, saved.Id, subject.Id, new MultiplayerPlayerKey("replacement-key"));
            Check(!host.Replication.TryResolveRef(reference, out _) && !renderers[host].Admits(replacement), "replaced player key rejected before reconciliation");
            host.Frame();
            Check(!replacementObject.IsAlive && !host.Presence.TryGetAvatar(subject.Id, out _), "key replacement retires old presence before accepting new pose");
            Check(!player.IsPresent && player.Entity == null, "retained player handle is retired after key replacement");
            Check(audience.IsActive && local.IsActive, "subject key replacement does not revoke unrelated audience leases");
            Check(!host.Replication.TryResolveRef(reference, out _), "retired player reference cannot resolve after reconciliation");
            var sessionAvatar = host.AvatarOf(viewer)!;
            using var sessionHide = Lease(seam.PlayerWorldView(renderers[host], host.Id.Value).Hide(sessionAvatar.Entity));
            Check(renderers[host].Hidden.Contains(sessionAvatar.Entity), "session replacement starts with a live native avatar hold");
            // Replace the actual engine snapshot identity while retaining the same role and participants.
            using var active = Lease(seam.PlayerWorldView(renderers[host], viewer.Id.Value).Fog(VisionFog.Blackout));
            host.Control.SnapshotOverride = Snapshot(host.Control.SessionSnapshot, new MultiplayerSessionId("replacement-session"), default, default);
            Check(!host.Replication.TryResolveRef(reference, out _), "foreign network session rejects old lifetime reference immediately");
            host.Frame();
            Check(!active.IsActive && !sessionHide.IsActive && !sessionAvatar.IsAlive && renderers[host].Hidden.Count == 0, "actual same-role network session replacement releases leases and native holds");
            foreach (var node in rig.All) { renderers[node].Dispose(); node.Runtime.Disconnect(); }
        }
        private static void CanonicalPlayerTargets()
        {
            var rig = new PresenceRig();
            void Install(PresenceNode node)
            {
                node.Runtime.Disconnect();
                node.Replication.Players = MultiplayerPlayers.Install(node.Runtime, node.Presence);
                node.Runtime.Connect();
            }
            Install(rig.Host); rig.OpenToMultiplayer();
            var viewer = rig.AddPlayer("viewer"); Install(viewer); rig.Join(viewer);
            var subject = rig.AddPlayer("subject");
            subject.Control.PlayerKey = new MultiplayerPlayerKey("persistent-subject");
            Install(subject); rig.Join(subject); rig.Frames(24);
            var host = rig.Host;
            using var hostRenderer = new AvatarRenderer(host);
            using var viewerRenderer = new AvatarRenderer(viewer);
            host.Replication.Views.Activate(new AvatarBinding(host.World!, hostRenderer));
            viewer.Replication.Views.Activate(new AvatarBinding(viewer.World!, viewerRenderer));
            var original = host.AvatarOf(subject)!.Entity;
            Check(host.Replication.Players!.TryCreateReference(original, out var canonical)
                && host.Replication.TryCreateRef(original, out var routed) && routed.Equals(canonical),
                "composed view naming retains the directory connection lifetime");
            Check(host.Presence.TryCreatePlayerRef(original, out var legacy)
                && !viewer.Replication.TryResolveRef(legacy, out _),
                "installed directory refuses the presence persistence-key reference");
            Check(viewer.Replication.TryResolveRef(canonical, out var remote)
                && ReferenceEquals(remote, viewer.AvatarOf(subject)!.Entity), "canonical reference resolves the remote view avatar");
            using var scope = new FakeModLifetime();
            var seam = host.Replication.Bind("example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), scope);
            using var hide = Lease(seam.PlayerWorldView(hostRenderer, viewer.Id.Value).Hide(original));
            rig.Frames(40);
            Check(viewerRenderer.Hidden.Contains(remote!) && hostRenderer.Hidden.Count == 0,
                "canonical player reference hides only the selected remote audience");
            host.AvatarOf(subject)!.Dispose(); viewer.AvatarOf(subject)!.Dispose(); rig.Frames(40);
            Check(host.Replication.TryResolveRef(canonical, out var replacement)
                && ReferenceEquals(replacement, host.AvatarOf(subject)!.Entity) && !ReferenceEquals(replacement, original)
                && viewerRenderer.Hidden.Contains(viewer.AvatarOf(subject)!.Entity),
                "canonical lifetime follows avatar replacement through native view admission");
            var saved = host.Control.SessionSnapshot;
            host.Control.SnapshotOverride = new MultiplayerSessionSnapshot(saved.Id, saved.State, saved.ProcessKind,
                saved.ExecutionSides, saved.LocalParticipantId, saved.Participants.Select(p => p.Id.Equals(subject.Id)
                    ? new MultiplayerParticipant(p.Id, p.DisplayName, p.IsLocal, p.IsConnected, p.IsHost, p.Quality,
                        p.PingMilliseconds, p.PlayerKey, Guid.Empty) : p).ToArray(),
                saved.Tick, saved.Seed, saved.HostParticipantId, saved.EndReason);
            Check(host.Presence.TryCreatePlayerRef(replacement!, out _)
                && !host.Replication.TryCreateRef(replacement!, out _),
                "missing connection lifetime cannot fall back to presence naming");
            host.Control.SnapshotOverride = null;
            rig.Network.Leave(subject.Control); rig.Frames(3); rig.Join(subject); rig.Frames(40);
            Check(!host.Replication.TryResolveRef(canonical, out _) && !viewer.Replication.TryResolveRef(canonical, out _)
                && viewerRenderer.Hidden.Count == 0, "same-key rejoin cannot revive a retired hide target");
            Check(host.Replication.TryCreateRef(host.AvatarOf(subject)!.Entity, out var fresh) && !fresh.Equals(canonical)
                && viewer.Replication.TryResolveRef(fresh, out _), "rejoined subject has a fresh canonical view target");
            foreach (var node in rig.All) node.Runtime.Disconnect();
        }
        private static void BodyRetryVisibility()
        {
            ShortSubjectReconnect();
            var rig = new PresenceRig(); rig.HostWith(1); rig.Frames(20);
            var client = rig.Players[0]; var subject = rig.Host;
            var factory = new FakeBodyFactory { Available = false };
            using var providerScope = new FakeModLifetime();
            Check(client.Replication.Bind("example.body", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), providerScope)
                .RegisterAvatarBodyFactory(factory).Succeeded, "body provider registered on client process");
            rig.Frames(2);
            var avatar = client.AvatarOf(subject)!; var entity = avatar.Entity;
            using var renderer = new AvatarRenderer(client);
            client.Replication.Views.Activate(new AvatarBinding(client.World!, renderer));
            using var ownership = new OwnedRendererVisibility<Visible>(v => v.Alive, v => v.Enabled, (v, enabled) => v.Enabled = enabled);
            var capsule = new Visible { Enabled = true };
            var parts = new List<Visible> { capsule };
            var hold = ownership.Hide(() => parts, () => renderer.Admits(entity));
            Check(!capsule.Enabled && !avatar.IsWearingBody, "capsule hidden while provider unavailable");
            factory.Available = true;
            Check(rig.RunUntil(() => avatar.IsWearingBody, 400) && ReferenceEquals(entity, client.AvatarOf(subject)!.Entity), "production body retry attaches under same entity root");
            var body = new Visible { Enabled = true }; var disabled = new Visible { Enabled = false };
            parts.Add(body); parts.Add(disabled); ownership.Refresh();
            Check(!body.Enabled && !disabled.Enabled && !capsule.Enabled, "new enabled body renderers stay hidden under active capsule hide");
            var overlap = ownership.Hide(() => parts, () => renderer.Admits(entity));
            ((TopiaForge.ModManager.TestDoubles.FakeEntity)factory.Made[0].Root).IsAlive = false;
            rig.Frames(2);
            Check(factory.Made.Count == 2 && factory.Made[0].Disposed && avatar.IsWearingBody
                && ReferenceEquals(entity, client.AvatarOf(subject)!.Entity), "destroyed cosmetic body is replaced under same current avatar");
            parts.Remove(body); ownership.Refresh();
            Check(body.Enabled, "detached body subtree restores original state");
            var replacement = new Visible { Enabled = true }; parts.Add(replacement); ownership.Refresh();
            hold.Dispose(); Check(!replacement.Enabled, "overlapping hide retains replacement body");
            overlap.Dispose(); Check(replacement.Enabled && capsule.Enabled && !disabled.Enabled, "last hold restores original states exactly");
            hold = ownership.Hide(() => parts, () => renderer.Admits(entity));
            client.SceneVersion++; ownership.Refresh();
            Check(capsule.Enabled && replacement.Enabled, "stale scene refuses refresh and restores prior holds");
            var foreign = new Visible { Enabled = true }; parts.Add(foreign); ownership.Refresh();
            Check(foreign.Enabled, "retired hide never adopts later foreign renderer"); hold.Dispose();
            rig.Frames(2); entity = client.AvatarOf(subject)!.Entity;
            hold = ownership.Hide(() => parts, () => renderer.Admits(entity));
            renderer.Dispose(); ownership.Refresh();
            Check(foreign.Enabled && capsule.Enabled, "renderer lifetime restores dynamically acquired body holds"); hold.Dispose();
            var selected = false;
            using var refused = ownership.Hide(() => { selected = true; return parts; }, () => false);
            ownership.Refresh();
            Check(!selected && foreign.Enabled, "foreign target refused before selecting any renderer");
            using var lifetimeHold = ownership.Hide(() => parts, () => true);
            ownership.Dispose();
            Check(foreign.Enabled && capsule.Enabled && !disabled.Enabled, "provider ownership disposal restores all dynamically acquired states");
            foreach (var node in rig.All) node.Runtime.Disconnect();
        }
        private static void ShortSubjectReconnect()
        {
            var rig = new PresenceRig(); rig.HostWith(1); rig.Frames(20);
            var host = rig.Host; var subject = rig.Players[0];
            using var renderer = new AvatarRenderer(host);
            host.Replication.Views.Activate(new AvatarBinding(host.World!, renderer));
            var entity = host.AvatarOf(subject)!.Entity;
            var visible = new Visible { Enabled = true }; var disabled = new Visible { Enabled = false };
            renderer.NativeParts.Add(entity, new List<Visible> { visible, disabled });
            using var scope = new FakeModLifetime();
            var seam = host.Replication.Bind("example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), scope);
            var controls = seam.PlayerWorldView(renderer, host.Id.Value);
            var first = Lease(controls.Hide(entity)); var duplicate = Lease(controls.Hide(entity));
            Check(renderer.NativeHideCalls == 1 && !visible.Enabled, "outer overlapping leases share one production dynamic hold");
            var overlap = renderer.Hide(entity).Value!;
            host.Replication.Views.Run(.25f); // Start immediately after the owner's quarter-second refresh.
            var start = rig.Clock.Read(); var snapshot = host.Control.SessionSnapshot;
            var key = host.PeerOf(subject)!.PlayerKey; var world = host.World; var scene = host.SceneVersion;
            void Link(bool connected)
            {
                if (connected) rig.Network.Restore(subject.Control); else rig.Network.LoseLink(subject.Control);
                host.Frame(.016f); rig.Clock.Advance(16_000);
                renderer.RefreshNative(); // The native LateUpdate path runs every frame, independently of the owner.
            }
            Link(false);
            Check(visible.Enabled && !disabled.Enabled && first.IsActive && duplicate.IsActive,
                "failed live admission restores renderers while outer leases survive grace");
            Check(ReferenceEquals(entity, host.AvatarOf(subject)!.Entity), "disconnect retains exact avatar root");
            Link(true);
            Check(rig.Clock.Read() - start == 32_000 && host.Control.SessionSnapshot.Id.Equals(snapshot.Id)
                && host.PeerOf(subject)!.PlayerKey.Equals(key) && host.World == world && host.SceneVersion == scene
                && ReferenceEquals(entity, host.AvatarOf(subject)!.Entity), "reconnect within 32ms preserves session key world scene and root");
            Check(!visible.Enabled && !disabled.Enabled && renderer.NativeHideCalls == 2,
                "production dynamic hold resumes after short reconnect without outer reacquisition");
            host.Replication.Views.Run(.25f); renderer.RefreshNative();
            Check(!visible.Enabled && renderer.NativeHideCalls == 2, "identical-entity owner refresh preserves resumed native holds");
            Link(false); overlap.Dispose(); overlap.Dispose(); Link(true);
            Check(!visible.Enabled && renderer.NativeHideCalls == 2, "disposing one suspended native overlap leaves the surviving hold refreshable");
            first.Dispose(); renderer.RefreshNative();
            Check(!visible.Enabled && duplicate.IsActive, "outer duplicate retains the resumed hide after first release");
            duplicate.Dispose(); renderer.RefreshNative();
            Check(visible.Enabled && !disabled.Enabled, "last outer release restores original states and balanced native refcounts");
            var selections = renderer.NativeSelections; renderer.RefreshNative();
            Check(renderer.NativeSelections == selections && visible.Enabled, "disposed suspended and active holds cannot reacquire");
            using var lifetime = Lease(controls.Hide(entity));
            Link(false); scope.Dispose(); Link(true);
            Check(!lifetime.IsActive && visible.Enabled && !disabled.Enabled, "scope disposal during suspension prevents reconnect resurrection");
            var foreign = new TopiaForge.Mods.Testing.FakeEntity(entity.Id, "Foreign", Vec3.Zero);
            Check(!renderer.Hide(foreign).Succeeded, "foreign same-id root cannot acquire dynamic renderer holds");
            using var stale = renderer.Hide(entity).Value!;
            host.SceneVersion++; renderer.RefreshNative();
            Check(visible.Enabled && !disabled.Enabled, "scene invalidation suspends writes and restores owned states");
            host.Frame(.016f); selections = renderer.NativeSelections; renderer.RefreshNative();
            Check(!renderer.Admits(entity) && renderer.NativeSelections == selections && visible.Enabled, "retired stale root is never selected again");
            host.Control.SnapshotOverride = Snapshot(host.Control.SessionSnapshot, new MultiplayerSessionId("replacement-session"), default, default);
            renderer.RefreshNative(); host.Frame(.016f); renderer.RefreshNative();
            Check(renderer.NativeSelections == selections && visible.Enabled, "session replacement cannot resume stale dynamic root");
            foreach (var node in rig.All) node.Runtime.Disconnect();
        }
        private static void ClientTransition()
        {
            var rig = new PresenceRig(); rig.HostWith(1); rig.Frames(30);
            var client = rig.Players[0]; var host = rig.Host;
            using var first = new AvatarRenderer(client);
            client.Replication.Views.Activate(new AvatarBinding(client.World!, first));
            var old = client.AvatarOf(host)!;
            Check(first.Admits(old.Entity), "world A admits current client avatar");
            Check(client.Replication.TryCreateRef(old.Entity, out var reference), "client names current lifetime");
            var session = client.Control.SessionSnapshot.Id;
            client.World = "world-two"; client.SceneVersion++;
            Check(!first.Admits(old.Entity), "transition immediately refuses creation-world avatar");
            using var second = new AvatarRenderer(client);
            client.Replication.Views.Activate(new AvatarBinding(client.World, second));
            rig.Frames(10);
            var current = client.AvatarOf(host)!;
            Check(!old.IsAlive && !ReferenceEquals(current, old), "client transition retires persistent avatar");
            Check(client.SceneVersion == 1 && client.Control.SessionSnapshot.Id.Equals(session), "transition keeps new generation and same network session");
            Check(client.Replication.TryResolveRef(reference, out var resolved) && ReferenceEquals(resolved, current.Entity), "same player lifetime resolves new world avatar");
            Check(second.Admits(current.Entity) && !first.Admits(current.Entity) && !second.Admits(old.Entity), "only active world B renderer admits replacement");
            using (var hold = second.Hide(current.Entity).Value!)
                Check(second.Hidden.Contains(current.Entity), "successful hide after A to B completion");
            Check(second.Hidden.Count == 0, "B hide restores on release");
            client.SceneVersion++; rig.Frames(2);
            Check(!current.IsAlive && second.Admits(client.AvatarOf(host)!.Entity), "same-world scene replacement recreates current avatar without rollback");
            foreach (var node in rig.All) node.Runtime.Disconnect();
        }
        private static MultiplayerSessionSnapshot Snapshot(MultiplayerSessionSnapshot source, MultiplayerSessionId id,
            ParticipantId changed, MultiplayerPlayerKey key) => new MultiplayerSessionSnapshot(id, source.State, source.ProcessKind,
                source.ExecutionSides, source.LocalParticipantId, source.Participants.Select(p => p.Id.Equals(changed)
                    ? new MultiplayerParticipant(p.Id, p.DisplayName, p.IsLocal, p.IsConnected, p.IsHost, p.Quality, p.PingMilliseconds, key) : p).ToArray(),
                source.Tick, source.Seed, source.HostParticipantId, source.EndReason);
        private sealed class AvatarBinding : IInternalWorldViewBinding
        {
            internal AvatarBinding(string world, IInternalWorldViewRenderer renderer) { WorldId = world; Renderer = renderer; }
            public string WorldId { get; }
            public IInternalWorldViewRenderer Renderer { get; }
            public bool TryNameAuthored(IEntity entity, out string scene, out string id) { scene = id = ""; return false; }
            public IEntity? ResolveAuthored(string scene, string id) => null;
        }
        private sealed class AvatarRenderer : IInternalWorldViewRenderer
        {
            private readonly ViewFixture context = new ViewFixture();
            internal readonly HashSet<IEntity> Hidden = new HashSet<IEntity>();
            internal bool OldSceneGuardWouldReject;
            internal readonly Dictionary<IEntity, List<Visible>> NativeParts = new Dictionary<IEntity, List<Visible>>();
            private readonly OwnedRendererVisibility<Visible> native = new OwnedRendererVisibility<Visible>(v => v.Alive, v => v.Enabled, (v, enabled) => v.Enabled = enabled);
            internal int NativeHideCalls, NativeSelections;
            internal void RefreshNative() => native.Refresh();
            internal AvatarRenderer(PresenceNode node)
            {
                context.Unwrap = entity => entity; // All engine objects here occupy the persistent scene, outside the map scene.
                context.CurrentAvatar = (entity, renderer) => node.Replication.Views.IsCurrentAvatar(entity, renderer);
            }
            internal bool Admits(IEntity entity) => NativeViewTarget.Resolve<IEntity>(context, this, entity, _ => false) != null;
            public bool IsAlive { get; private set; } = true;
            public bool EnvironmentAvailable => false;
            public bool FogAvailable => true;
            public IReadOnlyList<string> Presets => Array.Empty<string>();
            public event Action Stopped = delegate { };
            public bool HasPreset(string preset) => false;
            public OperationResult<bool> SetEnvironment(string? preset) => OperationResult<bool>.Success(true);
            public OperationResult<bool> SetFog(VisionFog? fog) => OperationResult<bool>.Success(true);
            public OperationResult<IDisposable> Hide(IEntity entity)
            {
                OldSceneGuardWouldReject = true;
                if (!Admits(entity)) return OperationResult<IDisposable>.Failure(ModErrorCode.InvalidArgument, "Foreign view target.");
                if (NativeParts.TryGetValue(entity, out var parts))
                {
                    NativeHideCalls++;
                    var hold = native.Hide(() => { NativeSelections++; return parts; }, () => Admits(entity));
                    return OperationResult<IDisposable>.Success(hold);
                }
                Hidden.Add(entity);
                return OperationResult<IDisposable>.Success(new AvatarHold(() => Hidden.Remove(entity)));
            }
            public void Dispose() { if (!IsAlive) return; Stopped(); IsAlive = false; native.Dispose(); context.Dispose(); }
        }
        private sealed class AvatarHold : IDisposable
        {
            private Action? end;
            internal AvatarHold(Action end) { this.end = end; }
            public void Dispose() { var action = end; end = null; action?.Invoke(); }
        }
    }
}
