using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace TopiaForge.ModManager
{
    // Only the private native group shape varies; routing, results and lease ownership remain in the host.
    internal sealed class NativeRobotSpeechBindings
    {
        internal MethodInfo Simple { get; }
        internal MethodInfo Named { get; }
        private readonly MethodInfo fromAssets;
        private readonly Type asset;
        private NativeRobotSpeechBindings(MethodInfo simple, MethodInfo named, MethodInfo fromAssets, Type asset)
        {
            Simple = simple;
            Named = named;
            this.fromAssets = fromAssets;
            this.asset = asset;
        }
        internal static NativeRobotSpeechBindings Resolve(Type head, Type graph, Type clip, Type simpleResult, Type namedResult)
        {
            foreach (var prefix in new[] { "Keyword", "Hint" })
            {
                var group = head.Assembly.GetType(prefix + "Group", false);
                if (group == null) continue;
                var factories = group.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method =>
                {
                    var parameters = method.GetParameters();
                    return method.Name == "FromAssets" && !method.IsGenericMethod && method.ReturnType == group && parameters.Length == 1
                        && parameters[0].ParameterType.IsGenericType && parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                        && parameters[0].ParameterType.GetGenericArguments()[0].FullName == prefix + "Asset";
                }).ToArray();
                if (factories.Length != 1) continue;
                var factory = factories[0];
                var asset = factory.GetParameters()[0].ParameterType.GetGenericArguments()[0];
                var simple = head.GetMethod("Say", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(string), group, graph, typeof(CancellationToken), clip, typeof(bool) }, null);
                var named = head.GetMethod("Say", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(string), group, graph, typeof(CancellationToken), clip, typeof(bool) }, null);
                if (factory.ReturnType == group && simple?.ReturnType == simpleResult && named?.ReturnType == namedResult
                    && !simple.IsGenericMethod && !named.IsGenericMethod)
                    return new NativeRobotSpeechBindings(simple!, named!, factory, asset);
            }
            throw new InvalidOperationException("Native robot speech bindings are unavailable.");
        }
        internal object InvokeNamed(object head, string speaker, string text, object graph, CancellationToken token)
        {
            try
            {
                var empty = fromAssets.Invoke(null, new object[] { Array.CreateInstance(asset, 0) });
                return Named.Invoke(head, new object?[] { speaker, text, empty, graph, token, null, false })
                    ?? throw new InvalidOperationException("Native robot speech returned no operation.");
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
