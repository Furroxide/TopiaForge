using System;
using System.Reflection;

namespace TopiaForge.Mods.GameBridge
{
    /// <summary>
    /// Shared clean-room helper for the source argument of the game's health mutations. Robotopia build 2409 took
    /// a diagnostic string; build 2478 takes the <c>GameObject</c> that caused the change. TopiaForge callers carry
    /// only a label, so a build with an object source receives no source object. Source-linked into the loader
    /// and RobotKit so the rule lives in one place. Unity-free on purpose: callers pass the GameObject type.
    /// </summary>
    internal static class NativeDamageSource
    {
        /// <summary>
        /// Selects the <paramref name="name"/> overload whose leading parameters are exactly
        /// <paramref name="leading"/> and whose last parameter is a supported source. A string source wins when a
        /// build exposes both shapes, because it keeps the diagnostic label.
        /// </summary>
        internal static MethodInfo? Select(Type declaring, string name, BindingFlags flags, Type sourceObjectType,
            params Type[] leading)
        {
            MethodInfo? labelled = null;
            MethodInfo? sourced = null;
            foreach (var method in declaring.GetMethods(flags))
            {
                if (method.Name != name || method.IsGenericMethod) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != leading.Length + 1) continue;
                var matches = true;
                for (var index = 0; index < leading.Length; index++)
                    if (parameters[index].ParameterType != leading[index]) { matches = false; break; }
                if (!matches) continue;
                var source = parameters[leading.Length].ParameterType;
                if (source == typeof(string)) labelled ??= method;
                else if (source == sourceObjectType) sourced ??= method;
            }
            return labelled ?? sourced;
        }

        /// <summary>The argument for a selected method's source parameter: the label, or no source object.</summary>
        internal static object? Argument(MethodInfo method, string source)
        {
            var parameters = method.GetParameters();
            return parameters[parameters.Length - 1].ParameterType == typeof(string) ? source : null;
        }
    }
}
