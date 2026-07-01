using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Robotopia.RobotKit
{
    // A tiny, dependency-free JSON reader/writer for the RoboAPI brain-query client. The mod assemblies do not ship a
    // JSON library (the game's Newtonsoft is not ours to bind, and System.Text.Json is not referenced), and the one
    // endpoint we call has a small, well-known shape — so a self-contained parser keeps the wire handling in one
    // unit-testable place with no external dependency. Deliberately Unity-free so it compiles into the net8.0 test
    // assembly. Parses to: Dictionary<string,object?> (objects), List<object?> (arrays), string, double, bool, null.
    internal static class MiniJson
    {
        // --- writer -------------------------------------------------------------------------------------------

        public static string Serialize(object? value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object? value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case float f:
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object?> map:
                    WriteObject(sb, map);
                    break;
                case IEnumerable<object?> list:
                    WriteArray(sb, list);
                    break;
                default:
                    // Unknown types are rendered as their invariant string form, quoted.
                    WriteString(sb, value.ToString() ?? string.Empty);
                    break;
            }
        }

        private static void WriteObject(StringBuilder sb, IDictionary<string, object?> map)
        {
            sb.Append('{');
            var first = true;
            foreach (var pair in map)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                WriteString(sb, pair.Key);
                sb.Append(':');
                WriteValue(sb, pair.Value);
            }

            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable<object?> list)
        {
            sb.Append('[');
            var first = true;
            foreach (var item in list)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                WriteValue(sb, item);
            }

            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }

        // --- reader -------------------------------------------------------------------------------------------

        // Parses a JSON document. Returns null on any malformed input (the caller treats a null/typed-miss as a
        // failed query rather than throwing).
        public static object? Deserialize(string? json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            var parser = new Parser(json!);
            try
            {
                var value = parser.ParseValue();
                parser.SkipWhitespace();
                return parser.AtEnd ? value : null;
            }
            catch
            {
                return null;
            }
        }

        private sealed class Parser
        {
            private readonly string text;
            private int index;

            public Parser(string text)
            {
                this.text = text;
            }

            public bool AtEnd => index >= text.Length;

            public object? ParseValue()
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new System.FormatException("unexpected end");
                }

                var c = text[index];
                switch (c)
                {
                    case '{':
                        return ParseObject();
                    case '[':
                        return ParseArray();
                    case '"':
                        return ParseString();
                    case 't':
                    case 'f':
                        return ParseBool();
                    case 'n':
                        ParseLiteral("null");
                        return null;
                    default:
                        return ParseNumber();
                }
            }

            private Dictionary<string, object?> ParseObject()
            {
                var result = new Dictionary<string, object?>();
                index++; // consume '{'
                SkipWhitespace();
                if (!AtEnd && text[index] == '}')
                {
                    index++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || text[index] != '"')
                    {
                        throw new System.FormatException("expected key");
                    }

                    var key = ParseString();
                    SkipWhitespace();
                    if (AtEnd || text[index] != ':')
                    {
                        throw new System.FormatException("expected ':'");
                    }

                    index++; // consume ':'
                    result[key] = ParseValue();
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw new System.FormatException("unterminated object");
                    }

                    var c = text[index++];
                    if (c == '}')
                    {
                        return result;
                    }

                    if (c != ',')
                    {
                        throw new System.FormatException("expected ',' or '}'");
                    }
                }
            }

            private List<object?> ParseArray()
            {
                var result = new List<object?>();
                index++; // consume '['
                SkipWhitespace();
                if (!AtEnd && text[index] == ']')
                {
                    index++;
                    return result;
                }

                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw new System.FormatException("unterminated array");
                    }

                    var c = text[index++];
                    if (c == ']')
                    {
                        return result;
                    }

                    if (c != ',')
                    {
                        throw new System.FormatException("expected ',' or ']'");
                    }
                }
            }

            private string ParseString()
            {
                var sb = new StringBuilder();
                index++; // consume opening quote
                while (!AtEnd)
                {
                    var c = text[index++];
                    if (c == '"')
                    {
                        return sb.ToString();
                    }

                    if (c == '\\')
                    {
                        if (AtEnd)
                        {
                            break;
                        }

                        var e = text[index++];
                        switch (e)
                        {
                            case '"':
                                sb.Append('"');
                                break;
                            case '\\':
                                sb.Append('\\');
                                break;
                            case '/':
                                sb.Append('/');
                                break;
                            case 'b':
                                sb.Append('\b');
                                break;
                            case 'f':
                                sb.Append('\f');
                                break;
                            case 'n':
                                sb.Append('\n');
                                break;
                            case 'r':
                                sb.Append('\r');
                                break;
                            case 't':
                                sb.Append('\t');
                                break;
                            case 'u':
                                if (index + 4 > text.Length)
                                {
                                    throw new System.FormatException("bad \\u escape");
                                }

                                var hex = text.Substring(index, 4);
                                index += 4;
                                sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                break;
                            default:
                                throw new System.FormatException("bad escape");
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }

                throw new System.FormatException("unterminated string");
            }

            private bool ParseBool()
            {
                if (text[index] == 't')
                {
                    ParseLiteral("true");
                    return true;
                }

                ParseLiteral("false");
                return false;
            }

            private void ParseLiteral(string literal)
            {
                if (index + literal.Length > text.Length ||
                    text.Substring(index, literal.Length) != literal)
                {
                    throw new System.FormatException("bad literal");
                }

                index += literal.Length;
            }

            private double ParseNumber()
            {
                var start = index;
                while (!AtEnd)
                {
                    var c = text[index];
                    if (c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E' || (c >= '0' && c <= '9'))
                    {
                        index++;
                    }
                    else
                    {
                        break;
                    }
                }

                var slice = text.Substring(start, index - start);
                if (double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }

                throw new System.FormatException("bad number");
            }

            public void SkipWhitespace()
            {
                while (!AtEnd)
                {
                    var c = text[index];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    {
                        index++;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }
    }
}
