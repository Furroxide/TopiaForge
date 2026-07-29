using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TopiaForge.GameCompat;

namespace TopiaForge.GameCompat.Extractor
{
    // Keeps a manifest honest against the code it claims to describe — a lying manifest is worse than none. This
    // is deliberately a source-literal scan (offline, no DLL) so it runs in CI. It is HEURISTIC: reflection strings
    // can be built dynamically, so findings are advisory and can be silenced per-mod via <modId>.audit-allow.json.
    // The high-signal direction it enforces is: every `", GameCode"` static type literal in a mod's source has a
    // corresponding manifest binding (so "edited the bridge, forgot the manifest" is caught).
    internal static class GameReflectionAuditor
    {
        private const int MaxSourceFileBytes = 2 * 1024 * 1024;
        private const int MaxAggregateSourceCharacters = 32 * 1024 * 1024;
        private const int MaxSourceFiles = 4096;
        private const int MaxProjectFiles = 32;
        private const int MaxProjectFileBytes = 1024 * 1024;
        private const int MaxAllowFileBytes = 256 * 1024;

        private static readonly Regex StaticTypeLiteral =
            new(@"""(?<type>[A-Za-z0-9_.+]+),\s*GameCode""", RegexOptions.Compiled);

        public sealed class AuditFinding
        {
            public string ModId = string.Empty;
            public string Kind = string.Empty; // "undeclared" | "stale"
            public string Detail = string.Empty;
        }

        public static List<AuditFinding> Audit(string repoRoot)
        {
            var findings = new List<AuditFinding>();
            var manifests = ManifestLoader.LoadAll(repoRoot);

            foreach (var (manifest, _) in manifests)
            {
                var sourceDir = ResolveModSourceDir(repoRoot, manifest.ModId);
                if (sourceDir == null)
                {
                    continue; // no matching mod folder (e.g. a synthetic/aggregate manifest); skip quietly
                }

                var sources = DiscoverCompiledSources(repoRoot, sourceDir);

                var allText = StripComments(ReadSources(sources, manifest.ModId));
                var allow = LoadAllow(repoRoot, manifest.ModId);

                // 1) undeclared: every "X, GameCode" literal should be declared as a binding on type X.
                var declaredTypes = new HashSet<string>(
                    manifest.Bindings.Select(b => b.DeclaringType), StringComparer.Ordinal);

                foreach (Match match in StaticTypeLiteral.Matches(allText))
                {
                    var type = match.Groups["type"].Value;
                    if (!declaredTypes.Contains(type) && !allow.Contains("type:" + type))
                    {
                        findings.Add(new AuditFinding
                        {
                            ModId = manifest.ModId,
                            Kind = "undeclared",
                            Detail = "source resolves 'Type.GetType(\"" + type + ", GameCode\")' but no manifest binding declares type '" + type + "'",
                        });
                    }
                }

                // 2) stale: every binding's member (or type simple name) should appear somewhere in the source.
                foreach (var binding in manifest.Bindings)
                {
                    if (allow.Contains("binding:" + binding.Id))
                    {
                        continue;
                    }

                    // Skip kinds whose "name" is not expected to appear verbatim in source: dynamic helpers
                    // (Uncheckable), constructors (identified by type + Activator, not a member literal), and
                    // ordinal-mapped enum members (the mod casts (int), it never writes the member name).
                    if (binding.MatchMode == MatchMode.Uncheckable ||
                        binding.Kind == BindingKind.Constructor ||
                        (binding.Kind == BindingKind.EnumValue && binding.HasExpectedOrdinal))
                    {
                        continue;
                    }

                    var needle = binding.Member.Length > 0 ? binding.Member : SimpleName(binding.DeclaringType);
                    if (needle.Length == 0)
                    {
                        continue;
                    }

                    if (!Regex.IsMatch(allText, "\\b" + Regex.Escape(needle) + "\\b"))
                    {
                        findings.Add(new AuditFinding
                        {
                            ModId = manifest.ModId,
                            Kind = "stale",
                            Detail = "binding '" + binding.Id + "' names '" + needle + "' which appears nowhere in the mod source (removed dependency? rename the binding or add to audit-allow)",
                        });
                    }
                }
            }

            return findings;
        }

        // SDK-style projects compile local **/*.cs files implicitly, but may also source-link shared code with
        // explicit Compile Include items. Scanning only the mod directory makes those real runtime bindings look
        // stale (and can miss undeclared bindings in shared helpers), so mirror the relevant project-item rules.
        private static List<string> DiscoverCompiledSources(string repoRoot, string sourceDir)
        {
            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var sources = new HashSet<string>(comparer);
            var projects = Directory.GetFiles(sourceDir, "*.csproj", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            if (projects.Count > MaxProjectFiles)
            {
                throw new InvalidDataException(
                    "Mod source directory exceeds the " + MaxProjectFiles + " project-file safety limit: "
                    + sourceDir);
            }

            if (projects.Count == 0)
            {
                AddDefaultSources(sourceDir, sources);
                return sources.OrderBy(path => path, StringComparer.Ordinal).ToList();
            }

            foreach (var project in projects)
            {
                var projectXml = ExtractorFileIo.ReadStableUtf8(
                    project,
                    MaxProjectFileBytes,
                    "GameCompat project file");
                var document = ParseProjectDocument(projectXml);
                var defaultItems = !document.Descendants()
                    .Where(element => element.Name.LocalName == "EnableDefaultCompileItems")
                    .Select(element => element.Value.Trim())
                    .Any(value => string.Equals(value, "false", StringComparison.OrdinalIgnoreCase));
                if (defaultItems)
                {
                    AddDefaultSources(Path.GetDirectoryName(project)!, sources);
                }

                foreach (var compile in document.Descendants().Where(element => element.Name.LocalName == "Compile"))
                {
                    var include = compile.Attribute("Include")?.Value;
                    if (!string.IsNullOrWhiteSpace(include))
                    {
                        foreach (var path in ExpandCompileItems(repoRoot, Path.GetDirectoryName(project)!, include))
                        {
                            AddSource(sources, path);
                        }
                    }
                }

                // Honor explicit removals after includes, matching MSBuild item evaluation for the simple literal/
                // glob forms used by TopiaForge projects. Update/Link metadata does not affect physical discovery.
                foreach (var compile in document.Descendants().Where(element => element.Name.LocalName == "Compile"))
                {
                    var remove = compile.Attribute("Remove")?.Value;
                    if (!string.IsNullOrWhiteSpace(remove))
                    {
                        foreach (var path in ExpandCompileItems(repoRoot, Path.GetDirectoryName(project)!, remove))
                        {
                            sources.Remove(path);
                        }
                    }
                }
            }

            return sources.OrderBy(path => path, StringComparer.Ordinal).ToList();
        }

        private static void AddDefaultSources(string sourceDir, ISet<string> sources)
        {
            foreach (var path in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, path).Replace('\\', '/');
                if (!HasDirectorySegment(relative, "obj") && !HasDirectorySegment(relative, "bin"))
                {
                    AddSource(sources, Path.GetFullPath(path));
                }
            }
        }

        private static IEnumerable<string> ExpandCompileItems(
            string repoRoot,
            string projectDir,
            string expressions)
        {
            foreach (var rawExpression in expressions.Split(';'))
            {
                var expression = rawExpression.Trim().Replace('\\', '/');
                if (expression.Length == 0 || expression.Contains("$(", StringComparison.Ordinal))
                {
                    continue; // evaluated-property items require MSBuild; no TopiaForge linked source uses one
                }

                var wildcard = expression.IndexOfAny(new[] { '*', '?' });
                if (wildcard < 0)
                {
                    var path = Path.GetFullPath(Path.Combine(
                        projectDir,
                        expression.Replace('/', Path.DirectorySeparatorChar)));
                    if (IsRepositorySource(repoRoot, path))
                    {
                        yield return path;
                    }

                    continue;
                }

                var prefix = expression.Substring(0, wildcard);
                var slash = prefix.LastIndexOf('/');
                var searchRelative = slash >= 0 ? prefix.Substring(0, slash + 1) : string.Empty;
                var searchRoot = Path.GetFullPath(Path.Combine(
                    projectDir,
                    searchRelative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsInside(repoRoot, searchRoot) || !Directory.Exists(searchRoot))
                {
                    continue;
                }

                var matcher = GlobRegex(expression);
                foreach (var path in Directory.EnumerateFiles(searchRoot, "*.cs", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(projectDir, path).Replace('\\', '/');
                    if (matcher.IsMatch(relative) && IsRepositorySource(repoRoot, path))
                    {
                        yield return Path.GetFullPath(path);
                    }
                }
            }
        }

        private static Regex GlobRegex(string expression)
        {
            var pattern = new System.Text.StringBuilder("^");
            for (var index = 0; index < expression.Length; index++)
            {
                var character = expression[index];
                if (character == '*' && index + 1 < expression.Length && expression[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < expression.Length && expression[index + 1] == '/')
                    {
                        index++;
                        pattern.Append("(?:.*/)?");
                    }
                    else
                    {
                        pattern.Append(".*");
                    }
                }
                else if (character == '*')
                {
                    pattern.Append("[^/]*");
                }
                else if (character == '?')
                {
                    pattern.Append("[^/]");
                }
                else
                {
                    pattern.Append(Regex.Escape(character.ToString()));
                }
            }

            pattern.Append('$');
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (OperatingSystem.IsWindows())
            {
                options |= RegexOptions.IgnoreCase;
            }

            return new Regex(pattern.ToString(), options);
        }

        private static bool IsRepositorySource(string repoRoot, string path)
        {
            if (!IsInside(repoRoot, path) ||
                !File.Exists(path) ||
                !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }

        private static bool IsInside(string root, string path)
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            return string.Equals(fullRoot, fullPath, comparison) ||
                   fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
        }

        private static bool HasDirectorySegment(string relativePath, string segment)
        {
            return relativePath.Split('/').Any(part =>
                string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));
        }

        private static string? ResolveModSourceDir(string repoRoot, string modId)
        {
            var modsRoot = Path.Combine(repoRoot, "mods");
            if (!Directory.Exists(modsRoot))
            {
                return null;
            }

            // Canonical first-party ids include the reverse-DNS owner prefix while source folders begin at
            // TopiaForge. Strip only that ownership prefix before comparing the collapsed technical identity.
            const string firstPartyPrefix = "io.github.furroxide.topiaforge.";
            var sourceIdentity = modId.StartsWith(firstPartyPrefix, StringComparison.Ordinal)
                ? "topiaforge." + modId.Substring(firstPartyPrefix.Length)
                : modId;
            var wanted = sourceIdentity.Replace(".", string.Empty).ToLowerInvariant();
            foreach (var dir in Directory.GetDirectories(modsRoot))
            {
                var folder = Path.GetFileName(dir).Replace(".", string.Empty).ToLowerInvariant();
                if (folder == wanted)
                {
                    return dir;
                }
            }

            return null;
        }

        private static HashSet<string> LoadAllow(string repoRoot, string modId)
        {
            var path = Path.Combine(repoRoot, "bindings", modId + ".audit-allow.json");
            var allow = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var root = JsonValue.Parse(ExtractorFileIo.ReadStableUtf8(
                    path,
                    MaxAllowFileBytes,
                    "GameCompat audit allow-list")).AsObject();
                foreach (var item in root.GetArray("allow").Items)
                {
                    allow.Add(item.AsString());
                }
            }
            catch (FileNotFoundException)
            {
                return allow;
            }
            catch (DirectoryNotFoundException)
            {
                return allow;
            }

            return allow;
        }

        private static XDocument ParseProjectDocument(string xml)
        {
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(
                text,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument = MaxProjectFileBytes,
                    XmlResolver = null,
                }))
            {
                return XDocument.Load(reader, LoadOptions.None);
            }
        }

        private static string ReadSources(IReadOnlyList<string> sources, string modId)
        {
            if (sources.Count > MaxSourceFiles)
            {
                throw new InvalidDataException(
                    "GameCompat source audit for " + modId + " exceeds the " + MaxSourceFiles
                    + " file safety limit.");
            }

            var combined = new StringBuilder();
            foreach (var source in sources)
            {
                var text = ExtractorFileIo.ReadStableUtf8(
                    source,
                    MaxSourceFileBytes,
                    "GameCompat C# source");
                var separatorLength = combined.Length == 0 ? 0 : 1;
                if (text.Length > MaxAggregateSourceCharacters - combined.Length - separatorLength)
                {
                    throw new InvalidDataException(
                        "GameCompat source audit for " + modId + " exceeds the "
                        + MaxAggregateSourceCharacters + " character aggregate safety limit.");
                }

                if (separatorLength != 0)
                {
                    combined.Append('\n');
                }

                combined.Append(text);
            }

            return combined.ToString();
        }

        private static void AddSource(ISet<string> sources, string path)
        {
            sources.Add(path);
            if (sources.Count > MaxSourceFiles)
            {
                throw new InvalidDataException(
                    "GameCompat source discovery exceeds the " + MaxSourceFiles + " file safety limit.");
            }
        }

        // Strip block and line comments so doc examples like `/// Type.GetType("X, GameCode")` don't register as
        // real bindings. Good enough for an advisory scanner (a `//` inside a string literal is vanishingly rare here).
        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            source = Regex.Replace(source, @"//[^\n]*", " ");
            return source;
        }

        private static string SimpleName(string typeName)
        {
            var text = typeName;
            var dot = text.LastIndexOf('.');
            if (dot >= 0)
            {
                text = text.Substring(dot + 1);
            }

            var nested = text.LastIndexOf('+');
            if (nested >= 0)
            {
                text = text.Substring(nested + 1);
            }

            return text;
        }
    }
}
