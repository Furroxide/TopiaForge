using System;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // The bodies other players' avatars wear in a multiplayer session (WS3.10): a copy of the game's own robot, dormant.
    // The runtime asks for one when an avatar appears and falls back to its capsule when none can be made, as at the
    // main menu, where no robot is loaded to copy.
    internal sealed class RobotCosmeticBodies : IInternalAvatarBodyFactory
    {
        private readonly RobotAgentService service;

        internal RobotCosmeticBodies(RobotAgentService service)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public OperationResult<IInternalAvatarBody> CreateBody(InternalAvatarBodyRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            try
            {
                return service.CreateCosmeticBody(request);
            }
            catch (Exception exception)
            {
                return OperationResult<IInternalAvatarBody>.Failure(
                    ModErrorCode.External,
                    "RobotKit could not make a robot body for a player's avatar (" + RobotKitLog.ExceptionType(exception) + ").");
            }
        }
    }

    // One robot body on an avatar: no brain, no locomotion, no colliders, every rigidbody kinematic, so the runtime's
    // avatar moves it and its capsule collider is what others bump into. Its animator's walk follows the avatar's speed.
    internal sealed class RobotCosmeticBody : IInternalAvatarBody
    {
        // A change of speed smaller than this does not touch the animator.
        private const float SpeedEpsilon = 0.01f;

        private readonly NativeEntityAdapter root;
        private readonly Animator? animator;
        private readonly int walk;
        private readonly AnimatorControllerParameterType walkType;
        private readonly bool hasWalk;
        private GameObject? body;
        private float shownSpeed = -1f;

        internal RobotCosmeticBody(GameObject body, string entityId, Animator? animator, string? walkParameter)
        {
            this.body = body ?? throw new ArgumentNullException(nameof(body));
            root = new NativeEntityAdapter(entityId, body);
            this.animator = animator;
            if (animator == null || string.IsNullOrEmpty(walkParameter)) return;
            foreach (var parameter in animator.parameters)
            {
                if (!string.Equals(parameter.name, walkParameter, StringComparison.Ordinal)) continue;
                walk = parameter.nameHash;
                walkType = parameter.type;
                hasWalk = parameter.type != AnimatorControllerParameterType.Trigger;
                break;
            }
        }

        public IEntity Root => root;

        public bool IsAlive => body != null;

        public void Animate(float speedMetersPerSecond, bool crouching)
        {
            if (!hasWalk || animator == null || body == null) return;
            if (Math.Abs(speedMetersPerSecond - shownSpeed) < SpeedEpsilon) return;
            shownSpeed = speedMetersPerSecond;
            switch (walkType)
            {
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(walk, speedMetersPerSecond);
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(walk, (int)Math.Round(speedMetersPerSecond));
                    break;
                default:
                    animator.SetBool(walk, speedMetersPerSecond > 0.2f);
                    break;
            }
        }

        public void Dispose()
        {
            var made = body;
            body = null;
            if (made != null) UnityEngine.Object.Destroy(made);
        }
    }

    internal sealed partial class RobotAgentService
    {
        private const BindingFlags StaticFields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly Type? LocomotionControllerType = Type.GetType("LocomotionController, GameCode", throwOnError: false);
        private int cosmeticCounter;
        private string? walkParameter;
        private bool walkParameterRead;

        // A dormant copy of the scene's default robot for a player's avatar. Made under the inactive incubator, so the
        // dormant brain is in place before the robot's own Awake runs, as Spawn does; then quietened, tinted and handed
        // over. The runtime parents it under the avatar, so it outlives this service's scene cleanup, and disposes it.
        internal OperationResult<IInternalAvatarBody> CreateCosmeticBody(InternalAvatarBodyRequest request)
        {
            if (disposed)
            {
                return OperationResult<IInternalAvatarBody>.Failure(ModErrorCode.InvalidState, "RobotKit has been disposed.");
            }

            var prefab = ResolveCachedPrefab();
            if (prefab == null)
            {
                return OperationResult<IInternalAvatarBody>.Failure(ModErrorCode.Unavailable, "No robot is loaded in this scene to copy for a player's avatar.");
            }

            EnsureRoots();
            if (incubator == null || root == null)
            {
                return OperationResult<IInternalAvatarBody>.Failure(ModErrorCode.Unavailable, "RobotKit could not create its spawn root.");
            }

            var clone = UnityEngine.Object.Instantiate(prefab, incubator.transform, false);
            clone.SetActive(false);
            clone.name = "TopiaForge Player Body";
            GameReflection.ConfigureBrain(clone, RobotBrainMode.Dormant, logger);
            Quieten(clone);
            Tint(clone, new Color(request.Red, request.Green, request.Blue, 1f));
            clone.transform.SetParent(root.transform, false);
            clone.SetActive(true);
            var animator = ResolveAnimator(clone);
            cosmeticCounter++;
            return OperationResult<IInternalAvatarBody>.Success(
                new RobotCosmeticBody(clone, "robotkit:avatar-body-" + cosmeticCounter, animator, ResolveWalkParameter()));
        }

        // Nothing native moves, thinks, collides or listens: the avatar moves the body, and its capsule is what others hit.
        private static void Quieten(GameObject clone)
        {
            foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour != null && GameReflection.IsNamed(behaviour, "LocomotionController", "NavMeshAgent", "BehaviorTree"))
                {
                    behaviour.enabled = false;
                }
            }

            foreach (var body in clone.GetComponentsInChildren<Rigidbody>(true))
            {
                body.isKinematic = true;
                body.detectCollisions = false;
            }

            foreach (var collider in clone.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
            }
        }

        private static void Tint(GameObject clone, Color color)
        {
            var block = new MaterialPropertyBlock();
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                renderer.GetPropertyBlock(block);
                block.SetColor("_BaseColor", color);
                block.SetColor("_EmissiveColor", color * 0.35f);
                renderer.SetPropertyBlock(block);
            }
        }

        private static Animator? ResolveAnimator(GameObject clone)
        {
            var body = GameReflection.FindComponent(clone, "RobotBody");
            if (body != null && GameReflection.GetPropertyValue(body, "Animator") is Animator native && native != null) return native;
            return clone.GetComponentInChildren<Animator>(true);
        }

        // The walk parameter the game's own locomotion drives, by the name it keeps; read once.
        private string? ResolveWalkParameter()
        {
            if (walkParameterRead) return walkParameter;
            walkParameterRead = true;
            try
            {
                walkParameter = LocomotionControllerType?.GetField("WALK_PARAMETER_NAME", StaticFields)?.GetValue(null) as string;
            }
            catch (Exception exception)
            {
                RobotKitDiagnostics.ReportOnce("avatar walk parameter", exception);
                walkParameter = null;
            }

            return walkParameter;
        }
    }
}
