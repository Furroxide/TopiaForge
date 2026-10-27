using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TopiaForge.Worlds.Maps
{
    /// <summary>Bounded strict JSON for untrusted map data, rejecting duplicate keys and nonfinite numbers.</summary>
    internal sealed class MapJson
    {
        private readonly string text;
        private int offset;
        private int remaining = 2000000;
        private MapJson(string text) { this.text = text; }

        internal static Dictionary<string, object?> Read(byte[] bytes)
        {
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw Invalid(); }
            var reader = new MapJson(text);
            var root = reader.Value(0) as Dictionary<string, object?>;
            reader.Space();
            if (root == null || reader.offset != reader.text.Length) throw Invalid();
            return root;
        }

        private object? Value(int depth)
        {
            if (depth > 64 || --remaining < 0) throw Invalid();
            Space();
            if (offset == text.Length) throw Invalid();
            switch (text[offset])
            {
                case '{':
                    offset++;
                    var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    if (Take('}')) return map;
                    do
                    {
                        Space();
                        var key = String();
                        if (!Take(':') || map.ContainsKey(key)) throw Invalid();
                        map.Add(key, Value(depth + 1));
                    } while (Take(','));
                    if (!Take('}')) throw Invalid();
                    return map;
                case '[':
                    offset++;
                    var list = new List<object?>();
                    if (Take(']')) return list;
                    do { list.Add(Value(depth + 1)); } while (Take(','));
                    if (!Take(']')) throw Invalid();
                    return list;
                case '"': return String();
                case 't': Literal("true"); return true;
                case 'f': Literal("false"); return false;
                case 'n': Literal("null"); return null;
                default: return Number();
            }
        }

        private string String()
        {
            if (offset >= text.Length || text[offset++] != '"') throw Invalid();
            var result = new StringBuilder();
            while (offset < text.Length)
            {
                var value = text[offset++];
                if (value == '"')
                {
                    var decoded = result.ToString();
                    for (var i = 0; i < decoded.Length; i++)
                    {
                        if (char.IsHighSurrogate(decoded[i]))
                        {
                            if (++i == decoded.Length || !char.IsLowSurrogate(decoded[i])) throw Invalid();
                        }
                        else if (char.IsLowSurrogate(decoded[i])) throw Invalid();
                    }
                    return decoded;
                }
                if (value < ' ') throw Invalid();
                if (value == '\\')
                {
                    if (offset == text.Length) throw Invalid();
                    switch (text[offset++])
                    {
                        case '"': value = '"'; break;
                        case '\\': value = '\\'; break;
                        case '/': value = '/'; break;
                        case 'b': value = '\b'; break;
                        case 'f': value = '\f'; break;
                        case 'n': value = '\n'; break;
                        case 'r': value = '\r'; break;
                        case 't': value = '\t'; break;
                        case 'u':
                            if (offset + 4 > text.Length || !ushort.TryParse(text.Substring(offset, 4),
                                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)) throw Invalid();
                            value = (char)code;
                            offset += 4;
                            break;
                        default: throw Invalid();
                    }
                }
                result.Append(value);
            }
            throw Invalid();
        }

        private double Number()
        {
            var start = offset;
            if (offset < text.Length && text[offset] == '-') offset++;
            if (offset == text.Length) throw Invalid();
            if (text[offset] == '0') offset++;
            else Digits();
            if (offset < text.Length && text[offset] == '.') { offset++; Digits(); }
            if (offset < text.Length && (text[offset] == 'e' || text[offset] == 'E'))
            {
                offset++;
                if (offset < text.Length && (text[offset] == '+' || text[offset] == '-')) offset++;
                Digits();
            }
            if (!double.TryParse(text.Substring(start, offset - start), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var result) || double.IsNaN(result) || double.IsInfinity(result)) throw Invalid();
            return result;
        }
        private void Digits()
        {
            var start = offset;
            while (offset < text.Length && text[offset] >= '0' && text[offset] <= '9') offset++;
            if (start == offset) throw Invalid();
        }
        private void Literal(string value)
        {
            if (offset + value.Length > text.Length || string.CompareOrdinal(text, offset, value, 0, value.Length) != 0) throw Invalid();
            offset += value.Length;
        }
        private bool Take(char value)
        {
            Space();
            if (offset == text.Length || text[offset] != value) return false;
            offset++;
            return true;
        }
        private void Space()
        {
            while (offset < text.Length && (text[offset] == ' ' || text[offset] == '\t' || text[offset] == '\r' || text[offset] == '\n')) offset++;
        }
        private static FormatException Invalid() => new FormatException("The map contains invalid or overly complex JSON. Ask its creator for a new export.");
    }
}
