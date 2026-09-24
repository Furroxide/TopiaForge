using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TopiaForge.Acceptance.Windows;

internal static class BoundedJson
{
    internal const int MaximumBytes = 256 * 1024;
    internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal static JsonElement Read(string path, int maximum = MaximumBytes) => Parse(ReadBytes(path, maximum), maximum);
    internal static byte[] ReadBytes(string path, int maximum = MaximumBytes)
    {
        PhysicalPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("Document exceeds its bound.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, Math.Min(buffer.Length, maximum + 1 - (int)output.Length))) != 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > maximum) throw new InvalidDataException("Document grew beyond its bound.");
        }
        return output.ToArray();
    }
    internal static JsonElement Parse(byte[] bytes, int maximum = MaximumBytes)
    {
        if (bytes.Length > maximum) throw new InvalidDataException("JSON exceeds its bound.");
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 24 });
        var objects = new Stack<HashSet<string>>();
        var nodes = 0;
        while (reader.Read())
        {
            if (++nodes > 24000) throw new InvalidDataException("JSON node limit exceeded.");
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                throw new InvalidDataException("Duplicate JSON property.");
        }
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        return document.RootElement.Clone();
    }
    internal static void Keys(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected an object.");
        var expected = keys.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Remove(property.Name)) throw new InvalidDataException("Unknown or duplicate field: " + property.Name);
        if (expected.Count != 0) throw new InvalidDataException("Missing fields: " + string.Join(",", expected));
    }
    internal static string Text(JsonElement value, string name, int maximum = 32767)
    {
        var field = value.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String) throw new InvalidDataException("Expected text: " + name);
        var text = field.GetString()!;
        if (text.Length == 0 || text.Length > maximum || text.Any(char.IsControl)) throw new InvalidDataException("Invalid text: " + name);
        return text;
    }
    internal static int Integer(JsonElement value, string name, int minimum, int maximum)
    {
        var field = value.GetProperty(name);
        if (!Regex.IsMatch(field.GetRawText(), "^[0-9]+$") || !field.TryGetInt32(out var number) || number < minimum || number > maximum)
            throw new InvalidDataException("Invalid integer: " + name);
        return number;
    }
    internal static string Digest(JsonElement value, string name)
    {
        var digest = Text(value, name, 64);
        if (!Regex.IsMatch(digest, "^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid digest: " + name);
        return digest;
    }
    internal static string Hash(string path)
    {
        PhysicalPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    internal static string PhysicalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("Absolute local path required.");
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            if (!Regex.IsMatch(full, "^[A-Za-z]:\\\\") || full[3..].Contains(':') || full.Contains('/')
                || full[3..].Split('\\').Any(part => part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
                throw new InvalidDataException("Noncanonical or alternate-stream Windows path.");
            var device = new System.Text.StringBuilder(32768);
            if (NativeMethods.QueryDosDevice(full[..2], device, device.Capacity) == 0
                || !device.ToString().StartsWith("\\Device\\HarddiskVolume", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Mapped or nonphysical paths are not admitted.");
        }
        for (var current = full; current != null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points are not admitted.");
        return full;
    }
    internal static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    internal static string Child(string root, string relative)
    {
        if (!Regex.IsMatch(relative, "^[a-zA-Z0-9][a-zA-Z0-9._/-]{0,255}$") || relative.Split('/').Any(p => p is "." or ".." or "" || p.EndsWith('.') || Regex.IsMatch(p, "^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:[.]|$)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Unsafe artifact path.");
        var child = PhysicalPath(Path.Combine(root, relative));
        if (!child.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artifact escapes its admitted root.");
        return child;
    }
    internal static void WriteNew(string path, object value)
    {
        PhysicalPath(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, Options);
        stream.Flush(true);
    }
}
