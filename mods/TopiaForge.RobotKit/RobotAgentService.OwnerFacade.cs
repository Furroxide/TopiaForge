using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    internal sealed partial class RobotAgentService
    {
        private sealed class OwnerFacade : IRobotAgentService, IRobotPlayerEntitySource
        {
            private readonly RobotAgentService service;
            private readonly IModLifetime lifetime;
            private readonly IRuntimeReplication? replication;
            private readonly IInternalNativeEntityReadContext? nativeReadContext;
            private readonly List<IRobotAgent> ownedAgents = new List<IRobotAgent>();

            public OwnerFacade(RobotAgentService service, IModContext context)
            {
                this.service = service;
                lifetime = context.Lifetime;
                replication = (context as IInternalReplicationContext)?.Replication;
                nativeReadContext = context as IInternalNativeEntityReadContext;
            }

            public bool IsAvailable => !lifetime.IsStopping && service.IsAvailable;
            public bool IsNavigationAvailable => !lifetime.IsStopping && service.IsNavigationAvailable;
            public IReadOnlyList<RobotTypeDescriptor> RobotTypes => service.RobotTypes;

            public IReadOnlyList<IRobotAgent> ActiveAgents
            {
                get
                {
                    ownedAgents.RemoveAll(agent => !agent.IsAlive);
                    return ownedAgents.ToArray();
                }
            }

            public bool TryGetRobot(IEntity entity, out IRobotAgent? agent)
            {
                if (lifetime.IsStopping)
                {
                    agent = null;
                    return false;
                }

                var resolved = service.FindAgentByEntity(entity);
                if (resolved == null)
                {
                    agent = null;
                    return false;
                }

                foreach (var owned in ownedAgents)
                {
                    if (string.Equals(owned.Id, resolved.Id, StringComparison.Ordinal)
                        || owned is OwnerRobotAgent wrapper && wrapper.Wraps(resolved))
                    {
                        agent = owned;
                        return true;
                    }
                }

                agent = null;
                return false;
            }

            public bool TryGetPlayerEntity(out IEntity? entity)
            {
                if (lifetime.IsStopping)
                {
                    entity = null;
                    return false;
                }

                return service.TryGetPlayerEntity(out entity);
            }

            public OperationResult<IRobotAgent> Spawn(RobotAgentSpawnRequest request)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<IRobotAgent>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod is stopping and cannot spawn robots.");
                }

                // Capture before spawning: a subsequent world, even in the same scene, must never adopt this handle.
                var readScope = nativeReadContext?.CaptureNativeEntityReadScope();
                var result = replication != null && service.Replication != null
                    ? service.Replication.Spawn(replication, request) : service.Spawn(request);
                if (!result.TryGetValue(out var agent))
                {
                    return result;
                }

                try
                {
                    var wrapper = new OwnerRobotAgent(agent, lifetime.Track(agent), lifetime, readScope);
                    ownedAgents.Add(wrapper);
                    return OperationResult<IRobotAgent>.Success(wrapper);
                }
                catch (ObjectDisposedException)
                {
                    return OperationResult<IRobotAgent>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod stopped before its spawned robot could be retained.");
                }
            }

            public async Task<OperationResult<ReachableSpawnResult>> FindReachableSpawnAsync(
                ReachableSpawnRequest request,
                CancellationToken cancellationToken = default)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.StoppingToken))
                {
                    return await service.FindReachableSpawnAsync(request, linked.Token);
                }
            }

            private sealed class OwnerRobotAgent : IRobotAgent, IInternalOwnedNativeEntity
            {
                private readonly IRobotAgent agent;
                private IDisposable? lifetimeLease;
                private readonly IModLifetime lifetime;

                public OwnerRobotAgent(IRobotAgent agent, IDisposable lifetimeLease, IModLifetime lifetime,
                    IInternalNativeEntityReadScope? readScope)
                {
                    this.agent = agent;
                    this.lifetimeLease = lifetimeLease;
                    this.lifetime = lifetime;
                    NativeReadScope = readScope;
                }

                public string Id => agent.Id;
                public IInternalNativeEntityReadScope? NativeReadScope { get; }
                public object? NativeObject => IsAlive ? (agent as IInternalNativeObjectEntity)?.NativeObject : null;
                public string Name => agent.Name;
                public bool IsAlive => lifetimeLease != null && !lifetime.IsStopping && agent.IsAlive;
                public Vec3 Position => agent.Position;
                public Vec3 HeadPosition => agent.HeadPosition;
                public RobotBrainMode BrainMode => agent.BrainMode;
                public bool IsMoving => agent.IsMoving;
                public bool HasReachedTarget => agent.HasReachedTarget;
                public float MoveSpeed => agent.MoveSpeed;
                public float TurnSpeed => agent.TurnSpeed;
                public float StopDistance => agent.StopDistance;
                public RobotGait Gait => agent.Gait;

                public bool Wraps(IEntity entity) => ReferenceEquals(agent, entity);
                public OperationResult<bool> SetBrainMode(RobotBrainMode mode) => agent.SetBrainMode(mode);
                public OperationResult<bool> ConfigureMovement(RobotMovementSettings settings) =>
                    agent.ConfigureMovement(settings);
                public OperationResult<bool> MoveTo(Vec3 position) => agent.MoveTo(position);
                public OperationResult<bool> Chase(IEntity target) =>
                    agent.Chase(target is OwnerRobotAgent wrapper ? wrapper.agent : target);
                public OperationResult<bool> Stop() => agent.Stop();
                public OperationResult<bool> SetTint(RobotColor color) => agent.SetTint(color);
                public OperationResult<bool> SetEmote(string emojiShortcode) => agent.SetEmote(emojiShortcode);
                public OperationResult<bool> SetName(string name) => agent.SetName(name);
                public OperationResult<bool> SetScale(float scale) => agent.SetScale(scale);
                public OperationResult<bool> SetInteraction(RobotInteractionOptions options) =>
                    agent.SetInteraction(options);
                public OperationResult<bool> ApplyDamage(float amount, RobotDamageType type, string source) =>
                    agent.ApplyDamage(amount, type, source);
                public OperationResult<bool> Kill(RobotDamageType type, string source) => agent.Kill(type, source);
                public OperationResult<bool> Ragdoll() => agent.Ragdoll();
                public OperationResult<bool> Knockback(Vec3 impulse) => agent.Knockback(impulse);

                public OperationResult<bool> Despawn()
                {
                    var result = agent.Despawn();
                    if (result.Succeeded) Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                    return result;
                }

                public void Dispose()
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }
    }
}
