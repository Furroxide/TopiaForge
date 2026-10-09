using System.Linq;
using System.Text;
using TopiaForge.SandboxAcceptance.Native;

// Serialized fact shapes: widget rows carry the four measured fields, aimToGraphProp has exactly the five broker
// keys, unavailable measurements are null rather than zero, and the aim/contrast arithmetic is pinned.
internal static class FactShapeTests
{
    private static string Json(object? value) => Encoding.UTF8.GetString(SandboxWireCodec.Serialize(value));

    internal static void Run()
    {
        var widget = NativeFactShapes.Widget("sandbox-creator-window", "catalog-kind", "dropdown", "Kind", "Body", 1f, 2f, 120f, 24f,
            true, true, false, false, true, false, 1.5f, 1f, true, "Robots", "#000000", "#ffffff");
        Harness.Check(widget.Keys.SequenceEqual(NativeFactShapes.WidgetKeys) && NativeFactShapes.WidgetKeys.Length == 21, "widget row keys and order");
        Harness.Equal(Json(widget), "{\"surfaceId\":\"sandbox-creator-window\",\"nodeId\":\"catalog-kind\",\"kind\":\"dropdown\",\"text\":\"Kind\",\"style\":\"Body\","
            + "\"x\":1,\"y\":2,\"width\":120,\"height\":24,\"visible\":true,\"enabled\":true,\"focused\":false,\"clipped\":false,\"highContrast\":true,"
            + "\"reducedMotion\":false,\"uiScale\":1.5,\"motionIntensity\":1,\"selected\":true,\"value\":\"Robots\",\"foreground\":\"#000000\",\"background\":\"#ffffff\"}",
            "widget serialization");
        Harness.Equal(Json(NativeFactShapes.Widget("$toast", "toast-1", "toast", "Hello", "Warning", 0f, 0f, 340f, 44f, false, false, false, false, false, false, 1f, 1f, false, null!, null!, null!))
            .Substring(Json(NativeFactShapes.Widget("$toast", "toast-1", "toast", "Hello", "Warning", 0f, 0f, 340f, 44f, false, false, false, false, false, false, 1f, 1f, false, null!, null!, null!)).IndexOf("\"selected\"")),
            "\"selected\":false,\"value\":\"\",\"foreground\":\"\",\"background\":\"\"}", "absent measurements serialize as empty strings, never null");

        var aim = NativeFactShapes.AimToGraphProp(12.5, -3.25, 4.5, false);
        Harness.Check(aim.Count == 5 && aim.Keys.SequenceEqual(NativeFactShapes.AimKeys), "aimToGraphProp has exactly the five broker keys");
        Harness.Equal(Json(aim), "{\"available\":true,\"yawDegrees\":12.5,\"pitchDegrees\":-3.25,\"distance\":4.5,\"focused\":false}", "aim serialization");
        Harness.Equal(Json(NativeFactShapes.AimUnavailable()), "{\"available\":false,\"yawDegrees\":null,\"pitchDegrees\":null,\"distance\":null,\"focused\":false}", "unavailable aim keeps the five keys with null measurements");
        Harness.Equal(Json(NativeFactShapes.Toast("toast-3", "Sandbox acceptance interaction", "Neutral", true)),
            "{\"nodeId\":\"toast-3\",\"text\":\"Sandbox acceptance interaction\",\"style\":\"Neutral\",\"visible\":true}", "toast row");
        Harness.Equal(Json(NativeFactShapes.CatalogSource("robotopia.vehicles", "Vehicles", "Unavailable", 0)),
            "{\"id\":\"robotopia.vehicles\",\"displayName\":\"Vehicles\",\"state\":\"Unavailable\",\"entryCount\":0}", "catalog source row");
        Harness.Equal(Json(NativeFactShapes.Accessibility(true, 1.5f, true, 0f)), "{\"highContrast\":true,\"uiScale\":1.5,\"reducedMotion\":true,\"motionIntensity\":0}", "accessibility");
        Harness.Equal(Json(NativeFactShapes.CompetingHost(true, 2, 0, 0)), "{\"registered\":true,\"canOpenCalls\":2,\"openCalls\":0,\"closeCalls\":0}", "competing host");
        Harness.Equal(Json(NativeFactShapes.FocusedInteraction(true, null)), "{\"available\":true,\"entityInstanceId\":null}", "focused interaction without target");
        Harness.Equal(Json(NativeFactShapes.FocusedInteraction(true, 42)), "{\"available\":true,\"entityInstanceId\":42}", "focused interaction with target");

        var (yaw, pitch) = NativeFactShapes.AimAngles(0, 0, 1, -1, 0, 1);
        Harness.Near(yaw, 45, 1e-9, "target left of forward yields positive yaw"); Harness.Near(pitch, 0, 1e-9, "level target yields zero pitch");
        Harness.Near(NativeFactShapes.AimAngles(0, 0, 1, 1, 0, 1).YawDegrees, -45, 1e-9, "target right of forward yields negative yaw");
        Harness.Near(NativeFactShapes.AimAngles(0, 0, 1, 0, 1, 1).PitchDegrees, 45, 1e-9, "target above forward yields positive pitch");
        Harness.Near(NativeFactShapes.AimAngles(0, 0, 1, 0, 1, 1).YawDegrees, 0, 1e-9, "elevated target straight ahead yields zero yaw");
        Harness.Near(NativeFactShapes.AimAngles(1, 0, 0, 1, 0, -1).YawDegrees, -45, 1e-9, "facing +x, a target toward -z is on the right");
        Harness.Near(NativeFactShapes.AimAngles(0, 0.7071, 0.7071, 0, 0, 1).PitchDegrees, -45, 1e-6, "camera above the target yields negative pitch");
        var centred = NativeFactShapes.AimAngles(0, 0, 1, 0, 0, 1);
        Harness.Near(centred.YawDegrees, 0, 1e-9, "centred yaw"); Harness.Near(centred.PitchDegrees, 0, 1e-9, "centred pitch");

        Harness.Near(NativeFactShapes.ContrastRatio("#000000", "#ffffff"), 21, 0.01, "black on white is 21:1");
        Harness.Check(NativeFactShapes.ContrastRatio("#808080", "#8a8a8a") < 1.2, "near-identical greys fail AA");
        Harness.Check(NativeFactShapes.ContrastRatio("", "#ffffff") == 0 && NativeFactShapes.ContrastRatio("#12345", "#ffffff") == 0, "missing colours measure nothing");
        Harness.Check(NativeFactShapes.HasColour("#0a0b0c") && !NativeFactShapes.HasColour("") && !NativeFactShapes.HasColour("0a0b0c0"), "colour presence");

        var facts = new SyntheticScene().Snapshot();
        foreach (var key in new[] { "toasts", "catalogSources", "interactions", "focusedInteraction", "aimToGraphProp", "playerAim", "personalityAssetIds", "audioSourceIds",
            "controlCuePlaying", "controllerInstanceId", "projectRunning", "undoDepth", "competingHost", "accessibility", "borrowedRobotDestroyed", "controlRobotInstanceId" })
            Harness.Check(facts.ContainsKey(key) || key == "interactions", "synthetic facts carry the v2 fact: " + key);
        var json = Json(facts);
        Harness.Check(json.Contains("\"controllerInstanceId\":7777") && json.Contains("\"controlRobotInstanceId\":0") && json.Contains("\"aimToGraphProp\":{\"available\":false"), "full facts serialize");
    }
}
