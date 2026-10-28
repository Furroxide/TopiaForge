using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal sealed class FirstPartyRobots
    {
        internal readonly Dictionary<string, FirstPartyRobot> Agents = new Dictionary<string, FirstPartyRobot>();
        internal readonly RobotReplication Provider;
        internal FirstPartyRobots(WorldPeer peer)
        {
            peer.Scene.ProviderObject = entity => Agents.TryGetValue(entity.Id, out var robot) ? robot.Body : null;
            Provider = new RobotReplication((request, replica) =>
            {
                var body = peer.Scene.Add(peer.Scene.ActiveScene!, request.Name ?? "Robot", rigidbody: true,
                    position: new NetVector3(request.Position.X, request.Position.Y, request.Position.Z));
                if (request.Facing is Vec3 facing)
                {
                    var yaw = Math.Atan2(facing.X, facing.Z) / 2;
                    body.Rotation = new NetQuaternion(0, (float)Math.Sin(yaw), 0, (float)Math.Cos(yaw));
                }
                var robot = new FirstPartyRobot(body, request, replica);
                Agents.Add(robot.Id, robot);
                return OperationResult<IRobotAgent>.Success(robot);
            }, entity =>
            {
                var robot = Agents[entity.Id];
                var q = robot.Body.Rotation;
                return new TransformState(robot.Position, new Quat(q.X, q.Y, q.Z, q.W), new Vec3(robot.Inner.Scale, robot.Inner.Scale, robot.Inner.Scale));
            });
            var seam = peer.Replication.Bind("io.example.robot-provider", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), peer.Lifetime);
            Check(seam.RegisterReplicaFactory(RobotReplicationWire.Kind, Provider).Succeeded);
            Check(seam.RegisterSpawnHandler(RobotReplicationWire.Kind, Provider).Succeeded);
        }
        private static void Check(bool value) { if (!value) throw new Exception("Robot registration failed."); }
    }

    internal sealed class FirstPartyRobot : IRobotAgent, IRobotReplicaState
    {
        internal readonly FakeWorldObject Body;
        internal readonly FakeRobotAgent Inner;
        internal readonly bool Replica;
        internal readonly RobotAgentSpawnRequest Request;
        internal IEntity? Chased;
        internal FirstPartyRobot(FakeWorldObject body, RobotAgentSpawnRequest request, bool replica)
        { Body = body; Replica = replica; Request = request; Inner = (FakeRobotAgent)new FakeRobotAgentService(new FakeModLifetime()).Spawn(request).Value!;
            if (replica) ReplicaState = new RobotReplicaState(new RobotMovementSettings(request.Gait, request.MoveSpeed, request.TurnSpeed, request.StopDistance)); }
        public RobotReplicaState? ReplicaState { get; }
        public string Id => Body.Entity.Id;
        public string Name => Inner.Name;
        public bool IsAlive => Body.IsAlive && Inner.IsAlive;
        public Vec3 Position => new Vec3(Body.Position.X, Body.Position.Y, Body.Position.Z);
        public Vec3 HeadPosition => Position;
        public RobotBrainMode BrainMode => Inner.BrainMode;
        public bool IsMoving => ReplicaState?.IsMoving ?? Inner.IsMoving;
        public bool HasReachedTarget => ReplicaState?.HasReachedTarget ?? Inner.HasReachedTarget;
        public float MoveSpeed => ReplicaState?.MoveSpeed ?? Inner.MoveSpeed;
        public float TurnSpeed => ReplicaState?.TurnSpeed ?? Inner.TurnSpeed;
        public float StopDistance => ReplicaState?.StopDistance ?? Inner.StopDistance;
        public RobotGait Gait => ReplicaState?.Gait ?? Inner.Gait;
        public OperationResult<bool> SetBrainMode(RobotBrainMode mode) => Inner.SetBrainMode(mode);
        public OperationResult<bool> ConfigureMovement(RobotMovementSettings settings) => Replica ? Refuse() : Inner.ConfigureMovement(settings);
        public OperationResult<bool> MoveTo(Vec3 position) => Replica ? Refuse() : Inner.MoveTo(position);
        public OperationResult<bool> Chase(IEntity target) { if (Replica) return Refuse(); Chased = target; return Inner.Chase(target); }
        public OperationResult<bool> Stop() => Replica ? Refuse() : Inner.Stop();
        public OperationResult<bool> SetTint(RobotColor color) => Inner.SetTint(color);
        public OperationResult<bool> SetEmote(string emojiShortcode) => Inner.SetEmote(emojiShortcode);
        public OperationResult<bool> SetName(string name) => Inner.SetName(name);
        public OperationResult<bool> SetScale(float scale) => Inner.SetScale(scale);
        public OperationResult<bool> SetInteraction(RobotInteractionOptions options) => Inner.SetInteraction(options);
        public OperationResult<bool> ApplyDamage(float amount, RobotDamageType type, string source) => Inner.ApplyDamage(amount, type, source);
        public OperationResult<bool> Kill(RobotDamageType type, string source) => Inner.Kill(type, source);
        public OperationResult<bool> Ragdoll() => Inner.Ragdoll();
        public OperationResult<bool> Knockback(Vec3 impulse) => Inner.Knockback(impulse);
        public OperationResult<bool> Despawn() { Body.Destroy(); return Inner.Despawn(); }
        private static OperationResult<bool> Refuse() => OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative, "Replica navigation is disabled.");
        public void Dispose() { Body.Destroy(); Inner.Dispose(); }
    }
}
