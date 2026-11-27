using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>Runtime ownership of physics bodies. All state and I/O stay on the game thread.</summary>
    internal sealed partial class NetworkWorldMotion : IMultiplayerMotion, IMultiplayerFrameStage, IWorldMessageHandler
    {
        private readonly NetworkWorld world;
        private readonly MultiplayerRuntime runtime;
        private readonly Func<long> clock;
        private readonly INetworkMotionBackend? backend;
        private readonly HeldItemOwnershipTracker heldItems;
        private readonly Dictionary<uint, HostedMotion> hosted = new Dictionary<uint, HostedMotion>();
        private readonly Dictionary<uint, uint> epochs = new Dictionary<uint, uint>();
        private readonly Dictionary<int, TokenBucket> incoming = new Dictionary<int, TokenBucket>();
        private readonly List<NetEntityMotion> local = new List<NetEntityMotion>();
        private readonly List<uint> ended = new List<uint>();
        private readonly WorldWireWriter writer = new WorldWireWriter(128);
        private readonly MotionMessage read = new MotionMessage();
        private readonly MotionMessage write = new MotionMessage();
        private TokenBucket outgoing = new TokenBucket(MotionRules.BurstBytes, MotionRules.BytesPerSecond, 0);
        private ulong generation;
        private uint nextRequest;
        private long nextToast;
        private int broadcastStart;

        internal NetworkWorldMotion(NetworkWorld world, MultiplayerRuntime runtime, Func<long> clock)
        {
            this.world = world;
            this.runtime = runtime;
            this.clock = clock;
            backend = world.Backend as INetworkMotionBackend;
            heldItems = new HeldItemOwnershipTracker(this, backend);
            world.Client.IsSimulating = IsSimulating;
            foreach (var type in new[] { MotionMessages.Grab, MotionMessages.Sample, MotionMessages.Release,
                MotionMessages.Handback, MotionMessages.Verdict, MotionMessages.State }) world.Channel.Register(type, this);
        }

        internal long Now => clock();
        internal MultiplayerIntents Intents => runtime.Intents;
        internal int LocalCount => local.Count;
        internal int TrackedCount => hosted.Count;
        internal long SamplesAccepted { get; private set; }
        internal long SamplesRefused { get; private set; }
        internal long BytesSent { get; private set; }
        internal long GrabsGranted { get; private set; }
        internal long GrabsRefused { get; private set; }

        /// <summary>Gets the host's ownership of one body, if it tracks one, for tests and diagnostics.</summary>
        internal bool TryGetHosted(NetworkEntityId id, out HostedMotion state) => hosted.TryGetValue(id.Value, out state!);

        /// <summary>Presence supplies the host's validated position when available. No network address crosses this seam.</summary>
        internal Func<ParticipantId, NetVector3?>? ParticipantPosition { get; set; }

        private bool TryParticipantPosition(ParticipantId participant, MotionMessage message, out NetVector3 position)
        {
            // Until presence installs its resolver, range is relative to the sender's reported local avatar.
            // Once installed, a missing validated avatar fails closed rather than trusting the packet again.
            var resolved = ParticipantPosition == null ? message.PlayerPosition : ParticipantPosition(participant);
            position = resolved.GetValueOrDefault();
            return resolved.HasValue && MotionRules.IsValid(position);
        }

        public OperationResult<IEntityMotion>? Acquire(RuntimeReplication context, IEntity entity, IEntityService inner)
        {
            if (!world.IsOnline || context.Profile.NetworkClass == ModNetworkClass.ClientLocal) return null;
            UnityMainThreadGuard.AssertCurrent();
            if (context.IsStopping || context.RouteCurrentIntent() == InternalRoute.NotRun)
                return Failure("This part of the mod is stopping or does not run here.");
            if (!TryTarget(entity, out var target, out var body))
                return world.Client.IsLocal(entity) ? Failure(RuntimeReplication.HostNotReady) : null;
            if (world.Client.IsActive && context.RouteCurrentIntent() == InternalRoute.Shadow)
                return Failure("The host moves this scripted body. Acquire motion from a player action to hold it here.");
            if (!CanAcquire(body.Entity)) return Failure("This player is already moving that object or too many others. Release one, then try again.");
            if (!TryLocalPosition(out var position)) return Failure("The player is not in the world yet. Try again after it loads.");
            var epoch = EpochOf(target);
            if (world.Host.IsActive)
            {
                // The host's own player hears a refusal as a joined player does: one toast that says why.
                var answer = GrabHere(target, epoch, position, out epoch);
                if (!IsGranted(answer)) return Refuse(context, answer);
            }

            var originalKinematic = body.IsKinematic;
            var result = inner.AcquireMotion(body.Entity);
            if (!result.TryGetValue(out var engine))
            {
                if (world.Host.IsActive) CancelHostGrab(target);
                return result;
            }

            var lease = Start(context, body, engine, target, epoch, position, originalKinematic);
            return OperationResult<IEntityMotion>.Success(lease);
        }

        /// <summary>
        /// Starts ownership of a native pickup. False when the item is no network body, which stays the game's alone;
        /// otherwise true, with the lease, or with null when the pickup is refused and the item must drop.
        /// </summary>
        internal bool TryAcquireNative(IEntity entity, out NetEntityMotion? lease)
        {
            lease = null;
            if (!TryTarget(entity, out var target, out var body)) return false;
            if (!CanAcquire(body.Entity) || !TryLocalPosition(out var position)) return true;
            var epoch = EpochOf(target);
            if (world.Host.IsActive && !IsGranted(GrabHere(target, epoch, position, out epoch))) return true;
            lease = Start(null, body, null, target, epoch, position, body.IsKinematic);
            return true;
        }

        internal bool HasNativeNetworkIdentity(IEntity entity) => world.Backend.TryGetNetworkId(entity, out _);

        private NetEntityMotion Start(RuntimeReplication? context, INetworkMotionBody body, IEntityMotion? engine,
            MotionTarget target, uint epoch, NetVector3 position, bool originalKinematic)
        {
            if (++nextRequest == 0) nextRequest++;
            var lease = new NetEntityMotion(this, context, body, engine, target, nextRequest,
                world.Host.IsActive ? epoch : unchecked(epoch + 1), Now, originalKinematic)
            { Granted = world.Host.IsActive };
            local.Add(lease);
            world.Client.SetSimulated(body.Entity, true);
            if (world.Client.IsActive)
            {
                write.Target = target;
                write.Request = lease.Request;
                write.Epoch = epoch;
                write.PlayerPosition = position;
                MotionWire.Write(writer, MotionMessages.Grab, write);
                if (!SendToHost()) EndLocal(lease);
            }

            return lease;
        }

        private bool CanAcquire(IEntity entity)
        {
            for (var index = local.Count - 1; index >= 0; index--)
            {
                var lease = local[index];
                if (lease.Entity.Id != entity.Id) continue;
                if (!lease.Releasing && !lease.Ended) return false;
                EndLocal(lease);
                local.RemoveAt(index);
            }

            return local.Count < MotionRules.MaximumLocalBodies;
        }

        private bool TryTarget(IEntity entity, out MotionTarget target, out INetworkMotionBody body)
        {
            target = default;
            body = null!;
            if (entity == null || !entity.IsAlive) return false;
            entity = backend?.MotionRoot(entity) ?? entity;
            if (world.Client.IsActive) return world.Client.TryMotionTarget(entity, out target, out body);
            if (world.Backend.TryGetNetworkId(entity, out var id) && world.Host.TryGetHosted(id, out var found) &&
                found.Object is INetworkMotionBody known)
            {
                target = new MotionTarget(id);
                body = known;
                return true;
            }

            if (!world.Host.Index.TryFind(entity, out var slot) || !(slot.Object is INetworkMotionBody scene)) return false;
            target = new MotionTarget(slot.Key);
            body = scene;
            return true;
        }

        private uint EpochOf(MotionTarget target)
        {
            if (world.Host.IsActive && world.Host.TryBindMotion(target, out var entity))
                return hosted.TryGetValue(entity.Id.Value, out var state) ? state.Rules.Epoch : 0;
            return target.Id.IsValid && epochs.TryGetValue(target.Id.Value, out var epoch) ? epoch : 0;
        }

        private bool TryLocalPosition(out NetVector3 position)
        {
            position = default;
            return backend != null && backend.TryGetLocalPosition(out position) && MotionRules.IsValid(position);
        }

        private bool IsSimulating(IEntity entity)
        {
            foreach (var lease in local)
                if (!lease.Ended && lease.Entity.Id == entity.Id) return true;
            return false;
        }

        internal void BeforeReceive()
        {
            if (generation == world.Channel.Generation) return;
            generation = world.Channel.Generation;
            if (world.Client.IsActive)
            {
                foreach (var lease in local) EndLocal(lease);
                local.Clear();
                epochs.Clear();
                heldItems.Reset();
            }
            else
            {
                foreach (var state in hosted.Values) state.Dirty = true;
            }
        }

        public void Run(float deltaTime)
        {
            if (!world.IsOnline) return;
            heldItems.Poll();
            PumpLocal();
            if (world.Host.IsActive) PumpHost();
        }

        internal void Reset()
        {
            heldItems.Reset();
            foreach (var lease in local) EndLocal(lease);
            local.Clear();
            foreach (var state in hosted.Values)
                if (state.Rules.State != OwnershipState.Server) state.Restore();
            hosted.Clear();
            broadcastStart = 0;
            replies.Clear();
            incoming.Clear();
            epochs.Clear();
            generation = world.Channel.Generation;
            outgoing = new TokenBucket(MotionRules.BurstBytes, MotionRules.BytesPerSecond, Now);
        }

        private void EndLocal(NetEntityMotion lease)
        {
            lease.End(world.Client.IsActive);
            if (world.Host.IsActive && lease.Target.Id.IsValid && hosted.TryGetValue(lease.Target.Id.Value, out var state) &&
                state.Rules.State != OwnershipState.Server && state.Rules.Owner.Participant != MotionRules.HostSlot)
                lease.Body.IsKinematic = true;
            world.Client.SetSimulated(lease.Entity, false);
        }

        private bool SendToHost()
        {
            if (!outgoing.TryTake(Now, writer.Length) || !world.Channel.TrySendToHost(writer)) return false;
            BytesSent += writer.Length;
            return true;
        }

        private static OperationResult<IEntityMotion> Failure(string message) =>
            OperationResult<IEntityMotion>.Failure(ModErrorCode.NotAuthoritative, message);

        private static bool IsGranted(OwnershipResult result) =>
            result == OwnershipResult.Granted || result == OwnershipResult.AlreadyOwned;

        private OperationResult<IEntityMotion> Refuse(RuntimeReplication context, OwnershipResult result)
        {
            var message = MotionRules.Refusal(result);
            Tell(context.Ui, message);
            return Failure(message);
        }

        /// <summary>Shows a refusal through the context's own UI service, at most one every few seconds across leases.</summary>
        private void Tell(IUiService? ui, string message)
        {
            if (ui == null || Now < nextToast) return;
            nextToast = Now + SpawnAuthorityRules.RefusalToastIntervalMicros;
            ui.ShowToast(message, UiTone.Warning);
        }
    }
}
