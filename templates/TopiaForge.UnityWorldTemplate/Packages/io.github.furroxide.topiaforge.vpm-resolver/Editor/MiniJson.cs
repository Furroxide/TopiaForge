// Strict, parse-only JSON reader for the embedded VPM recovery bridge. Unity's JsonUtility cannot deserialize
// dynamic `locked` and `dependencies` maps. The caller bounds input bytes before parsing; this reader also
// bounds nesting, rejects duplicate keys and trailing content, and guarantees progress on malformed input.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TopiaForge.VpmResolver
{
    internal static class MiniJson
    {
        private const int MaxDepth = 64;

        public static object Parse(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var index = 0;
            SkipWhitespace(json, ref index);
            if (index == json.Length)
            {
                throw Error(index, "expected a JSON value");
            }

            var value = ParseValue(json, ref index, 0);
            SkipWhitespace(json, ref index);
            if (index != json.Length)
            {
                throw Error(index, "unexpected trailing content");
            }

            return value;
        }

        private static object ParseValue(string json, ref int index, int depth)
        {
            if (depth > MaxDepth)
            {
                throw Error(index, "nesting exceeds the supported limit");
            }

            SkipWhitespace(json, ref index);
            if (index >= json.Length)
            {
                throw Error(index, "expected a JSON value");
            }

            switch (json[index])
            {
                case '{':
                    return ParseObject(json, ref index, depth);
                case '[':
                    return ParseArray(json, ref index, depth);
                case '"':
                    return ParseString(json, ref index);
                case 't':
                    ConsumeLiteral(json, ref index, "true");
                    return true;
                case 'f':
                    ConsumeLiteral(json, ref index, "false");
                    return false;
                case 'n':
                    ConsumeLiteral(json, ref index, "null");
                    return null;
                default:
                    return ParseNumber(json, ref index);
            }
        }

        private static Dictionary<string, object> ParseObject(
            string json,
            ref int index,
            int depth)
        {
            Expect(json, ref index, '{');
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, '}'))
            {
                return result;
            }

            while (true)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != '"')
                {
                    throw Error(index, "expected an object property name");
                }

                var key = ParseString(json, ref index);
                if (result.ContainsKey(key))
                {
                    throw Error(index, "duplicate object property");
                }

                SkipWhitespace(json, ref index);
                Expect(json, ref index, ':');
                var value = ParseValue(json, ref index, depth + 1);
                result.Add(key, value);

                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, '}'))
                {
                    return result;
                }

                Expect(json, ref index, ',');
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == '}')
                {
                    throw Error(index, "trailing commas are not allowed");
                }
            }
        }

        private static List<object> ParseArray(string json, ref int index, int depth)
        {
            Expect(json, ref index, '[');
            var result = new List<object>();
            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, ']'))
            {
                return result;
            }

            while (true)
            {
                result.Add(ParseValue(json, ref index, depth + 1));
                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, ']'))
                {
                    return result;
                }

                Expect(json, ref index, ',');
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ']')
                {
                    throw Error(index, "trailing commas are not allowed");
                }
            }
        }

        private static string ParseString(string json, ref int index)
        {
            Expect(json, ref index, '"');
            var builder = new StringBuilder();
            while (index < json.Length)
            {
                var current = json[index++];
                if (current == '"')
                {
                    return builder.ToString();
                }

                if (current < 0x20)
                {
                    throw Error(index - 1, "unescaped control character in string");
                }

                if (current != '\\')
                {
                    builder.Append(current);
                    continue;
                }

                if (index >= json.Length)
                {
                    throw Error(index, "unterminated string escape");
                }

                var escaped = json[index++];
                switch (escaped)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        var codeUnit = ParseUnicodeEscape(json, ref index);
                        if (char.IsLowSurrogate(codeUnit))
                        {
                            throw Error(index, "unexpected low surrogate escape");
                        }

                        builder.Append(codeUnit);
                        if (char.IsHighSurrogate(codeUnit))
                        {
                            if (index + 2 > json.Length || json[index] != '\\' || json[index + 1] != 'u')
                            {
                                throw Error(index, "high surrogate must be followed by a low surrogate escape");
                            }

                            index += 2;
                            var low = ParseUnicodeEscape(json, ref index);
                            if (!char.IsLowSurrogate(low))
                            {
                                throw Error(index, "high surrogate must be followed by a low surrogate escape");
                            }

                            builder.Append(low);
                        }

                        break;
                    default:
                        throw Error(index - 1, "unsupported string escape");
                }
            }

            throw Error(index, "unterminated string");
        }

        private static char ParseUnicodeEscape(string json, ref int index)
        {
            if (index + 4 > json.Length)
            {
                throw Error(index, "incomplete Unicode escape");
            }

            var value = 0;
            for (var offset = 0; offset < 4; offset++)
            {
                var digit = HexValue(json[index + offset]);
                if (digit < 0)
                {
                    throw Error(index + offset, "invalid Unicode escape");
                }

                value = (value << 4) | digit;
            }

            index += 4;
            return (char)value;
        }

        private static object ParseNumber(string json, ref int index)
        {
            var start = index;
            if (TryConsume(json, ref index, '-'))
            {
                if (index >= json.Length)
                {
                    throw Error(index, "incomplete number");
                }
            }

            if (TryConsume(json, ref index, '0'))
            {
                if (index < json.Length && char.IsDigit(json[index]))
                {
                    throw Error(index, "leading zero in number");
                }
            }
            else
            {
                if (index >= json.Length || json[index] < '1' || json[index] > '9')
                {
                    throw Error(index, "expected a JSON value");
                }

                while (index < json.Length && char.IsDigit(json[index]))
                {
                    index++;
                }
            }

            if (TryConsume(json, ref index, '.'))
            {
                ConsumeDigits(json, ref index, "fraction requires a digit");
            }

            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-'))
                {
                    index++;
                }

                ConsumeDigits(json, ref index, "exponent requires a digit");
            }

            var token = json.Substring(start, index - start);
            if (!double.TryParse(
                    token,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var number)
                || double.IsNaN(number)
                || double.IsInfinity(number))
            {
                throw Error(start, "invalid number");
            }

            return number;
        }

        private static void ConsumeDigits(string json, ref int index, string error)
        {
            var start = index;
            while (index < json.Length && char.IsDigit(json[index]))
            {
                index++;
            }

            if (start == index)
            {
                throw Error(index, error);
            }
        }

        private static void ConsumeLiteral(string json, ref int index, string literal)
        {
            if (index + literal.Length > json.Length)
            {
                throw Error(index, "invalid literal");
            }

            for (var offset = 0; offset < literal.Length; offset++)
            {
                if (json[index + offset] != literal[offset])
                {
                    throw Error(index, "invalid literal");
                }
            }

            index += literal.Length;
        }

        private static void Expect(string json, ref int index, char expected)
        {
            if (!TryConsume(json, ref index, expected))
            {
                throw Error(index, "expected '" + expected + "'");
            }
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

        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            return -1;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length)
            {
                var current = json[index];
                if (current != ' ' && current != '\t' && current != '\r' && current != '\n')
                {
                    return;
                }

                index++;
            }
        }

        private static FormatException Error(int index, string message) =>
            new FormatException($"Invalid JSON at offset {index}: {message}.");
    }
}
