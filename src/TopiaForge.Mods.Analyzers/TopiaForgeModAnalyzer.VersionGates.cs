using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using TopiaForge.ModManager.Core;

namespace TopiaForge.Mods.Analyzers
{
    public sealed partial class TopiaForgeModAnalyzer
    {
        internal const string RequiredVersionProperty = "TopiaForge.RequiredVersion";
        private const string VersionAttribute = "TopiaForge.Mods.RequiresTopiaForgeAttribute";
        private static readonly DiagnosticDescriptor SdkVersionRequired = new DiagnosticDescriptor(
            "TF1010", "SDK API needs a newer loader",
            "'{0}' needs TopiaForge {1} or newer, but topiaforge.mod.json admits {2}. Raise supportedLoaderVersionRange.",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: DocsRoot + "TF1010");

        private static void RegisterVersionGates(CompilationStartAnalysisContext context)
        {
            var manifest = ManifestSnapshot.Read(context.Options.AdditionalFiles, context.CancellationToken);
            context.RegisterSyntaxNodeAction(node => AnalyzeVersionGate(node, manifest),
                SyntaxKind.IdentifierName, SyntaxKind.GenericName,
                SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression,
                SyntaxKind.Attribute, SyntaxKind.BaseConstructorInitializer, SyntaxKind.ThisConstructorInitializer,
                SyntaxKind.ElementAccessExpression);
        }

        private static void AnalyzeVersionGate(SyntaxNodeAnalysisContext context, ManifestSnapshot manifest)
        {
            var node = context.Node;
            // Creation/attribute nodes check both the constructor and its declaring type once.
            if (node is SimpleNameSyntax && IsConstructedTypeName(node)) return;
            var info = context.SemanticModel.GetSymbolInfo(node, context.CancellationToken);
            var symbol = info.Symbol;
            // nameof can bind an overload group without selecting an overload.
            var symbols = symbol == null ? info.CandidateSymbols : ImmutableArray.Create(symbol);
            string? required = null;
            ISymbol? requiring = null;
            foreach (var candidate in symbols)
            {
                foreach (var member in RequirementSources(candidate, node))
                {
                    foreach (var attribute in member.GetAttributes())
                    {
                        if (attribute.AttributeClass?.ToDisplayString() != VersionAttribute
                            || attribute.ConstructorArguments.Length != 1
                            || !(attribute.ConstructorArguments[0].Value is string version)
                            || !VersionUtil.TryParse(version, out _)) continue;
                        if (required == null || !VersionUtil.IsAtLeast(required, version))
                        {
                            required = version;
                            requiring = candidate;
                        }
                    }
                }
            }

            if (required == null) return;
            if (VersionUtil.TryGetMinimum(manifest.LoaderRange, out var minimum)
                && minimum != null && VersionUtil.IsAtLeast(minimum, required)) return;
            context.ReportDiagnostic(Diagnostic.Create(SdkVersionRequired, node.GetLocation(),
                ImmutableDictionary<string, string?>.Empty.Add(RequiredVersionProperty, required),
                requiring!.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                required, manifest.LoaderRange));
        }

        private static bool IsConstructedTypeName(SyntaxNode node)
        {
            var current = node;
            while (current.Parent is QualifiedNameSyntax || current.Parent is AliasQualifiedNameSyntax)
                current = current.Parent;
            return (current.Parent is ObjectCreationExpressionSyntax creation && creation.Type == current)
                || (current.Parent is AttributeSyntax attribute && attribute.Name == current);
        }

        private static IEnumerable<ISymbol> RequirementSources(ISymbol symbol, SyntaxNode node)
        {
            yield return symbol.OriginalDefinition;
            if (symbol is IMethodSymbol method && method.ReducedFrom != null)
                yield return method.ReducedFrom.OriginalDefinition;
            if (symbol is IAliasSymbol alias) yield return alias.Target;
            if (symbol is IPropertySymbol property)
            {
                var access = node.Parent is MemberAccessExpressionSyntax member ? (SyntaxNode)member : node;
                while (access.Parent is ParenthesizedExpressionSyntax
                    || (access.Parent is ArgumentSyntax argument && argument.Parent is TupleExpressionSyntax))
                    access = access.Parent is ArgumentSyntax tupleItem ? tupleItem.Parent! : access.Parent!;
                var assignment = access.Parent as AssignmentExpressionSyntax;
                var writeOnly = assignment?.Left == access && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && property.RefKind == RefKind.None;
                var writes = assignment?.Left == access || access.Parent?.IsKind(SyntaxKind.PostIncrementExpression) == true
                    || access.Parent?.IsKind(SyntaxKind.PostDecrementExpression) == true
                    || access.Parent?.IsKind(SyntaxKind.PreIncrementExpression) == true
                    || access.Parent?.IsKind(SyntaxKind.PreDecrementExpression) == true;
                if (!writeOnly && property.GetMethod != null) yield return property.GetMethod;
                if (writes && property.SetMethod != null) yield return property.SetMethod;
            }

            if (symbol is IEventSymbol eventSymbol)
            {
                if (eventSymbol.AddMethod != null) yield return eventSymbol.AddMethod;
                if (eventSymbol.RemoveMethod != null) yield return eventSymbol.RemoveMethod;
            }

            for (var type = symbol.ContainingType; type != null; type = type.ContainingType)
                yield return type.OriginalDefinition;
        }
    }
}
