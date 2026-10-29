using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    internal sealed partial class RobotReplication
    {
        public OperationResult<bool> Execute(IEntity target, ReadOnlyMemory<byte> payload, Func<InternalNetRef, IEntity?> resolve)
        {
            if (!states.TryGetValue(target.Id, out var state) || !state.Agent.IsAlive)
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "Robot is no longer available.");
            try
            {
                // Decode completely before invoking anything: malformed tails must not cause partial effects.
                var apply = RobotReplicationWire.Read<Func<OperationResult<bool>>>(payload, reader =>
                {
                    var command = RobotReplicationWire.Enum<RobotCommand>(reader, (byte)RobotCommand.Despawn);
                    var agent = state.Agent;
                    switch (command)
                    {
                        case RobotCommand.Brain:
                            var brain = RobotReplicationWire.Enum<RobotBrainMode>(reader, 1);
                            return () => Update(state, agent.SetBrainMode(brain), Copy(state.Request, brain: brain));
                        case RobotCommand.Movement:
                            var gait = RobotReplicationWire.Enum<RobotGait>(reader, 2);
                            var movement = new RobotMovementSettings(gait, RobotReplicationWire.Number(reader, 0, 100),
                                RobotReplicationWire.Number(reader, 0, 3600), RobotReplicationWire.Number(reader, 0, 1000));
                            return () => Update(state, agent.ConfigureMovement(movement), Copy(state.Request, movement: movement));
                        case RobotCommand.MoveTo:
                            var position = RobotReplicationWire.Vector(reader);
                            var move = RobotReplicationWire.Motion(reader);
                            return () => Motion(state, () => agent.MoveTo(position), move);
                        case RobotCommand.Chase:
                            var reference = RobotReplicationWire.Reference(reader);
                            var chase = RobotReplicationWire.Motion(reader);
                            return () => Motion(state, () => resolve(reference) is IEntity chased && chased.IsAlive ? agent.Chase(chased)
                                : OperationResult<bool>.Failure(ModErrorCode.NotFound, "The chase target is no longer available."), chase);
                        case RobotCommand.Stop:
                            var stop = RobotReplicationWire.Motion(reader);
                            return () => Motion(state, agent.Stop, stop);
                        case RobotCommand.Tint:
                            var tint = RobotReplicationWire.Color(reader);
                            return () => Update(state, agent.SetTint(tint), Copy(state.Request, tint: tint));
                        case RobotCommand.Emote:
                            var emote = RobotReplicationWire.Text(reader, 64);
                            return () => { var result = agent.SetEmote(emote); if (result.Succeeded) { state.Emote = emote; state.Descriptor = null; } return result; };
                        case RobotCommand.Name:
                            var name = RobotReplicationWire.Text(reader, 128);
                            return () => Update(state, agent.SetName(name), Copy(state.Request, name: name));
                        case RobotCommand.Scale:
                            var scale = RobotReplicationWire.Number(reader, 0.05f, 20);
                            return () => Update(state, agent.SetScale(scale), Copy(state.Request, scale: scale));
                        case RobotCommand.Interaction:
                            var talk = RobotReplicationWire.Boolean(reader); var distance = RobotReplicationWire.Number(reader, 0, 100);
                            var interaction = talk ? RobotInteractionOptions.NativeTalkAtDistance(distance) : RobotInteractionOptions.DisableNativeTalk();
                            return () => Update(state, agent.SetInteraction(interaction), Copy(state.Request, interaction: interaction));
                        case RobotCommand.Damage:
                            var amount = RobotReplicationWire.Number(reader, 0, 100000);
                            var damage = RobotReplicationWire.Enum<RobotDamageType>(reader, 4);
                            var source = RobotReplicationWire.Text(reader, 128);
                            return () => agent.ApplyDamage(amount, damage, source);
                        case RobotCommand.Kill:
                            var death = RobotReplicationWire.Enum<RobotDamageType>(reader, 4);
                            var cause = RobotReplicationWire.Text(reader, 128);
                            return () => agent.Kill(death, cause);
                        case RobotCommand.Ragdoll: return agent.Ragdoll;
                        case RobotCommand.Knockback:
                            var impulse = RobotReplicationWire.Vector(reader);
                            return () => agent.Knockback(impulse);
                        case RobotCommand.Despawn: return agent.Despawn;
                        default: throw new ArgumentException("Invalid robot command.");
                    }
                });
                return apply();
            }
            catch (Exception) { return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Invalid robot command."); }
        }

        private static OperationResult<bool> Motion(State state, Func<OperationResult<bool>> apply, Guid motion)
        {
            // The token acknowledges fully decoded host processing, not success. A rejected Chase leaves the
            // previous intent intact, but must release its own wait; it cannot release a newer token's wait.
            try { return apply(); }
            finally { state.Motion = motion; state.Descriptor = null; }
        }

        private static OperationResult<bool> Update(State state, OperationResult<bool> result, RobotAgentSpawnRequest request)
        { if (result.Succeeded) { state.Request = request; state.Descriptor = null; } return result; }
    }
}
