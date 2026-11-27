using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.Mods.Analyzers
{
    /// <summary>Offers the nearest presets from a verified catalogue.</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EnvironmentPresetCodeFixProvider)), Shared]
    public sealed class EnvironmentPresetCodeFixProvider : CodeFixProvider
    {
        /// <inheritdoc/>
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("TF1015");
        /// <inheritdoc/>
        public override FixAllProvider? GetFixAllProvider() => null;
        /// <inheritdoc/>
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();
            if (!diagnostic.Properties.TryGetValue("presetSuggestions", out var values) || values == null) return;
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var expression = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (root == null || expression == null) return;
            foreach (var key in values.Split('|').Where(EnvironmentPresetCatalogue.ValidKey))
            {
                var replacement = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(key)).WithTriviaFrom(expression);
                context.RegisterCodeFix(CodeAction.Create("Use environment " + key,
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(expression, replacement))), "EnvironmentPreset:" + key), diagnostic);
            }
        }
    }
}
