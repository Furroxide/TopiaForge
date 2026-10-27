using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace TopiaForge.Mods.Analyzers
{
    public sealed partial class TopiaForgeModAnalyzer
    {
        private static readonly DiagnosticDescriptor MapWorldRequired = new DiagnosticDescriptor("TF1012", "Gamemode needs a map world",
            "This gamemode reads authored map content, but none of its launch targets selects a map world. Run topiaforge world add map <link>.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: DocsRoot + "TF1012");
        private static readonly DiagnosticDescriptor MissingSurveyEntity = new DiagnosticDescriptor("TF1013", "Entity is absent from the map survey",
            "Entity '{0}' is absent from the matching map survey. Run topiaforge world survey <world>; nearest ids: {1}.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: DocsRoot + "TF1013", customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });
        private static void RegisterAuthoredMaps(CompilationStartAnalysisContext context)
        {
            var data = AuthoredMapAnalysis.Read(context.Compilation, context.Options.AdditionalFiles, context.CancellationToken);
            if (data.Modes.Count == 0) return;
            context.RegisterSyntaxNodeAction(node =>
            {
                var symbol = node.SemanticModel.GetSymbolInfo(node.Node, node.CancellationToken).Symbol;
                var type = symbol?.ContainingType?.ToDisplayString();
                var needsMap = node.Node is InvocationExpressionSyntax && symbol?.Name == "RequireMap" && type == "TopiaForge.Mods.WorldReadinessExtensions"
                    || (node.Node is MemberAccessExpressionSyntax || node.Node is MemberBindingExpressionSyntax) && symbol?.Name == "Map" && type == "TopiaForge.Mods.WorldReadiness";
                var modes = data.Owners(node.ContainingSymbol);
                if (needsMap && modes.Count > 0 && modes.All(m => !m.UnknownWorld && !m.Worlds.Any(w => w.IsMap)))
                    node.ReportDiagnostic(Diagnostic.Create(MapWorldRequired, node.Node.GetLocation()));
                if (!(node.Node is InvocationExpressionSyntax call) || type != "TopiaForge.Mods.IAuthoredMap" ||
                    !(symbol?.Name == "Entity" || symbol?.Name == "TryGetEntity") || call.ArgumentList.Arguments.Count == 0) return;
                var invocation = node.SemanticModel.GetOperation(call, node.CancellationToken) as IInvocationOperation;
                var argument = invocation?.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value.Syntax as ExpressionSyntax;
                if (argument == null) return;
                var constant = node.SemanticModel.GetConstantValue(argument, node.CancellationToken);
                if (!(constant.Value is string id) || !MapId(id, 64)) return;
                var surveys = data.ForModes(modes);
                if (surveys.Count == 0 || surveys.Any(s => s.Records.ContainsKey(id))) return;
                var suggestions = AuthoredMapAnalysis.Suggestions(surveys, id);
                var properties = ImmutableDictionary<string, string?>.Empty.Add("entityId", id).Add("entitySuggestions", string.Join("|", suggestions));
                node.ReportDiagnostic(Diagnostic.Create(MissingSurveyEntity, argument.GetLocation(), properties, id, string.Join(", ", suggestions)));
            }, SyntaxKind.InvocationExpression, SyntaxKind.SimpleMemberAccessExpression, SyntaxKind.MemberBindingExpression);
            context.RegisterCompilationEndAction(end => AnalyzeAnchorFiles(end, data));
        }
        private static void AnalyzeAnchorFiles(CompilationAnalysisContext context, AuthoredMapAnalysis data)
        {
            if (data.Surveys.Count == 0) return;
            foreach (var file in context.Options.AdditionalFiles)
            {
                if (!context.Options.AnalyzerConfigOptionsProvider.GetOptions(file).TryGetValue("build_metadata.AdditionalFiles.TopiaForgeMapPackedPath", out var packed)
                    || !packed.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var json = AuthoredMapAnalysis.ReadJson(file, 256 * 1024, context.CancellationToken);
                if (!(AuthoredMapAnalysis.Value(json, "schemaVersion") is double version) || version != 1) continue;
                var anchors = AuthoredMapAnalysis.Array(json, "anchors");
                if (anchors.Count == 0 || anchors.Count > 1024) continue;
                var text = file.GetText(context.CancellationToken)!;
                var tokens = Regex.Matches(text.ToString(), "\"entityId\"\\s*:\\s*(\"[A-Za-z0-9_-]{1,64}\")", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (tokens.Count != anchors.Count) continue;
                for (var i = 0; i < anchors.Count; i++)
                {
                    var anchor = AuthoredMapAnalysis.Object(anchors[i]); var id = AuthoredMapAnalysis.Text(anchor, "entityId");
                    if (!MapId(id, 64) || data.Surveys.Any(s => s.Records.ContainsKey(id))) continue;
                    var group = tokens[i].Groups[1];
                    if (group.Value != "\"" + id + "\"") continue;
                    var suggestions = AuthoredMapAnalysis.Suggestions(data.Surveys, id, AuthoredMapAnalysis.Text(anchor, "name"), AuthoredMapAnalysis.Text(anchor, "modelId"));
                    var properties = ImmutableDictionary<string, string?>.Empty.Add("entityId", id).Add("entitySuggestions", string.Join("|", suggestions)).Add("anchorFile", file.Path);
                    var span = new TextSpan(group.Index, group.Length);
                    context.ReportDiagnostic(Diagnostic.Create(MissingSurveyEntity, Location.Create(file.Path, span, text.Lines.GetLinePositionSpan(span)),
                        properties, id, string.Join(", ", suggestions)));
                }
            }
        }
    }
}
