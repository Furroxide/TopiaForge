using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TopiaForge.ModManager.Core;

namespace TopiaForge.SandboxAcceptance.Native
{
    internal sealed class SandboxWireRequest
    {
        public string Challenge = "";
        public int Sequence;
        public string Operation = "";
        public string ScenarioId = "";
        public int Cycle;
    }

    internal static class SandboxWireCodec
    {
        internal const int MaximumBytes = 262144;
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly string[] Keys = { "schemaVersion", "challenge", "sequence", "operation", "scenarioId", "cycle" };
        internal static readonly string[] Operations = { "prepare", "begin", "capture", "advance", "unregister-source", "request-session-stop", "cleanup" };
        internal static SandboxWireRequest Parse(byte[] bytes, string challenge, int sequence)
        {
            if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException("Invalid wire frame size.");
            var text = Utf8.GetString(bytes);
            var values = ReadFlatObject(text);
            if (values.Count != Keys.Length || values.Keys.Any(key => !Keys.Contains(key, StringComparer.Ordinal)))
                throw new InvalidDataException("Wire fields differ from schema 1.");
            int Integer(string key)
            {
                var raw = values[key];
                if (raw.Length == 0 || raw.Length > 10 || raw.Any(c => c < '0' || c > '9') || raw.Length > 1 && raw[0] == '0'
                    || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    throw new InvalidDataException("Invalid integer: " + key);
                return number;
            }
            string Text(string key)
            {
                var raw = values[key];
                if (raw.Length < 2 || raw[0] != '"') throw new InvalidDataException("Expected string: " + key);
                var value = JsonUtil.Deserialize<string>(raw);
                if (value.Length == 0 || value.Length > 128 || value.Any(c => c < 32 || c > 126))
                    throw new InvalidDataException("Invalid bounded wire string.");
                return value;
            }
            var request = new SandboxWireRequest { Challenge = Text("challenge"), Sequence = Integer("sequence"),
                Operation = Text("operation"), ScenarioId = Text("scenarioId"), Cycle = Integer("cycle") };
            if (Integer("schemaVersion") != 1 || request.Challenge != challenge || request.Sequence != sequence
                || request.Cycle < 1 || request.Cycle > 10 || !Operations.Contains(request.Operation, StringComparer.Ordinal)
                || !SandboxNativeScenarios.Ids.Contains(request.ScenarioId, StringComparer.Ordinal))
                throw new InvalidDataException("Unsupported, replayed or mismatched wire request.");
            return request;
        }

        private static Dictionary<string, string> ReadFlatObject(string text)
        {
            var index = 0;
            void White() { while (index < text.Length && (text[index] == ' ' || text[index] == '\r' || text[index] == '\n' || text[index] == '\t')) index++; }
            string Quoted()
            {
                var begin = index;
                if (index >= text.Length || text[index++] != '"') throw new InvalidDataException("Expected a JSON string.");
                while (index < text.Length)
                {
                    var c = text[index++];
                    if (c < 32) throw new InvalidDataException("Unescaped control character.");
                    if (c == '\\')
                    {
                        if (index == text.Length) throw new InvalidDataException("Truncated escape.");
                        var escaped = text[index++];
                        if (escaped == 'u')
                        {
                            for (var n = 0; n < 4; n++)
                                if (index == text.Length || !Uri.IsHexDigit(text[index++])) throw new InvalidDataException("Invalid Unicode escape.");
                        }
                        else if ("\"\\/bfnrt".IndexOf(escaped) < 0) throw new InvalidDataException("Invalid escape.");
                    }
                    else if (c == '"') return text.Substring(begin, index - begin);
                }
                throw new InvalidDataException("Unterminated string.");
            }
            White();
            if (index == text.Length || text[index++] != '{') throw new InvalidDataException("Expected JSON object.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            White();
            if (index < text.Length && text[index] == '}') throw new InvalidDataException("Empty request.");
            while (true)
            {
                White(); var key = JsonUtil.Deserialize<string>(Quoted()); White();
                if (index == text.Length || text[index++] != ':') throw new InvalidDataException("Expected property separator.");
                White(); var start = index;
                if (index < text.Length && text[index] == '"') Quoted();
                else { while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++; }
                if (start == index || values.ContainsKey(key)) throw new InvalidDataException("Duplicate or invalid property.");
                values.Add(key, text.Substring(start, index - start)); White();
                if (index == text.Length) throw new InvalidDataException("Truncated object.");
                var separator = text[index++];
                if (separator == '}') break;
                if (separator != ',') throw new InvalidDataException("Expected object separator.");
            }
            White(); if (index != text.Length) throw new InvalidDataException("Trailing JSON content.");
            return values;
        }

        internal static byte[] Serialize(object? value)
        {
            var builder = new StringBuilder();
            Write(value, builder, 0);
            var bytes = Utf8.GetBytes(builder.ToString());
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Observation exceeds wire limit.");
            return bytes;
        }
        private static void Write(object? value, StringBuilder builder, int depth)
        {
            if (depth > 12) throw new InvalidDataException("Observation is too deeply nested.");
            if (value == null) { builder.Append("null"); return; }
            if (value is string text) { builder.Append(JsonUtil.Serialize(text)); return; }
            if (value is bool flag) { builder.Append(flag ? "true" : "false"); return; }
            if (value is float f && (float.IsNaN(f) || float.IsInfinity(f)) || value is double d && (double.IsNaN(d) || double.IsInfinity(d)))
                throw new InvalidDataException("Nonfinite observation.");
            if (value is int || value is long || value is float || value is double) { builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (value is IDictionary<string, object?> map)
            {
                builder.Append('{'); var first = true;
                foreach (var pair in map) { if (!first) builder.Append(','); first = false; builder.Append(JsonUtil.Serialize(pair.Key)).Append(':'); Write(pair.Value, builder, depth + 1); }
                builder.Append('}'); return;
            }
            if (value is IEnumerable sequence)
            {
                builder.Append('['); var first = true; var count = 0;
                foreach (var item in sequence) { if (++count > 4096) throw new InvalidDataException("Observation array too large."); if (!first) builder.Append(','); first = false; Write(item, builder, depth + 1); }
                builder.Append(']'); return;
            }
            throw new InvalidDataException("Unsupported observation type.");
        }
    }
}
