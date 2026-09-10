using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Core
{
    public static partial class ModManifestJson
    {
        // These are the schema's structural rules. Semantic ownership, dependency,
        // package-path portability, and pairing checks remain in the validator.
        private static readonly Regex ContributionPortablePath = new Regex(
            @"^(?!/)(?!.*\\)(?!.*:)(?!.*[\u0000-\u001F])(?!.*(?:^|/)(?:\.{1,2}|[Cc][Oo][Nn]|[Pp][Rr][Nn]|[Aa][Uu][Xx]|[Nn][Uu][Ll]|[Cc][Oo][Mm][1-9]|[Ll][Pp][Tt][1-9])(?:\.|/|$))(?!.*[. ](?:/|$))(?!.*//).+$",
            RegexOptions.CultureInvariant);

        private static void ValidateRawWorld(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path)
        {
            RawDeclaration(fields, path, "name");
            RawStrings(fields, path, "transitions", 1, 2, RawTransition);
            RawStrings(fields, path, "openTo", 0, 32, RawDeclarationId);
            RawBoolean(fields, path, "openToAnyCompatible");
            if (RawValue(fields, "openToAnyCompatible") == "true")
            {
                RawForbidden(fields, path, "openTo");
            }

            var contentPath = path + ".content";
            var content = ReadObject(contentPath, RequireRawProperty(fields, "content"));
            var kind = RawEnum(content, contentPath, "kind", "bundle", "provider", "game-scene", "discovered");
            // Reserve the dot and one nonempty suffix character for a legal 96-character instance id.
            if (kind == ModWorldContent.DiscoveredKind) RawText(fields, path, "id", 4, 94);
            RawPath(content, contentPath, "bundle", dll: false);
            RawText(content, contentPath, "prefab", 1, 512);
            RawText(content, contentPath, "sceneName", 1, 128);
            RawBinding(content, contentPath, "implementation");
            var required = kind == "bundle" ? new[] { "bundle", "prefab" }
                : kind == "game-scene" ? new[] { "sceneName" } : new[] { "implementation" };
            foreach (var field in new[] { "bundle", "prefab", "sceneName", "implementation" })
            {
                if (required.Contains(field)) RawRequired(content, contentPath, field);
                else RawForbidden(content, contentPath, field);
            }

            var spawnPath = path + ".spawn";
            var spawn = ReadObject(spawnPath, RequireRawProperty(fields, "spawn"));
            var spawnKind = RawEnum(spawn, spawnPath, "kind", "authored-marker", "provider-default");
            RawText(spawn, spawnPath, "markerName", 1, 128);
            if (spawnKind == "authored-marker") RawRequired(spawn, spawnPath, "markerName");
            else RawForbidden(spawn, spawnPath, "markerName");
        }

        private static void ValidateRawGamemode(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path)
        {
            RawDeclaration(fields, path, "name");
            RawBinding(fields, path, "implementation");
            RawEnum(fields, path, "sceneChangePolicy", "end-session", "keep-controller");
            var raw = RawValue(fields, "worldRequirements");
            if (raw == null) return;
            var requirementsPath = path + ".worldRequirements";
            var requirements = ReadObject(requirementsPath, raw);
            RawStrings(requirements, requirementsPath, "transitions", 1, 2, RawTransition);
            RawEnum(requirements, requirementsPath, "spawn", "authored-marker", "any");
        }

        private static void ValidateRawTarget(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path)
        {
            RawDeclaration(fields, path, "title");
            RawIdentifier(fields, path, "gamemode");
            RawEnum(fields, path, "transition", "auto", "scene-replacement", "additive-arena", "player-choice");
            var sortKey = RawValue(fields, "sortKey");
            if (sortKey != null && (!double.TryParse(sortKey, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || double.IsNaN(value) || double.IsInfinity(value)
                || value < 0 || value > 999 || Math.Truncate(value) != value))
            {
                RawFailure(path + ".sortKey", "must be an integer between 0 and 999.");
            }

            var policyPath = path + ".world";
            var policy = ReadObject(policyPath, RequireRawProperty(fields, "world"));
            var kind = RawEnum(policy, policyPath, "policy", "fixed", "list", "open");
            RawIdentifier(policy, policyPath, "default");
            RawStrings(policy, policyPath, "allow", 1, 64, RawDeclarationId);
            RawBoolean(policy, policyPath, "allowPlayerOverride");
            if (kind == "list") RawRequired(policy, policyPath, "allow");
            else RawForbidden(policy, policyPath, "allow");
            if (kind == "fixed" && RawValue(policy, "allowPlayerOverride") == "true")
            {
                RawFailure(policyPath + ".allowPlayerOverride", "must be false for the fixed policy.");
            }
        }

        private static void RawDeclaration(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path, string label)
        {
            RawIdentifier(fields, path, "id");
            RawText(fields, path, label, 1, 128);
            RawText(fields, path, "description", 0, 1024);
        }

        private static void RawBinding(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path, string name)
        {
            var raw = RawValue(fields, name);
            if (raw == null) return;
            var bindingPath = path + "." + name;
            var binding = ReadObject(bindingPath, raw);
            RawPath(binding, bindingPath, "assembly", dll: true);
            var type = RawText(binding, bindingPath, "type", 3, 512);
            if (type != null && !ManifestContributionValidator.IsValidTypeName(type))
            {
                RawFailure(bindingPath + ".type", "must be an ASCII namespace-qualified CLR type name.");
            }
        }

        private static void RawPath(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path, string name, bool dll)
        {
            var value = RawText(fields, path, name, 1, 1024);
            if (value != null && (!ContributionPortablePath.IsMatch(value)
                || (dll && !value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))))
            {
                RawFailure(path + "." + name, dll ? "must be a portable .dll path." : "must be a portable path.");
            }
        }

        private static void RawIdentifier(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path, string name)
        {
            var value = RawText(fields, path, name, 4, 96);
            if (value != null) RawDeclarationId(value, path + "." + name);
        }

        private static void RawDeclarationId(string value, string path)
        {
            if (value.Length < 4 || value.Length > 96 || !ManifestContributionValidator.HasDeclarationIdGrammar(value))
            {
                RawFailure(path, "must be a 4-96 character ASCII declaration identifier.");
            }
        }

        private static string? RawEnum(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path, string name, params string[] values)
        {
            var raw = RawValue(fields, name);
            if (raw == null) return null;
            var value = RawString(raw, path + "." + name);
            if (!values.Contains(value, StringComparer.Ordinal))
            {
                RawFailure(path + "." + name, "must be one of " + string.Join(", ", values) + ".");
            }

            return value;
        }

        private static void RawTransition(string value, string path)
        {
            if (value != ModTransitions.SceneReplacement && value != ModTransitions.AdditiveArena)
            {
                RawFailure(path, "must be scene-replacement or additive-arena.");
            }
        }

        private static void RawBoolean(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path, string name)
        {
            var value = RawValue(fields, name);
            if (value != null && value != "true" && value != "false")
            {
                RawFailure(path + "." + name, "must be a boolean.");
            }
        }

        private static string? RawText(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path, string name, int minimum, int maximum)
        {
            var raw = RawValue(fields, name);
            if (raw == null) return null;
            var value = RawString(raw, path + "." + name);
            var length = ManifestContributionValidator.UnicodeScalarLength(value);
            if (length < minimum || length > maximum)
            {
                RawFailure(path + "." + name, "must contain between " + minimum + " and " + maximum + " characters.");
            }

            return value;
        }

        private static string RawString(string raw, string path)
        {
            // DataContractJsonSerializer would coerce numbers/booleans and accept null.
            if (raw.Length == 0 || raw[0] != '"') RawFailure(path, "must be a string.");
            return JsonUtil.Deserialize<string>(raw);
        }

        private static void RawStrings(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path, string name,
            int minimum, int maximum, Action<string, string> validateItem)
        {
            var raw = RawValue(fields, name);
            if (raw == null) return;
            var arrayPath = path + "." + name;
            IReadOnlyList<string> values;
            try { values = JsonObjectMerge.ReadArrayValues(raw); }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Manifest field '" + arrayPath + "' must be an array.", exception);
            }

            if (values.Count < minimum || values.Count > maximum)
            {
                RawFailure(arrayPath, "must contain between " + minimum + " and " + maximum + " entries.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < values.Count; index++)
            {
                var itemPath = arrayPath + "[" + index + "]";
                var value = RawString(values[index], itemPath);
                if (!seen.Add(value)) RawFailure(arrayPath, "must contain unique entries.");
                validateItem(value, itemPath);
            }
        }

        private static string? RawValue(IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string name) =>
            fields.FirstOrDefault(field => field.Name == name)?.RawValue.Trim();

        private static void RawRequired(IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path, string name)
        {
            if (RawValue(fields, name) == null) RawFailure(path, "is missing required field '" + name + "'.");
        }

        private static void RawForbidden(IReadOnlyList<JsonObjectMerge.RawJsonProperty> fields, string path, string name)
        {
            if (RawValue(fields, name) != null) RawFailure(path, "cannot contain field '" + name + "' for this policy or kind.");
        }

        private static void RawFailure(string path, string message) =>
            throw new InvalidDataException("Manifest field '" + path + "' " + message);
    }
}
