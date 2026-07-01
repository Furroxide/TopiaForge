// Minimal parse-only JSON reader for the VPM resolver (Unity's JsonUtility can't deserialize the dynamic
// `locked`/`packages` maps in vpm-manifest.json / a repository listing). Returns Dictionary<string, object> for
// objects, List<object> for arrays, string/double/bool/null for scalars. Lenient and dependency-free.
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Robotopia.VpmResolver
{
    internal static class MiniJson
    {
        private const int MaxDepth = 200;

        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            var index = 0;
            var value = ParseValue(json, ref index, 0);
            return value;
        }

        private static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new System.Exception("JSON nesting too deep.");
            }

            SkipWhitespace(s, ref i);
            if (i >= s.Length)
            {
                return null;
            }

            var c = s[i];
            switch (c)
            {
                case '{':
                    return ParseObject(s, ref i, depth);
                case '[':
                    return ParseArray(s, ref i, depth);
                case '"':
                    return ParseString(s, ref i);
                case 't':
                case 'f':
                    return ParseBool(s, ref i);
                case 'n':
                    i += 4; // null
                    return null;
                default:
                    return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i, int depth)
        {
            var result = new Dictionary<string, object>();
            i++; // {
            while (i < s.Length)
            {
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == '}')
                {
                    i++;
                    return result;
                }

                var key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ':')
                {
                    i++;
                }

                var value = ParseValue(s, ref i, depth + 1);
                result[key] = value;

                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                }
            }

            return result;
        }

        private static List<object> ParseArray(string s, ref int i, int depth)
        {
            var result = new List<object>();
            i++; // [
            while (i < s.Length)
            {
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ']')
                {
                    i++;
                    return result;
                }

                result.Add(ParseValue(s, ref i, depth + 1));
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                }
            }

            return result;
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"')
                {
                    break;
                }

                if (c == '\\' && i < s.Length)
                {
                    var e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                var hex = s.Substring(i, 4);
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                {
                                    sb.Append((char)code);
                                }

                                i += 4;
                            }

                            break;
                        default: sb.Append(e); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static object ParseBool(string s, ref int i)
        {
            if (s[i] == 't')
            {
                i += 4; // true
                return true;
            }

            i += 5; // false
            return false;
        }

        private static object ParseNumber(string s, ref int i)
        {
            var start = i;
            while (i < s.Length && "-+.eE0123456789".IndexOf(s[i]) >= 0)
            {
                i++;
            }

            var token = s.Substring(start, i - start);
            return double.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)
                ? number
                : 0d;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }
        }
    }
}
