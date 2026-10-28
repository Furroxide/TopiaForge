using System;
using System.IO;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    internal sealed class RoutedRobotAgent : IRobotAgent, IInternalNativeObjectEntity
    {
        private readonly IRobotAgent agent;
        private readonly IRuntimeReplication replication;
        internal RoutedRobotAgent(IRobotAgent agent, IRuntimeReplication replication)
        { this.agent = agent; this.replication = replication; }
        public string Id => agent.Id;
        public object? NativeObject => (agent as IInternalNativeObjectEntity)?.NativeObject;
        public string Name => agent.Name;
        public bool IsAlive => agent.IsAlive;
        public Vec3 Position => agent.Position;
        public Vec3 HeadPosition => agent.HeadPosition;
        public RobotBrainMode BrainMode => agent.BrainMode;
        public bool IsMoving => agent.IsMoving;
        public bool HasReachedTarget => agent.HasReachedTarget;
        public float MoveSpeed => agent.MoveSpeed;
        public float TurnSpeed => agent.TurnSpeed;
        public float StopDistance => agent.StopDistance;
        public RobotGait Gait => agent.Gait;

        private OperationResult<bool> Send(RobotCommand command, Action<BinaryWriter>? write = null)
        {
            if (!(replication is IInternalEntityCommands commands))
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Robot command routing is unavailable.");
            try
            {
                var payload = RobotReplicationWire.Write(writer => { writer.Write((byte)command); write?.Invoke(writer); });
                return commands.SubmitEntityCommand(agent, RobotReplicationWire.Kind, payload);
            }
            catch (ArgumentException) { return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Invalid robot command values."); }
        }
        private OperationResult<bool> SendMotion(RobotCommand command, Action<BinaryWriter>? write = null)
        {
            var motion = Guid.NewGuid();
            var result = Send(command, writer => { write?.Invoke(writer); writer.Write(motion.ToByteArray()); });
            if (result.TryGetValue(out var sent) && sent) (agent as IRobotReplicaState)?.ReplicaState?.Await(motion);
            return result;
        }
        public OperationResult<bool> SetBrainMode(RobotBrainMode mode) => Send(RobotCommand.Brain, writer => writer.Write((byte)mode));
        public OperationResult<bool> ConfigureMovement(RobotMovementSettings settings) => Send(RobotCommand.Movement, writer =>
        { writer.Write((byte)settings.Gait); writer.Write(settings.MoveSpeed); writer.Write(settings.TurnSpeed); writer.Write(settings.StopDistance); });
        public OperationResult<bool> MoveTo(Vec3 position) => replication.Realm != InternalNetRealm.Client
            ? agent.MoveTo(position) : SendMotion(RobotCommand.MoveTo, writer => RobotReplicationWire.Vector(writer, position));
        public OperationResult<bool> Chase(IEntity target)
        {
            if (replication.Realm != InternalNetRealm.Client) return agent.Chase(target is RoutedRobotAgent routed ? routed.agent : target);
            return replication.TryCreateRef(target, out var reference)
                ? SendMotion(RobotCommand.Chase, writer => RobotReplicationWire.Reference(writer, reference))
                : OperationResult<bool>.Failure(ModErrorCode.NotFound, "Chase needs a current player or replicated entity.");
        }
        public OperationResult<bool> Stop() => replication.Realm != InternalNetRealm.Client ? agent.Stop() : SendMotion(RobotCommand.Stop);
        public OperationResult<bool> SetTint(RobotColor color) => Send(RobotCommand.Tint, writer => RobotReplicationWire.Color(writer, color));
        public OperationResult<bool> SetEmote(string emojiShortcode) => Send(RobotCommand.Emote, writer => RobotReplicationWire.Text(writer, emojiShortcode, 64));
        public OperationResult<bool> SetName(string name) => Send(RobotCommand.Name, writer => RobotReplicationWire.Text(writer, name, 128));
        public OperationResult<bool> SetScale(float scale) => Send(RobotCommand.Scale, writer => writer.Write(scale));
        public OperationResult<bool> SetInteraction(RobotInteractionOptions options)
        {
            if (options.CustomInteraction != null)
                return replication.Realm != InternalNetRealm.Client ? agent.SetInteraction(options)
                    : OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative, "Custom robot interactions must be installed by the host.");
            return Send(RobotCommand.Interaction, writer =>
            { writer.Write(options.NativeTalkMode == RobotNativeTalkMode.Enabled); writer.Write(options.NativeTalkDistance); });
        }
        public OperationResult<bool> ApplyDamage(float amount, RobotDamageType type, string source) => Send(RobotCommand.Damage, writer =>
        { writer.Write(amount); writer.Write((byte)type); RobotReplicationWire.Text(writer, source, 128); });
        public OperationResult<bool> Kill(RobotDamageType type, string source) => Send(RobotCommand.Kill, writer =>
        { writer.Write((byte)type); RobotReplicationWire.Text(writer, source, 128); });
        public OperationResult<bool> Ragdoll() => Send(RobotCommand.Ragdoll);
        public OperationResult<bool> Knockback(Vec3 impulse) => Send(RobotCommand.Knockback, writer => RobotReplicationWire.Vector(writer, impulse));
        public OperationResult<bool> Despawn() => Send(RobotCommand.Despawn);
        public void Dispose()
        {
            // The scope also retires its prediction through the runtime, even when it is already stopping.
            if (replication.Realm == InternalNetRealm.Client) Despawn();
            agent.Dispose();
        }
    }
}
