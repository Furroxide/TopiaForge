using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TopiaForge.Mods.Analyzers
{
    public sealed partial class TopiaForgeModAnalyzer
    {
        private static readonly DiagnosticDescriptor ParticipantViewChange = new DiagnosticDescriptor("TF1014", "Participant changes an authoritative view",
            "Participant code cannot change the world view or place blockers, even in single player. Move this call to the gamemode's server controller.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: DocsRoot + "TF1014", customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });
        private static readonly DiagnosticDescriptor UnknownEnvironmentPreset = new DiagnosticDescriptor("TF1015", "Unknown environment preset",
            "Environment '{0}' is absent from verified game build {1}. Nearest presets: {2}.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: DocsRoot + "TF1015");
        private readonly EnvironmentPresetCatalogue presets;
        /// <summary>Creates the analyzer with the shipped, qualified native preset catalogue.</summary>
        public TopiaForgeModAnalyzer() : this(EnvironmentPresetCatalogue.Shipped) { }
        internal TopiaForgeModAnalyzer(EnvironmentPresetCatalogue presets) { this.presets = presets; }
        private void RegisterWorldView(CompilationStartAnalysisContext context)
        {
            var participants = new ParticipantViewAnalysis();
            context.RegisterOperationAction(participants.Record, OperationKind.Invocation, OperationKind.ObjectCreation);
            context.RegisterCompilationEndAction(end =>
            {
                foreach (var location in participants.Diagnostics(end.CancellationToken))
                    end.ReportDiagnostic(Diagnostic.Create(ParticipantViewChange, location));
            });
            context.RegisterSyntaxNodeAction(node =>
            {
                var call = (InvocationExpressionSyntax)node.Node;
                if (!(node.SemanticModel.GetSymbolInfo(call, node.CancellationToken).Symbol is IMethodSymbol method)) return;
                var view = IsViewCall(method);
                if (!view || method.Name != "Environment" || !presets.Verified || call.ArgumentList.Arguments.Count == 0) return;
                var argument = call.ArgumentList.Arguments[0].Expression;
                var value = node.SemanticModel.GetConstantValue(argument, node.CancellationToken);
                if (!(value.Value is string key) || !EnvironmentPresetCatalogue.ValidKey(key) || presets.Keys.Contains(key, StringComparer.Ordinal)) return;
                var nearest = presets.Nearest(key);
                var properties = ImmutableDictionary<string, string?>.Empty.Add("presetSuggestions", string.Join("|", nearest));
                node.ReportDiagnostic(Diagnostic.Create(UnknownEnvironmentPreset, argument.GetLocation(), properties, key, presets.Build, string.Join(", ", nearest)));
            }, SyntaxKind.InvocationExpression);
        }
        internal static bool IsViewCall(IMethodSymbol method) =>
            (method.Name == "Environment" || method.Name == "Fog" || method.Name == "Hide") && Implements(method.ContainingType, "TopiaForge.Mods.IViewControls")
            || method.Name == "Hide" && (method.ReducedFrom ?? method).ContainingType.ToDisplayString() == "TopiaForge.Mods.WorldViewExtensions";
        internal static bool Implements(INamedTypeSymbol type, string name) => type.ToDisplayString() == name || type.AllInterfaces.Any(i => i.ToDisplayString() == name);
    }
}
