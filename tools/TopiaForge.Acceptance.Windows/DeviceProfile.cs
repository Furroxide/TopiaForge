using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

internal sealed record DeviceProfile(int Width, int Height, int Dpi, string DisplayName, string AudioEndpointId, bool LoopbackEnabled, string[] PersistenceFiles)
{
    internal static DeviceProfile Read(string path)
    {
        var root = BoundedJson.Read(path);
        BoundedJson.Keys(root, "schemaVersion", "kind", "operatorReference", "reviewerEvidence", "display", "audio", "persistenceFiles");
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
        return new(BoundedJson.Integer(display, "width", 640, 7680), BoundedJson.Integer(display, "height", 480, 4320), BoundedJson.Integer(display, "dpi", 96, 384), BoundedJson.Text(display, "deviceName", 128), BoundedJson.Text(audio, "endpointId", 1024), enabled.GetBoolean(), paths);
    }
}
