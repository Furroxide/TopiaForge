using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.RobotKit;

// A signature-only stand-in for the native result; no game implementation is reproduced.
internal sealed class ActionSuccess
{
    public bool ShouldIgnore { get; set; }
}

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotNativeNavigationTests
    {
        private static int assertions;

        internal static void Run()
        {
            RobotComposedSceneTests.Run();
            RobotNativeDriverTests.Run();
            assertions = 0;
            foreach (var distance in new[] { 0.771f, 0.793f, 0.805f })
            {
                var awaiter = new Awaiter();
                Check(NativeWalkCompletion.Poll(awaiter, false) == NativeWalkCompletion.Result.Pending,
                    "an unfinished native walk stays pending");
                Check(awaiter.Reads == 0, "pending sources are never consumed");
                Check(!NativeWalkCompletion.HasArrived(false, distance, 0.75f, NativeWalkCompletion.Result.Pending),
                    "the author stop distance is not inflated to cover sampled endpoints");
                awaiter.IsCompleted = true;
                var result = NativeWalkCompletion.Poll(awaiter, false);
                Check(result == NativeWalkCompletion.Result.Reached && awaiter.Reads == 1,
                    "native success must survive adapter polling and consume the source once");
                Check(NativeWalkCompletion.HasArrived(false, distance, 0.75f, result),
                    "successful native arrival at the observed endpoint completes the order");
                Check(NativeWalkCompletion.HasArrived(true, distance, 0.75f, NativeWalkCompletion.Result.Pending),
                    "a consumed arrival stays reached without restarting native navigation");
            }

            var cancelled = new Awaiter { IsCompleted = true };
            Check(NativeWalkCompletion.Poll(cancelled, true) == NativeWalkCompletion.Result.Failed && cancelled.Reads == 1,
                "a replaced, stopped or reconfigured intent drains but never adopts stale success");
            var ignored = new Awaiter { IsCompleted = true, Ignore = true };
            Check(NativeWalkCompletion.Poll(ignored, false) == NativeWalkCompletion.Result.Failed,
                "native Ignore is not arrival");
            var fault = new Awaiter { IsCompleted = true, Throw = true };
            Check(NativeWalkCompletion.Poll(fault, false) == NativeWalkCompletion.Result.Failed && fault.Reads == 1,
                "faults are consumed once and remain retryable failures");
            Check(NativeWalkCompletion.Poll(new object(), false) == NativeWalkCompletion.Result.Failed,
                "missing awaiter bindings fail closed");
            Check(NativeWalkCompletion.Poll(new UnknownResultAwaiter(), false) == NativeWalkCompletion.Result.Failed,
                "a completed task of another result type cannot claim arrival");
            Check(NativeWalkCompletion.Poll(new Awaiter { IsCompleted = true }, false) == NativeWalkCompletion.Result.Reached,
                "reflection caches recover after another awaiter shape");
            Check(!NativeWalkCompletion.HasArrived(false, 0.805f, 0.75f, NativeWalkCompletion.Result.Failed),
                "an unreachable walk outside the threshold does not arrive");
            Check(NativeWalkCompletion.HasArrived(false, 0.092f, 0, NativeWalkCompletion.Result.Pending),
                "the observed default-distance arrival remains supported");
            Check(!NativeWalkCompletion.HasArrived(true, 1.251f, 0.75f, NativeWalkCompletion.Result.Pending),
                "a target leaving the existing release boundary resumes pursuit");

            var robot = new RobotTypeDescriptor("robot01", "Robot01");
            var cop = new RobotTypeDescriptor("copbot", "CopBot");
            var catalog = new[] { robot, cop };
            Check(ReferenceEquals(RobotPrefabSelection.Select(catalog, null, r => r.Id), robot), "omitted type retains the default");
            Check(ReferenceEquals(RobotPrefabSelection.Select(catalog, "COPBOT", r => r.Id), cop), "explicit available type is exact ignoring case");
            Check(RobotPrefabSelection.Select(catalog, "unavailable-type", r => r.Id) == null, "missing type never substitutes Robot01");
            Check(RobotPrefabSelection.Select(Array.Empty<RobotTypeDescriptor>(), null, r => r.Id) == null, "an empty catalog cannot spawn");
            using var lifetime = new FakeModLifetime();
            var service = new FakeRobotAgentService(lifetime) { RobotTypes = catalog };
            Check(service.Spawn(new RobotAgentSpawnRequest(Vec3.Zero, robotTypeId: "unavailable-type")).ErrorCode == ModErrorCode.Unavailable
                && service.ActiveAgents.Count == 0, "testing service exposes unavailable cast without leaking a body");
            Check(service.Spawn(new RobotAgentSpawnRequest(Vec3.Zero, robotTypeId: "COPBOT")).Succeeded,
                "testing service accepts the same case-insensitive descriptor");
            Check(service.Spawn(new RobotAgentSpawnRequest(Vec3.Zero)).Succeeded, "testing service keeps the omitted-type default");
            lifetime.Dispose();
            Check(service.ActiveAgents.Count == 0, "typed and default spawns retain lifetime cleanup");
            Console.WriteLine("Robot native navigation: " + assertions + " assertions passed.");
        }

        private static void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Awaiter
        {
            public bool IsCompleted { get; set; }
            public bool Ignore { get; set; }
            public bool Throw { get; set; }
            public int Reads { get; private set; }
            public ActionSuccess GetResult()
            {
                if (++Reads != 1) throw new InvalidOperationException("Source consumed twice.");
                if (Throw) throw new OperationCanceledException();
                return new ActionSuccess { ShouldIgnore = Ignore };
            }
        }

        private sealed class UnknownResultAwaiter
        {
            public bool IsCompleted => true;
            public object GetResult() => new object();
        }
    }
}
