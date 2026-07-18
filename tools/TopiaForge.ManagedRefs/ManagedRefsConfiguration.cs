using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TopiaForge.ManagedRefs;

internal sealed record PublicArchive(string Platform, string Path, string Sha256);

internal sealed record PublicBuildConfiguration(
    int BuildId,
    string BaseUrl,
    string ManifestUrl,
    string SourcePlatform,
    IReadOnlyDictionary<string, PublicArchive> Archives)
{
    private const int MaxConfigurationBytes = 64 * 1024;
    private static readonly HashSet<string> AllowedRootProperties = new(StringComparer.Ordinal)
    {
        "buildId",
        "baseUrl",
        "manifestUrl",
        "sourcePlatform",
        "archives",
    };

    internal static PublicBuildConfiguration Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Robotopia game build config was not found: {path}");
        }

        try
        {
            var json = ReadBoundedUtf8(path, MaxConfigurationBytes);
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            return Parse(document.RootElement, path);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Robotopia game build config is not valid JSON: {path}. {exception.Message}",
                exception);
        }
    }

    private static string ReadBoundedUtf8(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[maxBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > maxBytes || stream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"Robotopia game build config exceeds the {maxBytes}-byte limit: {path}");
        }

        var offset = total >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(bytes, offset, total - offset);
    }

    internal static PublicBuildConfiguration Parse(JsonElement root, string sourceName)
    {
        RequireObject(root, "Robotopia game build config");
        ValidatePropertySet(root, AllowedRootProperties, "Robotopia game build config", exact: false);

        var buildId = ReadPositiveInt(root, "buildId", "Robotopia game build config");
        var baseUrl = ReadRequiredString(root, "baseUrl", "Robotopia game build config");
        var manifestUrl = ReadOptionalString(root, "manifestUrl", "Robotopia game build config");
        var sourcePlatform = ReadOptionalString(root, "sourcePlatform", "Robotopia game build config")
            .ToLowerInvariant();

        SafeHttpsUri.ParseAbsolute(baseUrl, "Robotopia public archive base URL");
        if (!string.IsNullOrEmpty(manifestUrl))
        {
            SafeHttpsUri.ParseAbsolute(manifestUrl, "Robotopia build manifest URL");
        }

        if (!root.TryGetProperty("archives", out var archiveElement))
        {
            throw new InvalidDataException("Robotopia game build config must define windows and mac archives.");
        }

        RequireObject(archiveElement, "Robotopia game build config archives");
        var expectedPlatforms = new HashSet<string>(StringComparer.Ordinal) { "windows", "mac" };
        ValidatePropertySet(
            archiveElement,
            expectedPlatforms,
            "Robotopia game build config archives",
            exact: true,
            "Robotopia game build config must contain exactly windows and mac archives.");

        var archives = new Dictionary<string, PublicArchive>(StringComparer.Ordinal);
        foreach (var platform in expectedPlatforms)
        {
            var entry = archiveElement.GetProperty(platform);
            RequireObject(entry, $"Archive entry '{platform}'");
            var expectedEntryProperties = new HashSet<string>(StringComparer.Ordinal) { "path", "sha256" };
            ValidatePropertySet(
                entry,
                expectedEntryProperties,
                $"Archive entry '{platform}'",
                exact: true,
                $"Archive entry '{platform}' must contain exactly path and sha256 in {sourceName}.");

            var archivePath = ReadRequiredString(entry, "path", $"Archive entry '{platform}'");
            var sha256 = ReadRequiredString(entry, "sha256", $"Archive entry '{platform}'")
                .ToLowerInvariant();
            Sha256Value.Validate(sha256, $"Archive entry '{platform}' has an invalid SHA-256 in {sourceName}.");
            archives.Add(platform, new PublicArchive(platform, archivePath, sha256));
        }

        if (!string.IsNullOrEmpty(sourcePlatform) && !archives.ContainsKey(sourcePlatform))
        {
            throw new InvalidDataException($"No archive entry named '{sourcePlatform}' exists in {sourceName}.");
        }

        return new PublicBuildConfiguration(buildId, baseUrl, manifestUrl, sourcePlatform, archives);
    }

    internal PublicArchive SelectArchive(string commandLinePlatform, string environmentPlatform, string sourceName)
    {
        var platform = FirstNonEmpty(commandLinePlatform, environmentPlatform, SourcePlatform, "windows")
            .ToLowerInvariant();
        if (!Archives.TryGetValue(platform, out var archive))
        {
            throw new InvalidDataException($"No archive entry named '{platform}' exists in {sourceName}.");
        }

        return archive;
    }

    private static string FirstNonEmpty(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        throw new InvalidOperationException("No non-empty value was supplied.");
    }

    internal static void RequireObject(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} must be a JSON object.");
        }
    }

    internal static string ReadRequiredString(JsonElement root, string property, string label)
    {
        if (!root.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{label} must define a non-empty '{property}' string.");
        }

        return value.GetString()!.Trim();
    }

    internal static string ReadOptionalString(JsonElement root, string property, string label)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{label} property '{property}' must be a string when present.");
        }

        return value.GetString()?.Trim() ?? string.Empty;
    }

    internal static int ReadPositiveInt(JsonElement root, string property, string label)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            throw new InvalidDataException($"{label} has an invalid {property}.");
        }

        int result;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt32(out result))
            {
                throw new InvalidDataException($"{label} has an invalid {property}.");
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            if (!int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out result))
            {
                throw new InvalidDataException($"{label} has an invalid {property}.");
            }
        }
        else
        {
            throw new InvalidDataException($"{label} has an invalid {property}.");
        }

        if (result <= 0)
        {
            throw new InvalidDataException($"{label} has an invalid {property}.");
        }

        return result;
    }

    internal static void ValidatePropertySet(
        JsonElement root,
        IReadOnlySet<string> allowed,
        string label,
        bool exact,
        string? exactError = null)
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!actual.Add(property.Name))
            {
                throw new InvalidDataException($"{label} contains duplicate property '{property.Name}'.");
            }

            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException(exactError ?? $"{label} contains unknown property '{property.Name}'.");
            }
        }

        if (exact && !actual.SetEquals(allowed))
        {
            throw new InvalidDataException(exactError ?? $"{label} does not contain the required properties.");
        }
    }
}

internal sealed record PublicBuildManifest(int BuildId, IReadOnlyDictionary<string, PublicArchive> Archives)
{
    internal static PublicBuildManifest Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            var root = document.RootElement;
            PublicBuildConfiguration.RequireObject(root, "Latest Robotopia build manifest");
            RejectDuplicateProperties(root, "Latest Robotopia build manifest");
            var buildId = PublicBuildConfiguration.ReadPositiveInt(
                root,
                "id",
                "Latest Robotopia build manifest");

            var archives = new Dictionary<string, PublicArchive>(StringComparer.Ordinal);
            foreach (var platform in new[] { "windows", "mac" })
            {
                if (!root.TryGetProperty(platform, out var entry) || entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                RejectDuplicateProperties(entry, $"Latest manifest {platform} archive");

                var path = PublicBuildConfiguration.ReadRequiredString(
                    entry,
                    "path",
                    $"Latest manifest {platform} archive");
                var sha256 = PublicBuildConfiguration.ReadRequiredString(
                    entry,
                    "sha256",
                    $"Latest manifest {platform} archive").ToLowerInvariant();
                Sha256Value.Validate(sha256, $"Latest manifest {platform} archive has an invalid SHA-256.");
                archives.Add(platform, new PublicArchive(platform, path, sha256));
            }

            return new PublicBuildManifest(buildId, archives);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Robotopia build manifest is not valid JSON. {exception.Message}",
                exception);
        }
    }

    internal void AssertMatches(PublicBuildConfiguration configuration)
    {
        if (BuildId != configuration.BuildId)
        {
            throw new InvalidDataException(
                $"Latest manifest reports build {BuildId}, while this checkout is pinned to build {configuration.BuildId}.");
        }

        foreach (var configuredArchive in configuration.Archives.Values)
        {
            if (!Archives.TryGetValue(configuredArchive.Platform, out var manifestArchive))
            {
                throw new InvalidDataException(
                    $"Latest manifest is missing the {configuredArchive.Platform} archive.");
            }

            if (!string.Equals(configuredArchive.Path, manifestArchive.Path, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Pinned {configuredArchive.Platform} path '{configuredArchive.Path}' does not match manifest path '{manifestArchive.Path}'.");
            }

            if (!string.Equals(configuredArchive.Sha256, manifestArchive.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Pinned {configuredArchive.Platform} SHA does not match latest manifest.");
            }
        }
    }

    internal void AssertSelectedArchiveMatches(PublicArchive configuredArchive)
    {
        if (!Archives.TryGetValue(configuredArchive.Platform, out var manifestArchive))
        {
            throw new InvalidDataException($"Latest manifest is missing the {configuredArchive.Platform} archive.");
        }

        if (!string.Equals(configuredArchive.Path, manifestArchive.Path, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Pinned {configuredArchive.Platform} path '{configuredArchive.Path}' does not match manifest path '{manifestArchive.Path}'.");
        }

        if (!string.Equals(configuredArchive.Sha256, manifestArchive.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Pinned {configuredArchive.Platform} SHA does not match latest manifest.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement root, string label)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException($"{label} contains duplicate property '{property.Name}'.");
            }
        }
    }
}

internal static partial class Sha256Value
{
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidSha256Regex();

    internal static bool IsValid(string value) => ValidSha256Regex().IsMatch(value);

    internal static void Validate(string value, string error)
    {
        if (!IsValid(value))
        {
            throw new InvalidDataException(error);
        }
    }
}

internal static class SafeHttpsUri
{
    internal static Uri Join(string baseUrl, string path)
    {
        string candidate;
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute))
        {
            candidate = absolute.AbsoluteUri;
        }
        else
        {
            if (path.Contains('?', StringComparison.Ordinal) ||
                path.Contains('#', StringComparison.Ordinal) ||
                HasParentSegment(path))
            {
                throw new InvalidDataException($"Robotopia archive path is not a safe relative URL path: {path}");
            }

            candidate = $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/', '\\')}";
        }

        return ParseAbsolute(candidate, "Robotopia download URL");
    }

    internal static Uri ParseAbsolute(string value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException(
                $"{label} must be a credential-free HTTPS URL without a query string or fragment.");
        }

        return uri;
    }

    private static bool HasParentSegment(string value)
    {
        var normalized = value.Replace('\\', '/');
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                return true;
            }
        }

        return false;
    }
}
