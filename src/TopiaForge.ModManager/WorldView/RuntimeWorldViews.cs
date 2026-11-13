using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class RuntimeWorldViews : IMultiplayerRuntimeObserver, IMultiplayerFrameStage
    {
        private readonly MultiplayerReplication services;
        private readonly Dictionary<IInternalWorldViewRenderer, WorldViewLeases> worlds = new Dictionary<IInternalWorldViewRenderer, WorldViewLeases>();
        private readonly Dictionary<ParticipantId, Delivery> deliveries = new Dictionary<ParticipantId, Delivery>();
        private readonly Dictionary<ParticipantId, MultiplayerPlayerKey> admitted = new Dictionary<ParticipantId, MultiplayerPlayerKey>();
        private MultiplayerSessionId session;
        private IInternalWorldViewBinding? binding;
        private readonly Dictionary<IEntity, NamedTarget> names = new Dictionary<IEntity, NamedTarget>();
        private MultiplayerSessionSnapshot? seenRoster;
        private IInternalSystemChannel? channel;
        private ulong generation;
        private readonly WorldViewWire wire = new WorldViewWire();
        private ViewSnapshot received = ViewSnapshot.Empty;
        private bool invalidStream;
        private bool resetting;
        private float retry;
        internal RuntimeWorldViews(MultiplayerReplication services) { this.services = services; }
        internal bool IsCurrentAvatar(IEntity entity, IInternalWorldViewRenderer renderer) =>
            binding != null && ReferenceEquals(binding.Renderer, renderer) && renderer.IsAlive
            && services.Presence?.IsCurrentViewAvatar(entity, binding.WorldId) == true;
        internal IEntity? ResolveProviderEntity(IEntity entity, IInternalWorldViewRenderer renderer)
        {
            if (binding == null || !ReferenceEquals(binding.Renderer, renderer) || !renderer.IsAlive || !entity.IsAlive) return null;
            // Owned handles still carry exact runtime world authority offline. Registry handles on clients must
            // resolve through the current factory instance, never through a native-object marker alone.
            if (entity is IInternalOwnedNativeEntity) return entity;
            return (services.References as NetworkWorld)?.ResolveViewProviderEntity(entity);
        }
        private bool Client => services.Runtime.Role == InternalSessionRole.Client;
        private string? Local => services.Runtime.Control?.SessionSnapshot.LocalParticipantId?.Value ?? "local";
        internal WorldViewLeases For(IInternalWorldViewRenderer renderer)
        {
            if (worlds.TryGetValue(renderer, out var value)) return value;
            return CreateWorld(renderer);
        }
        private WorldViewLeases CreateWorld(IInternalWorldViewRenderer renderer)
        {
            var value = new WorldViewLeases(renderer, () => Local, (entity, player) =>
                (!names.TryGetValue(entity, out var known) || known.Name.Kind != 3 || ReferenceEquals(ResolveKnown(known), entity))
                && (services.Runtime.Realm == NetRealm.Offline || player == Local || TryName(entity, out _)),
                entity => names.TryGetValue(entity, out var target) ? ResolveKnown(target) : entity.IsAlive ? entity : null,
                entity => { if (!names.ContainsKey(entity) && TryName(entity, out var target)) names.Add(entity, new NamedTarget(target, entity)); },
                entity => names.Remove(entity),
                // A player's name stops resolving for the whole reconnect grace. The root presence retains for them
                // keeps its hold, and RefreshNames renames the target in place when the new lifetime arrives.
                (entity, held) => names.TryGetValue(entity, out var known) && known.Name.Kind == 3
                    && ReferenceEquals(known.Root, held) && Retained(held));
            worlds.Add(renderer, value);
            void End()
            {
                value.Ended -= End; worlds.Remove(renderer);
                if (ReferenceEquals(binding?.Renderer, renderer) && !resetting) { ClearProjection(); binding = null; names.Clear(); }
            }
            value.Ended += End;
            return value;
        }
        internal void Activate(IInternalWorldViewBinding value)
        {
            if (ReferenceEquals(binding, value)) return;
            ClearProjection();
            foreach (var world in worlds.Values.ToArray()) world.Dispose();
            worlds.Clear(); names.Clear(); binding = value;
            For(value.Renderer);
            if (Client) ApplyReceived();
            deliveries.Clear();
        }
        public void Attached(IInternalMultiplayerControl control)
        {
            var opened = control.OpenSystemChannel(InternalSystemChannelKind.PerPlayer);
            if (opened.TryGetValue(out var result)) channel = result;
        }
        public void Detaching(IInternalMultiplayerControl control)
        { channel?.Dispose(); channel = null; Reset(true); }
        public void RealmChanged(NetRealm previous, NetRealm next)
        {
            // Opening a solo world to guests keeps its shared leases and native values.
            if (previous == NetRealm.Offline && next == NetRealm.ListenHost) { deliveries.Clear(); return; }
            if (previous != next) Reset(true);
        }
        private void Reset(bool endLeases)
        {
            ClearProjection(); wire.Reset(); received = ViewSnapshot.Empty; invalidStream = false;
            deliveries.Clear(); admitted.Clear(); names.Clear(); seenRoster = null; generation = 0; session = default;
            resetting = true;
            try { if (endLeases) foreach (var world in worlds.Values.ToArray()) world.Dispose(); }
            finally { resetting = false; if (endLeases) worlds.Clear(); }
        }
        public void Run(float deltaTime)
        {
            if (channel == null || !channel.IsOpen) return;
            if (generation != channel.Generation)
            {
                generation = channel.Generation;
                if (Client) { ClearProjection(); wire.Reset(); received = ViewSnapshot.Empty; invalidStream = false; }
            }
            if (Client)
            {
                while (channel.TryReceive(out var message))
                {
                    var host = services.Runtime.Control?.SessionSnapshot.HostParticipantId ?? InternalSystemMessage.HeadlessHostSender;
                    if (invalidStream) continue;
                    if (!message.Sender.Equals(host) || !wire.Read(message.Payload, out var next))
                    { invalidStream = true; wire.Reset(); received = ViewSnapshot.Empty; ClearProjection(); continue; }
                    if (next != null) { received = next; ApplyReceived(); }
                }
                retry += deltaTime;
                if (retry >= .25f) { retry = 0; ApplyReceived(); }
                return;
            }
            var snapshot = services.Runtime.Control?.SessionSnapshot;
            if (snapshot != null) RefreshRoster(snapshot);
            retry += deltaTime;
            if (retry >= .25f && names.Count != 0)
            { retry = 0; foreach (var world in worlds.Values) world.RefreshLocal(); }
            SendViews();
        }
        private void SendViews()
        {
            var snapshot = services.Runtime.Control?.SessionSnapshot;
            if (snapshot == null || channel == null) return;
            RefreshRoster(snapshot);
            for (var index = 0; index < snapshot.Participants.Count; index++)
            {
                var player = snapshot.Participants[index];
                if (player.IsLocal) continue;
                var epoch = channel.GetGeneration(player.Id);
                if (epoch == 0) { deliveries.Remove(player.Id); continue; }
                if (!deliveries.TryGetValue(player.Id, out var delivery) || delivery.Generation != epoch)
                    deliveries[player.Id] = delivery = new Delivery(epoch);
                var version = binding != null ? For(binding.Renderer).Version : -1;
                if (delivery.Pending == null && delivery.Version != version)
                {
                    var view = Derive(player.Id.Value);
                    delivery.Version = version;
                    if (delivery.Last == null || !view.Same(delivery.Last)) { delivery.Pending = view; delivery.Step = 0; }
                }
                // Fair, bounded progress; the inherited channel grants the actual byte/message credits.
                for (var budget = 0; budget < 4 && delivery.Pending != null; budget++)
                {
                    var bytes = delivery.Bytes ??= WorldViewWire.Write(delivery.Pending, delivery.Step);
                    var target = InternalSystemTarget.Only(player.Id);
                    if (!channel.CanSend(target, bytes.Length)) break;
                    var sent = channel.Send(new ArraySegment<byte>(bytes), target);
                    if (!sent.TryGetValue(out var count) || count != 1) break;
                    delivery.Bytes = null;
                    if (++delivery.Step == WorldViewWire.Steps(delivery.Pending))
                    { delivery.Last = delivery.Pending; delivery.Pending = null; }
                }
            }
        }
        private void RefreshRoster(MultiplayerSessionSnapshot snapshot)
        {
            if (session.IsValid && !session.Equals(snapshot.Id))
            {
                Reset(true);
            }
            session = snapshot.Id;
            if (ReferenceEquals(seenRoster, snapshot)) return;
            seenRoster = snapshot;
            // Removing another player's leases can apply the local view immediately.
            var namesChanged = RefreshNames();
            ReconcileParticipants(snapshot);
            // Quality-only or equivalent roster publications must not restart a backpressured baseline.
            if (namesChanged)
                foreach (var delivery in deliveries.Values)
                {
                    // A new begin supersedes partial wire snapshots; never reuse old fragments with new names.
                    delivery.Pending = null;
                    delivery.Bytes = null;
                    delivery.Step = 0;
                    delivery.Version = long.MinValue;
                }
        }

        private void ReconcileParticipants(MultiplayerSessionSnapshot snapshot)
        {
            var participants = snapshot.Participants;
            if (SameAdmission(participants)) return;
            foreach (var player in admitted.ToArray())
                if (!snapshot.TryGetParticipant(player.Key, out var current) || !current!.PlayerKey.Equals(player.Value))
                {
                    foreach (var world in worlds.Values) world.RemovePlayer(player.Key.Value);
                    admitted.Remove(player.Key);
                }
            foreach (var id in deliveries.Keys.ToArray())
                if (!snapshot.TryGetParticipant(id, out _)) deliveries.Remove(id);
            for (var index = 0; index < participants.Count; index++)
            {
                var player = participants[index];
                admitted[player.Id] = player.PlayerKey;
            }
        }

        private bool SameAdmission(IReadOnlyList<MultiplayerParticipant> participants)
        {
            if (admitted.Count != participants.Count) return false;
            for (var index = 0; index < participants.Count; index++)
            {
                var player = participants[index];
                if (!admitted.TryGetValue(player.Id, out var key) || !key.Equals(player.PlayerKey)) return false;
            }
            return true;
        }

        private bool RefreshNames()
        {
            var changed = false;
            foreach (var target in names.Values)
                if (target.Name.Kind == 3 && ResolveKnown(target) == null
                    && TryPlayerName(target.Root, out var current) && !target.Name.Equals(current))
                {
                    target.Name = current;
                    changed = true;
                }
            return changed;
        }

        private ViewSnapshot Derive(string player)
        {
            if (binding == null || !binding.Renderer.IsAlive) return ViewSnapshot.Empty;
            var leases = For(binding.Renderer);
            var targets = new List<ViewTarget>();
            foreach (var entity in leases.HiddenFor(player)) if (TryName(entity, out var target)) targets.Add(target);
            return new ViewSnapshot(binding.WorldId, leases.EnvironmentFor(player), leases.FogFor(player),
                targets.Distinct().OrderBy(t => t.Kind).ThenBy(t => t.Scene, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal).ToArray());
        }
        private bool TryName(IEntity entity, out ViewTarget target)
        {
            if (names.TryGetValue(entity, out var known)) { target = known.Name; return true; }
            target = default;
            if (binding?.TryNameAuthored(entity, out var scene, out var id) == true)
            { target = new ViewTarget(1, scene, id); return true; }
            if (!services.TryCreateRef(entity, out var reference)) return false;
            if (reference.Kind == InternalNetRefKind.Player) return TryPlayerName(entity, out target);
            target = new ViewTarget(2, "", reference.Value!); return true;
        }
        private bool TryPlayerName(IEntity entity, out ViewTarget target)
        {
            target = default;
            if (!services.TryCreateRef(entity, out var reference) || reference.Kind != InternalNetRefKind.Player
                || !services.TryResolveRef(reference, out var current) || !ReferenceEquals(current, entity)) return false;
            target = new ViewTarget(3, "", reference.Value!);
            return true;
        }
        private IEntity? Resolve(ViewTarget target)
        {
            if (target.Kind == 1) return binding?.ResolveAuthored(target.Scene, target.Id);
            return services.TryResolveRef(new InternalNetRef(target.Kind == 3 ? InternalNetRefKind.Player : InternalNetRefKind.Entity, target.Id), out var entity)
                && entity?.IsAlive == true ? entity : null;
        }
        private IEntity? ResolveKnown(NamedTarget target)
        {
            var root = Resolve(target.Name);
            // Only a successful canonical resolution can advance the root retained for a later lifetime refresh.
            if (target.Name.Kind == 3 && root != null) target.Root = root;
            return root;
        }
        private bool Retained(IEntity entity) => entity.IsAlive && services.Presence?.IsRetainedAvatar(entity) == true;
        private sealed class NamedTarget
        {
            internal ViewTarget Name;
            internal IEntity Root;
            internal NamedTarget(ViewTarget name, IEntity root) { Name = name; Root = root; }
        }
        private sealed class Delivery
        {
            internal readonly ulong Generation;
            internal long Version = long.MinValue;
            internal ViewSnapshot? Last;
            internal ViewSnapshot? Pending;
            internal int Step;
            internal byte[]? Bytes;
            internal Delivery(ulong generation) { Generation = generation; }
        }
    }
}
