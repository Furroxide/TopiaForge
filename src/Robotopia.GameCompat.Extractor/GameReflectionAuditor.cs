using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Robotopia.GameCompat;

namespace Robotopia.GameCompat.Extractor
{
    // Keeps a manifest honest against the code it claims to describe — a lying manifest is worse than none. This
    // is deliberately a source-literal scan (offline, no DLL) so it runs in CI. It is HEURISTIC: reflection strings
    // can be built dynamically, so findings are advisory and can be silenced per-mod via <modId>.audit-allow.json.
    // The high-signal direction it enforces is: every `", GameCode"` static type literal in a mod's source has a
    // corresponding manifest binding (so "edited the bridge, forgot the manifest" is caught).
    internal static class GameReflectionAuditor
    {
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

                var sources = Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories)
                    .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                                !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    .ToList();

                var allText = StripComments(string.Join("\n", sources.Select(File.ReadAllText)));
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

        private static string? ResolveModSourceDir(string repoRoot, string modId)
        {
            var modsRoot = Path.Combine(repoRoot, "mods");
            if (!Directory.Exists(modsRoot))
            {
                return null;
            }

            // modId "robotopia.robotkit" <-> folder "Robotopia.RobotKit": compare the collapsed, lowercased tails.
            var wanted = modId.Replace(".", string.Empty).ToLowerInvariant();
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
            if (!File.Exists(path))
            {
                return allow;
            }

            try
            {
                var root = JsonValue.Parse(File.ReadAllText(path)).AsObject();
                foreach (var item in root.GetArray("allow").Items)
                {
                    allow.Add(item.AsString());
                }
            }
            catch
            {
                // A malformed allow file is ignored (the audit stays advisory).
            }

            return allow;
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
