using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>A JSON value in the canonical subset the multiplayer documents use.</summary>
    internal abstract class JsonNode
    {
    }

    internal sealed class JsonObjectNode : JsonNode
    {
        private readonly SortedDictionary<string, JsonNode> members = new SortedDictionary<string, JsonNode>(StringComparer.Ordinal);

        internal IEnumerable<KeyValuePair<string, JsonNode>> Members => members;

        internal int Count => members.Count;

        internal JsonObjectNode Add(string key, JsonNode value)
        {
            if (!CanonicalJson.IsKey(key)) throw new ArgumentException("A key is 1 to 64 ASCII letters and digits.", nameof(key));
            if (members.ContainsKey(key)) throw new ArgumentException("A key appears once.", nameof(key));
            members.Add(key, value ?? throw new ArgumentNullException(nameof(value)));
            return this;
        }

        internal JsonObjectNode Add(string key, string? value) => Add(key, value == null ? JsonNullNode.Instance : new JsonStringNode(value));

        internal JsonObjectNode Add(string key, long value) => Add(key, new JsonIntegerNode(value));

        internal JsonObjectNode Add(string key, bool value) => Add(key, value ? JsonBooleanNode.True : JsonBooleanNode.False);

        internal bool TryGet(string key, out JsonNode value) => members.TryGetValue(key, out value!);

        /// <summary>Whether the object has exactly these keys, so a document with an unknown member is refused.</summary>
        internal bool HasExactly(params string[] keys)
        {
            if (keys.Length != members.Count) return false;
            foreach (var key in keys)
            {
                if (!members.ContainsKey(key)) return false;
            }

            return true;
        }
    }

    internal sealed class JsonArrayNode : JsonNode
    {
        internal JsonArrayNode(IReadOnlyList<JsonNode> items)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
        }

        internal IReadOnlyList<JsonNode> Items { get; }
    }

    internal sealed class JsonStringNode : JsonNode
    {
        internal JsonStringNode(string value)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        internal string Value { get; }
    }

    internal sealed class JsonIntegerNode : JsonNode
    {
        internal JsonIntegerNode(long value)
        {
            if (value > CanonicalJson.MaximumSafeInteger || value < -CanonicalJson.MaximumSafeInteger)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Integers stay within the range every language reads exactly.");
            }

            Value = value;
        }

        internal long Value { get; }
    }

    internal sealed class JsonBooleanNode : JsonNode
    {
        internal static readonly JsonBooleanNode True = new JsonBooleanNode(true);
        internal static readonly JsonBooleanNode False = new JsonBooleanNode(false);

        private JsonBooleanNode(bool value)
        {
            Value = value;
        }

        internal bool Value { get; }
    }

    internal sealed class JsonNullNode : JsonNode
    {
        internal static readonly JsonNullNode Instance = new JsonNullNode();

        private JsonNullNode()
        {
        }
    }

    /// <summary>
    /// Canonical JSON for signed and shared documents: a subset of RFC 8785 that the game, the launcher and the control
    /// plane all produce byte for byte. Objects, arrays, strings, booleans, null and integers within plus or minus
    /// 2^53 - 1; keys of ASCII letters and digits in ordinal order; no whitespace; strings escape only the quote,
    /// the backslash and control characters, and carry everything else as UTF-8.
    /// </summary>
    internal static class CanonicalJson
    {
        internal const long MaximumSafeInteger = 9_007_199_254_740_991;
        internal const int MaximumDepth = 16;
        private const int MaximumKeyLength = 64;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static bool IsKey(string? key)
        {
            if (string.IsNullOrEmpty(key) || key!.Length > MaximumKeyLength) return false;
            foreach (var character in key)
            {
                var letter = (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z');
                if (!letter && (character < '0' || character > '9')) return false;
            }

            return true;
        }

        internal static byte[] Serialize(JsonNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var builder = new StringBuilder(256);
            Write(builder, node, 0);
            return StrictUtf8.GetBytes(builder.ToString());
        }

        /// <summary>
        /// Parses <paramref name="bytes"/> and accepts it only when it is already canonical: re-serializing the result
        /// must give the same bytes back, so a signature never covers an ambiguous document.
        /// </summary>
        internal static bool TryParse(byte[] bytes, int maximumBytes, out JsonNode node, out string error) =>
            TryRead(bytes, maximumBytes, anyLayout: false, out node, out error);

        /// <summary>
        /// Parses <paramref name="bytes"/> in any layout another program may leave a record of this game's own in: a UTF-8
        /// byte-order mark, white space between values, and members in any order. It does not insist on canonical bytes,
        /// so a needless escape reads as its character, but it refuses what <see cref="TryParse"/> refuses before that
        /// comparison: malformed UTF-8, a repeated key, a number that is not an integer in range, nesting past
        /// <see cref="MaximumDepth"/>, anything after the document, and more than <paramref name="maximumBytes"/>. Only
        /// this game's identity, ban list and known-hosts record read this way. A signed or shared document is read with
        /// <see cref="TryParse"/>, so a signature still never covers anything but canonical bytes.
        /// </summary>
        internal static bool TryParseAnyLayout(byte[] bytes, int maximumBytes, out JsonNode node, out string error) =>
            TryRead(bytes, maximumBytes, anyLayout: true, out node, out error);

        private static bool TryRead(byte[] bytes, int maximumBytes, bool anyLayout, out JsonNode node, out string error)
        {
            node = JsonNullNode.Instance;
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length == 0 || bytes.Length > maximumBytes)
            {
                error = "The document is empty or larger than " + maximumBytes.ToString(CultureInfo.InvariantCulture) + " bytes.";
                return false;
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(bytes);
            }
            catch (ArgumentException)
            {
                error = "The document is not valid UTF-8.";
                return false;
            }

            // A byte-order mark decodes as U+FEFF, which only a document of any layout may start with.
            if (anyLayout && text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            var reader = new Reader(text, anyLayout);
            try
            {
                var parsed = reader.ReadValue(0);
                if (!reader.AtDocumentEnd()) throw new FormatException("Unexpected content after the document.");
                if (!anyLayout && !WireText.FixedTimeEquals(Serialize(parsed), bytes)) throw new FormatException("The document is not in canonical form.");
                node = parsed;
                error = string.Empty;
                return true;
            }
            catch (Exception exception) when (exception is FormatException || exception is ArgumentException)
            {
                error = exception.Message;
                return false;
            }
        }

        private static void Write(StringBuilder builder, JsonNode node, int depth)
        {
            if (depth > MaximumDepth) throw new ArgumentException("The document nests too deeply.");
            switch (node)
            {
                case JsonObjectNode objectNode:
                    builder.Append('{');
                    var first = true;
                    foreach (var member in objectNode.Members)
                    {
                        if (!first) builder.Append(',');
                        first = false;
                        WriteString(builder, member.Key);
                        builder.Append(':');
                        Write(builder, member.Value, depth + 1);
                    }

                    builder.Append('}');
                    break;
                case JsonArrayNode arrayNode:
                    builder.Append('[');
                    for (var index = 0; index < arrayNode.Items.Count; index++)
                    {
                        if (index > 0) builder.Append(',');
                        Write(builder, arrayNode.Items[index], depth + 1);
                    }

                    builder.Append(']');
                    break;
                case JsonStringNode stringNode:
                    WriteString(builder, stringNode.Value);
                    break;
                case JsonIntegerNode integerNode:
                    builder.Append(integerNode.Value.ToString(CultureInfo.InvariantCulture));
                    break;
                case JsonBooleanNode booleanNode:
                    builder.Append(booleanNode.Value ? "true" : "false");
                    break;
                case JsonNullNode _:
                    builder.Append("null");
                    break;
                default:
                    throw new ArgumentException("Unknown JSON node.");
            }
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u00").Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
                        }
                        else if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                        {
                            builder.Append(character).Append(value[++index]);
                        }
                        else if (char.IsSurrogate(character))
                        {
                            throw new ArgumentException("A string holds an unpaired surrogate.");
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        private sealed class Reader
        {
            private readonly string text;

            /// <summary>Whether white space may stand between values, as in a document of any layout; canonical bytes have none.</summary>
            private readonly bool anyLayout;
            private int position;

            internal Reader(string text, bool anyLayout)
            {
                this.text = text;
                this.anyLayout = anyLayout;
            }

            internal bool AtEnd => position == text.Length;

            /// <summary>Whether nothing but the white space the layout allows is left.</summary>
            internal bool AtDocumentEnd()
            {
                SkipLayout();
                return AtEnd;
            }

            internal JsonNode ReadValue(int depth)
            {
                if (depth > MaximumDepth) throw new FormatException("The document nests too deeply.");
                SkipLayout();
                if (AtEnd) throw new FormatException("The document ended early.");
                switch (text[position])
                {
                    case '{':
                        return ReadObject(depth);
                    case '[':
                        return ReadArray(depth);
                    case '"':
                        return new JsonStringNode(ReadString());
                    case 't':
                        Expect("true");
                        return JsonBooleanNode.True;
                    case 'f':
                        Expect("false");
                        return JsonBooleanNode.False;
                    case 'n':
                        Expect("null");
                        return JsonNullNode.Instance;
                    default:
                        return ReadInteger();
                }
            }

            private JsonObjectNode ReadObject(int depth)
            {
                position++;
                var result = new JsonObjectNode();
                if (TryConsume('}')) return result;
                while (true)
                {
                    SkipLayout();
                    if (AtEnd || text[position] != '"') throw new FormatException("An object key must be a string.");
                    var key = ReadString();
                    if (!IsKey(key)) throw new FormatException("An object key must be 1 to 64 ASCII letters and digits.");
                    if (result.TryGet(key, out _)) throw new FormatException("An object repeats a key.");
                    if (!TryConsume(':')) throw new FormatException("An object key must be followed by a colon.");
                    result.Add(key, ReadValue(depth + 1));
                    if (TryConsume('}')) return result;
                    if (!TryConsume(',')) throw new FormatException("Object members must be separated by commas.");
                }
            }

            private JsonArrayNode ReadArray(int depth)
            {
                position++;
                var items = new List<JsonNode>();
                if (TryConsume(']')) return new JsonArrayNode(items);
                while (true)
                {
                    items.Add(ReadValue(depth + 1));
                    if (TryConsume(']')) return new JsonArrayNode(items);
                    if (!TryConsume(',')) throw new FormatException("Array items must be separated by commas.");
                }
            }

            private string ReadString()
            {
                position++;
                var builder = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw new FormatException("A string is not terminated.");
                    var character = text[position++];
                    if (character == '"') break;
                    if (character < 0x20) throw new FormatException("A string holds a raw control character.");
                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }

                    if (AtEnd) throw new FormatException("A string ends inside an escape.");
                    var escape = text[position++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u': builder.Append(ReadUnicodeEscape()); break;
                        default: throw new FormatException("A string holds an unknown escape.");
                    }
                }

                var value = builder.ToString();
                for (var index = 0; index < value.Length; index++)
                {
                    if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) index++;
                    else if (char.IsSurrogate(value[index])) throw new FormatException("A string holds an unpaired surrogate.");
                }

                return value;
            }

            private char ReadUnicodeEscape()
            {
                if (position + 4 > text.Length) throw new FormatException("A unicode escape is truncated.");
                var value = 0;
                for (var index = 0; index < 4; index++)
                {
                    var digit = text[position++];
                    int nibble;
                    if (digit >= '0' && digit <= '9') nibble = digit - '0';
                    else if (digit >= 'a' && digit <= 'f') nibble = digit - 'a' + 10;
                    else if (digit >= 'A' && digit <= 'F') nibble = digit - 'A' + 10;
                    else throw new FormatException("A unicode escape holds a non-hexadecimal digit.");
                    value = (value << 4) | nibble;
                }

                return (char)value;
            }

            private JsonIntegerNode ReadInteger()
            {
                var start = position;
                if (TryConsume('-') && AtEnd) throw new FormatException("A number is truncated.");
                if (AtEnd || text[position] < '0' || text[position] > '9') throw new FormatException("Unexpected character in the document.");
                if (text[position] == '0' && position + 1 < text.Length && text[position + 1] >= '0' && text[position + 1] <= '9')
                {
                    throw new FormatException("A number has a leading zero.");
                }

                while (!AtEnd && text[position] >= '0' && text[position] <= '9') position++;
                if (!AtEnd && (text[position] == '.' || text[position] == 'e' || text[position] == 'E'))
                {
                    throw new FormatException("Only integers are allowed.");
                }

                var digits = text.Substring(start, position - start);
                if (digits.Length > 17 ||
                    !long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
                    value > MaximumSafeInteger || value < -MaximumSafeInteger)
                {
                    throw new FormatException("An integer is outside the exactly representable range.");
                }

                return new JsonIntegerNode(value);
            }

            private void Expect(string literal)
            {
                if (string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0) throw new FormatException("Unexpected literal in the document.");
                position += literal.Length;
            }

            private bool TryConsume(char character)
            {
                SkipLayout();
                if (AtEnd || text[position] != character) return false;
                position++;
                return true;
            }

            /// <summary>Steps over the white space JSON allows between values, in a document of any layout only.</summary>
            private void SkipLayout()
            {
                if (!anyLayout) return;
                while (!AtEnd && (text[position] == ' ' || text[position] == '\t' || text[position] == '\n' || text[position] == '\r')) position++;
            }
        }
    }
}
