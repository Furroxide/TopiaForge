using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Source-level conventions for first-party mods that the type system can't express.
    ///
    /// Most mod sources reference UnityEngine and therefore cannot be compiled into this offline test
    /// assembly, which is exactly how a main-thread deadlock and an unvalidated config document both reached
    /// a release candidate. These scans cover the checked-in sources directly, so they hold for every mod
    /// regardless of whether its types can be loaded here.
    /// </summary>
    internal static class ModConcurrencyConventionTests
    {
        public static void Run()
        {
            NoBlockingWaitsOnSdkTasks();
            BoundedConfigsDeclareAValidator();
            Console.WriteLine("ModConcurrencyConventionTests passed.");
        }

        /// <summary>
        /// SDK asset and scene tasks complete on Unity's main thread — <c>OwnerAssetService.LoadBundleAsync</c>
        /// finishes from an <c>AssetBundleCreateRequest.completed</c> callback that asserts the main thread.
        /// Blocking the main thread on one stops the engine's update pump, so the callback never runs and the
        /// game hangs permanently. Mods must poll (<c>IsCompleted</c>) and drain instead of waiting.
        /// </summary>
        private static void NoBlockingWaitsOnSdkTasks()
        {
            var modsRoot = Path.Combine(Program.FindRepoRoot(), "mods");
            var files = EnumerateModSources(modsRoot).ToArray();
            var sources = files.ToDictionary(file => file, File.ReadAllText, StringComparer.OrdinalIgnoreCase);
            var pollScopes = BuildPollScopes(sources);
            var violations = new List<string>();

            foreach (var file in files)
            {
                foreach (var site in FindBlockingCalls(sources[file]))
                {
                    // Blocking on a freshly returned task is never a poll: the caller cannot have observed
                    // IsCompleted on a task it is creating in the same expression. This is always a hang.
                    if (site.ReceiverIsInvocation)
                    {
                        violations.Add(Describe(modsRoot, file, site)
                            + " blocks on a task returned by the same expression. Start the task, keep it in a"
                            + " field, and drain it from the mod's per-frame update once IsCompleted is true.");
                        continue;
                    }

                    // Blocking on a stored task is the legitimate drain pattern, but only when the surrounding
                    // type actually polls. Partial types are scoped across their sibling files.
                    if (!pollScopes[file])
                    {
                        violations.Add(Describe(modsRoot, file, site)
                            + " blocks on a task, but nothing in its type checks IsCompleted first. Guard the"
                            + " drain with an IsCompleted poll.");
                    }
                }
            }

            if (violations.Count > 0)
            {
                throw new InvalidOperationException(
                    "First-party mods must never block on an SDK task:" + Environment.NewLine
                    + string.Join(Environment.NewLine, violations.OrderBy(text => text, StringComparer.Ordinal)));
            }
        }

        /// <summary>
        /// A config type with bounded numeric members must declare ISelfNormalizingConfig. ConfigDefinition then
        /// normalizes it on every path IModConfigService validates — defaults, load, migration, and save — so a
        /// hand-edited or corrupted document cannot reach gameplay with NaN, negative, or inverted values. This
        /// asserts the declaration rather than a hand-copied validator lambda: the declaration is what makes the
        /// behavior automatic, and it is the thing that cannot be silently omitted from a working mod.
        /// </summary>
        private static void BoundedConfigsDeclareAValidator()
        {
            var modsRoot = Path.Combine(Program.FindRepoRoot(), "mods");
            var sources = EnumerateModSources(modsRoot)
                .ToDictionary(file => file, File.ReadAllText, StringComparer.OrdinalIgnoreCase);
            var violations = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var pair in sources)
            {
                foreach (Match match in Regex.Matches(
                             pair.Value,
                             @"new\s+ConfigDefinition\s*<\s*(?<type>[A-Za-z_][A-Za-z0-9_.]*)\s*>\s*\(",
                             RegexOptions.CultureInvariant))
                {
                    var configType = match.Groups["type"].Value;
                    var shortName = configType.Substring(configType.LastIndexOf('.') + 1);
                    if (!seen.Add(shortName) || !HasBoundedNumericMember(sources.Values, shortName))
                    {
                        continue;
                    }

                    if (!DeclaresSelfNormalizing(sources.Values, shortName))
                    {
                        violations.Add(Path.GetRelativePath(modsRoot, pair.Key)
                            + ": " + shortName + " has bounded numeric members but does not declare"
                            + " ISelfNormalizingConfig, so a stored document is never normalized. Implement the"
                            + " interface and bound every member in Normalize().");
                    }
                }
            }

            if (violations.Count > 0)
            {
                throw new InvalidOperationException(
                    "Bounded first-party configs must normalize on load:" + Environment.NewLine
                    + string.Join(Environment.NewLine, violations.OrderBy(text => text, StringComparer.Ordinal)));
            }
        }

        private static bool DeclaresSelfNormalizing(IEnumerable<string> sources, string configTypeName)
        {
            var declaration = new Regex(
                @"\bclass\s+" + Regex.Escape(configTypeName) + @"\b\s*:\s*[^\{]*\bISelfNormalizingConfig\b",
                RegexOptions.CultureInvariant);
            return sources.Any(source => declaration.IsMatch(source));
        }

        private static bool HasBoundedNumericMember(IEnumerable<string> sources, string configTypeName)
        {
            var declaration = new Regex(
                @"\bclass\s+" + Regex.Escape(configTypeName) + @"\b",
                RegexOptions.CultureInvariant);
            foreach (var source in sources)
            {
                var match = declaration.Match(source);
                if (!match.Success)
                {
                    continue;
                }

                var body = ExtractTypeBody(source, match.Index);
                if (Regex.IsMatch(
                        body,
                        @"\[DataMember[^\]]*\]\s*public\s+(?:float|double|decimal|int|long|short|byte)\s",
                        RegexOptions.CultureInvariant))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ExtractTypeBody(string source, int declarationIndex)
        {
            var open = source.IndexOf('{', declarationIndex);
            if (open < 0)
            {
                return string.Empty;
            }

            var depth = 0;
            for (var index = open; index < source.Length; index++)
            {
                if (source[index] == '{')
                {
                    depth++;
                }
                else if (source[index] == '}' && --depth == 0)
                {
                    return source.Substring(open, index - open + 1);
                }
            }

            return source.Substring(open);
        }

        /// <summary>
        /// Splits the argument list starting at the given open parenthesis on top-level commas. Only bracket
        /// pairs nest — angle brackets are deliberately ignored because `=>` would otherwise read as a closer,
        /// which would truncate every lambda-valued argument. Literals and comments are skipped so a bracket
        /// or comma inside them cannot shift the depth.
        /// </summary>
        private static List<string> SplitTopLevelArguments(string source, int openParenIndex)
        {
            var arguments = new List<string>();
            var depth = 0;
            var start = openParenIndex + 1;
            for (var index = openParenIndex; index < source.Length; index++)
            {
                index = SkipLiteralOrComment(source, index);
                if (index >= source.Length)
                {
                    break;
                }

                var character = source[index];
                if (character == '(' || character == '[' || character == '{')
                {
                    depth++;
                }
                else if (character == ')' || character == ']' || character == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        Add(arguments, source, start, index);
                        return arguments;
                    }
                }
                else if (character == ',' && depth == 1)
                {
                    Add(arguments, source, start, index);
                    start = index + 1;
                }
            }

            return arguments;

            static void Add(List<string> target, string text, int from, int to)
            {
                var argument = text.Substring(from, to - from).Trim();
                if (argument.Length > 0)
                {
                    target.Add(argument);
                }
            }
        }

        /// <summary>Returns the index of the last character of a literal or comment starting at the index.</summary>
        private static int SkipLiteralOrComment(string source, int index)
        {
            if (index + 1 < source.Length && source[index] == '/' && source[index + 1] == '/')
            {
                var end = source.IndexOf('\n', index);
                return end < 0 ? source.Length : end;
            }

            if (index + 1 < source.Length && source[index] == '/' && source[index + 1] == '*')
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                return end < 0 ? source.Length : end + 1;
            }

            if (source[index] != '"' && source[index] != '\'')
            {
                return index;
            }

            var quote = source[index];
            var verbatim = index > 0 && source[index - 1] == '@' && quote == '"';
            for (var scan = index + 1; scan < source.Length; scan++)
            {
                if (!verbatim && source[scan] == '\\')
                {
                    scan++;
                    continue;
                }

                if (source[scan] != quote)
                {
                    continue;
                }

                if (verbatim && scan + 1 < source.Length && source[scan + 1] == quote)
                {
                    scan++;
                    continue;
                }

                return scan;
            }

            return source.Length;
        }

        /// <summary>
        /// Maps each source file to whether its declaring type polls a task anywhere. Partial types share one
        /// scope across their sibling files, so a drain helper may live beside the poll that guards it.
        /// </summary>
        private static Dictionary<string, bool> BuildPollScopes(IReadOnlyDictionary<string, string> sources)
        {
            var partialGroups = new Dictionary<string, bool>(StringComparer.Ordinal);
            var fileTypes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in sources)
            {
                var partial = Regex.Match(
                    pair.Value,
                    @"\bpartial\s+(?:sealed\s+|abstract\s+|static\s+)*class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
                    RegexOptions.CultureInvariant);
                if (!partial.Success)
                {
                    fileTypes[pair.Key] = null;
                    continue;
                }

                // Scope by directory + type name: sibling partial files of the same type share a poll scope.
                var key = (Path.GetDirectoryName(pair.Key) ?? string.Empty) + "|" + partial.Groups["name"].Value;
                fileTypes[pair.Key] = key;
                partialGroups[key] = partialGroups.TryGetValue(key, out var polls) && polls
                    || pair.Value.Contains("IsCompleted", StringComparison.Ordinal);
            }

            return sources.Keys.ToDictionary(
                file => file,
                file =>
                {
                    var key = fileTypes[file];
                    return key != null
                        ? partialGroups[key]
                        : sources[file].Contains("IsCompleted", StringComparison.Ordinal);
                },
                StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<BlockingCall> FindBlockingCalls(string source)
        {
            // `X.GetAwaiter().GetResult()`, `X.Wait()`, and `<something>Task.Result` are the three shapes that
            // synchronously block. The captured character before the member access says whether the receiver was
            // a stored value (identifier) or a call made in the same expression (closing parenthesis).
            const string Patterns =
                @"(?<recv>[)\]\w])\s*\.\s*GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\(\s*\)"
                + @"|(?<recv>[)\]\w])\s*\.\s*Wait\s*\(\s*\)"
                + @"|(?<recv>[)\]]|\w*[Tt]ask)\s*\.\s*Result\b";

            foreach (Match match in Regex.Matches(source, Patterns, RegexOptions.CultureInvariant))
            {
                var receiver = match.Groups["recv"].Value;
                yield return new BlockingCall(
                    LineOf(source, match.Index),
                    match.Value.Trim(),
                    receiver.EndsWith(")", StringComparison.Ordinal)
                        || receiver.EndsWith("]", StringComparison.Ordinal));
            }
        }

        private static IEnumerable<string> EnumerateModSources(string modsRoot)
        {
            var separator = Path.DirectorySeparatorChar;
            return Directory.EnumerateFiles(modsRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(separator + "bin" + separator, StringComparison.Ordinal)
                    && !path.Contains(separator + "obj" + separator, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal);
        }

        private static string Describe(string modsRoot, string file, BlockingCall site) =>
            Path.GetRelativePath(modsRoot, file) + ":" + site.Line + " `" + site.Text + "`";

        private static int LineOf(string source, int index)
        {
            var line = 1;
            for (var position = 0; position < index && position < source.Length; position++)
            {
                if (source[position] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        private readonly struct BlockingCall
        {
            public BlockingCall(int line, string text, bool receiverIsInvocation)
            {
                Line = line;
                Text = text;
                ReceiverIsInvocation = receiverIsInvocation;
            }

            public int Line { get; }
            public string Text { get; }
            public bool ReceiverIsInvocation { get; }
        }
    }
}
