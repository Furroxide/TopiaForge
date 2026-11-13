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
        private bool viewApplied;
        // A complete view was read on this attachment, whatever world was bound when it arrived.
        private bool viewReceived;
        private MultiplayerSessionId appliedSession;
        private bool avatarFailureLogged;
        private bool unreadableLogged;
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
        // Whether this viewer's screen holds the entity hidden: the host's own leases or the projected view.
        internal bool HoldsLocally(IEntity entity)
        {
            foreach (var held in projected.Values) if (ReferenceEquals(held.Entity, entity)) return true;
            foreach (var world in worlds.Values) if (world.HoldsEntity(entity)) return true;
            return false;
        }
        // A joined player has applied the host's first complete view of this session and world, or has no view stream
        // at all. The view a lost link left behind still counts: it holds until the next link's view replaces it.
        internal bool ReadyToDraw => !Client || channel == null
            || viewApplied && appliedSession.Equals(services.Runtime.Control?.SessionSnapshot.Id ?? default);
        // Presence made an avatar root and draws it next. Apply the views now so the hold is there for that draw.
        // False when the root should be held and is not: presence leaves it undrawn and asks again next frame.
        internal bool AvatarMade(IEntity root)
        {
            string? failure = null;
            try
            {
                if (Client) { ObserveRoster(); ApplyReceived(); }
                else foreach (var world in worlds.Values) world.RefreshLocal();
                if (!ShouldHold(root) || HoldsLocally(root)) { avatarFailureLogged = false; return true; }
                failure = "the hold was refused";
            }
            catch (Exception exception)
            {
                failure = exception.GetType().Name;
            }
            if (!avatarFailureLogged)
                services.Runtime.Log.Warn("Multiplayer: the world view could not hold a new avatar (" + failure
                    + "); the avatar is drawn once it is held.");
            avatarFailureLogged = true;
            return false;
        }
        // A view counts for a world when it is that world's, or when the host has no world view at all (nothing to hold).
        private static bool ViewsWorld(ViewSnapshot view, string world) => view.World.Length == 0 || view.World == world;
        // Whether a view this player applies names the root: a listed player on a client, a local lease on the host.
        private bool ShouldHold(IEntity root)
        {
            if (Client)
            {
                foreach (var target in received.Hidden)
                    if (target.Kind == 3 && ReferenceEquals(ResolveListed(target), root)) return true;
                return false;
            }
            foreach (var world in worlds.Values) if (world.Wants(root)) return true;
            return false;
        }
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
                entity => { if (!names.ContainsKey(entity) && TryName(entity, out var target)) names.Add(entity, Named(target, entity)); },
                entity => names.Remove(entity),
                // A player's name stops resolving for the whole reconnect grace. The root presence retains for them
                // keeps its hold, and RefreshNames renames the target in place when the new lifetime arrives.
                (entity, held) => names.TryGetValue(entity, out var known) && known.Name.Kind == 3
                    && ReferenceEquals(known.Root, held) && Retained(held, known.Player));
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
            // A new world waits for its own first complete view; one that already arrived for it still counts, even
            // if it arrived while the old world was still bound.
            viewApplied = viewReceived && ViewsWorld(received, value.WorldId);
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
            ClearProjection(); wire.Reset(); received = ViewSnapshot.Empty; invalidStream = false; viewApplied = false; viewReceived = false;
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
                // A new link restarts the wire only. Presence keeps drawing the last poses while a link is down,
                // so the last complete view and its holds stay until the new link's first complete view replaces
                // them: this hides too much for a moment, never too little.
                if (Client) { wire.Reset(); invalidStream = false; }
            }
            if (Client)
            {
                ObserveRoster();
                while (channel.TryReceive(out var message))
                {
                    var host = services.Runtime.Control?.SessionSnapshot.HostParticipantId ?? InternalSystemMessage.HeadlessHostSender;
                    if (invalidStream) continue;
                    // A stream that cannot be read is ignored until the next link, and the last view keeps holding.
                    if (!message.Sender.Equals(host) || !wire.Read(message.Payload, out var next))
                    {
                        invalidStream = true; wire.Reset();
                        if (!unreadableLogged)
                            services.Runtime.Log.Warn("Multiplayer: a world view message from the host could not be read; "
                                + "the last view stays until the next connection.");
                        unreadableLogged = true;
                        continue;
                    }
                    if (next != null)
                    {
                        received = next; viewReceived = true; unreadableLogged = false;
                        viewApplied = binding == null || ViewsWorld(next, binding.WorldId);
                        appliedSession = services.Runtime.Control?.SessionSnapshot.Id ?? default;
                        ApplyReceived();
                    }
                }
                retry += deltaTime;
                if (retry >= .25f) { retry = 0; ApplyReceived(); }
                return;
            }
            var snapshot = services.Runtime.Control?.SessionSnapshot;
            if (snapshot != null) RefreshRoster(snapshot);
            retry += deltaTime;
            if (retry >= .25f && names.Count != 0)
            { retry = 0; foreach (var world in worlds.Values) world.RefreshLocal(retryRefused: true); }
            SendViews();
        }
        private void SendViews()
        {
            var snapshot = services.Runtime.Control?.SessionSnapshot;
            if (snapshot == null || channel == null) return;
            RefreshRoster(snapshot);
            // A bound world whose renderer is ending sends nothing until it is replaced or unbound.
            if (binding != null && !binding.Renderer.IsAlive) return;
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
            var namesChanged = RefreshNames(snapshot);
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

        private bool RefreshNames(MultiplayerSessionSnapshot snapshot)
        {
            var changed = false;
            foreach (var target in names.Values)
            {
                if (target.Name.Kind != 3) continue;
                // A participant who left, or came back under another key, is a new player: the old target never
                // follows them, and the gamemode decides whether to hide the new admission.
                if (target.Player is ParticipantId id
                    && (!snapshot.TryGetParticipant(id, out var row) || row == null || !row.PlayerKey.Equals(target.Key)))
                    target.Player = null;
                if (ResolveKnown(target) != null) continue;
                // Follow the participant's current lifetime even when the old root is gone: the hold reaches the new
                // root when presence makes it (AvatarMade), before its first draw.
                ViewTarget current;
                if (target.Player is ParticipantId player && services.TryCreatePlayerRef(player, out var reference))
                    current = new ViewTarget(3, "", reference.Value!);
                else if (!TryPlayerName(target.Root, out current)) continue;
                if (target.Name.Equals(current)) continue;
                target.Name = current;
                changed = true;
            }
            return changed;
        }

        private ViewSnapshot Derive(string player)
        {
            // Only a host with no world view sends the empty view: clients take it as "nothing to hold".
            if (binding == null) return ViewSnapshot.Empty;
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
        private bool Retained(IEntity entity, ParticipantId? player = null) =>
            entity.IsAlive && services.Presence?.IsRetainedAvatar(entity, player) == true;
        private NamedTarget Named(ViewTarget name, IEntity entity)
        {
            var named = new NamedTarget(name, entity);
            if (name.Kind == 3 && services.TryGetPlayerParticipant(entity, out var participant, out var key))
            { named.Player = participant; named.Key = key; }
            return named;
        }
        private sealed class NamedTarget
        {
            internal ViewTarget Name;
            internal IEntity Root;
            // The participant and key a player name was taken for, while that admission lasts.
            internal ParticipantId? Player;
            internal MultiplayerPlayerKey Key;
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
