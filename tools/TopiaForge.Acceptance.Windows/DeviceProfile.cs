using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

internal sealed record DeviceProfile(int Width, int Height, int Dpi, string DisplayName, string AudioEndpointId, bool LoopbackEnabled, string[] PersistenceFiles, ScreenBaselineSet? ScreenBaselines = null)
{
    internal static DeviceProfile Read(string path) => Parse(BoundedJson.Read(path));
    internal static DeviceProfile Parse(JsonElement root)
    {
        var keys = new List<string> { "schemaVersion", "kind", "operatorReference", "reviewerEvidence", "display", "audio", "persistenceFiles" };
        var baselines = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("screenBaselines", out _);
        if (baselines) keys.Add("screenBaselines");
        BoundedJson.Keys(root, keys.ToArray());
        if (BoundedJson.Integer(root, "schemaVersion", 1, 1) != 1 || BoundedJson.Text(root, "kind") != "sandbox-device-profile-v1") throw new InvalidDataException("Unsupported device profile.");
        _ = BoundedJson.Text(root, "operatorReference", 1024);
        _ = BoundedJson.Text(root, "reviewerEvidence", 1024);
        var display = root.GetProperty("display");
        BoundedJson.Keys(display, "width", "height", "dpi", "deviceName");
        var audio = root.GetProperty("audio");
        BoundedJson.Keys(audio, "endpointId", "loopbackEnabled");
        var enabled = audio.GetProperty("loopbackEnabled");
        if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Loopback permission must be explicit.");
        var files = root.GetProperty("persistenceFiles");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 128) throw new InvalidDataException("Persistence allowlist exceeds its bound.");
        var paths = files.EnumerateArray().Select(f => f.GetString() ?? throw new InvalidDataException("Invalid persistence path.")).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new InvalidDataException("Duplicate persistence file.");
        var width = BoundedJson.Integer(display, "width", 640, 7680);
        var height = BoundedJson.Integer(display, "height", 480, 4320);
        return new(width, height, BoundedJson.Integer(display, "dpi", 96, 384), BoundedJson.Text(display, "deviceName", 128), BoundedJson.Text(audio, "endpointId", 1024), enabled.GetBoolean(), paths,
            baselines ? ScreenBaselineSet.Read(root.GetProperty("screenBaselines"), width, height) : null);
    }
}
