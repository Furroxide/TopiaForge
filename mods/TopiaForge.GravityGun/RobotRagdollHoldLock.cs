using System;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.GravityGun
{
    internal sealed class RobotRagdollHoldLock : IGravityGunTarget
    {
        private readonly IGravityGunTarget inner;
        private readonly Component? locomotion;
        private readonly IModLogger logger;
        private bool loggedFailure;

        public RobotRagdollHoldLock(IGravityGunTarget inner, Component? locomotion, IModLogger logger)
        {
            this.inner = inner;
            this.locomotion = locomotion;
            this.logger = logger;
            KeepRagdoll();
        }

        public string Name => inner.Name;

        public Vector3 Position => inner.Position;

        public bool IsAlive => inner.IsAlive;

        public void UpdateHold(Camera camera, float holdDistance, GravityGunConfig config, float deltaTime)
        {
            KeepRagdoll();
            inner.UpdateHold(camera, holdDistance, config, deltaTime);
        }

        public void Throw(Vector3 direction, GravityGunConfig config)
        {
            inner.Throw(direction, config);
        }

        public void Release()
        {
            inner.Release();
        }

        private void KeepRagdoll()
        {
            if (locomotion == null || locomotion.gameObject == null)
            {
                return;
            }

            try
            {
                if (IsCurrentState("Ragdoll"))
                {
                    return;
                }

                if (InvokeStateMethod("ForceSetState", "Ragdoll"))
                {
                    return;
                }

                if (InvokeNoArg("ForceRagdoll"))
                {
                    return;
                }

                InvokeBool("EnableRagdoll", true);
            }
            catch (Exception ex)
            {
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    logger.Debug("Gravity Gun could not lock robot ragdoll state: " + ex.Message);
                }
            }
        }

        private bool IsCurrentState(string stateName)
        {
            var property = locomotion!.GetType().GetProperty(
                "CurrentState",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var value = property?.GetValue(locomotion, null);
            return value != null && string.Equals(value.ToString(), stateName, StringComparison.OrdinalIgnoreCase);
        }

        private bool InvokeNoArg(string methodName)
        {
            var method = locomotion!.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 0);
            if (method == null)
            {
                return false;
            }

            method.Invoke(locomotion, Array.Empty<object>());
            return true;
        }

        private bool InvokeBool(string methodName, bool value)
        {
            var method = locomotion!.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m =>
                {
                    var parameters = m.GetParameters();
                    return m.Name == methodName && parameters.Length == 1 && parameters[0].ParameterType == typeof(bool);
                });
            if (method == null)
            {
                return false;
            }

            method.Invoke(locomotion, new object[] { value });
            return true;
        }

        private bool InvokeStateMethod(string methodName, string stateName)
        {
            var method = locomotion!.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m =>
                {
                    var parameters = m.GetParameters();
                    return m.Name == methodName && parameters.Length == 1 && parameters[0].ParameterType.IsEnum;
                });
            if (method == null)
            {
                return false;
            }

            var state = Enum.Parse(method.GetParameters()[0].ParameterType, stateName);
            method.Invoke(locomotion, new[] { state });
            return true;
        }
    }
}
