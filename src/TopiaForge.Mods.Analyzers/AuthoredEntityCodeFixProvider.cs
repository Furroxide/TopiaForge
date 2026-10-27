using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace TopiaForge.Mods.Analyzers
{
    /// <summary>Offers surveyed identifiers without guessing which authored entity the author intended.</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AuthoredEntityCodeFixProvider)), Shared]
    public sealed class AuthoredEntityCodeFixProvider : CodeFixProvider
    {
        /// <inheritdoc/>
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("TF1013");
        /// <inheritdoc/>
        public override FixAllProvider? GetFixAllProvider() => null;
        /// <inheritdoc/>
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();
            if (!diagnostic.Properties.TryGetValue("entitySuggestions", out var values) || values == null) return;
            var anchor = diagnostic.Properties.TryGetValue("anchorFile", out var path)
                ? context.Document.Project.AdditionalDocuments.FirstOrDefault(d => d.FilePath == path || d.Name == path) : null;
            if (path != null && anchor == null) return;
            var text = anchor == null ? await context.Document.GetTextAsync(context.CancellationToken).ConfigureAwait(false)
                : await anchor.GetTextAsync(context.CancellationToken).ConfigureAwait(false);
            var span = diagnostic.Location.SourceSpan;
            if (span.End > text.Length) return;
            if (anchor != null && (!diagnostic.Properties.TryGetValue("entityId", out var previous) || text.ToString(span) != "\"" + previous + "\"")) return;
            foreach (var id in values.Split('|').Where(v => TopiaForgeModAnalyzer.MapId(v, 64)))
            {
                var changed = text.WithChanges(new TextChange(span, "\"" + id + "\""));
                context.RegisterCodeFix(CodeAction.Create("Use surveyed entity " + id, _ => Task.FromResult(anchor == null
                    ? context.Document.Project.Solution.WithDocumentText(context.Document.Id, changed)
                    : anchor.Project.Solution.WithAdditionalDocumentText(anchor.Id, changed)), "AuthoredEntity:" + id), diagnostic);
            }
        }
    }
}
