using System;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.GravityGun
{
    internal static class RobotGrabSupport
    {
        private static readonly string[] RobotComponentNames =
        {
            "RobotBody",
            "AgentHead",
            "LocomotionController",
            "RobotBodyPart",
            "LLMAgent"
        };

        public static bool TryCreateTarget(
            RaycastHit hit,
            GravityGunConfig config,
            IModLogger logger,
            out IGravityGunTarget? target)
        {
            target = null;
            if (hit.collider == null)
            {
                return false;
            }

            var robotComponent = FindRobotComponent(hit.collider);
            if (robotComponent == null)
            {
                return false;
            }

            var root = ResolveRobotRoot(robotComponent);
            var locomotion = FindLocomotion(root, robotComponent);
            if (locomotion != null)
            {
                InvokeNoArg(locomotion, "CancelWalking", logger);
                InvokeNoArg(locomotion, "DropHeldItemIfAny", logger);
                if (!InvokeNoArg(locomotion, "ForceRagdoll", logger))
                {
                    InvokeNoArg(locomotion, "EnableRagdoll", logger, true);
                }
            }

            var group = HeldRigidbodyGroup.Capture(FindDynamicBodies(root.transform), config, root.name);
            if (group != null)
            {
                target = new RobotRagdollHoldLock(group, locomotion, logger);
                logger.Debug("Gravity Gun acquired robot Rigidbody group: " + root.name);
                return true;
            }

            target = new RobotRagdollHoldLock(new HeldTransform(root.transform), locomotion, logger);
            logger.Debug("Gravity Gun acquired robot transform: " + root.name);
            return true;
        }

        private static Component? FindRobotComponent(Collider collider)
        {
            var components = collider.GetComponentsInParent<Component>(true);
            foreach (var name in RobotComponentNames)
            {
                var component = components.FirstOrDefault(c => c != null && c.GetType().Name == name);
                if (component != null)
                {
                    return component;
                }
            }

            return null;
        }

        private static Component ResolveRobotRoot(Component component)
        {
            var directBody = TryGetComponentProperty(component, "Body") ??
                TryGetComponentProperty(component, "MaybeBody");
            if (directBody != null)
            {
                return directBody;
            }

            var parentBody = component.GetComponentsInParent<Component>(true)
                .FirstOrDefault(c => c != null && c.GetType().Name == "RobotBody");
            return parentBody ?? component;
        }

        private static Component? FindLocomotion(Component root, Component original)
        {
            var locomotion = TryGetComponentProperty(root, "Locomotion");
            if (locomotion != null)
            {
                return locomotion;
            }

            locomotion = root.GetComponentsInChildren<Component>(true)
                .FirstOrDefault(c => c != null && c.GetType().Name == "LocomotionController");
            if (locomotion != null)
            {
                return locomotion;
            }

            return original.GetComponentsInParent<Component>(true)
                .FirstOrDefault(c => c != null && c.GetType().Name == "LocomotionController");
        }

        private static Component? TryGetComponentProperty(Component component, string propertyName)
        {
            try
            {
                var property = component.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return property?.GetValue(component, null) as Component;
            }
            catch
            {
                return null;
            }
        }

        private static bool InvokeNoArg(Component component, string methodName, IModLogger logger)
        {
            return InvokeNoArg(component, methodName, logger, null);
        }

        private static bool InvokeNoArg(Component component, string methodName, IModLogger logger, object? argument)
        {
            try
            {
                var methods = component.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name == methodName);
                foreach (var method in methods)
                {
                    var parameters = method.GetParameters();
                    if (argument == null && parameters.Length == 0)
                    {
                        method.Invoke(component, Array.Empty<object>());
                        return true;
                    }

                    if (argument != null &&
                        parameters.Length == 1 &&
                        parameters[0].ParameterType.IsInstanceOfType(argument))
                    {
                        method.Invoke(component, new[] { argument });
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug("Gravity Gun robot hook failed for " + methodName + ": " + ex.Message);
            }

            return false;
        }

        private static Rigidbody[] FindDynamicBodies(Transform root)
        {
            return root.GetComponentsInChildren<Rigidbody>(true)
                .Where(body => body != null && body.gameObject != null && !body.isKinematic)
                .ToArray();
        }
    }
}
