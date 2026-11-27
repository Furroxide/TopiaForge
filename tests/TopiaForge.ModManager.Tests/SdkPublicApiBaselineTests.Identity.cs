using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SdkPublicApiBaselineTests
    {
        private static string TypeHead(string header)
        {
            var text = WithoutAnnotations(header);
            foreach (var separator in new[] { " : ", " where " })
            {
                var index = text.IndexOf(separator, StringComparison.Ordinal);
                if (index >= 0) text = text.Substring(0, index);
            }
            return text;
        }

        private static Dictionary<string, string> TypeParameters(string header)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var name = TypeHead(header);
            foreach (Match group in Regex.Matches(name, "<([^<>]+)>"))
                foreach (var parameter in group.Groups[1].Value.Split(','))
                    result.Add(parameter.Trim(), "!" + result.Count);
            return result;
        }

        private static string TypeKey(string header)
        {
            var head = TypeHead(header);
            var name = head.Substring(LastTopLevelSpace(head) + 1);
            // Sealing a class, adding Flags, or changing variance is not a new type.
            var kind = Regex.Match(head, @"(?:^| )(class|struct|interface|enum|delegate) ").Groups[1].Value;
            return kind + " " + NormalizeIdentity(name, TypeParameters(header));
        }

        private static string MemberKey(string line, string header)
        {
            var text = WithoutAnnotations(line).Trim();
            var split = text.IndexOf(' ');
            var kind = text.Substring(0, split);
            var body = text.Substring(split + 1);
            var parameters = TypeParameters(header);
            var flags = Regex.Match(body, @"^(?:extension )?\[([a-z,]+)\] ");
            var isStatic = body.StartsWith("static ", StringComparison.Ordinal)
                || body.StartsWith("const ", StringComparison.Ordinal)
                || flags.Groups[1].Value.Split(',').Contains("static");
            // Method flags, extension syntax and field modifiers do not identify an overload.
            if (flags.Success) body = body.Substring(flags.Length);
            body = Regex.Replace(body, @"^(?:(?:extension|static|readonly|const) )+", string.Empty);
            string signature;
            if (kind == "method" || kind == "constructor")
            {
                var open = body.IndexOf('(');
                var close = MatchingClose(body, open, '(', ')');
                var head = body.Substring(0, open);
                if (head.EndsWith(">", StringComparison.Ordinal))
                {
                    var genericOpen = MatchingOpen(head, '<', '>');
                    var generics = head.Substring(genericOpen + 1, head.Length - genericOpen - 2).Split(',');
                    for (var index = 0; index < generics.Length; index++) parameters[generics[index].Trim()] = "!!" + index;
                    head = head.Substring(0, genericOpen) + "`" + generics.Length;
                }
                signature = head + "(" + ParameterKey(body.Substring(open + 1, close - open - 1)) + ")";
            }
            else if (kind == "property" || kind == "event")
            {
                signature = body.Substring(0, body.IndexOf(" { ", StringComparison.Ordinal));
                if (signature.EndsWith("]", StringComparison.Ordinal))
                {
                    var open = MatchingOpen(signature, '[', ']');
                    signature = signature.Substring(0, open) + "[" + ParameterKey(signature.Substring(open + 1, signature.Length - open - 2)) + "]";
                }
            }
            else if (kind == "field")
            {
                var equals = body.IndexOf(" = ", StringComparison.Ordinal);
                signature = equals < 0 ? body : body.Substring(0, equals);
            }
            else throw new InvalidOperationException("Unknown SDK baseline member kind: " + kind);
            return kind + (isStatic ? " static " : " ") + NormalizeIdentity(signature, parameters);
        }

        private static string NormalizeIdentity(string text, Dictionary<string, string> parameters) =>
            Regex.Replace(text.Replace("?", string.Empty, StringComparison.Ordinal), @"(?<![\w.])\w+(?![\w.])",
                match => parameters.TryGetValue(match.Value, out var name) ? name : match.Value);

        private static string ParameterKey(string text) => string.Join(", ", SplitParameters(text).Select(parameter =>
        {
            var equals = parameter.IndexOf(" = ", StringComparison.Ordinal);
            if (equals >= 0) parameter = parameter.Substring(0, equals);
            parameter = parameter.Substring(0, LastTopLevelSpace(parameter));
            if (parameter.StartsWith("params ", StringComparison.Ordinal)) parameter = parameter.Substring(7);
            foreach (var modifier in new[] { "ref ", "in ", "out " })
                if (parameter.StartsWith(modifier, StringComparison.Ordinal)) return parameter.Substring(modifier.Length) + "&";
            return parameter;
        }));

        private static IEnumerable<string> SplitParameters(string text)
        {
            var depth = 0;
            var quoted = false;
            var start = 0;
            for (var index = 0; index < text.Length; index++)
            {
                var value = text[index];
                if (quoted && value == '\\') { index++; continue; }
                if (value == '"') { quoted = !quoted; continue; }
                if (quoted) continue;
                if (value == '<' || value == '[' || value == '(') depth++;
                if (value == '>' || value == ']' || value == ')') depth--;
                if (value != ',' || depth != 0) continue;
                yield return text.Substring(start, index - start).Trim();
                start = index + 1;
            }
            if (start < text.Length) yield return text.Substring(start).Trim();
        }

        private static int LastTopLevelSpace(string text)
        {
            var depth = 0;
            for (var index = text.Length - 1; index >= 0; index--)
            {
                if (text[index] == '>' || text[index] == ']') depth++;
                else if (text[index] == '<' || text[index] == '[') depth--;
                else if (text[index] == ' ' && depth == 0) return index;
            }
            return -1;
        }

        private static int MatchingOpen(string text, char open, char close)
        {
            var depth = 0;
            for (var index = text.Length - 1; index >= 0; index--)
            {
                if (text[index] == close) depth++;
                else if (text[index] == open && --depth == 0) return index;
            }
            throw new InvalidOperationException("Unbalanced SDK baseline signature.");
        }

        private static int MatchingClose(string text, int start, char open, char close)
        {
            var depth = 0;
            var quoted = false;
            for (var index = start; index < text.Length; index++)
            {
                if (quoted && text[index] == '\\') { index++; continue; }
                if (text[index] == '"') { quoted = !quoted; continue; }
                if (quoted) continue;
                if (text[index] == open) depth++;
                else if (text[index] == close && --depth == 0) return index;
            }
            throw new InvalidOperationException("Unbalanced SDK baseline parameters.");
        }
    }
}
