using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    internal sealed partial class RobotAgentService
    {
        // RobotKit constructs the service on the game thread. Resolving a facade does not establish thread ownership.
        private readonly int mainThreadId = Thread.CurrentThread.ManagedThreadId;

        private void AssertMainThread([CallerMemberName] string operation = "RobotKit operation")
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
            {
                throw new InvalidOperationException(
                    "TFSDK100: " + operation + " must be called on the Robotopia game thread. " +
                    "Use Context.Scheduler.NextFrame when continuing work from a background task.");
            }
        }

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

            public bool IsAvailable
            {
                get
                {
                    service.AssertMainThread();
                    return !lifetime.IsStopping && service.IsAvailable;
                }
            }

            public bool IsNavigationAvailable
            {
                get
                {
                    service.AssertMainThread();
                    return !lifetime.IsStopping && service.IsNavigationAvailable;
                }
            }

            public IReadOnlyList<RobotTypeDescriptor> RobotTypes
            {
                get
                {
                    service.AssertMainThread();
                    return service.RobotTypes;
                }
            }

            public IReadOnlyList<IRobotAgent> ActiveAgents
            {
                get
                {
                    service.AssertMainThread();
                    ownedAgents.RemoveAll(agent => !agent.IsAlive);
                    return ownedAgents.ToArray();
                }
            }

            public bool TryGetRobot(IEntity entity, out IRobotAgent? agent)
            {
                service.AssertMainThread();
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
                service.AssertMainThread();
                if (lifetime.IsStopping)
                {
                    entity = null;
                    return false;
                }

                return service.TryGetPlayerEntity(out entity);
            }

            public OperationResult<IRobotAgent> Spawn(RobotAgentSpawnRequest request)
            {
                service.AssertMainThread();
                if (lifetime.IsStopping)
                {
                    return OperationResult<IRobotAgent>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod is stopping and cannot spawn robots.");
                }

                if (request == null) throw new ArgumentNullException(nameof(request));

                // Capture before spawning: a subsequent world, even in the same scene, must never adopt this handle.
                var readScope = nativeReadContext?.CaptureNativeEntityReadScope();
                OwnerRobotAgent? wrapper = null;
                // Native activation may query the bridge before ownership settles. Until then callbacks are inert.
                var interaction = OwnerRobotAgent.BindInteraction(request.Interaction, () => wrapper);
                var boundRequest = new RobotAgentSpawnRequest(request.Position, request.Facing, request.BrainMode,
                    request.Gait, request.MoveSpeed, request.TurnSpeed, request.StopDistance, request.Tint,
                    request.Name, request.Scale, interaction, request.RobotTypeId);
                var ownerLifetime = lifetime;
                var result = replication != null && service.Replication != null
                    ? service.Replication.Spawn(replication, boundRequest,
                        () => IsScopeCurrent(ownerLifetime, readScope))
                    : service.Spawn(boundRequest);
                if (!result.TryGetValue(out var agent))
                {
                    return result;
                }

                try
                {
                    wrapper = new OwnerRobotAgent(service, agent, lifetime.Track(agent), lifetime, readScope, replication);
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
                service.AssertMainThread();
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.StoppingToken))
                {
                    return await service.FindReachableSpawnAsync(request, linked.Token);
                }
            }

            private static bool IsScopeCurrent(IModLifetime lifetime, IInternalNativeEntityReadScope? readScope) =>
                !lifetime.IsStopping && (readScope == null || readScope.IsCurrent(lifetime));

            private sealed class OwnerRobotAgent : IRobotAgent, IInternalOwnedNativeEntity, IInternalSpeechAgent
            {
                private readonly RobotAgentService service;
                private readonly IRobotAgent agent;
                private IDisposable? lifetimeLease;
                private readonly IModLifetime lifetime;
                private readonly IRuntimeReplication? replication;

                public OwnerRobotAgent(RobotAgentService service, IRobotAgent agent, IDisposable lifetimeLease,
                    IModLifetime lifetime, IInternalNativeEntityReadScope? readScope, IRuntimeReplication? replication)
                {
                    this.service = service;
                    this.agent = agent;
                    this.lifetimeLease = lifetimeLease;
                    this.lifetime = lifetime;
                    this.replication = replication;
                    NativeReadScope = readScope;
                }

                public Task<OperationResult<bool>> SayAsync(string text, CancellationToken token)
                {
                    var refusal = ScopeRefusal();
                    return refusal != null ? Task.FromResult(refusal) : agent.SayAsync(text, token);
                }
                public string Id => agent.Id;
                public IInternalNativeEntityReadScope? NativeReadScope { get; }
                public object? NativeObject => IsAlive ? (agent as IInternalNativeObjectEntity)?.NativeObject : null;
                public string Name => agent.Name;
                public bool IsAlive => lifetimeLease != null && IsScopeCurrent(lifetime, NativeReadScope) && agent.IsAlive;
                public Vec3 Position => agent.Position;
                public Vec3 HeadPosition => agent.HeadPosition;
                public RobotBrainMode BrainMode => agent.BrainMode;
                public bool IsMoving => agent.IsMoving;
                public bool HasReachedTarget => agent.HasReachedTarget;
                public float MoveSpeed => agent.MoveSpeed;
                public float TurnSpeed => agent.TurnSpeed;
                public float StopDistance => agent.StopDistance;
                public RobotGait Gait => agent.Gait;

                private OperationResult<bool>? ScopeRefusal()
                {
                    if (lifetimeLease == null || lifetime.IsStopping)
                        return OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The robot scope has ended.");
                    if (NativeReadScope != null && !NativeReadScope.IsCurrent(lifetime))
                        return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The robot belongs to a retired world.");
                    return null;
                }

                internal static RobotInteractionOptions BindInteraction(RobotInteractionOptions options,
                    Func<IRobotAgent?> resolve)
                {
                    if (options == null) throw new ArgumentNullException(nameof(options));
                    var custom = options.CustomInteraction;
                    if (custom == null) return options;

                    RobotInteractionContext Rebind(IRobotAgent owner, RobotInteractionContext context) =>
                        new RobotInteractionContext(owner, context.AgentPosition, context.HandPosition, context.Distance);

                    // The bridge's native context never escapes to author callbacks. The retained owner handle
                    // continues to use its routed agent, so guest origin is checked when a later call is made.
                    return RobotInteractionOptions.Custom(new RobotCustomInteraction(custom.Prompt, context =>
                    {
                        var owner = resolve();
                        if (owner?.IsAlive == true) custom.Interact?.Invoke(Rebind(owner, context));
                    }, custom.Distance, custom.ScreenRectExpansion, context =>
                    {
                        var owner = resolve();
                        return owner?.IsAlive == true && (custom.CanInteract?.Invoke(Rebind(owner, context)) ?? true);
                    }));
                }

                public bool Wraps(IEntity entity) => ReferenceEquals(agent, entity);
                public OperationResult<bool> SetBrainMode(RobotBrainMode mode) => ScopeRefusal() ?? agent.SetBrainMode(mode);
                public OperationResult<bool> ConfigureMovement(RobotMovementSettings settings) =>
                    ScopeRefusal() ?? agent.ConfigureMovement(settings);
                public OperationResult<bool> MoveTo(Vec3 position) => ScopeRefusal() ?? agent.MoveTo(position);
                public OperationResult<bool> Chase(IEntity target) =>
                    ScopeRefusal() ?? agent.Chase(target is OwnerRobotAgent wrapper ? wrapper.agent : target);
                public OperationResult<bool> Stop() => ScopeRefusal() ?? agent.Stop();
                public OperationResult<bool> SetTint(RobotColor color) => ScopeRefusal() ?? agent.SetTint(color);
                public OperationResult<bool> SetEmote(string emojiShortcode) => ScopeRefusal() ?? agent.SetEmote(emojiShortcode);
                public OperationResult<bool> SetName(string name) => ScopeRefusal() ?? agent.SetName(name);
                public OperationResult<bool> SetScale(float scale) => ScopeRefusal() ?? agent.SetScale(scale);
                public OperationResult<bool> SetInteraction(RobotInteractionOptions options) =>
                    ScopeRefusal() ?? agent.SetInteraction(BindInteraction(options, () => this));
                public OperationResult<bool> ApplyDamage(float amount, RobotDamageType type, string source) =>
                    ScopeRefusal() ?? agent.ApplyDamage(amount, type, source);
                public OperationResult<bool> Kill(RobotDamageType type, string source) => ScopeRefusal() ?? agent.Kill(type, source);
                public OperationResult<bool> Ragdoll() => ScopeRefusal() ?? agent.Ragdoll();
                public OperationResult<bool> Knockback(Vec3 impulse) => ScopeRefusal() ?? agent.Knockback(impulse);

                public OperationResult<bool> Despawn()
                {
                    service.AssertMainThread();
                    if (lifetimeLease == null) return OperationResult<bool>.Success(false);

                    var refusal = ScopeRefusal();
                    if (refusal != null)
                    {
                        // A forwarded guest call on the host cannot turn a scope refusal into native cleanup.
                        if (replication?.Realm != InternalNetRealm.Client &&
                            replication is IInternalEntityCommands commands && commands.IsGuestOrigin)
                            return refusal;

                        // Release the tracked instance. The routed disposer skips commands after scope retirement.
                        var wasAlive = agent.IsAlive;
                        Dispose();
                        return OperationResult<bool>.Success(wasAlive);
                    }

                    var result = agent.Despawn();
                    if (result.Succeeded) Dispose();
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
