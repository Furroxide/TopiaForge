using System;
using System.Reflection;

namespace TopiaForge.ModManager
{
    internal static class NativeWorldReflection
    {
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

        internal static MethodInfo? CheckpointLoader(Type loader, Type checkpoint) =>
            ExactMethod(loader, "LoadSceneImpl", Static, typeof(bool), checkpoint);

        internal static NativeAwaiterMethods? Awaiter(Type awaitable)
        {
            var getAwaiter = ExactMethod(awaitable, "GetAwaiter", Instance);
            if (getAwaiter == null || getAwaiter.ReturnType == typeof(void)) return null;
            var type = getAwaiter.ReturnType;
            var getResult = ExactMethod(type, "GetResult", Instance);
            var continuation = ExactMethod(type, "OnCompleted", Instance, typeof(Action));
            if (getResult == null || continuation?.ReturnType != typeof(void)) return null;
            PropertyInfo? completed = null;
            foreach (var candidate in type.GetProperties(Instance))
            {
                if (candidate.Name != "IsCompleted" || candidate.PropertyType != typeof(bool)
                    || candidate.GetIndexParameters().Length != 0 || candidate.GetGetMethod() == null) continue;
                if (completed != null) return null;
                completed = candidate;
            }
            return completed == null ? null : new NativeAwaiterMethods(getAwaiter, getResult, completed, continuation);
        }

        private static MethodInfo? ExactMethod(Type type, string name, BindingFlags flags, params Type[] parameters)
        {
            MethodInfo? selected = null;
            foreach (var method in type.GetMethods(flags))
            {
                if (method.Name != name || method.IsGenericMethod || method.ContainsGenericParameters) continue;
                var actual = method.GetParameters();
                if (actual.Length != parameters.Length) continue;
                var matches = true;
                for (var index = 0; index < actual.Length; index++)
                    if (actual[index].ParameterType != parameters[index]) { matches = false; break; }
                if (!matches) continue;
                if (selected != null) return null;
                selected = method;
            }
            return selected;
        }
    }

    internal sealed class NativeAwaiterMethods
    {
        internal NativeAwaiterMethods(MethodInfo getAwaiter, MethodInfo getResult, PropertyInfo completed, MethodInfo continuation)
        { GetAwaiter = getAwaiter; GetResult = getResult; IsCompleted = completed; OnCompleted = continuation; }
        internal MethodInfo GetAwaiter { get; }
        internal MethodInfo GetResult { get; }
        internal PropertyInfo IsCompleted { get; }
        internal MethodInfo OnCompleted { get; }
    }
}
