using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    internal sealed class RobotSceneEditorService :
        IRobotSceneEditorService,
        IOwnerContextBoundExtensionFactory,
        IDisposable
    {
        private const int MaximumTargets = 256;
        private readonly RobotAgentService agents;
        private readonly IModLogger logger;
        private readonly Dictionary<int, RobotEditLease> leases = new Dictionary<int, RobotEditLease>();
        private bool disposed;

        public RobotSceneEditorService(RobotAgentService agents, IModLogger logger)
        {
            this.agents = agents ?? throw new ArgumentNullException(nameof(agents));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool IsAvailable => !disposed && RobotPersonalityBridge.IsAvailable;

        public IReadOnlyList<IRobotEditTarget> Targets
        {
            get
            {
                if (!IsAvailable)
                {
                    return Array.Empty<IRobotEditTarget>();
                }

                var agentType = RobotPersonalityBridge.AgentType!;

                try
                {
                    return UnityEngine.Object.FindObjectsByType(agentType, FindObjectsSortMode.InstanceID)
                        .OfType<Component>()
                        .Select(agent => CreateTarget(agent))
                        .Where(target => target != null)
                        .Cast<IRobotEditTarget>()
                        .Take(MaximumTargets)
                        .ToArray();
                }
                catch (Exception exception)
                {
                    logger.Debug("Robot scene target discovery is unavailable: " + RobotKitLog.ExceptionType(exception));
                    return Array.Empty<IRobotEditTarget>();
                }
            }
        }

        public bool TryResolve(IRobotAgent agent, out IRobotEditTarget? target)
        {
            var resolved = !IsAvailable || agent == null ? null : agents.FindAgentByEntity(agent);
            if (resolved == null || !resolved.IsAlive || !(resolved is INativeEntityAdapter native))
            {
                target = null;
                return false;
            }

            var component = GameReflection.FindComponent(native.NativeGameObject, "LLMAgent");
            target = component == null || !component.gameObject.activeInHierarchy ? null : CreateTarget(component, managed: true);
            return target != null;
        }

        public OperationResult<IRobotEditLease> BeginTemporaryEdit(IRobotEditTarget target) => BeginTemporaryEdit(target, null);

        internal OperationResult<IRobotEditLease> BeginTemporaryEdit(IRobotEditTarget target, Func<bool>? remoteCaller)
        {
            if (disposed)
            {
                return OperationResult<IRobotEditLease>.Failure(ModErrorCode.InvalidState, "Robot editor is disposed.");
            }

            if (!RobotPersonalityBridge.IsAvailable)
            {
                return OperationResult<IRobotEditLease>.Failure(
                    ModErrorCode.Unavailable,
                    "Native robot personality editing bindings are unavailable.");
            }

            if (!(target is RobotEditTarget nativeTarget) || !nativeTarget.IsAlive)
            {
                return OperationResult<IRobotEditLease>.Failure(ModErrorCode.NotFound, "Robot edit target is no longer available.");
            }

            try
            {
                if (nativeTarget.IsManaged != nativeTarget.Managed)
                {
                    return OperationResult<IRobotEditLease>.Failure(ModErrorCode.Conflict, RobotEditTarget.ManagedRobotMessage);
                }

                var instanceId = nativeTarget.InstanceId;
                if (leases.TryGetValue(instanceId, out var existing) && existing.IsActive)
                {
                    return OperationResult<IRobotEditLease>.Failure(ModErrorCode.Conflict, "Another creator session is editing this robot.");
                }

                var lease = new RobotEditLease(nativeTarget, logger, () => leases.Remove(instanceId), remoteCaller);
                leases[instanceId] = lease;
                return OperationResult<IRobotEditLease>.Success(lease);
            }
            catch (Exception exception)
            {
                logger.Debug("Robot edit snapshot failed: " + RobotKitLog.ExceptionType(exception));
                return OperationResult<IRobotEditLease>.Failure(
                    nativeTarget.IsAlive ? ModErrorCode.External : ModErrorCode.NotFound,
                    "The robot changed or disappeared before its temporary edit could begin.");
            }
        }

        public void OnSceneChanged()
        {
            RestoreAll();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            RestoreAll();
        }

        object IOwnerContextBoundExtensionFactory.CreateOwnerFacade(Type contractType, IModContext context)
        {
            if (contractType != typeof(IRobotSceneEditorService))
            {
                throw new ArgumentException("Unsupported RobotKit editor contract: " + contractType.FullName, nameof(contractType));
            }

            return new OwnerFacade(this, context.Lifetime, (context as IInternalReplicationContext)?.Replication);
        }

        // Scene discovery offers only native scene robots. A RobotKit robot resolved from its own agent is a managed
        // target, which allows a personality preview and nothing that RobotKit's routed controls already own.
        private RobotEditTarget? CreateTarget(Component agent, bool managed = false)
        {
            if (agent == null || agent.gameObject == null || !agent.gameObject.scene.IsValid())
            {
                return null;
            }

            var body = agent.GetComponentInParent(RobotPersonalityBridge.RobotBodyType ?? agent.GetType());
            var root = body == null ? agent.gameObject : body.gameObject;
            if (RobotEditTarget.HasManagedRobot(root, agent) != managed || HasProgressionComponent(root))
            {
                return null;
            }

            return new RobotEditTarget(root, agent, managed);
        }

        private static bool HasProgressionComponent(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                {
                    continue;
                }

                var name = component.GetType().Name;
                if (name.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Checkpoint", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Achievement", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Progress", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void RestoreAll()
        {
            foreach (var lease in leases.Values.Reverse().ToArray())
            {
                lease.Dispose();
            }

            leases.Clear();
        }

        private sealed class OwnerFacade : IRobotSceneEditorService
        {
            private readonly RobotSceneEditorService service;
            private readonly IModLifetime lifetime;
            private readonly IRuntimeReplication? replication;

            public OwnerFacade(RobotSceneEditorService service, IModLifetime lifetime, IRuntimeReplication? replication)
            {
                this.service = service;
                this.lifetime = lifetime;
                this.replication = replication;
            }

            // A client's process, or host code running on a guest's input, must not give a robot a personality or wake
            // its native brain: that would run text a guest wrote, or a brain only one PC runs.
            private bool RemoteCaller() => replication != null && (replication.Realm == InternalNetRealm.Client ||
                replication is IInternalEntityCommands commands && commands.IsGuestOrigin);

            public bool IsAvailable => !lifetime.IsStopping && service.IsAvailable;
            public IReadOnlyList<IRobotEditTarget> Targets => IsAvailable ? service.Targets : Array.Empty<IRobotEditTarget>();

            public bool TryResolve(IRobotAgent agent, out IRobotEditTarget? target)
            {
                if (!IsAvailable)
                {
                    target = null;
                    return false;
                }

                return service.TryResolve(agent, out target);
            }

            public OperationResult<IRobotEditLease> BeginTemporaryEdit(IRobotEditTarget target)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<IRobotEditLease>.Failure(ModErrorCode.Cancelled, "The consuming mod is stopping.");
                }

                var result = service.BeginTemporaryEdit(target, RemoteCaller);
                if (!result.TryGetValue(out var lease))
                {
                    return result;
                }

                try
                {
                    if (lifetime.IsStopping)
                    {
                        lease.Dispose();
                        return OperationResult<IRobotEditLease>.Failure(
                            ModErrorCode.Cancelled,
                            "The consuming mod is stopping.");
                    }

                    lifetime.Track(lease);
                    return result;
                }
                catch (Exception exception)
                {
                    lease.Dispose();
                    service.logger.Debug("Robot edit lease tracking failed: " + RobotKitLog.ExceptionType(exception));
                    return OperationResult<IRobotEditLease>.Failure(
                        lifetime.IsStopping ? ModErrorCode.Cancelled : ModErrorCode.External,
                        lifetime.IsStopping
                            ? "The consuming mod is stopping."
                            : "The temporary robot edit could not be attached to the consuming mod lifetime.");
                }
            }
        }
    }
}
