using System.Text;

namespace TopiaForge.Acceptance.Windows;

internal static class BrokerContractTests
{
    internal static void Run()
    {
        var count = 0;
        void Assert(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; }
        void Refuse(Action action, string name)
        {
            try { action(); } catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or ArgumentException) { count++; return; }
            throw new InvalidOperationException("Expected refusal: " + name);
        }
        Refuse(() => BoundedJson.Parse(Encoding.UTF8.GetBytes("{\"pid\":1,\"pid\":2}")), "duplicate identity");
        Refuse(() => BoundedJson.Parse(Encoding.UTF8.GetBytes("{\"a\":{\"x\":1,\"x\":2}}")), "nested duplicate");
        Refuse(() => BoundedJson.Parse(new byte[BoundedJson.MaximumBytes + 1]), "oversized frame");
        Refuse(() => BoundedJson.Parse(Encoding.UTF8.GetBytes("{} trailing")), "trailing payload");
        Refuse(() => BoundedJson.Keys(BoundedJson.Parse(Encoding.UTF8.GetBytes("{\"a\":1,\"unknown\":2}")), "a"), "unknown field");
        Refuse(() => BoundedJson.Integer(BoundedJson.Parse(Encoding.UTF8.GetBytes("{\"n\":1.0}")), "n", 1, 5), "floating sequence");
        Refuse(() => BoundedJson.Integer(BoundedJson.Parse(Encoding.UTF8.GetBytes("{\"n\":0}")), "n", 1, 5), "zero sequence");
        Refuse(() => BoundedJson.Digest(BoundedJson.Parse(Encoding.UTF8.GetBytes("{\"hash\":\"ABC\"}")), "hash"), "wrong challenge shape");
        foreach (var geometry in new[] { new[] { -1d, 0, 10, 10 }, new[] { 0d, 0, double.NaN, 10 }, new[] { 0d, 0, 1921, 10 }, new[] { 0d, 0, 10, 0 }, new[] { 1910d, 1070, 11, 11 } })
            Refuse(() => WindowsInput.TargetPoint(geometry[0], geometry[1], geometry[2], geometry[3], 1920, 1080), "unsafe widget bounds");
        var point = WindowsInput.TargetPoint(100, 200, 80, 40, 1920, 1080);
        Assert(point.X == 140 && point.Y == 860, "bottom-left screen conversion");
        var absolute = WindowsInput.AbsolutePoint(60000, 200, -30000, 0, 120000, 1080);
        Assert(absolute.X == (int)(90000L * 65535 / 119999), "wide virtual display does not overflow");
        Assert(ScreenCapture.CountColors(new byte[400]) == 1, "blank frame oracle");
        Assert(ScreenCapture.CountColors(new byte[] { 0, 0, 0, 255, 1, 2, 3, 0 }) == 2, "alpha excluded from visible color");
        Refuse(() => ScreenCapture.CountColors(new byte[3]), "invalid pixel layout");
        foreach (var json in new[] { "{\"kind\":\"shell\",\"command\":\"anything\"}", "{\"kind\":\"key\",\"key\":\"LWin\"}", "{\"kind\":\"click\",\"nodeId\":\"launch-personal-game\"}", "{\"kind\":\"request\",\"operation\":\"invoke\"}", "{\"kind\":\"replace-text\",\"nodeId\":\"persona-name\",\"textFromFact\":\"secret\"}" })
            Refuse(() => DriverManifest.ValidateStep(BoundedJson.Parse(Encoding.UTF8.GetBytes(json))), "closed actuation vocabulary");
        Assert(DriverManifest.Cycles("ten-cycles") == 10 && DriverManifest.Cycles("lifecycle-routes") == 3, "mandatory cycle counts");
        var manifest = new DriverManifest(Path.Combine(Environment.CurrentDirectory, "tests", "TopiaForge.SandboxAcceptanceNative", "driver-actions-v1.json"));
        Assert(manifest.Action("spawn-catalog").Length == 3, "actual dynamic catalog manifest");
        Refuse(() => manifest.Action("external-action"), "untrusted native next action");
        var cancel = BoundedJson.Parse(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"kind\":\"sandbox-broker-cancel-v1\",\"challenge\":\"" + new string('a', 64) + "\"}"));
        BrokerCancellation.Validate(cancel, new string('a', 64)); count++;
        Refuse(() => BrokerCancellation.Validate(cancel, new string('b', 64)), "foreign cancellation challenge");
        Refuse(() => BrokerCancellation.Validate(BoundedJson.Parse(Encoding.UTF8.GetBytes("{}")), new string('a', 64)), "incomplete cancellation");
        Console.WriteLine($"Broker contract checks passed: {count}.");
    }
}
