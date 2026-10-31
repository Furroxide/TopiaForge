using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    // The native adapter supplies bodies; this layer owns the same host/replica contract in tests and in the game.
    internal sealed partial class RobotReplication : IInternalReplicaCommands, IInternalSharedSpawnHandler, IInternalReplicaCommandLifetime
    {
        private readonly Func<RobotAgentSpawnRequest, bool, OperationResult<IRobotAgent>> spawn;
        private readonly Func<IEntity, TransformState> transform;
        private readonly IRuntimeReplication provider;
        private readonly Dictionary<string, State> states = new Dictionary<string, State>(StringComparer.Ordinal);

        internal RobotReplication(Func<RobotAgentSpawnRequest, bool, OperationResult<IRobotAgent>> spawn,
            Func<IEntity, TransformState> transform, IRuntimeReplication provider)
        { this.spawn = spawn; this.transform = transform; this.provider = provider; }

        internal OperationResult<IRobotAgent> Spawn(IRuntimeReplication replication, RobotAgentSpawnRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (replication.Realm == InternalNetRealm.Client && replication.NetworkClass == InternalNetworkClass.ClientLocal)
                return OperationResult<IRobotAgent>.Failure(ModErrorCode.NotAuthoritative, "Robots in co-op must be spawned by a shared mod.");
            if (replication.Realm == InternalNetRealm.Client && request.Interaction.CustomInteraction != null)
                return OperationResult<IRobotAgent>.Failure(ModErrorCode.NotAuthoritative, "Custom robot interactions must be installed by the host.");
            try
            {
                var payload = RobotReplicationWire.Spawn(request);
                // Validate the original arguments before canonicalization can erase invalid brain/talk values.
                RobotReplicationWire.Spawn(payload);
                if (IsGuestOrigin(replication))
                {
                    request = WithoutNativeBrain(request);
                    payload = RobotReplicationWire.Spawn(request);
                }
                var descriptor = new InternalSpawnDescriptor(RobotReplicationWire.Kind,
                    new TransformState(request.Position, Facing(request.Facing), new Vec3(request.Scale, request.Scale, request.Scale)), payload);
                var result = replication.RequestSpawn(descriptor, () => Make(request, replication.Realm == InternalNetRealm.Client));
                if (!result.TryGetValue(out var entity) || !states.TryGetValue(entity.Id, out var state))
                    return OperationResult<IRobotAgent>.Failure(result.ErrorCode, result.ErrorMessage);
                BindSpeech(state.Agent, replication);
                return OperationResult<IRobotAgent>.Success(new RoutedRobotAgent(state.Agent, replication));
            }
            catch (ArgumentException) { return OperationResult<IRobotAgent>.Failure(ModErrorCode.InvalidArgument, "Robot spawn values must be bounded plain text and finite numbers."); }
        }

        public OperationResult<IEntity> Spawn(InternalSpawnDescriptor descriptor) => MakeDescriptor(descriptor, false);
        public OperationResult<IEntity> CreateReplica(InternalSpawnDescriptor descriptor) => MakeDescriptor(descriptor, true);

        private OperationResult<IEntity> MakeDescriptor(InternalSpawnDescriptor descriptor, bool replica)
        {
            try
            {
                var decoded = RobotReplicationWire.Spawn(descriptor.Payload);
                // The runtime's transform wins over any position embedded by a sender.
                var rotation = descriptor.Transform.Rotation;
                var facing = new Vec3(2 * (rotation.X * rotation.Z + rotation.W * rotation.Y), 0,
                    1 - 2 * (rotation.X * rotation.X + rotation.Y * rotation.Y));
                var request = Copy(decoded.Request, position: descriptor.Transform.Position, facing: facing);
                var result = Make(request, replica);
                if (result.TryGetValue(out var entity) && decoded.Emote.Length != 0)
                { states[entity.Id].Emote = decoded.Emote; states[entity.Id].Agent.SetEmote(decoded.Emote); }
                if (replica && result.TryGetValue(out var body)) ApplyReplica(body, descriptor);
                return result;
            }
            catch (Exception) { return OperationResult<IEntity>.Failure(ModErrorCode.InvalidArgument, "Invalid robot description."); }
        }

        private OperationResult<IEntity> Make(RobotAgentSpawnRequest request, bool replica)
        {
            if (replica || IsGuestOrigin(provider)) request = WithoutNativeBrain(request);
            var made = spawn(request, replica);
            if (!made.TryGetValue(out var agent)) return OperationResult<IEntity>.Failure(made.ErrorCode, made.ErrorMessage);
            var state = new State(agent, request, replica);
            states.Add(agent.Id, state);
            // Replicas have a canonical receiving association before any host-origin route is attached.
            BindPlayback(state, null);
            return OperationResult<IEntity>.Success(agent);
        }

        public void ResetPendingCommands(IEntity target)
        {
            if (states.TryGetValue(target.Id, out var state))
                (state.Agent as IRobotReplicaState)?.ReplicaState?.ResetPending();
        }

        public void ReleaseReplica(IEntity replica) => Despawn(replica);
        public void Despawn(IEntity entity)
        {
            if (!states.TryGetValue(entity.Id, out var state)) return;
            states.Remove(entity.Id);
            state.Agent.Dispose();
        }

        internal void Forget(string id) => states.Remove(id);

        internal void Clear() => states.Clear();

        public InternalSpawnDescriptor Describe(IEntity target)
        {
            var state = states[target.Id];
            var moving = state.Agent.IsMoving; var reached = state.Agent.HasReachedTarget;
            if (moving != state.Moving || reached != state.Reached) state.Descriptor = null;
            state.Moving = moving; state.Reached = reached;
            return state.Descriptor ??= new InternalSpawnDescriptor(RobotReplicationWire.Kind, transform(state.Agent),
                RobotReplicationWire.Spawn(state.Request, state.Emote, moving, reached, state.Motion));
        }

        public OperationResult<bool> ApplyReplica(IEntity target, InternalSpawnDescriptor descriptor)
        {
            if (!states.TryGetValue(target.Id, out var state)) return OperationResult<bool>.Failure(ModErrorCode.NotFound, "Robot body is unavailable.");
            var decoded = RobotReplicationWire.Spawn(descriptor.Payload);
            var request = WithoutNativeBrain(decoded.Request);
            // No host brain or native conversation is enabled on this path, even if the host asks for one.
            state.Agent.SetBrainMode(RobotBrainMode.Dormant);
            state.Agent.SetInteraction(RobotInteractionOptions.DisableNativeTalk());
            state.Agent.SetScale(request.Scale);
            if (request.Tint.HasValue) state.Agent.SetTint(request.Tint.Value);
            if (request.Name != null) state.Agent.SetName(request.Name);
            if (decoded.Emote != state.Emote) state.Agent.SetEmote(decoded.Emote);
            (state.Agent as IRobotReplicaState)?.ReplicaState?.Apply(request, decoded.Moving, decoded.Reached, decoded.Motion);
            state.Request = request; state.Emote = decoded.Emote; state.Descriptor = null;
            return OperationResult<bool>.Success(true);
        }

        private sealed class State
        {
            internal State(IRobotAgent agent, RobotAgentSpawnRequest request, bool replica)
            { Agent = agent; Request = request; IsReplica = replica; }
            internal IRobotAgent Agent { get; }
            internal bool IsReplica { get; }
            internal object? SpeechAssociation;
            internal IRuntimeReplication? SpeechConsumer;
            internal RobotAgentSpawnRequest Request { get; set; }
            internal string Emote { get; set; } = "";
            internal InternalSpawnDescriptor? Descriptor { get; set; }
            internal bool Moving, Reached;
            internal Guid Motion;
        }

        private static Quat Facing(Vec3? facing)
        {
            if (!facing.HasValue || facing.Value.X * facing.Value.X + facing.Value.Z * facing.Value.Z <= 0.0001f)
                return Quat.Identity;
            var halfYaw = Math.Atan2(facing.Value.X, facing.Value.Z) * 0.5;
            return new Quat(0, (float)Math.Sin(halfYaw), 0, (float)Math.Cos(halfYaw));
        }

        private static RobotAgentSpawnRequest Copy(RobotAgentSpawnRequest value, Vec3? position = null,
            RobotBrainMode? brain = null, RobotMovementSettings? movement = null, RobotColor? tint = null,
            string? name = null, float? scale = null, RobotInteractionOptions? interaction = null, Vec3? facing = null) =>
            new RobotAgentSpawnRequest(position ?? value.Position, facing ?? value.Facing, brain ?? value.BrainMode,
                movement?.Gait ?? value.Gait, movement?.MoveSpeed ?? value.MoveSpeed, movement?.TurnSpeed ?? value.TurnSpeed,
                movement?.StopDistance ?? value.StopDistance, tint ?? value.Tint, name ?? value.Name,
                scale ?? value.Scale, interaction ?? value.Interaction, value.RobotTypeId);
    }
}
