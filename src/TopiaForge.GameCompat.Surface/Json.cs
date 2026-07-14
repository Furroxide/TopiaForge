using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TopiaForge.GameCompat
{
    // A tiny, dependency-free JSON value model with a CANONICAL writer and a permissive reader. Canonical
    // means: object keys are emitted in ordinal-sorted order, two-space indent, '\n' line endings on every
    // platform, and invariant-culture number formatting. That determinism is what lets the surface baseline be
    // a byte-stable, diff-friendly artifact and lets GameCompatTests assert a snapshot round-trips to itself.
    // The reader tolerates whatever ordering/whitespace it is given.
    public abstract class JsonValue
    {
        public string ToCanonical()
        {
            var builder = new StringBuilder();
            Write(builder, 0);
            builder.Append('\n');
            return builder.ToString();
        }

        internal abstract void Write(StringBuilder builder, int indent);

        public static JsonValue Parse(string text)
        {
            var parser = new JsonParser(text);
            var value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd)
            {
                throw new FormatException("Trailing content after JSON value at offset " + parser.Position);
            }

            return value;
        }

        // ---- Convenience accessors (throw on shape mismatch so callers fail loudly on a malformed artifact). ----
        public JsonObject AsObject() => this as JsonObject ?? throw new FormatException("Expected a JSON object");

        public JsonArray AsArray() => this as JsonArray ?? throw new FormatException("Expected a JSON array");

        public string AsString() => (this as JsonString)?.Value ?? throw new FormatException("Expected a JSON string");

        public long AsLong() => (this as JsonNumber)?.ToInt64() ?? throw new FormatException("Expected a JSON number");

        public bool AsBool() => (this as JsonBool)?.Value ?? throw new FormatException("Expected a JSON boolean");

        internal static void WriteIndent(StringBuilder builder, int indent)
        {
            for (var i = 0; i < indent; i++)
            {
                builder.Append("  ");
            }
        }
    }

    public sealed class JsonObject : JsonValue
    {
        // Ordinal-sorted so serialization order is deterministic regardless of insertion order.
        private readonly SortedDictionary<string, JsonValue> _members = new SortedDictionary<string, JsonValue>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, JsonValue> Members => _members;

        public int Count => _members.Count;

        public JsonObject Set(string key, JsonValue value)
        {
            _members[key] = value ?? JsonNull.Instance;
            return this;
        }

        public JsonObject Set(string key, string value) => Set(key, new JsonString(value));

        public JsonObject Set(string key, long value) => Set(key, new JsonNumber(value));

        public JsonObject Set(string key, bool value) => Set(key, value ? JsonBool.True : JsonBool.False);

        public bool Has(string key) => _members.ContainsKey(key);

        public JsonValue? Get(string key) => _members.TryGetValue(key, out var value) ? value : null;

        public string GetString(string key, string fallback = "") => Get(key) is JsonString s ? s.Value : fallback;

        public long GetLong(string key, long fallback = 0) => Get(key) is JsonNumber n ? n.ToInt64() : fallback;

        public bool GetBool(string key, bool fallback = false) => Get(key) is JsonBool b ? b.Value : fallback;

        public JsonObject GetObject(string key) => Get(key)?.AsObject() ?? new JsonObject();

        public JsonArray GetArray(string key) => Get(key)?.AsArray() ?? new JsonArray();

        internal override void Write(StringBuilder builder, int indent)
        {
            if (_members.Count == 0)
            {
                builder.Append("{}");
                return;
            }

            builder.Append("{\n");
            var first = true;
            foreach (var member in _members)
            {
                if (!first)
                {
                    builder.Append(",\n");
                }

                first = false;
                WriteIndent(builder, indent + 1);
                JsonString.WriteEscaped(builder, member.Key);
                builder.Append(": ");
                member.Value.Write(builder, indent + 1);
            }

            builder.Append('\n');
            WriteIndent(builder, indent);
            builder.Append('}');
        }
    }

    public sealed class JsonArray : JsonValue
    {
        private readonly List<JsonValue> _items = new List<JsonValue>();

        public IReadOnlyList<JsonValue> Items => _items;

        public int Count => _items.Count;

        public JsonArray Add(JsonValue value)
        {
            _items.Add(value ?? JsonNull.Instance);
            return this;
        }

        internal override void Write(StringBuilder builder, int indent)
        {
            if (_items.Count == 0)
            {
                builder.Append("[]");
                return;
            }

            builder.Append("[\n");
            for (var i = 0; i < _items.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(",\n");
                }

                WriteIndent(builder, indent + 1);
                _items[i].Write(builder, indent + 1);
            }

            builder.Append('\n');
            WriteIndent(builder, indent);
            builder.Append(']');
        }
    }

    public sealed class JsonString : JsonValue
    {
        public JsonString(string value)
        {
            Value = value ?? string.Empty;
        }

        public string Value { get; }

        internal override void Write(StringBuilder builder, int indent) => WriteEscaped(builder, Value);

        internal static void WriteEscaped(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var ch in value)
            {
                switch (ch)
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
                        if (ch < 0x20)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(ch);
                        }

                        break;
                }
            }

            builder.Append('"');
        }
    }

    public sealed class JsonNumber : JsonValue
    {
        private readonly bool _isInteger;
        private readonly long _integer;
        private readonly double _real;

        public JsonNumber(long value)
        {
            _isInteger = true;
            _integer = value;
        }

        public JsonNumber(double value)
        {
            _isInteger = false;
            _real = value;
        }

        public long ToInt64() => _isInteger ? _integer : (long)_real;

        internal override void Write(StringBuilder builder, int indent)
        {
            if (_isInteger)
            {
                builder.Append(_integer.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                // "R" round-trips; invariant culture keeps '.' as the separator on every machine.
                builder.Append(_real.ToString("R", CultureInfo.InvariantCulture));
            }
        }
    }

    public sealed class JsonBool : JsonValue
    {
        public static readonly JsonBool True = new JsonBool(true);
        public static readonly JsonBool False = new JsonBool(false);

        private JsonBool(bool value)
        {
            Value = value;
        }

        public bool Value { get; }

        internal override void Write(StringBuilder builder, int indent) => builder.Append(Value ? "true" : "false");
    }

    public sealed class JsonNull : JsonValue
    {
        public static readonly JsonNull Instance = new JsonNull();

        private JsonNull()
        {
        }

        internal override void Write(StringBuilder builder, int indent) => builder.Append("null");
    }

    // Compact recursive-descent parser. Permissive about ordering/whitespace; strict about structure.
    internal sealed class JsonParser
    {
        private readonly string _text;
        private int _position;

        public JsonParser(string text)
        {
            _text = text ?? string.Empty;
        }

        public int Position => _position;

        public bool AtEnd => _position >= _text.Length;

        public JsonValue ParseValue()
        {
            SkipWhitespace();
            if (AtEnd)
            {
                throw new FormatException("Unexpected end of JSON");
            }

            var ch = _text[_position];
            switch (ch)
            {
                case '{':
                    return ParseObject();
                case '[':
                    return ParseArray();
                case '"':
                    return new JsonString(ParseString());
                case 't':
                case 'f':
                    return ParseBool();
                case 'n':
                    Expect("null");
                    return JsonNull.Instance;
                default:
                    return ParseNumber();
            }
        }

        private JsonValue ParseObject()
        {
            var result = new JsonObject();
            _position++; // consume '{'
            SkipWhitespace();
            if (!AtEnd && _text[_position] == '}')
            {
                _position++;
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                var key = ParseString();
                SkipWhitespace();
                Consume(':');
                var value = ParseValue();
                result.Set(key, value);
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new FormatException("Unterminated object");
                }

                var next = _text[_position++];
                if (next == '}')
                {
                    break;
                }

                if (next != ',')
                {
                    throw new FormatException("Expected ',' or '}' in object at offset " + (_position - 1));
                }
            }

            return result;
        }

        private JsonValue ParseArray()
        {
            var result = new JsonArray();
            _position++; // consume '['
            SkipWhitespace();
            if (!AtEnd && _text[_position] == ']')
            {
                _position++;
                return result;
            }

            while (true)
            {
                var value = ParseValue();
                result.Add(value);
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new FormatException("Unterminated array");
                }

                var next = _text[_position++];
                if (next == ']')
                {
                    break;
                }

                if (next != ',')
                {
                    throw new FormatException("Expected ',' or ']' in array at offset " + (_position - 1));
                }
            }

            return result;
        }

        private string ParseString()
        {
            Consume('"');
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new FormatException("Unterminated string");
                }

                var ch = _text[_position++];
                if (ch == '"')
                {
                    break;
                }

                if (ch == '\\')
                {
                    if (AtEnd)
                    {
                        throw new FormatException("Unterminated escape");
                    }

                    var escape = _text[_position++];
                    switch (escape)
                    {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            var hex = _text.Substring(_position, 4);
                            builder.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _position += 4;
                            break;
                        default:
                            throw new FormatException("Invalid escape \\" + escape);
                    }
                }
                else
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        private JsonValue ParseBool()
        {
            if (_text[_position] == 't')
            {
                Expect("true");
                return JsonBool.True;
            }

            Expect("false");
            return JsonBool.False;
        }

        private JsonValue ParseNumber()
        {
            var start = _position;
            var isReal = false;
            while (!AtEnd)
            {
                var ch = _text[_position];
                if (ch == '-' || ch == '+' || (ch >= '0' && ch <= '9'))
                {
                    _position++;
                }
                else if (ch == '.' || ch == 'e' || ch == 'E')
                {
                    isReal = true;
                    _position++;
                }
                else
                {
                    break;
                }
            }

            var token = _text.Substring(start, _position - start);
            if (token.Length == 0)
            {
                throw new FormatException("Invalid number at offset " + start);
            }

            if (isReal)
            {
                return new JsonNumber(double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture));
            }

            return new JsonNumber(long.Parse(token, NumberStyles.Integer, CultureInfo.InvariantCulture));
        }

        public void SkipWhitespace()
        {
            while (!AtEnd)
            {
                var ch = _text[_position];
                if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
                {
                    _position++;
                }
                else
                {
                    break;
                }
            }
        }

        private void Consume(char expected)
        {
            if (AtEnd || _text[_position] != expected)
            {
                throw new FormatException("Expected '" + expected + "' at offset " + _position);
            }

            _position++;
        }

        private void Expect(string literal)
        {
            if (_position + literal.Length > _text.Length || _text.Substring(_position, literal.Length) != literal)
            {
                throw new FormatException("Expected '" + literal + "' at offset " + _position);
            }

            _position += literal.Length;
        }
    }
}
