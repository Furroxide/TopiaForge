using System;
using System.Collections.Generic;

namespace TopiaForge.Prompts
{
    internal enum PromptDirectiveCompositionOutcome
    {
        Appended = 0,
        Blank = 1,
        Duplicate = 2,
        TooLong = 3,
    }

    /// <summary>
    /// Pure composition policy shared by native prompt bridges. It preserves the game's source collection,
    /// normalizes only the newly registered directive, and bounds text before it reaches the remote brain.
    /// </summary>
    internal static class PromptDirectiveComposer
    {
        public const int MaximumDirectiveCharacters = 1800;

        public static PromptDirectiveCompositionOutcome Append(
            IReadOnlyList<string>? source,
            string? directive,
            out IReadOnlyList<string> composed)
        {
            composed = source ?? Array.Empty<string>();
            if (!TryGetTrimmedBounds(directive, out var first, out var length))
            {
                return PromptDirectiveCompositionOutcome.Blank;
            }

            if (length > MaximumDirectiveCharacters)
            {
                return PromptDirectiveCompositionOutcome.TooLong;
            }

            for (var index = 0; index < composed.Count; index++)
            {
                if (TrimmedEquals(composed[index], directive!, first, length))
                {
                    return PromptDirectiveCompositionOutcome.Duplicate;
                }
            }

            var normalized = first == 0 && length == directive!.Length
                ? directive!
                : directive!.Substring(first, length);
            var copy = new List<string>(composed.Count + 1);
            for (var index = 0; index < composed.Count; index++)
            {
                copy.Add(composed[index]);
            }

            copy.Add(normalized);
            composed = copy;
            return PromptDirectiveCompositionOutcome.Appended;
        }

        private static bool TryGetTrimmedBounds(string? value, out int first, out int length)
        {
            first = 0;
            length = 0;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            while (first < value!.Length && char.IsWhiteSpace(value[first]))
            {
                first++;
            }

            var last = value.Length - 1;
            while (last >= first && char.IsWhiteSpace(value[last]))
            {
                last--;
            }

            length = last - first + 1;
            return length > 0;
        }

        private static bool TrimmedEquals(string? candidate, string directive, int directiveFirst, int directiveLength)
        {
            if (!TryGetTrimmedBounds(candidate, out var candidateFirst, out var candidateLength) ||
                candidateLength != directiveLength)
            {
                return false;
            }

            for (var index = 0; index < directiveLength; index++)
            {
                if (candidate![candidateFirst + index] != directive[directiveFirst + index])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
