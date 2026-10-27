using System;
using System.Collections.Generic;
using System.Text;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>Local slash commands; double quotes group arguments, backslash escapes a quote or backslash.</summary>
    internal static class ChatCommand
    {
        internal static bool TryParse(string text, out string name, out IReadOnlyList<string> arguments)
        {
            name = string.Empty;
            arguments = Array.Empty<string>();
            if (!ChatWire.IsValid(text) || text[0] != '/') return false;
            var words = new List<string>();
            var word = new StringBuilder();
            var quoted = false;
            var started = false;
            for (var index = 1; index < text.Length; index++)
            {
                var value = text[index];
                if (value == '"') { quoted = !quoted; started = true; continue; }
                if (value == '\\' && index + 1 < text.Length && (text[index + 1] == '\\' || text[index + 1] == '"')) value = text[++index];
                else if (char.IsWhiteSpace(value) && !quoted)
                {
                    if (started) { words.Add(word.ToString()); word.Clear(); started = false; }
                    continue;
                }
                word.Append(value);
                started = true;
            }
            if (quoted) return false;
            if (started) words.Add(word.ToString());
            if (words.Count == 0 || words.Count > 17 || words[0].Length == 0) return false;
            name = words[0];
            words.RemoveAt(0);
            arguments = words;
            return true;
        }
    }
}
