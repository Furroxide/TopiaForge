using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TopiaForge.Acceptance.Windows;

/// v2 vocabulary, atom bound and manifest checks; no device, input or game is touched.
internal static class BrokerContractTestsV2
{
    private static JsonElement Json(string text) => BoundedJson.Parse(Encoding.UTF8.GetBytes(text));
    internal static void Run(Action<bool, string> assert, Action<Action, string> refuse)
    {
        foreach (var json in new[]
        {
            "{\"kind\":\"key\",\"key\":\"MouseRight\"}", "{\"kind\":\"key\",\"key\":\"Enter\"}", "{\"kind\":\"key\",\"key\":\"F5\",\"milliseconds\":300}",
            "{\"kind\":\"click\",\"nodeId\":\"$scroll-3\"}", "{\"kind\":\"click\",\"nodeId\":\"$close\",\"surfaceId\":\"$toast\"}", "{\"kind\":\"click\",\"nodeId\":\"$body\"}",
            "{\"kind\":\"request\",\"operation\":\"accessibility-scale-200\"}", "{\"kind\":\"request\",\"operation\":\"cleanup\"}", "{\"kind\":\"request\",\"operation\":\"prepare\"}", "{\"kind\":\"request\",\"operation\":\"begin\"}",
            "{\"kind\":\"scroll-into-view\",\"nodeId\":\"undo-last\"}", "{\"kind\":\"scroll-into-view\",\"nodeId\":\"undo-last\",\"containerId\":\"roster-list\"}",
            "{\"kind\":\"scroll-into-view\",\"nodeId\":\"undo-last\",\"containerId\":\"$scroll-3\"}", "{\"kind\":\"scroll-into-view\",\"nodeId\":\"$scroll-1\",\"containerId\":\"$scroll-1\"}",
            "{\"kind\":\"scroll-into-view\",\"nodeId\":\"launch-personal-game\",\"containerId\":\"$scroll-0\"}", "{\"kind\":\"scroll-into-view\",\"nodeId\":\"roster-list\",\"containerId\":\"$scroll-0\",\"itemIdFromFact\":\"secret\"}",
            "{\"kind\":\"scroll-into-view\",\"nodeId\":\"undo-last\",\"containerId\":\"$scroll-0\",\"x\":10}", "{\"kind\":\"scroll-into-view\",\"nodeId\":\"undo-last\",\"containerId\":\"$scroll-0\",\"surfaceId\":\"$toast\"}",
            "{\"kind\":\"mouse-move\",\"dx\":401,\"dy\":0}", "{\"kind\":\"mouse-move\",\"dx\":0,\"dy\":-401}", "{\"kind\":\"mouse-move\",\"dx\":1.5,\"dy\":0}",
            "{\"kind\":\"mouse-move\",\"dx\":\"120\",\"dy\":0}", "{\"kind\":\"mouse-move\",\"dx\":120}", "{\"kind\":\"mouse-move\",\"dx\":120,\"dy\":0,\"nodeId\":\"$close\"}",
            "{\"kind\":\"key-hold\",\"key\":\"W\",\"milliseconds\":49}", "{\"kind\":\"key-hold\",\"key\":\"W\",\"milliseconds\":1001}", "{\"kind\":\"key-hold\",\"key\":\"LWin\",\"milliseconds\":300}",
            "{\"kind\":\"key-hold\",\"key\":\"W\"}", "{\"kind\":\"key-hold\",\"key\":\"W\",\"milliseconds\":300.0}",
            "{\"kind\":\"aim\",\"fact\":\"aimToGraphProp\",\"maxIterations\":41,\"gain\":5}", "{\"kind\":\"aim\",\"fact\":\"aimToGraphProp\",\"maxIterations\":0,\"gain\":5}",
            "{\"kind\":\"aim\",\"fact\":\"aimToGraphProp\",\"maxIterations\":40,\"gain\":21}", "{\"kind\":\"aim\",\"fact\":\"aimToGraphProp\",\"maxIterations\":40,\"gain\":0}",
            "{\"kind\":\"aim\",\"fact\":\"playerAim\",\"maxIterations\":40,\"gain\":5}", "{\"kind\":\"aim\",\"fact\":\"aimToGraphProp\",\"maxIterations\":40,\"gain\":5,\"dx\":1}", "{\"kind\":\"aim\",\"fact\":\"aimToGraphProp\",\"gain\":5}",
            "{\"kind\":\"replace-text\",\"nodeId\":\"catalog-search\",\"text\":\"a\\u0007\"}", "{\"kind\":\"replace-text\",\"nodeId\":\"catalog-search\",\"text\":\"" + new string('a', 1025) + "\"}"
        }) refuse(() => DriverVocabulary.ValidateStep(Json(json)), "closed v2 vocabulary: " + json);
        foreach (var json in new[]
        {
            "{\"kind\":\"key\",\"key\":\"MouseLeft\"}", "{\"kind\":\"key\",\"key\":\"Return\"}", "{\"kind\":\"key\",\"key\":\"Down\"}", "{\"kind\":\"key\",\"key\":\"Up\"}", "{\"kind\":\"key\",\"key\":\"Space\"}",
            "{\"kind\":\"click\",\"nodeId\":\"$close\"}", "{\"kind\":\"click\",\"nodeId\":\"undo-last\"}", "{\"kind\":\"click\",\"nodeId\":\"catalog-kind\"}",
            "{\"kind\":\"request\",\"operation\":\"accessibility-reset\"}", "{\"kind\":\"request\",\"operation\":\"despawn-control-robot\"}",
            "{\"kind\":\"scroll-into-view\",\"nodeId\":\"undo-last\",\"containerId\":\"$scroll-0\"}", "{\"kind\":\"scroll-into-view\",\"nodeId\":\"roster-list\",\"containerId\":\"$scroll-0\",\"itemIdFromFact\":\"borrowedRosterId\"}",
            "{\"kind\":\"mouse-move\",\"dx\":-400,\"dy\":400}", "{\"kind\":\"key-hold\",\"key\":\"W\",\"milliseconds\":50}", "{\"kind\":\"key-hold\",\"key\":\"MouseLeft\",\"milliseconds\":1000}",
            "{\"kind\":\"aim\",\"fact\":\"aimToGraphProp\",\"maxIterations\":40,\"gain\":20}", "{\"kind\":\"replace-text\",\"nodeId\":\"catalog-search\",\"text\":\"\"}"
        }) { DriverVocabulary.ValidateStep(Json(json)); assert(true, "accepted v2 atom: " + json); }
        assert(DriverVocabulary.ReplacementText(Json("{\"text\":\"\"}")).Length == 0, "empty replacement clears a field");
        assert(DriverVocabulary.SignedInteger(Json("{\"n\":-400}"), "n", -400, 400) == -400, "signed bound lower edge");
        refuse(() => DriverVocabulary.SignedInteger(Json("{\"n\":-401}"), "n", -400, 400), "signed bound below");
        refuse(() => DriverVocabulary.SignedInteger(Json("{\"n\":\"-1\"}"), "n", -400, 400), "signed text");
        assert(DriverVocabulary.WireOperations.Length == 19 && DriverVocabulary.WireOperations[0] == "prepare" && DriverVocabulary.WireOperations[^1] == "cleanup" && !DriverVocabulary.RequestOperations.Contains("cleanup"), "closed wire vocabulary");
        assert(WindowsInput.KeyCode("MouseLeft") == 0x01 && WindowsInput.KeyCode("Return") == 0x0d && WindowsInput.KeyCode("Down") == 0x28 && WindowsInput.KeyCode("Up") == 0x26 && WindowsInput.KeyCode("Space") == 0x20 && WindowsInput.KeyCode("Tab") == 0x09, "declared key codes");
        refuse(() => WindowsInput.KeyCode("LWin"), "undeclared key code");
        assert(BrokerDriver.AimDelta(12.5, -3.2, 4) == (-50, 13, false), "aim delta follows -yaw*gain, -pitch*gain");
        assert(BrokerDriver.AimDelta(170, -95, 20) == (-400, 400, true), "aim delta clamps to the relative move bound and records it");
        assert(BrokerDriver.AimDelta(0.04, 0, 1) == (0, 0, false), "aim delta rounds sub-count error to zero");
        refuse(() => BrokerDriver.AimDelta(double.NaN, 0, 5), "non-finite aim fact");
        refuse(() => BrokerDriver.AimDelta(1, 1, 21), "aim gain bound");
        assert(BrokerDriver.ScrollDirection(10, 20, 100, 680) == "down" && BrokerDriver.ScrollDirection(900, 20, 100, 680) == "up", "wheel direction from measured geometry");
        refuse(() => BrokerDriver.ScrollDirection(double.PositiveInfinity, 20, 100, 680), "wheel direction needs finite geometry");
        try { _ = new DriverManifest(Json("{\"schemaVersion\":1,\"kind\":\"sandbox-native-driver-actions-v1\",\"targetId\":\"x\"}")); throw new InvalidOperationException("v1 manifest accepted"); }
        catch (InvalidDataException error) when (error.Message.Contains(DriverManifest.RetiredKind + " is retired") && error.Message.Contains(DriverManifest.Kind)) { assert(true, "v1 manifest rejected precisely"); }
        RunProbe(assert, refuse);
        RunManifest(assert, refuse);
    }
    /// Wheel probe planning: full-width bands free of nested scroll views and virtual lists.
    private static void RunProbe(Action<bool, string> assert, Action<Action, string> refuse)
    {
        static MeasuredRect Rect(double x, double y, double width, double height) => new(x, y, width, height);
        var pane = Rect(0, 0, 400, 680);
        var probe = ScrollProbePlanner.Plan(pane, [Rect(0, 400, 400, 264), Rect(0, 40, 400, 264)]);
        assert(probe.X == 200 && probe.Y == 352 && probe.BandY == 304 && probe.BandHeight == 96, "largest uncovered band between nested lists");
        assert(ScrollProbePlanner.Plan(pane, []).Y == 340, "uncovered container uses its centre");
        var clipped = ScrollProbePlanner.Plan(pane, [Rect(0, -100, 400, 140), Rect(500, 0, 100, 680)]);
        assert(clipped.BandY == 40 && clipped.BandHeight == 640 && clipped.Y == 360, "covering rects are clipped to the container and non-intersecting rects are ignored");
        assert(ScrollProbePlanner.Plan(pane, [Rect(0, 0, 400, 300), Rect(0, 309, 400, 371)]).Y == 304.5, "nine-pixel band is the minimum");
        var tie = ScrollProbePlanner.Plan(pane, [Rect(0, 100, 400, 480)]);
        assert(tie.BandY == 0 && tie.BandHeight == 100, "equal bands prefer the lower one");
        foreach (var covered in new[] { new[] { Rect(0, 0, 400, 680) }, new[] { Rect(0, 0, 400, 300), Rect(0, 308, 400, 372) }, new[] { Rect(-10, -10, 420, 700) } })
        {
            try { ScrollProbePlanner.Plan(pane, covered); throw new InvalidOperationException("covered container planned"); }
            catch (InvalidOperationException error) when (error.Message.Contains("no wheel point")) { assert(true, "no uncovered wheel point refused"); }
        }
        try { ScrollProbePlanner.Plan(Rect(0, 0, 8, 680), []); throw new InvalidOperationException("thin container planned"); }
        catch (InvalidOperationException error) when (error.Message.Contains("too small")) { assert(true, "container narrower than the inset refused"); }
        refuse(() => ScrollProbePlanner.Plan(Rect(0, 0, double.NaN, 680), []), "non-finite container geometry");
        refuse(() => ScrollProbePlanner.Plan(pane, [Rect(0, 0, 400, double.PositiveInfinity)]), "non-finite covering geometry");
        assert(ScrollProbePlanner.NestedKinds.SequenceEqual(["scroll", "list"]), "nested kinds are scroll views and virtual lists");
        var point = WindowsInput.PointTarget(200, 352, 1920, 1080);
        assert(point.X == 200 && point.Y == 728, "wheel point converts from bottom-left client coordinates");
        refuse(() => WindowsInput.PointTarget(0, 352, 1920, 1080), "wheel point on the client edge");
        refuse(() => WindowsInput.PointTarget(200, 1080, 1920, 1080), "wheel point outside the client");
        refuse(() => WindowsInput.PointTarget(double.NaN, 352, 1920, 1080), "non-finite wheel point");
    }
    private static void RunManifest(Action<bool, string> assert, Action<Action, string> refuse)
    {
        var path = Path.Combine(Environment.CurrentDirectory, "tests", "TopiaForge.SandboxAcceptanceNative", "driver-actions-v2.json");
        var manifest = new DriverManifest(path);
        assert(manifest.Steps("routing", 1).Length == 10 && manifest.Steps("routing", 2)[0] == "register-competing-host" && manifest.Steps("routing", 2)[^1] == "unregister-competing-host", "per-cycle routing recipes");
        assert(manifest.Steps("borrowed-robot", 2).Contains("external-write") && manifest.Steps("borrowed-robot", 3).Contains("destroy-borrowed") && !manifest.Steps("borrowed-robot", 1).Contains("external-write"), "per-cycle borrowed-robot recipes");
        assert(manifest.Steps("ten-cycles", 10).SequenceEqual(manifest.Steps("ten-cycles", 1)) && manifest.Steps("ten-cycles", 1).Contains("camera-hidden") && manifest.Steps("ten-cycles", 1).Length == 13, "shared ten-cycle recipe");
        assert(manifest.Steps("catalog-editing", 1).SequenceEqual(["open", "search-nonmatching", "filter-robots", "filter-all", "$catalog", "end-session"]), "catalog expansion marker");
        assert(manifest.Steps("lifecycle-routes", 2).Contains("toggle-during-transition") && manifest.Steps("lifecycle-routes", 3).Contains("toggle-in-menu"), "lifecycle route toggles");
        refuse(() => manifest.Steps("routing", 3), "undeclared cycle");
        refuse(() => manifest.Steps("launch", 1), "undeclared scenario");
        assert(manifest.Action("aim-graph-prop").Single().GetProperty("kind").GetString() == "aim", "aim recipe");
        assert(manifest.Action("move-while-visible").Single().GetProperty("milliseconds").GetInt32() == 300, "key-hold recipe");
        assert(manifest.Action("camera-hidden").Single().GetProperty("dx").GetInt32() == 120, "mouse-move recipe");
        assert(manifest.Action("undo").Any(a => a.GetProperty("kind").GetString() == "scroll-into-view"), "scroll recipe");
        assert(manifest.Action("interact").Single().GetProperty("key").GetString() == "MouseLeft", "interaction key");
        assert(manifest.Action("hide-close").Single().GetProperty("nodeId").GetString() == "$close", "close chrome recipe");
        refuse(() => manifest.Action("hide"), "retired v1 action");
        assert(DriverVocabulary.Actions.All(a => manifest.Action(a).Length >= 1), "complete v2 inventory");
        var text = Encoding.UTF8.GetString(BoundedJson.ReadBytes(path));
        DriverManifest Mutate(Action<JsonNode> change)
        {
            var node = JsonNode.Parse(text)!;
            change(node);
            return new DriverManifest(Json(node.ToJsonString()));
        }
        refuse(() => Mutate(n => n["schemaVersion"] = 1), "v2 kind with v1 schema");
        refuse(() => Mutate(n => n["actions"]!.AsObject().Remove("undo")), "incomplete v2 inventory");
        refuse(() => Mutate(n => n["actions"]!["hide"] = new JsonArray(new JsonObject { ["kind"] = "key", ["key"] = "F5" })), "retired action recipe");
        refuse(() => Mutate(n => n["actions"]!["camera-hidden"]![0]!["dx"] = 401), "recipe outside the move bound");
        refuse(() => Mutate(n => n["actions"]!["move-while-visible"]![0]!["milliseconds"] = 1001), "recipe outside the hold bound");
        refuse(() => Mutate(n => n["actions"]!["aim-graph-prop"]![0]!["gain"] = 21), "recipe outside the aim gain bound");
        refuse(() => Mutate(n => n["actions"]!["undo"]![0]!["containerId"] = "$scroll-9"), "recipe with an undeclared container");
        refuse(() => Mutate(n => n["operations"]!.AsArray().Add("shell")), "operations beyond the wire vocabulary");
        refuse(() => Mutate(n => n["operations"]!.AsArray().RemoveAt(0)), "operations missing prepare");
        refuse(() => Mutate(n => n["scenarios"]![8]!["steps"]!.AsArray().Add("hide")), "undeclared scenario step");
        refuse(() => Mutate(n => n["scenarios"]![0]!["cycleSteps"]![1]!["cycle"] = 3), "per-cycle steps out of order");
        refuse(() => Mutate(n => n["scenarios"]![0]!["cycleSteps"]!.AsArray().RemoveAt(1)), "per-cycle steps missing a cycle");
        refuse(() => Mutate(n => n["scenarios"]![0]!["cycles"] = 1), "cycle count differs from the contract");
        refuse(() => Mutate(n => n["scenarios"]![1]!["inventoryExpansion"]!["perEntry"]!.AsArray().Add("launch")), "undeclared catalog expansion action");
        refuse(() => Mutate(n => n["scenarios"]![7]!["routes"]![0]!["operation"] = "external-write"), "undeclared lifecycle route");
        refuse(() => Mutate(n => n["scenarios"]![7]!["freshSessionPerCycle"] = false), "lifecycle without fresh sessions");
        refuse(() => Mutate(n => n["scenarios"]!.AsArray().RemoveAt(5)), "missing scenario");
        assert(Mutate(n => n["actions"]!["camera-hidden"]![0]!["dx"] = -400).Action("camera-hidden").Single().GetProperty("dx").GetInt32() == -400, "recipe at the move bound edge accepted");
    }
}
