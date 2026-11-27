using System;
using System.IO;
using System.Text.RegularExpressions;

namespace TopiaForge.Mods.Analyzers.Tests
{
    internal static partial class Program
    {
        // The IDE opens a rule's help link from the diagnostic's id. Every rule's link reaches its entry: the docs site
        // redirects it to the rule's heading on the reference page, and that heading exists.
        private static void EveryRuleLinksToItsEntry()
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "TopiaForge.slnx")))
            {
                root = Path.GetDirectoryName(root);
            }

            Assert(root != null, "the analyzer tests run inside the repository");
            var redirects = File.ReadAllText(Path.Combine(root!, "website", "astro.config.mjs"));
            var reference = File.ReadAllText(Path.Combine(root!, "docs", "Diagnostics.md"));
            foreach (var rule in new TopiaForgeModAnalyzer().SupportedDiagnostics)
            {
                var path = new Uri(rule.HelpLinkUri).AbsolutePath;
                var anchor = rule.Id.ToLowerInvariant();
                Assert(redirects.Contains("'" + path + "': '/reference/diagnostics/#" + anchor + "'"),
                    rule.Id + "'s help link, " + rule.HelpLinkUri + ", redirects to its entry in website/astro.config.mjs");
                Assert(Regex.IsMatch(reference, "^### " + rule.Id + "[ \t]*\r?$", RegexOptions.Multiline),
                    rule.Id + " has its entry in docs/Diagnostics.md, which the redirect names");
            }
        }
    }
}
