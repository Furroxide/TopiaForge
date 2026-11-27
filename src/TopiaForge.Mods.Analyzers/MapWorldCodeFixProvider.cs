using System;
using System.Collections.Immutable;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace TopiaForge.Mods.Analyzers
{
    /// <summary>Repairs an unambiguous map link or missing required Worlds dependency.</summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MapWorldCodeFixProvider)), Shared]
    public sealed class MapWorldCodeFixProvider : CodeFixProvider
    {
        /// <inheritdoc/>
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("TF1011");
        /// <inheritdoc/>
        public override FixAllProvider? GetFixAllProvider() => null;
        /// <inheritdoc/>
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();
            if (!diagnostic.Properties.TryGetValue("mapFix", out var fix) || fix == null) return;
            var files = context.Document.Project.AdditionalDocuments.Where(file =>
                string.Equals(Path.GetFileName(file.FilePath ?? file.Name), "topiaforge.mod.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (files.Length != 1) return;
            var file = files[0]; var text = await file.GetTextAsync(context.CancellationToken).ConfigureAwait(false);
            if (text.Length > 1024 * 1024) return;
            ManifestSnapshot manifest;
            try { manifest = ManifestSnapshot.Parse(text.ToString()); }
            catch (FormatException) { return; }
            if (manifest.HasDuplicateKeys) return;
            TextChange change; string title;
            if (fix == "id" && diagnostic.Properties.TryGetValue("mapValue", out var id) && TopiaForgeModAnalyzer.MapId(id, 64))
            {
                var matches = manifest.MapWorlds.Where(world => world.Spans.TryGetValue("map", out var span)
                    && span == diagnostic.Location.SourceSpan && TopiaForgeModAnalyzer.MapPageId(world.Value("map") ?? "") == id).ToArray();
                if (matches.Length != 1) return;
                change = new TextChange(diagnostic.Location.SourceSpan, "\"" + id + "\""); title = "Use the published map id";
            }
            else if (fix == "dependency" && !manifest.RequiredWorlds)
            {
                var item = "\"" + ManifestSnapshot.WorldsDependency + "\": \">=0.2.0 <0.3.0\"";
                change = manifest.DependencyInsertion >= 0 ? new TextChange(new TextSpan(manifest.DependencyInsertion, 0),
                    (manifest.DependencyCount == 0 ? "" : ", ") + item)
                    : new TextChange(new TextSpan(manifest.RootInsertion, 0), ", \"dependencies\": {" + item + "}");
                title = "Add the required TopiaForge Worlds dependency";
            }
            else return;
            var changed = text.WithChanges(change);
            context.RegisterCodeFix(CodeAction.Create(title,
                _ => Task.FromResult(file.Project.Solution.WithAdditionalDocumentText(file.Id, changed)), "MapWorld:" + fix), diagnostic);
        }
    }
}
