using System;
using System.Collections.Immutable;
using System.Composition;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;
using TopiaForge.ModManager.Core;

namespace TopiaForge.Mods.Analyzers
{
    /// <summary>Raises both manifest floors when their bounded ranges can be edited unambiguously.</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SdkVersionCodeFixProvider)), Shared]
    public sealed class SdkVersionCodeFixProvider : CodeFixProvider
    {
        private static readonly Regex SimpleRange = new Regex(@"^\s*>=\s*(\S+)\s+<\s*(\S+)\s*$");
        /// <inheritdoc/>
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("TF1010");
        /// <inheritdoc/>
        public override FixAllProvider? GetFixAllProvider() => null;

        /// <inheritdoc/>
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();
            if (!diagnostic.Properties.TryGetValue(TopiaForgeModAnalyzer.RequiredVersionProperty, out var required)
                || required == null) return;
            var manifests = context.Document.Project.AdditionalDocuments.Where(file => string.Equals(
                Path.GetFileName(file.FilePath ?? file.Name), "topiaforge.mod.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (manifests.Length != 1) return;
            var document = manifests[0];
            var text = await document.GetTextAsync(context.CancellationToken).ConfigureAwait(false);
            if (text.Length > 1024 * 1024) return;
            ManifestSnapshot manifest;
            try { manifest = ManifestSnapshot.Parse(text.ToString()); }
            catch (FormatException) { return; }
            var loader = Raise(manifest.LoaderRange, required);
            var sdk = Raise(manifest.SdkRange, required);
            if (loader == null || sdk == null || manifest.LoaderSpan.IsEmpty || manifest.SdkSpan.IsEmpty) return;
            var changed = text.WithChanges(
                new TextChange(manifest.LoaderSpan, Quote(loader)),
                new TextChange(manifest.SdkSpan, Quote(sdk)));
            context.RegisterCodeFix(CodeAction.Create("Raise the loader and SDK minimum to " + required,
                _ => Task.FromResult(document.Project.Solution.WithAdditionalDocumentText(document.Id, changed)),
                equivalenceKey: "RaiseTopiaForgeMinimum"), diagnostic);
        }

        private static string? Raise(string range, string required)
        {
            var match = SimpleRange.Match(range);
            if (!match.Success || !VersionUtil.TryParseRange(range) || !VersionUtil.TryParse(required, out _)) return null;
            var lower = match.Groups[1].Value;
            var upper = match.Groups[2].Value;
            if (!VersionUtil.TryParse(lower, out _) || !VersionUtil.TryParse(upper, out _)) return null;
            // Never widen a ceiling or lower an already stricter floor.
            if (VersionUtil.IsAtLeast(required, upper)) return null;
            if (VersionUtil.IsAtLeast(lower, required)) return range;
            return range.Substring(0, match.Groups[1].Index) + required
                + range.Substring(match.Groups[1].Index + match.Groups[1].Length);
        }

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\f", "\\f").Replace("\b", "\\b") + "\"";
    }
}
