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
        IOwnerBoundExtensionFactory,
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
                        .Select(CreateTarget)
                        .Where(target => target != null)
                        .Cast<IRobotEditTarget>()
                        .Take(MaximumTargets)
                        .ToArray();
                }
                catch (Exception exception)
                {
                    logger.Debug("Robot scene target discovery is unavailable: " + exception.Message);
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
            target = component == null ? null : CreateTarget(component);
            return target != null;
        }

        public OperationResult<IRobotEditLease> BeginTemporaryEdit(IRobotEditTarget target)
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

            var instanceId = nativeTarget.InstanceId;
            if (leases.TryGetValue(instanceId, out var existing) && existing.IsActive)
            {
                return OperationResult<IRobotEditLease>.Failure(ModErrorCode.Conflict, "Another creator session is editing this robot.");
            }

            try
            {
                var lease = new RobotEditLease(nativeTarget, logger, () => leases.Remove(instanceId));
                leases[instanceId] = lease;
                return OperationResult<IRobotEditLease>.Success(lease);
            }
            catch (Exception exception)
            {
                logger.Debug("Robot edit snapshot failed: " + exception.Message);
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

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(Type contractType, string ownerModId, IModLifetime lifetime)
        {
            if (contractType != typeof(IRobotSceneEditorService))
            {
                throw new ArgumentException("Unsupported RobotKit editor contract: " + contractType.FullName, nameof(contractType));
            }

            return new OwnerFacade(this, lifetime);
        }

        private RobotEditTarget? CreateTarget(Component agent)
        {
            if (agent == null || agent.gameObject == null || !agent.gameObject.scene.IsValid())
            {
                return null;
            }

            var body = agent.GetComponentInParent(RobotPersonalityBridge.RobotBodyType ?? agent.GetType());
            var root = body == null ? agent.gameObject : body.gameObject;
            var isManaged = root.GetComponent<RobotAgentEntityIdentityAnchor>() != null;
            if (!isManaged && HasProgressionComponent(root))
            {
                return null;
            }

            return new RobotEditTarget(root, agent, !isManaged);
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

            public OwnerFacade(RobotSceneEditorService service, IModLifetime lifetime)
            {
                this.service = service;
                this.lifetime = lifetime;
            }

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

                var result = service.BeginTemporaryEdit(target);
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
                    service.logger.Debug("Robot edit lease tracking failed: " + exception.Message);
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
