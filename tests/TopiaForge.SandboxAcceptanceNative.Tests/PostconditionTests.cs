using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.SandboxAcceptance.Native;

// Every v2 scenario walked through the progress observer with a synthetic scene, plus the refusal of each new
// postcondition when its measured fact is missing or wrong.
internal static class PostconditionTests
{
    private static long frame = 100, clock = 1000;

    internal static void Run()
    {
        Routing(); Catalog(); Borrowed(); SourceUnload(); HideReopen(); GraphRollback(); Lifecycle(); TenCycles(); Refusal();
    }

    private static Dictionary<string, object?> Describe(SandboxNativeScenarios scenarios)
    { var described = new Dictionary<string, object?>(); scenarios.Describe(described); return described; }
    private static SandboxNativeScenarios Begin(string id, int cycle, SyntheticScene scene, SandboxNativeScenarios? existing = null)
    { var scenarios = existing ?? new SandboxNativeScenarios(); frame++; clock += 20; scenarios.Begin(id, cycle, frame, clock, scene.Snapshot()); return scenarios; }
    private static Dictionary<string, object?> Advance(SandboxNativeScenarios scenarios, string id, int cycle, Dictionary<string, object?> facts)
    { frame++; clock += 20; scenarios.Advance(id, cycle, frame, clock, facts); return Describe(scenarios); }
    private static void Step(SandboxNativeScenarios scenarios, string id, int cycle, SyntheticScene scene, string action, Action<SyntheticScene>? mutate = null)
    {
        Harness.Check(scenarios.ExpectedAction == action, id + " c" + cycle + " expects " + action + " but serves " + scenarios.ExpectedAction);
        var before = (int)Describe(scenarios)["stepIndex"]!;
        mutate?.Invoke(scene);
        var after = Advance(scenarios, id, cycle, scene.Snapshot());
        Harness.Check((int)after["stepIndex"]! == before + 1, id + " c" + cycle + " observes " + action + ": " + after["scenarioReason"]);
    }
    private static void Stalls(SandboxNativeScenarios scenarios, string id, int cycle, SyntheticScene scene, string label, Action<SyntheticScene>? mutate = null)
    {
        mutate?.Invoke(scene);
        var before = (int)Describe(scenarios)["stepIndex"]!;
        var after = Advance(scenarios, id, cycle, scene.Snapshot());
        Harness.Check((int)after["stepIndex"]! == before && (string)after["scenarioState"]! == "waiting"
            && ((string)after["scenarioReason"]!).StartsWith("waiting-for-native-postcondition:", StringComparison.Ordinal), label + ": " + after["scenarioReason"]);
    }
    private static void Observed(SandboxNativeScenarios scenarios, string label) => Harness.Check((string)Describe(scenarios)["scenarioState"]! == "observed", label + " observed");

    private static void Routing()
    {
        var scene = new SyntheticScene(); var scenarios = Begin("routing", 1, scene);
        Step(scenarios, "routing", 1, scene, "open", s => s.Open());
        Stalls(scenarios, "routing", 1, scene, "low contrast text refuses high contrast", s => { s.HighContrast = true; s.LowContrastWidget = true; });
        Step(scenarios, "routing", 1, scene, "accessibility-high-contrast", s => s.LowContrastWidget = false);
        Stalls(scenarios, "routing", 1, scene, "hide button must grow 1.4x", s => { s.UiScale = 1.5f; s.HideHeight = 39f; });
        Step(scenarios, "routing", 1, scene, "accessibility-scale-150", s => s.HideHeight = 45f);
        Step(scenarios, "routing", 1, scene, "accessibility-reduced-motion", s => s.ReducedMotion = true);
        Stalls(scenarios, "routing", 1, scene, "reset requires every preference back", s => { s.ReducedMotion = false; s.HighContrast = false; });
        Step(scenarios, "routing", 1, scene, "accessibility-reset", s => s.UiScale = 1f);
        Step(scenarios, "routing", 1, scene, "focus-next", s => s.Focused = "catalog-search");
        Step(scenarios, "routing", 1, scene, "hide-f5", s => s.Hide());
        Step(scenarios, "routing", 1, scene, "reopen", s => s.Open());
        Step(scenarios, "routing", 1, scene, "duplicate-toggle");
        Step(scenarios, "routing", 1, scene, "end-session", s => s.EndSession());
        Observed(scenarios, "routing c1");
        Begin("routing", 2, scene, scenarios);
        Stalls(scenarios, "routing", 2, scene, "an opened competing host refuses registration", s => { s.CompetingRegistered = true; s.CompetingOpenCalls = 1; });
        Step(scenarios, "routing", 2, scene, "register-competing-host", s => s.CompetingOpenCalls = 0);
        Step(scenarios, "routing", 2, scene, "open", s => s.Open());
        Step(scenarios, "routing", 2, scene, "hide-f5", s => s.Hide());
        Step(scenarios, "routing", 2, scene, "reopen", s => s.Open());
        Step(scenarios, "routing", 2, scene, "end-session", s => s.EndSession());
        Step(scenarios, "routing", 2, scene, "unregister-competing-host", s => s.CompetingRegistered = false);
        Observed(scenarios, "routing c2");
    }

    private static void Catalog()
    {
        var scene = new SyntheticScene(); var scenarios = Begin("catalog-editing", 1, scene);
        Step(scenarios, "catalog-editing", 1, scene, "open", s => s.Open());
        Stalls(scenarios, "catalog-editing", 1, scene, "rows still listed refuse the nonmatching search", s => s.SpawnEnabled = false);
        Step(scenarios, "catalog-editing", 1, scene, "search-nonmatching", s => s.CatalogRows.Clear());
        Stalls(scenarios, "catalog-editing", 1, scene, "a content row refuses the robot filter", s => { s.CatalogKind = "Robots"; s.CatalogRows = new List<string> { SyntheticScene.PropRow }; });
        Step(scenarios, "catalog-editing", 1, scene, "filter-robots", s => s.CatalogRows = new List<string> { SyntheticScene.RobotRow });
        Step(scenarios, "catalog-editing", 1, scene, "filter-all", s => { s.CatalogKind = "All content"; s.CatalogRows = new List<string> { SyntheticScene.PropRow, SyntheticScene.RobotRow }; s.SpawnEnabled = true; });
        var prop = 0;
        Stalls(scenarios, "catalog-editing", 1, scene, "a spawn without a rendered selection is not observed", s => prop = s.AddEntity());
        Step(scenarios, "catalog-editing", 1, scene, "spawn-catalog", s => { s.SelectedRoster.Add("owned-content:1"); s.UndoDepth = 1; });
        Step(scenarios, "catalog-editing", 1, scene, "edit-transform", s => { s.TransformOf(prop)[1] += 1f; s.UndoDepth++; });
        Step(scenarios, "catalog-editing", 1, scene, "edit-rotation", s => { s.TransformOf(prop)[4] = 0.7071068f; s.TransformOf(prop)[6] = 0.7071068f; s.UndoDepth++; });
        Step(scenarios, "catalog-editing", 1, scene, "edit-scale", s => { for (var axis = 7; axis < 10; axis++) s.TransformOf(prop)[axis] = 1.25f; s.UndoDepth++; });
        var duplicate = 0;
        Stalls(scenarios, "catalog-editing", 1, scene, "a duplicate off its offset is not observed", s => { duplicate = s.AddEntity(transform: new[] { 1f, 1f, 2f, 0f, 0f, 0f, 1f, 1f, 1f, 1f }); s.UndoDepth++; });
        Step(scenarios, "catalog-editing", 1, scene, "duplicate", s => s.TransformOf(duplicate)[2] = 1f);
        Stalls(scenarios, "catalog-editing", 1, scene, "undo needs the history depth to fall", s => s.RemoveEntity(duplicate));
        Step(scenarios, "catalog-editing", 1, scene, "undo", s => s.UndoDepth--);
        Step(scenarios, "catalog-editing", 1, scene, "remove", s => { s.RemoveEntity(prop); s.SelectedRoster.Clear(); });
        var robot = 0;
        Step(scenarios, "catalog-editing", 1, scene, "spawn-catalog", s => { robot = s.AddEntity(robot: true); s.SelectedRoster.Add("owned-robot:1"); s.UndoDepth = 1; });
        Step(scenarios, "catalog-editing", 1, scene, "edit-transform", s => { s.TransformOf(robot)[1] += 1f; s.UndoDepth++; });
        var twin = 0;
        Step(scenarios, "catalog-editing", 1, scene, "duplicate", s => { twin = s.AddEntity(robot: true, transform: new[] { 1f, 1f, 1f, 0f, 0f, 0f, 1f, 1f, 1f, 1f }); s.UndoDepth++; });
        Step(scenarios, "catalog-editing", 1, scene, "undo", s => { s.RemoveEntity(twin); s.UndoDepth--; });
        Step(scenarios, "catalog-editing", 1, scene, "remove", s => { s.RemoveEntity(robot); s.SelectedRoster.Clear(); });
        Step(scenarios, "catalog-editing", 1, scene, "end-session", s => s.EndSession());
        Observed(scenarios, "catalog-editing");
    }

    private static void Borrowed()
    {
        var scene = new SyntheticScene(); var scenarios = Begin("borrowed-robot", 1, scene);
        Step(scenarios, "borrowed-robot", 1, scene, "open", s => s.Open());
        Step(scenarios, "borrowed-robot", 1, scene, "select-borrowed", s => s.Focused = "roster-list/robot-native:robot-scene:9001");
        Step(scenarios, "borrowed-robot", 1, scene, "edit-transform", s => s.BorrowedTransform[1] += 1f);
        Step(scenarios, "borrowed-robot", 1, scene, "edit-personality", s => { s.Brain["hackedPersonalityId"] = 5001; s.Brain["hackedPersonalityFingerprint"] = "fp-1"; });
        Step(scenarios, "borrowed-robot", 1, scene, "edit-brain", s => s.Brain["state"] = "Dormant");
        Step(scenarios, "borrowed-robot", 1, scene, "end-session", s => { s.EndSession(); s.Borrowed = SyntheticScene.BorrowedRobot(); });
        Observed(scenarios, "borrowed-robot c1");
        Harness.Check((int)Describe(scenarios)["previewedPersonalityId"]! == 5001, "the previewed personality identity is recorded");
        Begin("borrowed-robot", 2, scene, scenarios);
        Step(scenarios, "borrowed-robot", 2, scene, "open", s => s.Open());
        Step(scenarios, "borrowed-robot", 2, scene, "select-borrowed", s => s.Focused = "roster-list/robot-native:robot-scene:9001");
        Step(scenarios, "borrowed-robot", 2, scene, "edit-transform", s => s.BorrowedTransform[1] += 1f);
        Step(scenarios, "borrowed-robot", 2, scene, "edit-personality", s => { s.Brain["hackedPersonalityId"] = 5002; s.Brain["hackedPersonalityFingerprint"] = "fp-2"; });
        Stalls(scenarios, "borrowed-robot", 2, scene, "an external write short of 2 m is not observed", s => s.BorrowedTransform[0] += 1.5f);
        Step(scenarios, "borrowed-robot", 2, scene, "external-write", s => s.BorrowedTransform[0] += 0.5f);
        Stalls(scenarios, "borrowed-robot", 2, scene, "end-session needs the restoration warning toast",
            s => { s.EndSession(); s.Brain["hackedPersonalityId"] = 0; s.Brain["hackedPersonalityFingerprint"] = "none"; });
        Step(scenarios, "borrowed-robot", 2, scene, "end-session", s => s.Toast("Session ended with restoration warnings: moved outside Creator Tools", "Warning"));
        Observed(scenarios, "borrowed-robot c2");
        scene.Borrowed = SyntheticScene.BorrowedRobot(); scene.Toasts.Clear();
        Begin("borrowed-robot", 3, scene, scenarios);
        Step(scenarios, "borrowed-robot", 3, scene, "open", s => s.Open());
        Step(scenarios, "borrowed-robot", 3, scene, "select-borrowed", s => s.Focused = "roster-list/robot-native:robot-scene:9001");
        Step(scenarios, "borrowed-robot", 3, scene, "edit-transform", s => { s.BorrowedTransform[1] += 1f; s.RobotEditLeases = 1; });
        Stalls(scenarios, "borrowed-robot", 3, scene, "destroy-borrowed needs the destroyed flag", s => s.Borrowed = null);
        Step(scenarios, "borrowed-robot", 3, scene, "destroy-borrowed", s => s.BorrowedDestroyed = true);
        Stalls(scenarios, "borrowed-robot", 3, scene, "a retained edit lease refuses end-session", s => s.EndSession());
        Step(scenarios, "borrowed-robot", 3, scene, "end-session", s => s.RobotEditLeases = 0);
        Observed(scenarios, "borrowed-robot c3");
    }

    private static void SourceUnload()
    {
        var scene = new SyntheticScene(); var scenarios = Begin("source-unload", 1, scene);
        Step(scenarios, "source-unload", 1, scene, "open", s => s.Open());
        Step(scenarios, "source-unload", 1, scene, "spawn-prop", s => s.AddEntity());
        Step(scenarios, "source-unload", 1, scene, "duplicate", s => s.AddEntity(transform: new[] { 1f, 0f, 1f, 0f, 0f, 0f, 1f, 1f, 1f, 1f }));
        var character = 0; var control = 0;
        Step(scenarios, "source-unload", 1, scene, "spawn-character", s => character = s.AddEntity(robot: true));
        Stalls(scenarios, "source-unload", 1, scene, "a control robot without native identity is not observed", s => control = s.AddEntity(robot: true));
        Step(scenarios, "source-unload", 1, scene, "spawn-control-robot", s => s.ControlRobot = control);
        Stalls(scenarios, "source-unload", 1, scene, "a vanished vehicle source refuses teardown", s => { s.Content.Clear(); s.RemoveEntity(character); s.CatalogIds.Clear(); s.VehicleState = "Ready"; });
        Step(scenarios, "source-unload", 1, scene, "unregister-source", s => s.VehicleState = "Unavailable");
        Step(scenarios, "source-unload", 1, scene, "end-session", s => s.EndSession());
        Step(scenarios, "source-unload", 1, scene, "despawn-control-robot", s => { s.RemoveEntity(control); s.ControlRobot = 0; });
        Observed(scenarios, "source-unload c1");
        Begin("source-unload", 2, scene, scenarios);
        Step(scenarios, "source-unload", 2, scene, "open", s => s.Open());
        Step(scenarios, "source-unload", 2, scene, "spawn-prop", s => s.AddEntity());
        Step(scenarios, "source-unload", 2, scene, "run-graph", s => s.RunGraph());
        Stalls(scenarios, "source-unload", 2, scene, "a still-running graph refuses teardown", s => { s.Content.Clear(); s.CatalogIds.Clear(); });
        Step(scenarios, "source-unload", 2, scene, "unregister-source", s => { s.ProjectRunning = false; s.GraphPlaying = 0; s.AimAvailable = false; });
        Step(scenarios, "source-unload", 2, scene, "end-session", s => { s.EndSession(); s.Robots.Clear(); s.Interactions = 0; s.Conversations = 0; });
        Observed(scenarios, "source-unload c2");
    }

    private static void HideReopen()
    {
        var scene = new SyntheticScene(); var scenarios = Begin("hide-reopen", 1, scene);
        Step(scenarios, "hide-reopen", 1, scene, "open", s => s.Open());
        Step(scenarios, "hide-reopen", 1, scene, "spawn-prop", s => s.AddEntity());
        Step(scenarios, "hide-reopen", 1, scene, "select-borrowed", s => s.Focused = "roster-list/robot-native:robot-scene:9001");
        Step(scenarios, "hide-reopen", 1, scene, "edit-transform", s => s.BorrowedTransform[1] += 1f);
        int graphProp = 0, graphRobot = 0;
        Step(scenarios, "hide-reopen", 1, scene, "run-graph", s => { s.RunGraph(); graphProp = (int)s.Content.Last()["instanceId"]!; graphRobot = (int)s.Robots.Last()["instanceId"]!; });
        Stalls(scenarios, "hide-reopen", 1, scene, "movement while visible is a failure", s => s.Position[0] += 1f);
        Step(scenarios, "hide-reopen", 1, scene, "move-while-visible", s => s.Position[0] -= 1f);
        Step(scenarios, "hide-reopen", 1, scene, "hide-close", s => s.Hide());
        Step(scenarios, "hide-reopen", 1, scene, "move-player", s => s.Position[2] += 1f);
        Stalls(scenarios, "hide-reopen", 1, scene, "an unchanged aim refuses camera-hidden");
        Step(scenarios, "hide-reopen", 1, scene, "camera-hidden", s => s.Aim = new[] { 0.5f, 0f, 0.866f });
        Stalls(scenarios, "hide-reopen", 1, scene, "reopen over a stopped graph is a failure", s => { s.Open(); s.ProjectRunning = false; });
        Step(scenarios, "hide-reopen", 1, scene, "reopen", s => s.ProjectRunning = true);
        Step(scenarios, "hide-reopen", 1, scene, "text-focus", s => s.Search = "W");
        Step(scenarios, "hide-reopen", 1, scene, "hide-f5", s => s.Hide());
        Step(scenarios, "hide-reopen", 1, scene, "reopen", s => s.Open());
        Step(scenarios, "hide-reopen", 1, scene, "stop-graph", s => s.StopGraph(graphProp, graphRobot));
        Step(scenarios, "hide-reopen", 1, scene, "end-session", s => { s.EndSession(); s.Borrowed = SyntheticScene.BorrowedRobot(); });
        Observed(scenarios, "hide-reopen");
    }

    private static void GraphRollback()
    {
        var scene = new SyntheticScene(); var scenarios = Begin("graph-rollback", 1, scene);
        Step(scenarios, "graph-rollback", 1, scene, "open", s => s.Open());
        Step(scenarios, "graph-rollback", 1, scene, "spawn-prop", s => s.AddEntity());
        Step(scenarios, "graph-rollback", 1, scene, "control-cue", s => s.ControlCue = true);
        int graphProp = 0, graphRobot = 0;
        Step(scenarios, "graph-rollback", 1, scene, "run-graph", s => { s.RunGraph(); graphProp = (int)s.Content.Last()["instanceId"]!; graphRobot = (int)s.Robots.Last()["instanceId"]!; });
        Step(scenarios, "graph-rollback", 1, scene, "hide-f5", s => s.Hide());
        Stalls(scenarios, "graph-rollback", 1, scene, "an unfocused prop refuses the aim step");
        Step(scenarios, "graph-rollback", 1, scene, "aim-graph-prop", s => s.AimFocused = true);
        Stalls(scenarios, "graph-rollback", 1, scene, "the wrong branch toast refuses interact", s => { s.Toast("Sandbox acceptance interaction"); s.Toast("Sandbox acceptance WRONG BRANCH"); });
        Step(scenarios, "graph-rollback", 1, scene, "interact", s => s.Toasts.RemoveAt(1));
        Step(scenarios, "graph-rollback", 1, scene, "reopen", s => s.Open());
        Stalls(scenarios, "graph-rollback", 1, scene, "a silenced control cue refuses stop-graph", s => { s.StopGraph(graphProp, graphRobot); s.ControlCue = false; });
        Step(scenarios, "graph-rollback", 1, scene, "stop-graph", s => s.ControlCue = true);
        Step(scenarios, "graph-rollback", 1, scene, "stop-control-cue", s => s.ControlCue = false);
        Stalls(scenarios, "graph-rollback", 1, scene, "a host still named after the graph cue refuses end-session", s => s.EndSession());
        Step(scenarios, "graph-rollback", 1, scene, "end-session", s => s.GraphAudio.Clear());
        Observed(scenarios, "graph-rollback c1");
        Harness.Check(Describe(scenarios)["graphAudioIds"] is int[] ids && ids.SequenceEqual(new[] { 3100 }), "graph audio hosts seen during the run are recorded");
        Begin("graph-rollback", 2, scene, scenarios);
        Step(scenarios, "graph-rollback", 2, scene, "open", s => s.Open());
        Step(scenarios, "graph-rollback", 2, scene, "spawn-prop", s => s.AddEntity());
        Step(scenarios, "graph-rollback", 2, scene, "stop-before-start");
        for (var run = 0; run < 2; run++)
        {
            Step(scenarios, "graph-rollback", 2, scene, "run-graph", s => { s.RunGraph(); graphProp = (int)s.Content.Last()["instanceId"]!; graphRobot = (int)s.Robots.Last()["instanceId"]!; });
            Step(scenarios, "graph-rollback", 2, scene, "stop-graph", s => s.StopGraph(graphProp, graphRobot));
        }
        Step(scenarios, "graph-rollback", 2, scene, "end-session", s => { s.EndSession(); s.GraphAudio.Clear(); });
        Observed(scenarios, "graph-rollback c2");
        // A missing v2 fact at its step is unavailable evidence, never a default.
        var missing = new SandboxNativeScenarios(); var scene2 = new SyntheticScene(); Begin("graph-rollback", 1, scene2, missing);
        Step(missing, "graph-rollback", 1, scene2, "open", s => s.Open());
        Step(missing, "graph-rollback", 1, scene2, "spawn-prop", s => s.AddEntity());
        var gone = scene2.Snapshot(); gone.Remove("controlCuePlaying");
        var state = Advance(missing, "graph-rollback", 1, gone);
        Harness.Check((string)state["scenarioState"]! == "unavailable" && ((string)state["scenarioReason"]!).Contains("controlCuePlaying"), "missing controlCuePlaying is unavailable");
        var toastless = new SandboxNativeScenarios(); var scene3 = new SyntheticScene(); Begin("graph-rollback", 1, scene3, toastless);
        Step(toastless, "graph-rollback", 1, scene3, "open", s => s.Open());
        Step(toastless, "graph-rollback", 1, scene3, "spawn-prop", s => s.AddEntity());
        Step(toastless, "graph-rollback", 1, scene3, "control-cue", s => s.ControlCue = true);
        Step(toastless, "graph-rollback", 1, scene3, "run-graph", s => s.RunGraph());
        Step(toastless, "graph-rollback", 1, scene3, "hide-f5", s => s.Hide());
        Step(toastless, "graph-rollback", 1, scene3, "aim-graph-prop", s => s.AimFocused = true);
        var withoutToasts = scene3.Snapshot(); withoutToasts.Remove("toasts");
        state = Advance(toastless, "graph-rollback", 1, withoutToasts);
        Harness.Check((string)state["scenarioState"]! == "unavailable" && ((string)state["scenarioReason"]!).Contains("toasts"), "missing toasts at interact is unavailable");
    }

    private static void Lifecycle()
    {
        var scene = new SyntheticScene(); var scenarios = Begin("lifecycle-routes", 1, scene);
        Step(scenarios, "lifecycle-routes", 1, scene, "open", s => s.Open());
        Step(scenarios, "lifecycle-routes", 1, scene, "spawn-prop", s => s.AddEntity());
        Stalls(scenarios, "lifecycle-routes", 1, scene, "a lingering controller refuses the idle route", s => { s.Phase = "Idle"; s.SessionId = ""; s.Hide(); s.Content.Clear(); s.CreatorSessions = 0; });
        Step(scenarios, "lifecycle-routes", 1, scene, "stop-world-session", s => s.Controller = null);
        Observed(scenarios, "lifecycle-routes c1");
        var restart = new SyntheticScene { SessionId = "synthetic-session-2" }; Begin("lifecycle-routes", 2, restart, scenarios);
        Step(scenarios, "lifecycle-routes", 2, restart, "open", s => s.Open());
        Step(scenarios, "lifecycle-routes", 2, restart, "spawn-prop", s => s.AddEntity());
        Step(scenarios, "lifecycle-routes", 2, restart, "stop-world-session", s => { s.Phase = "Stopping"; s.Hide(); s.Content.Clear(); s.CreatorSessions = 0; });
        Stalls(scenarios, "lifecycle-routes", 2, restart, "the same controller identity refuses the transition toggle", s => { s.Phase = "Running"; s.SessionId = "synthetic-session-3"; });
        Step(scenarios, "lifecycle-routes", 2, restart, "toggle-during-transition", s => s.Controller = 8888);
        Step(scenarios, "lifecycle-routes", 2, restart, "move-player", s => s.Position[0] += 1f);
        Observed(scenarios, "lifecycle-routes c2");
        var menu = new SyntheticScene { SessionId = "synthetic-session-4" }; Begin("lifecycle-routes", 3, menu, scenarios);
        Step(scenarios, "lifecycle-routes", 3, menu, "open", s => s.Open());
        Step(scenarios, "lifecycle-routes", 3, menu, "spawn-prop", s => s.AddEntity());
        Step(scenarios, "lifecycle-routes", 3, menu, "stop-world-session", s => { s.Phase = "Idle"; s.SessionId = ""; s.Controller = null; s.Hide(); s.Content.Clear(); s.CreatorSessions = 0; });
        Stalls(scenarios, "lifecycle-routes", 3, menu, "an opened host in the menu is a failure", s => s.ActiveHost = SyntheticScene.Owner);
        Step(scenarios, "lifecycle-routes", 3, menu, "toggle-in-menu", s => s.ActiveHost = "");
        Observed(scenarios, "lifecycle-routes c3");
    }

    private static void TenCycles()
    {
        var scene = new SyntheticScene(); var scenarios = new SandboxNativeScenarios();
        for (var cycle = 1; cycle <= 2; cycle++)
        {
            Begin("ten-cycles", cycle, scene, scenarios);
            Step(scenarios, "ten-cycles", cycle, scene, "open", s => s.Open());
            Step(scenarios, "ten-cycles", cycle, scene, "spawn-prop", s => s.AddEntity());
            Step(scenarios, "ten-cycles", cycle, scene, "select-borrowed", s => s.Focused = "roster-list/robot-native:robot-scene:9001");
            Step(scenarios, "ten-cycles", cycle, scene, "edit-transform", s => s.BorrowedTransform[1] += 1f);
            Step(scenarios, "ten-cycles", cycle, scene, "edit-personality", s => { s.Brain["hackedPersonalityId"] = 6000 + cycle; s.Brain["hackedPersonalityFingerprint"] = "fp-" + cycle; });
            Step(scenarios, "ten-cycles", cycle, scene, "edit-brain", s => s.Brain["llmDisabled"] = "False");
            Step(scenarios, "ten-cycles", cycle, scene, "hide-f5", s => s.Hide());
            Step(scenarios, "ten-cycles", cycle, scene, "move-player", s => s.Position[0] += 1f);
            Step(scenarios, "ten-cycles", cycle, scene, "camera-hidden", s => s.Aim = new[] { 0.1f * cycle, 0f, 0.9f });
            Step(scenarios, "ten-cycles", cycle, scene, "reopen", s => s.Open());
            int graphProp = 0, graphRobot = 0;
            Step(scenarios, "ten-cycles", cycle, scene, "run-graph", s => { s.RunGraph(); graphProp = (int)s.Content.Last()["instanceId"]!; graphRobot = (int)s.Robots.Last()["instanceId"]!; });
            Step(scenarios, "ten-cycles", cycle, scene, "stop-graph", s => s.StopGraph(graphProp, graphRobot));
            Step(scenarios, "ten-cycles", cycle, scene, "end-session", s => { s.EndSession(); s.Borrowed = SyntheticScene.BorrowedRobot(); s.GraphAudio.Clear(); });
            Observed(scenarios, "ten-cycles c" + cycle);
            Harness.Check((int)Describe(scenarios)["completedCycles"]! == cycle, "ten-cycles completed count");
        }
    }

    private static void Refusal()
    {
        var scene = new SyntheticScene { StatusCaption = false }; var scenarios = Begin("persistence-refusal", 1, scene);
        Stalls(scenarios, "persistence-refusal", 1, scene, "the rendered isolation caption is required");
        Step(scenarios, "persistence-refusal", 1, scene, "observe-refusal", s => s.StatusCaption = true);
        Observed(scenarios, "persistence-refusal");
    }
}
