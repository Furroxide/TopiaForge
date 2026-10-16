using System.Linq;
using TopiaForge.SandboxAcceptance.Native;

// Bounded operations are accepted for the prepared cycle only while the observer expects exactly that action.
internal static class OperationGateTests
{
    internal static void Run()
    {
        var scenarios = new SandboxNativeScenarios();
        foreach (var operation in SandboxWireOperations.Bounded)
            Harness.Reject(() => SandboxWireOperations.Authorize(scenarios, "routing", 2, operation), "no active scenario accepts " + operation);
        var scene = new SyntheticScene();
        scenarios.Begin("graph-rollback", 1, 10, 100, scene.Snapshot());
        Harness.Check(scenarios.ExpectedAction == "open", "graph-rollback c1 starts with open");
        foreach (var operation in SandboxWireOperations.Bounded)
            Harness.Reject(() => SandboxWireOperations.Authorize(scenarios, "graph-rollback", 1, operation), "refused before its step: " + operation);
        scene.Open(); scenarios.Advance("graph-rollback", 1, 11, 200, scene.Snapshot());
        scene.AddEntity(); scenarios.Advance("graph-rollback", 1, 12, 300, scene.Snapshot());
        Harness.Check(scenarios.ExpectedAction == "control-cue", "graph-rollback c1 reaches the control cue request");
        SandboxWireOperations.Authorize(scenarios, "graph-rollback", 1, "control-cue");
        Harness.Check(true, "expected operation accepted");
        foreach (var operation in SandboxWireOperations.Bounded.Where(o => o != "control-cue"))
            Harness.Reject(() => SandboxWireOperations.Authorize(scenarios, "graph-rollback", 1, operation), "unexpected operation refused: " + operation);
        Harness.Reject(() => SandboxWireOperations.Authorize(scenarios, "graph-rollback", 2, "control-cue"), "wrong cycle refused");
        Harness.Reject(() => SandboxWireOperations.Authorize(scenarios, "ten-cycles", 1, "control-cue"), "wrong scenario refused");
        Harness.Reject(() => SandboxWireOperations.Authorize(scenarios, "graph-rollback", 1, "capture"), "lifecycle operations never pass the gate");
        scene.ControlCue = true;
        scenarios.Advance("graph-rollback", 1, 13, 400, scene.Snapshot());
        Harness.Check(scenarios.ExpectedAction == "run-graph", "observed cue advances to run-graph");
        Harness.Reject(() => SandboxWireOperations.Authorize(scenarios, "graph-rollback", 1, "control-cue"), "an operation is accepted once, at its own step only");

        var lifecycle = new SandboxNativeScenarios();
        var world = new SyntheticScene();
        lifecycle.Begin("lifecycle-routes", 1, 10, 100, world.Snapshot());
        Harness.Reject(() => SandboxWireOperations.Authorize(lifecycle, "lifecycle-routes", 1, "request-session-stop"), "session stop refused before spawn-prop");
        world.Open(); lifecycle.Advance("lifecycle-routes", 1, 11, 200, world.Snapshot());
        world.AddEntity(); lifecycle.Advance("lifecycle-routes", 1, 12, 300, world.Snapshot());
        Harness.Check(lifecycle.ExpectedAction == "stop-world-session", "lifecycle route reaches the session request");
        SandboxWireOperations.Authorize(lifecycle, "lifecycle-routes", 1, "request-session-stop");
        Harness.Check(true, "request-session-stop accepted at stop-world-session");
        Harness.Reject(() => SandboxWireOperations.Authorize(lifecycle, "lifecycle-routes", 1, "unregister-source"), "unrelated bounded operation refused at stop-world-session");

        var done = new SandboxNativeScenarios();
        var refusal = new SyntheticScene();
        done.Begin("persistence-refusal", 1, 10, 100, refusal.Snapshot());
        done.Advance("persistence-refusal", 1, 11, 200, refusal.Snapshot());
        Harness.Check(done.ExpectedAction == "", "an observed scenario expects nothing");
        Harness.Reject(() => SandboxWireOperations.Authorize(done, "persistence-refusal", 1, "external-write"), "an observed scenario accepts no bounded operation");
    }
}
