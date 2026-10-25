using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SdkPublicApiBaselineTests
    {
        private const string RequirementMarker = " [requires-topiaforge=";
        private const string MarkerAdvice = "[RequiresTopiaForge(\"<first-release-version>\")]";
        private static readonly Regex TrailingRequirement = new Regex(" \\[requires-topiaforge=\"(?:[^\"\\\\]|\\\\.)*\"\\]$");
        private static readonly Regex TrailingObsolete =
            new Regex(" \\[obsolete message=\"(?:[^\"\\\\]|\\\\.)*\" error=(?:true|false)\\]$");

        private static string FormatRequirement(MemberInfo member)
        {
            string? required = null;
            for (MemberInfo? current = member; current != null; current = current.DeclaringType)
            {
                var attribute = current.GetCustomAttribute<RequiresTopiaForgeAttribute>(inherit: false);
                if (attribute == null) continue;
                if (!VersionUtil.TryParse(attribute.Version, out _))
                    throw new InvalidOperationException(current.Name + " needs a valid SemVer in RequiresTopiaForge.");
                if (required == null || !VersionUtil.IsAtLeast(required, attribute.Version)) required = attribute.Version;
            }

            return required == null ? string.Empty : RequirementMarker + JsonSerializer.Serialize(required) + "]";
        }

        // APIs are matched by type and member name, not by line, so a changed line for an API that is already
        // there (a deprecation, a parameter rename or nullability fix, a new base interface) is never mistaken
        // for a new API, and an API that is already there never gains a marker.
        private static void RequireVersionedAdditions(string expected, string actual)
        {
            var old = ApiTypes(expected);
            foreach (var type in ApiTypes(actual))
            {
                if (!old.TryGetValue(type.Key, out var before))
                {
                    // A new type's marker covers its members, so every line carries it.
                    foreach (var line in type.Value.Members.Prepend(type.Value.Header))
                        if (!IsMarked(line)) throw NewApi(line);
                    continue;
                }

                if (IsMarked(type.Value.Header) && !IsMarked(before.Header)) throw ExistingApi(type.Value.Header);
                foreach (var group in type.Value.Members.GroupBy(MemberKey, StringComparer.Ordinal))
                {
                    var oldLines = before.Members.Where(line => MemberKey(line) == group.Key).ToList();
                    var added = group.Where(line => !oldLines.Contains(line)).ToList();
                    var removed = oldLines.Where(line => !group.Contains(line)).ToList();
                    // Lines beyond those they replace are new overloads or members, and each needs a marker.
                    var surplus = Math.Max(0, added.Count - removed.Count);
                    var marked = added.Count(IsMarked);
                    if (marked < surplus) throw NewApi(added.First(line => !IsMarked(line)));
                    if (marked - surplus > removed.Count(IsMarked)) throw ExistingApi(added.First(IsMarked));
                    RequireVersionedAccessors(oldLines, added);
                }
            }
        }

        // A property or event that is already there may gain an accessor, which needs its own marker.
        private static void RequireVersionedAccessors(List<string> oldLines, List<string> added)
        {
            var before = oldLines.SelectMany(Accessors).ToList();
            foreach (var line in added.Where(line => !IsMarked(line)))
            {
                foreach (var (name, marked) in Accessors(line))
                {
                    var existing = before.Where(accessor => accessor.Name == name).ToList();
                    if (existing.Count == 0 && !marked)
                        throw new InvalidOperationException("New SDK API " + line.Trim() + " adds the " + name +
                            " accessor; add " + MarkerAdvice + " to that accessor before refreshing the baseline.");
                    if (existing.Count > 0 && marked && existing.All(accessor => !accessor.Marked)) throw ExistingApi(line);
                }
            }
        }

        private static InvalidOperationException NewApi(string line) => new InvalidOperationException("New SDK API " +
            line.Trim() + " must add " + MarkerAdvice + " to the member or its declaring type before refreshing the baseline.");

        private static InvalidOperationException ExistingApi(string line) => new InvalidOperationException("SDK API " +
            line.Trim() + " is already in the baseline, so it must not gain [RequiresTopiaForge]: mods that admit older " +
            "loaders use it. Mark only APIs that are new in this release.");

        private static bool IsMarked(string line) => TrailingRequirement.IsMatch(line);

        private static string WithoutAnnotations(string line) =>
            TrailingObsolete.Replace(TrailingRequirement.Replace(line, string.Empty), string.Empty);

        private static Dictionary<string, ApiType> ApiTypes(string baseline)
        {
            var types = new Dictionary<string, ApiType>(StringComparer.Ordinal);
            ApiType? current = null;
            foreach (var line in baseline.Split('\n'))
            {
                if (line.Length == 0 || line[0] == '#' || line.StartsWith("assembly ", StringComparison.Ordinal)
                    || line.StartsWith("target-framework ", StringComparison.Ordinal)) continue;
                if (line[0] != ' ') types[TypeKey(line)] = current = new ApiType(line);
                else current?.Members.Add(line);
            }

            return types;
        }

        // A type's kind and name, without its base types, constraints or annotations.
        private static string TypeKey(string header)
        {
            var text = WithoutAnnotations(header);
            foreach (var separator in new[] { " : ", " where " })
            {
                var index = text.IndexOf(separator, StringComparison.Ordinal);
                if (index >= 0) text = text.Substring(0, index);
            }

            return text;
        }

        // A member's kind and name. Overloads share a key; parameters, types and annotations are left out.
        private static string MemberKey(string line)
        {
            var text = WithoutAnnotations(line).Trim();
            var space = text.IndexOf(' ');
            var kind = space < 0 ? text : text.Substring(0, space);
            int end;
            string head;
            switch (kind)
            {
                case "constructor":
                    return kind;
                case "method":
                    end = text.IndexOf('(');
                    head = end < 0 ? text : TrimTrailingGroup(text.Substring(0, end), '<', '>');
                    break;
                case "property":
                case "event":
                    end = text.IndexOf(" { ", StringComparison.Ordinal);
                    head = end < 0 ? text : TrimTrailingGroup(text.Substring(0, end), '[', ']');
                    break;
                case "field":
                    end = text.IndexOf(" = ", StringComparison.Ordinal);
                    head = end < 0 ? text : text.Substring(0, end);
                    break;
                default:
                    return text;
            }

            return kind + " " + head.Substring(head.LastIndexOf(' ') + 1);
        }

        // Removes a trailing bracketed group: a method's generic parameters or an indexer's parameters.
        private static string TrimTrailingGroup(string text, char open, char close)
        {
            if (text.Length == 0 || text[text.Length - 1] != close) return text;
            var depth = 0;
            for (var index = text.Length - 1; index >= 0; index--)
            {
                if (text[index] == close) depth++;
                else if (text[index] == open && --depth == 0) return text.Substring(0, index);
            }

            return text;
        }

        private static IEnumerable<(string Name, bool Marked)> Accessors(string line)
        {
            var text = WithoutAnnotations(line).Trim();
            if (!text.StartsWith("property ", StringComparison.Ordinal) && !text.StartsWith("event ", StringComparison.Ordinal))
                yield break;
            var open = text.IndexOf(" { ", StringComparison.Ordinal);
            if (open < 0 || !text.EndsWith("; }", StringComparison.Ordinal)) yield break;
            foreach (var accessor in text.Substring(open + 3, text.Length - open - 6).Split(new[] { "; " }, StringSplitOptions.None))
                yield return (accessor.Split(' ')[0], accessor.Contains(RequirementMarker.TrimStart(), StringComparison.Ordinal));
        }

        private sealed class ApiType
        {
            public ApiType(string header)
            {
                Header = header;
            }

            public string Header { get; }
            public List<string> Members { get; } = new List<string>();
        }

        [RequiresTopiaForge("0.2.0")]
        private sealed class VersionedFixture
        {
            public void Inherited() { }
            public int Later { [RequiresTopiaForge("0.3.0")] get => 0; }
        }
    }
}
