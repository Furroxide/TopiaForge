using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // Bridge to the game's OWN agent locomotion: instead of re-implementing pathfinding/following, a controlled
    // robot walks by calling the native WalkSession.Walk(AgentHead, ActionTarget, ...) — which pathfinds
    // (RoboPath.Pathfinder), follows (LocomotionController.FollowPath), repaths as a GameObject target moves,
    // retries when stuck, and drives the native walk animation. All access is reflection because GameCode is not
    // referenced; every entry point degrades to a safe default so a renamed/missing symbol never throws.
    internal static class LocomotionBridge
    {
        public enum WalkPoll
        {
            Pending,
            Done
        }

        public enum Gait
        {
            Walk,
            Run,
            Sprint
        }

        // Result of polling a reachability pathfind started by BeginPathfind.
        public enum PathPoll
        {
            Pending,
            Reachable,
            Unreachable
        }

        private const BindingFlags InstanceFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private const BindingFlags StaticFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static readonly Type? WalkSessionType = Type.GetType("WalkSession, GameCode", throwOnError: false);
        private static readonly Type? AgentHeadType = Type.GetType("AgentHead, GameCode", throwOnError: false);
        private static readonly Type? ActionTargetType = Type.GetType("ActionTarget, GameCode", throwOnError: false);
        private static readonly Type? LocomotionType = Type.GetType("LocomotionController, GameCode", throwOnError: false);
        private static readonly Type? PathfinderType = Type.GetType("RoboPath.Pathfinder, GameCode", throwOnError: false);
        private static readonly Type? GoalType = Type.GetType("RoboPath.Goal, GameCode", throwOnError: false);
        private static readonly Type? PathFindSettingsType = Type.GetType("RoboPath.PathFindSettings, GameCode", throwOnError: false);

        private static bool resolved;
        private static bool resolveOk;
        private static MethodInfo? walkMethod;               // static WalkSession.Walk(AgentHead, ActionTarget, TaskGraph, CancellationToken, bool, float, float, TimeSpan, Func<Vector3,bool>, bool, bool)
        private static MethodInfo? mostRelevantHead;         // static AgentHead.MostRelevantHead(GameObject)
        private static ConstructorInfo? actionTargetFromObject;   // ActionTarget(GameObject, string)
        private static ConstructorInfo? actionTargetFromPosition; // ActionTarget(Vector3, string)
        private static PropertyInfo? isInControlProp;        // LocomotionController.IsInControl
        private static MethodInfo? forceRagdollMethod;       // LocomotionController.ForceRagdoll()
        private static MethodInfo? receiveForceMethod;       // LocomotionController.ReceiveForce(Vector3, ForceMode)
        private static FieldInfo? walkSpeedField;            // LocomotionController.walkSpeed
        private static FieldInfo? runningSpeedField;         // LocomotionController.runningSpeed
        private static FieldInfo? sprintingSpeedField;       // LocomotionController.sprintingSpeed
        private static FieldInfo? turnSpeedField;            // LocomotionController.turnSpeed

        // Memoized from the first awaiter's runtime type. Load-bearing assumption: every BeginWalk goes through
        // the same WalkSession.Walk -> UniTask<ActionSuccess>, so all awaiters share one type. If a future
        // overload ever returns a differently-shaped awaiter, resolve these per-type instead.
        private static PropertyInfo? awaiterIsCompleted;     // UniTask<ActionSuccess>.Awaiter.IsCompleted
        private static MethodInfo? awaiterGetResult;         // UniTask<ActionSuccess>.Awaiter.GetResult()
        private static Component? cachedPathfinder;

        // Reachability/walkability reflection (RoboPath), resolved lazily on first use and independent of the walk
        // reflection above. Every entry point degrades to a safe default so a renamed/missing symbol never throws.
        private static bool pathfindResolved;
        private static bool pathfindOk;
        private static MethodInfo? pathfindStatic;           // static Pathfinder.Pathfind(Vector3, Goal, PathFindSettings, TaskGraph, CancellationToken, GameObject)
        private static MethodInfo? createSamplerMethod;      // Pathfinder.CreateSampler(PathFindSettings, Goal, GameObject) -> IPathSampler
        private static MethodInfo? getPathFindSettingsMethod; // LocomotionController.GetPathFindSettings()
        private static ConstructorInfo? goalFromPosition;    // Goal(Vector3, float, float, Func<Vector3,bool>)
        // The pathfind awaiter is UniTask<Path>.Awaiter — a DIFFERENT type than the walk awaiter, so memoize its
        // members separately rather than reusing awaiterIsCompleted/awaiterGetResult above.
        private static PropertyInfo? pathAwaiterIsCompleted;
        private static MethodInfo? pathAwaiterGetResult;
        private static MethodInfo? sampleAtMethod;           // IPathSampler.SampleAt(Vector3, out Collider)
        private static FieldInfo? sampleHitField;            // IPathSampler.Sample.hit (Nullable<Hit>)
        private static FieldInfo? hitPosField;               // IPathSampler.Sample.Hit.pos
        private static FieldInfo? pathCompleteField;         // RoboPath.Path.complete

        private static bool EnsureReflection()
        {
            if (resolved)
            {
                return resolveOk;
            }

            resolved = true;
            try
            {
                if (WalkSessionType == null || AgentHeadType == null || ActionTargetType == null || LocomotionType == null)
                {
                    return false;
                }

                // The static convenience overload of Walk (the one that takes the AgentHead as its first arg).
                walkMethod = WalkSessionType.GetMethods(StaticFlags).FirstOrDefault(candidate =>
                    candidate.Name == "Walk" &&
                    candidate.GetParameters() is { Length: 11 } parameters &&
                    parameters[0].ParameterType == AgentHeadType);

                mostRelevantHead = AgentHeadType.GetMethod(
                    "MostRelevantHead", StaticFlags, null, new[] { typeof(GameObject) }, null);

                actionTargetFromObject = ActionTargetType.GetConstructor(new[] { typeof(GameObject), typeof(string) });
                actionTargetFromPosition = ActionTargetType.GetConstructor(new[] { typeof(Vector3), typeof(string) });

                isInControlProp = LocomotionType.GetProperty("IsInControl", InstanceFlags);
                forceRagdollMethod = LocomotionType.GetMethod("ForceRagdoll", InstanceFlags, null, Type.EmptyTypes, null);
                receiveForceMethod = LocomotionType.GetMethod(
                    "ReceiveForce", InstanceFlags, null, new[] { typeof(Vector3), typeof(ForceMode) }, null);
                walkSpeedField = LocomotionType.GetField("walkSpeed", InstanceFlags);
                runningSpeedField = LocomotionType.GetField("runningSpeed", InstanceFlags);
                sprintingSpeedField = LocomotionType.GetField("sprintingSpeed", InstanceFlags);
                turnSpeedField = LocomotionType.GetField("turnSpeed", InstanceFlags);

                resolveOk = walkMethod != null && actionTargetFromObject != null && actionTargetFromPosition != null;
            }
            catch (Exception ex)
            {
                resolveOk = false;
                RobotKitDiagnostics.ReportOnce("locomotion contract discovery", ex);
            }

            return resolveOk;
        }

        public static bool LocomotionAvailable()
        {
            return EnsureReflection();
        }

        // Drop the per-scene pathfinder cache so a torn-down scene's stale (advisory) reference does not linger
        // into the next scene before Unity's fake-null would otherwise clear it.
        public static void ResetSceneCache()
        {
            cachedPathfinder = null;
        }

        // Is the game's pathfinder singleton present in the current scene? Cached on the live component, which
        // Unity reports as destroyed (fake-null) after a scene change, so a new scene re-scans automatically.
        public static bool NavAvailable()
        {
            if (PathfinderType == null)
            {
                return false;
            }

            if (cachedPathfinder != null)
            {
                return true;
            }

            try
            {
                foreach (var component in UnityEngine.Object.FindObjectsByType<Component>(FindObjectsSortMode.None))
                {
                    if (GameReflection.IsNamed(component, "Pathfinder"))
                    {
                        cachedPathfinder = component;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("pathfinder scene discovery", ex);
            }

            return false;
        }

        private static bool EnsurePathfindReflection()
        {
            if (pathfindResolved)
            {
                return pathfindOk;
            }

            pathfindResolved = true;
            try
            {
                if (PathfinderType == null || GoalType == null)
                {
                    return false;
                }

                // static Pathfinder.Pathfind(Vector3 start, Goal goal, PathFindSettings settings, TaskGraph tg,
                //                            CancellationToken ct, GameObject ignoreObject = null)
                pathfindStatic = PathfinderType.GetMethods(StaticFlags).FirstOrDefault(candidate =>
                    candidate.Name == "Pathfind" &&
                    candidate.GetParameters() is { Length: 6 } parameters &&
                    parameters[0].ParameterType == typeof(Vector3) &&
                    parameters[1].ParameterType == GoalType);

                // instance Pathfinder.CreateSampler(PathFindSettings settings, Goal goal, GameObject ignoreObject)
                createSamplerMethod = PathfinderType.GetMethods(InstanceFlags).FirstOrDefault(candidate =>
                    candidate.Name == "CreateSampler" &&
                    candidate.GetParameters() is { Length: 3 } parameters &&
                    parameters[1].ParameterType == GoalType);

                // Goal(Vector3 position, float minStopDistance, float maxGoalDistance, Func<Vector3,bool> goalFilter)
                goalFromPosition = GoalType.GetConstructor(
                    new[] { typeof(Vector3), typeof(float), typeof(float), typeof(Func<Vector3, bool>) });

                if (LocomotionType != null)
                {
                    getPathFindSettingsMethod = LocomotionType.GetMethod(
                        "GetPathFindSettings", InstanceFlags, null, Type.EmptyTypes, null);
                }

                pathfindOk = pathfindStatic != null && goalFromPosition != null;
            }
            catch (Exception ex)
            {
                pathfindOk = false;
                RobotKitDiagnostics.ReportOnce("pathfinder contract discovery", ex);
            }

            return pathfindOk;
        }

        // Read the designer-tuned PathFindSettings off a robot (or prefab) LocomotionController, boxed for reuse.
        // GetPathFindSettings is a pure serialized-field read, so this is safe on a disabled/incubated component.
        // Returns null if the symbol is absent; callers fall back to BuildDefaultSettings.
        public static object? GetPathFindSettings(GameObject robot)
        {
            if (!EnsurePathfindReflection() || getPathFindSettingsMethod == null || LocomotionType == null || robot == null)
            {
                return null;
            }

            try
            {
                var locomotion = robot.GetComponentInChildren(LocomotionType, true);
                return locomotion != null ? getPathFindSettingsMethod.Invoke(locomotion, Array.Empty<object>()) : null;
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("pathfinder settings read", ex);
                return null;
            }
        }

        // Build a sensible PathFindSettings (boxed) when none could be read off a robot — mirrors the game's
        // LocomotionController serialized defaults so reachability still uses a realistic agent footprint.
        private static object? BuildDefaultSettings()
        {
            if (PathFindSettingsType == null)
            {
                return null;
            }

            try
            {
                var box = Activator.CreateInstance(PathFindSettingsType);
                SetField(box, "maxYStep", 0.2f);
                SetField(box, "maxSlopeAngle", 25f);
                SetField(box, "agentRadius", 0.25f);
                SetField(box, "agentHeight", 1.8f);
                SetField(box, "minObstacleMass", 20f);
                SetField(box, "maxSamples", (uint)5000);
                SetField(box, "priorityClass", 0);
                SetField(box, "ignoreGaps", false);
                SetField(box, "edgeProximityPenalty", 0f);
                SetField(box, "layerMask", -1);
                SetField(box, "ignoreStatics", false);
                return box;
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("default pathfinder settings construction", ex);
                return null;
            }

            void SetField(object? target, string name, object value)
            {
                if (target != null)
                {
                    PathFindSettingsType?.GetField(name)?.SetValue(target, value);
                }
            }
        }

        private static object? ResolveSettings(object? settingsBox)
        {
            return settingsBox ?? BuildDefaultSettings();
        }

        // Box a RoboPath.Goal for a fixed world position (no goal GameObject, so the walkability sampler never
        // reports "inside goal"). minStopDistance is how close to the goal counts as reaching it.
        private static object? BuildPositionGoal(Vector3 position, float minStopDistance)
        {
            if (goalFromPosition == null)
            {
                return null;
            }

            try
            {
                return goalFromPosition.Invoke(new object?[] { position, minStopDistance, 0f, null });
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("pathfinder goal construction", ex);
                return null;
            }
        }

        // Create a one-off walkability sampler (RaycastedGraph) over the native grid for the given agent footprint.
        // Reused across many SampleWalkable calls in one spawn search; returns null if the symbol is unavailable.
        public static object? CreateWalkabilitySampler(object? settingsBox)
        {
            if (!EnsurePathfindReflection() || createSamplerMethod == null || !NavAvailable() || cachedPathfinder == null)
            {
                return null;
            }

            var settings = ResolveSettings(settingsBox);
            var goal = BuildPositionGoal(Vector3.zero, 0.1f);
            if (settings == null || goal == null)
            {
                return null;
            }

            try
            {
                return createSamplerMethod.Invoke(cachedPathfinder, new object?[] { settings, goal, null });
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("walkability sampler construction", ex);
                return null;
            }
        }

        // Synchronously test whether 'groundPoint' sits on a walkable navigation cell for the sampler's agent
        // footprint, snapping to the grid-sampled ground on success. Catches steep slopes, gaps, and cells occupied
        // by static/dynamic obstacles — things a bare downward raycast cannot tell apart from valid ground.
        public static bool SampleWalkable(object? sampler, Vector3 groundPoint, out Vector3 snapped)
        {
            snapped = groundPoint;
            if (sampler == null)
            {
                return false;
            }

            try
            {
                sampleAtMethod ??= sampler.GetType().GetMethod(
                    "SampleAt", BindingFlags.Public | BindingFlags.Instance);
                if (sampleAtMethod == null)
                {
                    return false;
                }

                var args = new object?[] { groundPoint, null };
                var sample = sampleAtMethod.Invoke(sampler, args);
                if (sample == null)
                {
                    return false;
                }

                sampleHitField ??= sample.GetType().GetField("hit", BindingFlags.Public | BindingFlags.Instance);
                // A Nullable<Hit> field reflects as null when empty, or a boxed Hit when it has a value.
                var hit = sampleHitField?.GetValue(sample);
                if (hit == null)
                {
                    return false;
                }

                hitPosField ??= hit.GetType().GetField("pos", BindingFlags.Public | BindingFlags.Instance);
                if (hitPosField?.GetValue(hit) is Vector3 pos)
                {
                    snapped = pos;
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("walkability sampling", ex);
                return false;
            }
        }

        // Begin a one-shot reachability pathfind from 'from' to 'to'. Returns the boxed UniTask<Path> awaiter to
        // poll on later frames with PollPathfind, or null if it could not start. Must be called on the main thread
        // (the native pathfinder runs on the Unity player loop).
        public static object? BeginPathfind(
            Vector3 from, Vector3 to, float goalMinStop, object? settingsBox, CancellationToken ct)
        {
            if (!EnsurePathfindReflection() || pathfindStatic == null || !NavAvailable())
            {
                return null;
            }

            var settings = ResolveSettings(settingsBox);
            var goal = BuildPositionGoal(to, goalMinStop);
            if (settings == null || goal == null)
            {
                return null;
            }

            try
            {
                // (start, goal, settings, taskGraph:null, ct, ignoreObject:null)
                var task = pathfindStatic.Invoke(null, new object?[] { from, goal, settings, null, ct, null });
                if (task == null)
                {
                    return null;
                }

                var getAwaiter = task.GetType().GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance);
                return getAwaiter?.Invoke(task, null);
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("pathfind dispatch", ex);
                return null;
            }
        }

        // Poll a reachability pathfind started by BeginPathfind. Pending until the native pathfind completes; on
        // completion, Reachable iff a complete path was found (path != null && path.complete). Cancellation or any
        // fault is treated as Unreachable.
        public static PathPoll PollPathfind(object awaiter)
        {
            try
            {
                var awaiterType = awaiter.GetType();
                pathAwaiterIsCompleted ??= awaiterType.GetProperty("IsCompleted", BindingFlags.Public | BindingFlags.Instance);
                if (pathAwaiterIsCompleted == null)
                {
                    return PathPoll.Unreachable;
                }

                if (pathAwaiterIsCompleted.GetValue(awaiter) is not bool completed || !completed)
                {
                    return PathPoll.Pending;
                }

                pathAwaiterGetResult ??= awaiterType.GetMethod("GetResult", BindingFlags.Public | BindingFlags.Instance);
                object? path;
                try
                {
                    path = pathAwaiterGetResult?.Invoke(awaiter, null);
                }
                catch (Exception ex)
                {
                    RobotKitDiagnostics.ReportOnce("pathfind completion", ex);
                    return PathPoll.Unreachable; // cancellation / fault
                }

                if (path == null)
                {
                    return PathPoll.Unreachable;
                }

                pathCompleteField ??= path.GetType().GetField("complete", BindingFlags.Public | BindingFlags.Instance);
                return pathCompleteField?.GetValue(path) is true ? PathPoll.Reachable : PathPoll.Unreachable;
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("pathfind result inspection", ex);
                return PathPoll.Unreachable;
            }
        }

        // Resolve the AgentHead that drives this robot (needed as the WalkSession subject). Prefer the native
        public static object? ResolveHead(GameObject robot)
        {
            if (!EnsureReflection())
            {
                return null;
            }

            try
            {
                if (mostRelevantHead != null && mostRelevantHead.Invoke(null, new object[] { robot }) is { } head)
                {
                    return head;
                }
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("native agent-head resolution", ex);
            }

            return GameReflection.FindComponent(robot, "AgentHead");
        }

        // True only when the robot's LocomotionController is in its in-control (AgentSync) state, which WalkSession
        // requires; while ragdolled/getting up/falling this is false and we must not start a walk.
        public static bool IsInControl(GameObject robot)
        {
            if (!EnsureReflection() || isInControlProp == null || LocomotionType == null)
            {
                return false;
            }

            try
            {
                var locomotion = robot.GetComponentInChildren(LocomotionType, true);
                return locomotion != null && isInControlProp.GetValue(locomotion) is true;
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("locomotion control-state read", ex);
                return false;
            }
        }

        // Begin a native walk to either a live GameObject (tracked + repathed as it moves) or a fixed position.
        // Returns the boxed UniTask<ActionSuccess> awaiter to poll on later frames, or null if it could not start.
        public static object? BeginWalk(
            object head,
            GameObject? targetObject,
            Vector3 targetPosition,
            float minStopDistance,
            Gait gait,
            CancellationToken ct)
        {
            if (!EnsureReflection() || walkMethod == null)
            {
                return null;
            }

            try
            {
                var target = BuildActionTarget(targetObject, targetPosition);
                if (target == null)
                {
                    return null;
                }

                var runToGoal = gait != Gait.Walk;
                var sprintToGoal = gait == Gait.Sprint;
                var args = new object?[]
                {
                    head,                 // AgentHead
                    target,               // ActionTarget (boxed struct)
                    null,                 // TaskGraph
                    ct,                   // CancellationToken
                    true,                 // quiet (no chat bubbles / lore side effects)
                    minStopDistance,      // minStopDistance
                    0f,                   // maxGoalDistance
                    TimeSpan.Zero,        // waitAtEnd
                    null,                 // goalFilter
                    runToGoal,            // runToGoal
                    sprintToGoal          // sprintToGoal
                };

                var task = walkMethod.Invoke(null, args);
                if (task == null)
                {
                    return null;
                }

                var getAwaiter = task.GetType().GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance);
                return getAwaiter?.Invoke(task, null);
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("native walk dispatch", ex);
                return null;
            }
        }

        private static object? BuildActionTarget(GameObject? targetObject, Vector3 targetPosition)
        {
            try
            {
                if (targetObject != null && actionTargetFromObject != null)
                {
                    return actionTargetFromObject.Invoke(new object[] { targetObject, "target" });
                }

                return actionTargetFromPosition?.Invoke(new object[] { targetPosition, "target" });
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("walk target construction", ex);
                return null;
            }
        }

        // Poll a walk started by BeginWalk. Pending until the native walk task finishes (success, cancellation,
        // retries exhausted, or any fault are all treated as Done — the caller decides whether to restart).
        public static WalkPoll PollWalk(object awaiter)
        {
            try
            {
                var awaiterType = awaiter.GetType();
                awaiterIsCompleted ??= awaiterType.GetProperty("IsCompleted", BindingFlags.Public | BindingFlags.Instance);
                if (awaiterIsCompleted == null)
                {
                    return WalkPoll.Done;
                }

                if (awaiterIsCompleted.GetValue(awaiter) is not bool completed || !completed)
                {
                    return WalkPoll.Pending;
                }

                // UniTask sources are pooled/versioned: call GetResult exactly once, then drop the awaiter.
                awaiterGetResult ??= awaiterType.GetMethod("GetResult", BindingFlags.Public | BindingFlags.Instance);
                try
                {
                    awaiterGetResult?.Invoke(awaiter, null);
                }
                catch (Exception ex)
                {
                    // Cancellation/retry/fault all complete the walk; the caller restarts as appropriate.
                    RobotKitDiagnostics.ReportOnce("native walk completion", ex);
                }

                return WalkPoll.Done;
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("walk result inspection", ex);
                return WalkPoll.Done;
            }
        }

        // Best-effort override of the native gait speed (the field matching the active gait) and turn speed.
        // Zero leaves the prefab's serialized value untouched.
        public static void ApplySpeeds(GameObject robot, Gait gait, float moveSpeed, float turnSpeed)
        {
            if (!EnsureReflection() || LocomotionType == null)
            {
                return;
            }

            try
            {
                var locomotion = robot.GetComponentInChildren(LocomotionType, true);
                if (locomotion == null)
                {
                    return;
                }

                if (moveSpeed > 0f)
                {
                    var field = gait switch
                    {
                        Gait.Walk => walkSpeedField,
                        Gait.Sprint => sprintingSpeedField,
                        _ => runningSpeedField
                    };
                    field?.SetValue(locomotion, moveSpeed);
                }

                if (turnSpeed > 0f)
                {
                    turnSpeedField?.SetValue(locomotion, turnSpeed);
                }
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("locomotion speed override", ex);
            }
        }

        public static void ForceRagdoll(GameObject robot)
        {
            InvokeOnLocomotion(robot, locomotion => forceRagdollMethod?.Invoke(locomotion, Array.Empty<object>()));
        }

        public static void ReceiveForce(GameObject robot, Vector3 force)
        {
            InvokeOnLocomotion(robot, locomotion =>
                receiveForceMethod?.Invoke(locomotion, new object[] { force, ForceMode.Impulse }));
        }

        private static void InvokeOnLocomotion(GameObject robot, Action<Component> action)
        {
            if (!EnsureReflection() || LocomotionType == null)
            {
                return;
            }

            try
            {
                var locomotion = robot.GetComponentInChildren(LocomotionType, true);
                if (locomotion != null)
                {
                    action(locomotion);
                }
            }
            catch (Exception ex)
            {
                RobotKitDiagnostics.ReportOnce("locomotion action", ex);
            }
        }
    }
}
