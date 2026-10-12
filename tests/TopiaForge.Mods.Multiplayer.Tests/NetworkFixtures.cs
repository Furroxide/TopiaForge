using System.Text.Json;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Reads the shared fixtures under tests/fixtures/multiplayer, which the launcher's Dart tests and the control plane's
/// TypeScript tests replay too, so all three implementations are held to the same bytes.
/// </summary>
internal static class NetworkFixtures
{
    internal static JsonElement Load(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), "tests", "fixtures", "multiplayer", relativePath);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    internal static string Text(this JsonElement element, string name) => element.GetProperty(name).GetString()!;

    internal static string? OptionalText(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static byte[] Hex(string text) => Convert.FromHexString(text);

    internal static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TopiaForge.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binaries.");
    }
}
