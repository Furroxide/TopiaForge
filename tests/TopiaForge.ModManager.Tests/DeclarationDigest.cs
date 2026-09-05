using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Every contribution field, with null for absence and JSON values kept distinct.</summary>
    internal static class DeclarationDigest
    {
        private static readonly IReadOnlyDictionary<string, string[]> Fields =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["worlds"] = new[] { "id", "name", "description", "content", "transitions", "spawn", "openTo", "openToAnyCompatible" },
                ["gamemodes"] = new[] { "id", "name", "description", "implementation", "worldRequirements", "sceneChangePolicy" },
                ["launchTargets"] = new[] { "id", "title", "description", "sortKey", "gamemode", "world", "transition" },
                ["content"] = new[] { "kind", "bundle", "prefab", "implementation", "sceneName" },
                ["implementation"] = new[] { "assembly", "type" },
                ["spawn"] = new[] { "kind", "markerName" },
                ["worldRequirements"] = new[] { "transitions", "spawn" },
                ["world"] = new[] { "policy", "default", "allow", "allowPlayerOverride" }
            };

        public static JsonElement Of(ModManifest manifest)
        {
            using var document = JsonDocument.Parse(manifest.Contributions == null
                ? "{}" : JsonUtil.Serialize(manifest.Contributions));
            var result = new Dictionary<string, object?>();
            foreach (var kind in new[] { "worlds", "gamemodes", "launchTargets" })
            {
                result[kind] = document.RootElement.TryGetProperty(kind, out var entries)
                    ? entries.EnumerateArray().Select(entry => Normalize(entry, kind)).ToArray()
                    : Array.Empty<object>();
            }
            using var normalized = JsonDocument.Parse(JsonSerializer.Serialize(result));
            return normalized.RootElement.Clone();
        }

        private static object Normalize(JsonElement source, string kind)
        {
            var result = new Dictionary<string, object?>();
            foreach (var property in source.EnumerateObject())
            {
                if (!Fields[kind].Contains(property.Name, StringComparer.Ordinal))
                    throw new InvalidOperationException("Normalization is missing contribution field " + kind + "." + property.Name);
            }
            foreach (var field in Fields[kind])
            {
                result[field] = !source.TryGetProperty(field, out var value) ? null
                    : value.ValueKind == JsonValueKind.Object && Fields.ContainsKey(field)
                        ? Normalize(value, field) : value.Clone();
            }
            return result;
        }

        public static bool Equal(JsonElement left, JsonElement right)
        {
            if (left.ValueKind != right.ValueKind) return false;
            if (left.ValueKind == JsonValueKind.Object)
            {
                var properties = left.EnumerateObject().ToArray();
                return properties.Length == right.EnumerateObject().Count()
                    && properties.All(property => right.TryGetProperty(property.Name, out var value)
                        && Equal(property.Value, value));
            }
            if (left.ValueKind == JsonValueKind.Array)
            {
                var a = left.EnumerateArray().ToArray();
                var b = right.EnumerateArray().ToArray();
                return a.Length == b.Length && a.Zip(b, Equal).All(equal => equal);
            }
            if (left.ValueKind == JsonValueKind.Number)
                return left.GetDecimal() == right.GetDecimal();
            if (left.ValueKind == JsonValueKind.String)
                return left.GetString() == right.GetString();
            return true;
        }
    }
}
