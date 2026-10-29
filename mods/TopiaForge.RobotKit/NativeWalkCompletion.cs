using System;
using System.Reflection;

namespace TopiaForge.RobotKit
{
    // Native success is an arrival signal, not just an awaiter becoming complete. Ignore, cancellation,
    // faults and changed bindings must never turn an unreachable order into a successful one.
    internal static class NativeWalkCompletion
    {
        internal enum Result { Pending, Reached, Failed }

        private static Type? cachedAwaiterType;
        private static PropertyInfo? isCompleted;
        private static MethodInfo? getResult;
        private static Type? cachedResultType;
        private static PropertyInfo? shouldIgnore;

        internal static Result Poll(object awaiter, bool cancelled)
        {
            try
            {
                var type = awaiter.GetType();
                if (cachedAwaiterType != type)
                {
                    cachedAwaiterType = type;
                    isCompleted = type.GetProperty("IsCompleted", BindingFlags.Public | BindingFlags.Instance);
                    getResult = type.GetMethod("GetResult", BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                }
                if (isCompleted?.PropertyType != typeof(bool) || getResult == null) return Result.Failed;
                if (isCompleted.GetValue(awaiter) is not true) return Result.Pending;

                // Consume a pooled/versioned source exactly once, even when its intent was cancelled.
                var value = getResult.Invoke(awaiter, null);
                if (cancelled || value == null) return Result.Failed;
                var resultType = value.GetType();
                if (cachedResultType != resultType)
                {
                    cachedResultType = resultType;
                    shouldIgnore = resultType.FullName == "ActionSuccess"
                        ? resultType.GetProperty("ShouldIgnore", BindingFlags.Public | BindingFlags.Instance)
                        : null;
                }
                return shouldIgnore?.PropertyType == typeof(bool) && shouldIgnore.GetValue(value) is false
                    ? Result.Reached : Result.Failed;
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("native walk completion", ex);
                return Result.Failed;
            }
        }

        internal static bool HasArrived(bool reached, float distance, float stopDistance, Result completion)
        {
            var threshold = Math.Max(0.1f, stopDistance);
            // Preserve the existing release hysteresis. It is not an extra author arrival tolerance.
            if (reached) return distance <= threshold + 0.5f;
            return completion == Result.Reached || distance <= threshold;
        }
    }
}
