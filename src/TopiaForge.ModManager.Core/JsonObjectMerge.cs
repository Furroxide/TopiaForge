using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Replaces selected JSON object members while retaining unrelated members as raw JSON. Contract-aware merges
    /// recurse through typed schema objects without treating collection data as schema. This remains dependency-free
    /// so the netstandard loader can preserve fields introduced by newer mod versions.
    /// </summary>
    public static class JsonObjectMerge
    {
        public static string Merge(
            string json,
            IReadOnlyDictionary<string, string> replacementJsonByName)
        {
            if (replacementJsonByName == null)
            {
                throw new ArgumentNullException(nameof(replacementJsonByName));
            }

            var properties = Parse(json ?? string.Empty);
            var writtenReplacements = new HashSet<string>(StringComparer.Ordinal);
            var builder = new StringBuilder(Math.Max(32, (json?.Length ?? 0) + 64));
            builder.Append('{');
            var needsComma = false;

            foreach (var property in properties)
            {
                var hasReplacement = replacementJsonByName.TryGetValue(property.Name, out var replacement);
                if (hasReplacement && !writtenReplacements.Add(property.Name))
                {
                    // Collapse duplicate instances of a replaced key; emitting two values would leave the
                    // consumer-dependent "first or last wins" ambiguity in place.
                    continue;
                }

                AppendComma(builder, ref needsComma);
                builder.Append(property.RawName).Append(':')
                    .Append(hasReplacement ? RequireReplacement(property.Name, replacement) : property.RawValue);
            }

            foreach (var replacement in replacementJsonByName)
            {
                if (!writtenReplacements.Add(replacement.Key))
                {
                    continue;
                }

                AppendComma(builder, ref needsComma);
                builder.Append(JsonUtil.Serialize(replacement.Key)).Append(':')
                    .Append(RequireReplacement(replacement.Key, replacement.Value));
            }

            return builder.Append('}').ToString();
        }

        /// <summary>
        /// Merges a newly serialized typed contract into an older document. Members known to the contract are
        /// replaced (or removed when serialization intentionally omitted them); unknown members are retained.
        /// Nested contract objects follow the same rule, while collections and dictionaries are replaced as
        /// complete values so removing an item from a typed config remains possible.
        /// </summary>
        public static string MergeSerializedContract(
            string existingJson,
            string replacementJson,
            Type contractType)
        {
            if (contractType == null)
            {
                throw new ArgumentNullException(nameof(contractType));
            }

            var schema = JsonContractSchema.Build(contractType);
            return MergeContractObjects(existingJson, replacementJson, schema);
        }

        /// <summary>Validates that a string is one complete, strict JSON object.</summary>
        public static void ValidateObject(string json)
        {
            Parse(json ?? string.Empty);
        }

        /// <summary>Returns the decoded top-level property names of one strict JSON object.</summary>
        internal static IReadOnlyList<string> ReadPropertyNames(string json)
        {
            return Parse(json ?? string.Empty).Select(property => property.Name).ToList();
        }

        /// <summary>Returns decoded names and strict raw values for one JSON object.</summary>
        internal static IReadOnlyList<RawJsonProperty> ReadProperties(string json)
        {
            return Parse(json ?? string.Empty)
                .Select(property => new RawJsonProperty(property.Name, property.RawValue))
                .ToList();
        }

        /// <summary>Returns strict raw elements for one JSON array.</summary>
        internal static IReadOnlyList<string> ReadArrayValues(string json)
        {
            var values = new List<string>();
            var index = 0;
            SkipWhitespace(json, ref index);
            Require(json, ref index, '[');
            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, ']'))
            {
                SkipWhitespace(json, ref index);
                RequireEnd(json, index);
                return values;
            }

            while (true)
            {
                SkipWhitespace(json, ref index);
                var valueStart = index;
                ScanValue(json, ref index);
                var valueEnd = index;
                while (valueEnd > valueStart && IsJsonWhitespace(json[valueEnd - 1]))
                {
                    valueEnd--;
                }

                if (valueEnd == valueStart)
                {
                    throw new FormatException("JSON array contains an empty value.");
                }

                var rawValue = json.Substring(valueStart, valueEnd - valueStart);
                StrictJsonValueValidator.Validate(rawValue);
                values.Add(rawValue);
                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, ']'))
                {
                    break;
                }

                Require(json, ref index, ',');
            }

            SkipWhitespace(json, ref index);
            RequireEnd(json, index);
            return values;
        }

        private static string MergeContractObjects(
            string existingJson,
            string replacementJson,
            JsonContractSchema schema)
        {
            var existingProperties = Parse(existingJson ?? string.Empty);
            var replacementProperties = Parse(replacementJson ?? string.Empty);
            var replacements = new Dictionary<string, Property>(StringComparer.Ordinal);
            foreach (var replacement in replacementProperties)
            {
                replacements[replacement.Name] = replacement;
            }

            var written = new HashSet<string>(StringComparer.Ordinal);
            var builder = new StringBuilder(Math.Max(
                32,
                (existingJson?.Length ?? 0) + (replacementJson?.Length ?? 0) + 16));
            builder.Append('{');
            var needsComma = false;

            foreach (var existing in existingProperties)
            {
                if (replacements.TryGetValue(existing.Name, out var replacement))
                {
                    if (!written.Add(existing.Name))
                    {
                        continue;
                    }

                    AppendComma(builder, ref needsComma);
                    builder.Append(replacement.RawName).Append(':')
                        .Append(MergeContractValue(existing, replacement, schema));
                    continue;
                }

                if (schema.KnownMembers.Contains(existing.Name))
                {
                    // EmitDefaultValue=false and similar serializer policies deliberately omitted this known
                    // member. Do not resurrect its stale value while preserving unrelated forward fields.
                    continue;
                }

                AppendComma(builder, ref needsComma);
                builder.Append(existing.RawName).Append(':').Append(existing.RawValue);
            }

            foreach (var replacement in replacementProperties)
            {
                if (!written.Add(replacement.Name))
                {
                    continue;
                }

                var selected = replacements[replacement.Name];
                AppendComma(builder, ref needsComma);
                builder.Append(selected.RawName).Append(':').Append(selected.RawValue);
            }

            return builder.Append('}').ToString();
        }

        private static string MergeContractValue(
            Property existing,
            Property replacement,
            JsonContractSchema schema)
        {
            if (schema.ObjectMembers.TryGetValue(replacement.Name, out var nestedSchema)
                && IsObjectValue(existing.RawValue)
                && IsObjectValue(replacement.RawValue))
            {
                return MergeContractObjects(existing.RawValue, replacement.RawValue, nestedSchema);
            }

            return replacement.RawValue;
        }

        private static bool IsObjectValue(string json)
        {
            var index = 0;
            SkipWhitespace(json, ref index);
            return index < json.Length && json[index] == '{';
        }

        private static string RequireReplacement(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Replacement JSON is required for '" + name + "'.", nameof(value));
            }

            var required = value!;
            ValidateValue(name, required);
            return required;
        }

        private static void AppendComma(StringBuilder builder, ref bool needsComma)
        {
            if (needsComma)
            {
                builder.Append(',');
            }

            needsComma = true;
        }

        private static List<Property> Parse(string json)
        {
            var properties = new List<Property>();
            var index = 0;
            SkipWhitespace(json, ref index);
            Require(json, ref index, '{');
            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, '}'))
            {
                SkipWhitespace(json, ref index);
                RequireEnd(json, index);
                return properties;
            }

            while (true)
            {
                SkipWhitespace(json, ref index);
                var nameStart = index;
                ScanString(json, ref index);
                var rawName = json.Substring(nameStart, index - nameStart);
                StrictJsonValueValidator.Validate(rawName);
                var name = JsonUtil.Deserialize<string>(rawName);

                SkipWhitespace(json, ref index);
                Require(json, ref index, ':');
                SkipWhitespace(json, ref index);
                var valueStart = index;
                ScanValue(json, ref index);
                var valueEnd = index;
                while (valueEnd > valueStart && IsJsonWhitespace(json[valueEnd - 1]))
                {
                    valueEnd--;
                }

                if (valueEnd == valueStart)
                {
                    throw new FormatException("JSON object member '" + name + "' has no value.");
                }

                var rawValue = json.Substring(valueStart, valueEnd - valueStart);
                ValidateValue(name, rawValue);
                properties.Add(new Property(name, rawName, rawValue));
                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, '}'))
                {
                    break;
                }

                Require(json, ref index, ',');
            }

            SkipWhitespace(json, ref index);
            RequireEnd(json, index);
            return properties;
        }

        private static void ScanValue(string json, ref int index)
        {
            if (index >= json.Length)
            {
                throw new FormatException("Unexpected end of JSON value.");
            }

            if (json[index] == '"')
            {
                ScanString(json, ref index);
                return;
            }

            if (json[index] == '{' || json[index] == '[')
            {
                var objectDepth = 0;
                var arrayDepth = 0;
                var inString = false;
                var escaped = false;
                while (index < json.Length)
                {
                    var character = json[index++];
                    if (inString)
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (character == '\\')
                        {
                            escaped = true;
                        }
                        else if (character == '"')
                        {
                            inString = false;
                        }

                        continue;
                    }

                    switch (character)
                    {
                        case '"': inString = true; break;
                        case '{': objectDepth++; break;
                        case '}': objectDepth--; break;
                        case '[': arrayDepth++; break;
                        case ']': arrayDepth--; break;
                    }

                    if (objectDepth < 0 || arrayDepth < 0)
                    {
                        throw new FormatException("Unbalanced JSON value.");
                    }

                    if (objectDepth == 0 && arrayDepth == 0)
                    {
                        return;
                    }
                }

                throw new FormatException("Unterminated JSON object or array value.");
            }

            while (index < json.Length && json[index] != ',' && json[index] != '}' && json[index] != ']')
            {
                index++;
            }
        }

        private static void ValidateValue(string name, string rawValue)
        {
            try
            {
                // The scanner finds the exact raw slice so unknown fields can be retained byte-for-byte. Validate
                // it with a strict grammar first: DataContractJsonSerializer accepts non-JSON values such as NaN
                // and leading-zero numbers, which would make Dart/standard JSON readers reject the merged file.
                StrictJsonValueValidator.Validate(rawValue);
            }
            catch (FormatException ex)
            {
                throw new FormatException("JSON object member '" + name + "' has an invalid value.", ex);
            }
        }

        private static void ScanString(string json, ref int index)
        {
            Require(json, ref index, '"');
            var escaped = false;
            while (index < json.Length)
            {
                var character = json[index++];
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    return;
                }
            }

            throw new FormatException("Unterminated JSON string.");
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && IsJsonWhitespace(json[index]))
            {
                index++;
            }
        }

        private static bool IsJsonWhitespace(char value)
        {
            // RFC 8259 permits exactly these four characters between tokens. char.IsWhiteSpace also accepts
            // form-feed, non-breaking space, and other Unicode separators that standard JSON readers reject.
            return value == ' ' || value == '\t' || value == '\r' || value == '\n';
        }

        private static bool TryConsume(string json, ref int index, char expected)
        {
            if (index >= json.Length || json[index] != expected)
            {
                return false;
            }

            index++;
            return true;
        }

        private static void Require(string json, ref int index, char expected)
        {
            if (!TryConsume(json, ref index, expected))
            {
                throw new FormatException("Expected '" + expected + "' at JSON offset " + index + ".");
            }
        }

        private static void RequireEnd(string json, int index)
        {
            if (index != json.Length)
            {
                throw new FormatException("Unexpected content after the top-level JSON object.");
            }
        }

        private sealed class Property
        {
            public Property(string name, string rawName, string rawValue)
            {
                Name = name;
                RawName = rawName;
                RawValue = rawValue;
            }

            public string Name { get; }
            public string RawName { get; }
            public string RawValue { get; }
        }

        internal sealed class RawJsonProperty
        {
            public RawJsonProperty(string name, string rawValue)
            {
                Name = name;
                RawValue = rawValue;
            }

            public string Name { get; }
            public string RawValue { get; }
        }

    }
}
