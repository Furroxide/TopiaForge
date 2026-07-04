using System;
using System.Linq;
using System.Reflection;
using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.RobotKit
{
    // Clean-room reflection into the game assembly (GameCode). No GameCode types are referenced at compile time;
    // every lookup is by name and guarded so a renamed/missing symbol degrades instead of throwing. This is the
    // shared, battle-tested surface for resolving robot components, configuring the native brain, and reusing the
    // native health/expression systems.
    internal static class GameReflection
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly Type? RobotBodyType = Type.GetType("RobotBody, GameCode", throwOnError: false);
        private static readonly Type? AgentStateType = Type.GetType("AgentState, GameCode", throwOnError: false);
        private static readonly Type? DamageTypeType = Type.GetType("DamageType, GameCode", throwOnError: false);

        // Non-allocating "is this collider part of a game robot?" — used on hot paths (placement, queries).
        public static bool IsGameRobotInParent(Component? component)
        {
            if (component == null)
            {
                return false;
            }

            if (RobotBodyType != null)
            {
                return component.GetComponentInParent(RobotBodyType) != null;
            }

            return HasComponentInParent(component, "RobotBody");
        }

        public static Component? FindComponent(GameObject root, params string[] typeNames)
        {
            return root.GetComponentsInChildren<Component>(true)
                .FirstOrDefault(component => IsNamed(component, typeNames));
        }

        public static bool HasComponent(GameObject root, params string[] typeNames)
        {
            return FindComponent(root, typeNames) != null;
        }

        public static object? GetFieldValue(object target, string fieldName)
        {
            try
            {
                return target.GetType().GetField(fieldName, InstanceFlags)?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        public static object? GetPropertyValue(object target, string propertyName)
        {
            try
            {
                return target.GetType().GetProperty(propertyName, InstanceFlags)?.GetValue(target, null);
            }
            catch
            {
                return null;
            }
        }

        public static bool SetFieldValue(object target, string fieldName, object value)
        {
            try
            {
                var field = target.GetType().GetField(fieldName, InstanceFlags);
                if (field == null)
                {
                    return false;
                }

                field.SetValue(target, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool Invoke(Component component, string methodName, IModLogger? logger, params object[] arguments)
        {
            try
            {
                var method = component.GetType()
                    .GetMethods(InstanceFlags)
                    .FirstOrDefault(candidate => CanInvoke(candidate, methodName, arguments));
                if (method == null)
                {
                    return false;
                }

                method.Invoke(component, arguments);
                return true;
            }
            catch (Exception ex)
            {
                logger?.Debug("RobotKit reflection call failed for " + component.GetType().Name + "." + methodName + ": " + ex.Message);
                return false;
            }
        }

        // A robot's native brain state as it was BEFORE any dormant writes, so the brain can be best-effort woken
        // back up when a mod switches the robot to Autonomous at runtime.
        internal sealed class BrainStateSnapshot
        {
            public readonly System.Collections.Generic.List<(Behaviour Tree, bool Enabled)> BehaviorTrees =
                new System.Collections.Generic.List<(Behaviour, bool)>();

            public object? InitialState;
            public object? State;
            public object? LlmDisabled;
        }

        // Capture the native brain's pristine state (call BEFORE the dormant writes, while the clone is inactive).
        public static BrainStateSnapshot? CaptureBrainState(GameObject root)
        {
            try
            {
                var snapshot = new BrainStateSnapshot();
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component is Behaviour behaviour && IsNamed(component, "BehaviorTree"))
                    {
                        snapshot.BehaviorTrees.Add((behaviour, behaviour.enabled));
                    }
                    else if (IsNamed(component, "LLMAgent"))
                    {
                        snapshot.InitialState = GetFieldValue(component, "initialState");
                        snapshot.State = GetFieldValue(component, "state");
                        snapshot.LlmDisabled = GetFieldValue(component, "llmDisabled");
                    }
                }

                return snapshot;
            }
            catch
            {
                return null;
            }
        }

        // Put a freshly-spawned robot's brain into the requested mode WITHOUT removing the LLMAgent (WalkSession
        // needs head.Agent to resolve, and a body whose Agent is null forces the Animator into Standby). Dormant:
        // disable the BehaviorTree (so the native action loop never issues its own walk/say), and set the LLMAgent
        // dormant (Standby initial state + LLM disabled) so it makes no plans and no RoboAPI calls — the mod owns
        // decisions. Autonomous: leave everything native so the robot thinks for itself. Called while the clone is
        // still inactive (before Awake/OnEnable), so the settings take effect as the robot comes up.
        public static void ConfigureBrain(GameObject root, RobotBrainMode mode, IModLogger logger)
        {
            ApplyBrainMode(root, mode, null, logger);
        }

        // Runtime brain switch. Dormant = the proven spawn-time writes (also correct on a live robot). Autonomous
        // = best-effort wake-up: restore the captured BehaviorTree flags (enable-all without a snapshot), restore
        // the LLMAgent state fields, re-enable the LLM, and Reset() so the agent re-enters its native loop.
        public static void ApplyBrainMode(GameObject root, RobotBrainMode mode, BrainStateSnapshot? original, IModLogger logger)
        {
            if (mode == RobotBrainMode.Autonomous)
            {
                WakeBrain(root, original, logger);
                return;
            }

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component is Behaviour behaviour && IsNamed(component, "BehaviorTree"))
                {
                    behaviour.enabled = false;
                }
                else if (IsNamed(component, "LLMAgent"))
                {
                    MakeAgentDormant(component, logger);
                }
            }
        }

        private static void WakeBrain(GameObject root, BrainStateSnapshot? original, IModLogger logger)
        {
            try
            {
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component is Behaviour behaviour && IsNamed(component, "BehaviorTree"))
                    {
                        var enabled = true;
                        if (original != null)
                        {
                            foreach (var (tree, wasEnabled) in original.BehaviorTrees)
                            {
                                if (ReferenceEquals(tree, behaviour))
                                {
                                    enabled = wasEnabled;
                                    break;
                                }
                            }
                        }

                        behaviour.enabled = enabled;
                    }
                    else if (IsNamed(component, "LLMAgent"))
                    {
                        WakeAgent(component, original, logger);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug("RobotKit could not wake the LLM agent: " + ex.Message);
            }
        }

        private static void WakeAgent(Component agent, BrainStateSnapshot? original, IModLogger logger)
        {
            var type = agent.GetType();

            // Re-enable the LLM first (mirror of the dormant writes), preferring the captured original values.
            if (!SetFieldIfPresent(type, agent, "llmDisabled", original?.LlmDisabled ?? false))
            {
                var enableTestMode = type.GetMethod("EnableTestMode", InstanceFlags);
                try
                {
                    enableTestMode?.Invoke(agent, new object[] { true });
                }
                catch (Exception ex)
                {
                    logger.Debug("RobotKit could not re-enable the LLM: " + ex.Message);
                }
            }

            var initialState = original?.InitialState ?? FirstNonStandbyState();
            if (initialState != null)
            {
                SetFieldIfPresent(type, agent, "initialState", initialState);
                SetFieldIfPresent(type, agent, "state", original?.State ?? initialState);
            }

            // Best-effort: Reset() re-runs the agent's initial-state entry so it starts thinking again.
            Invoke(agent, "Reset", logger);
        }

        // The native default state to fall back to when no snapshot exists: the enum's first non-Standby value.
        private static object? FirstNonStandbyState()
        {
            if (AgentStateType == null)
            {
                return null;
            }

            try
            {
                foreach (var value in Enum.GetValues(AgentStateType))
                {
                    if (!string.Equals(value?.ToString(), "Standby", StringComparison.OrdinalIgnoreCase))
                    {
                        return value;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        // Is this object (prefab or live instance) a game robot? Cheap type-based check with a name-walk fallback;
        // used to keep robots out of prop catalogs.
        public static bool HasRobotBody(GameObject root)
        {
            if (root == null)
            {
                return false;
            }

            try
            {
                if (RobotBodyType != null)
                {
                    return root.GetComponentInChildren(RobotBodyType, true) != null;
                }

                return HasComponent(root, "RobotBody");
            }
            catch
            {
                return false;
            }
        }

        private static void MakeAgentDormant(Component agent, IModLogger logger)
        {
            try
            {
                var type = agent.GetType();

                // Standby initial state: LLMAgent.Awake calls Reset() which sets state = initialState, and enters
                // standby when initialState == Standby. Set both for safety (Awake may or may not have run yet).
                if (AgentStateType != null)
                {
                    var standby = Enum.Parse(AgentStateType, "Standby");
                    SetFieldIfPresent(type, agent, "initialState", standby);
                    SetFieldIfPresent(type, agent, "state", standby);
                }

                // Disable the actual LLM call without disabling the component.
                if (!SetFieldIfPresent(type, agent, "llmDisabled", true))
                {
                    var enableTestMode = type.GetMethod("EnableTestMode", InstanceFlags);
                    enableTestMode?.Invoke(agent, new object[] { false });
                }
            }
            catch (Exception ex)
            {
                logger.Debug("RobotKit could not make the LLM agent dormant: " + ex.Message);
            }
        }

        private static bool SetFieldIfPresent(Type type, object target, string fieldName, object value)
        {
            var field = type.GetField(fieldName, InstanceFlags);
            if (field == null)
            {
                return false;
            }

            field.SetValue(target, value);
            return true;
        }

        // Has the native death pipeline run on this robot? (Transformations.Kill adds a Killed marker component.)
        public static bool HasKilledComponent(GameObject root)
        {
            return root != null && HasComponent(root, "Killed");
        }

        // Deal damage through the robot's native Health component (drives the native hurt/death/ragdoll pipeline).
        public static bool ApplyDamage(GameObject root, float amount, RobotDamageType type, string source, IModLogger? logger)
        {
            var health = FindComponent(root, "Health");
            if (health == null || DamageTypeType == null)
            {
                return false;
            }

            try
            {
                var damageType = Enum.ToObject(DamageTypeType, (int)type);
                var method = health.GetType().GetMethods(InstanceFlags).FirstOrDefault(candidate =>
                    candidate.Name == "Damage" &&
                    candidate.GetParameters() is { Length: 3 } parameters &&
                    parameters[0].ParameterType == typeof(float) &&
                    parameters[1].ParameterType == DamageTypeType);
                if (method == null)
                {
                    return false;
                }

                method.Invoke(health, new[] { Mathf.Max(0f, amount), damageType, source });
                return true;
            }
            catch (Exception ex)
            {
                logger?.Debug("RobotKit Health.Damage failed: " + ex.Message);
                return false;
            }
        }

        // Best-effort facial emote via the native RobotBody.StartEmote(string, CancellationToken) (fire-and-forget).
        public static void StartEmote(GameObject root, string emojiShortcode, IModLogger? logger)
        {
            var body = FindComponent(root, "RobotBody");
            if (body == null)
            {
                return;
            }

            try
            {
                var method = body.GetType().GetMethods(InstanceFlags).FirstOrDefault(candidate =>
                    candidate.Name == "StartEmote" && candidate.GetParameters().Length == 2);
                method?.Invoke(body, new object[] { emojiShortcode ?? string.Empty, System.Threading.CancellationToken.None });
            }
            catch (Exception ex)
            {
                logger?.Debug("RobotKit StartEmote failed: " + ex.Message);
            }
        }

        // Resolve the RobotBody root GameObject from any component on (or under) a robot: prefer the Body/MaybeBody
        // property, then walk parents for a RobotBody, then fall back to the transform root.
        public static GameObject GetRobotBodyRoot(Component component)
        {
            var body = GetPropertyValue(component, "Body") as Component ??
                GetPropertyValue(component, "MaybeBody") as Component;
            if (body != null)
            {
                return body.gameObject;
            }

            var parentBody = component.GetComponentsInParent<Component>(true)
                .FirstOrDefault(candidate => IsNamed(candidate, "RobotBody"));
            if (parentBody != null)
            {
                return parentBody.gameObject;
            }

            return component.transform.root.gameObject;
        }

        public static bool HasComponentInParent(Component? component, params string[] typeNames)
        {
            if (component == null)
            {
                return false;
            }

            foreach (var candidate in component.GetComponentsInParent<Component>(true))
            {
                if (IsNamed(candidate, typeNames))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsNamed(Component? component, params string[] typeNames)
        {
            if (component == null)
            {
                return false;
            }

            var type = component.GetType();
            while (type != null)
            {
                if (typeNames.Any(name => string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                type = type.BaseType;
            }

            return false;
        }

        private static bool CanInvoke(MethodInfo method, string methodName, object[] arguments)
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
            {
                return false;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != arguments.Length)
            {
                return false;
            }

            for (var index = 0; index < parameters.Length; index++)
            {
                var argument = arguments[index];
                if (argument == null)
                {
                    continue;
                }

                if (!parameters[index].ParameterType.IsInstanceOfType(argument))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
