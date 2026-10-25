using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
            "'{0}' needs TopiaForge {1} or newer, but topiaforge.mod.json admits {2}. {3}.",
            Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
            helpLinkUri: DocsRoot + "TF1010");

        private static void RegisterVersionGates(CompilationStartAnalysisContext context)
        {
            var manifest = ManifestSnapshot.Read(context.Options.AdditionalFiles, context.CancellationToken);
            // A library without its own manifest, such as a service's contracts project, has no floor to raise.
            if (!manifest.Exists) return;
            context.RegisterSyntaxNodeAction(node => AnalyzeVersionGate(node, manifest),
                SyntaxKind.IdentifierName, SyntaxKind.GenericName,
                SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression,
                SyntaxKind.Attribute, SyntaxKind.BaseConstructorInitializer, SyntaxKind.ThisConstructorInitializer,
                SyntaxKind.ElementAccessExpression, SyntaxKind.ElementBindingExpression);
            context.RegisterSymbolAction(symbol => AnalyzeImplementedVersionGate(symbol, manifest),
                SymbolKind.Method, SymbolKind.Property, SymbolKind.Event);
        }

        private static void AnalyzeVersionGate(SyntaxNodeAnalysisContext context, ManifestSnapshot manifest)
        {
            var node = context.Node;
            // Creation/attribute nodes check both the constructor and its declaring type once. Documentation
            // comments are not code: a cref runs nothing.
            if ((node is SimpleNameSyntax && IsConstructedTypeName(node)) || node.IsPartOfStructuredTrivia()) return;
            var info = context.SemanticModel.GetSymbolInfo(node, context.CancellationToken);
            var symbol = info.Symbol;
            // nameof can bind an overload group without selecting an overload.
            var symbols = symbol == null ? info.CandidateSymbols : ImmutableArray.Create(symbol);
            string? required = null;
            ISymbol? requiring = null;
            foreach (var candidate in symbols)
            {
                foreach (var member in RequirementSources(candidate, node))
                    Consider(member, candidate, ref required, ref requiring);
            }

            if (required != null) Report(context.ReportDiagnostic, node.GetLocation(), manifest, required, requiring!);
        }

        // An override or an explicit interface implementation of a newer member only runs on a loader that has it.
        private static void AnalyzeImplementedVersionGate(SymbolAnalysisContext context, ManifestSnapshot manifest)
        {
            var symbol = context.Symbol;
            // Accessors are checked with their property or event.
            if (symbol is IMethodSymbol { AssociatedSymbol: not null }) return;
            var location = symbol.Locations.FirstOrDefault(item => item.IsInSource);
            if (location == null) return;
            string? required = null;
            ISymbol? requiring = null;
            foreach (var implemented in ImplementedMembers(symbol))
            {
                foreach (var member in ImplementedRequirementSources(symbol, implemented))
                    Consider(member, implemented, ref required, ref requiring);
            }

            if (required != null) Report(context.ReportDiagnostic, location, manifest, required, requiring!);
        }

        private static void Consider(ISymbol member, ISymbol candidate, ref string? required, ref ISymbol? requiring)
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

        private static void Report(
            Action<Diagnostic> report, Location location, ManifestSnapshot manifest, string required, ISymbol requiring)
        {
            if (VersionUtil.TryGetMinimum(manifest.LoaderRange, out var minimum)
                && minimum != null && VersionUtil.IsAtLeast(minimum, required)) return;
            report(Diagnostic.Create(SdkVersionRequired, location,
                ImmutableDictionary<string, string?>.Empty.Add(RequiredVersionProperty, required),
                requiring.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                required, manifest.LoaderRange, Advice(manifest.LoaderRange, required)));
        }

        // When the range's ceiling is below the API too, as in every mod made for 0.1, raising the floor alone
        // leaves an empty range, so name both fields and bounds that work.
        private static string Advice(string range, string required)
        {
            if (VersionUtil.AllowsRange(required, range)) return "Raise supportedLoaderVersionRange";
            VersionUtil.TryParse(required, out var version);
            return "Raise supportedLoaderVersionRange and supportedSdkVersionRange to admit " + required
                + ", for example \">=" + required + " <" + version.Major + "." + (version.Minor + 1) + ".0\"";
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
                var (reads, writes) = PropertyAccessors(node, property);
                if (reads && property.GetMethod != null) yield return property.GetMethod;
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

        // Which accessors a property reference calls: a plain assignment sets (a ref-returning property is still
        // read), a compound assignment or an increment gets and sets, and anything else gets.
        private static (bool Reads, bool Writes) PropertyAccessors(SyntaxNode node, IPropertySymbol property)
        {
            // An attribute's named argument sets the property when the attribute is read.
            if (node.Parent is NameEqualsSyntax { Parent: AttributeArgumentSyntax }) return (false, true);
            // The property is the accessed name, never the expression a member is accessed on.
            var access = node.Parent is MemberAccessExpressionSyntax member && member.Name == node ? member
                : node.Parent is MemberBindingExpressionSyntax binding && binding.Name == node ? binding
                : node;
            while (access.Parent is ParenthesizedExpressionSyntax
                || (access.Parent is ArgumentSyntax argument && argument.Parent is TupleExpressionSyntax))
                access = access.Parent is ArgumentSyntax tupleItem ? tupleItem.Parent! : access.Parent!;
            if (access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access)
            {
                // `Property = { ... }` in an object initializer fills the object the getter returns.
                if (assignment.Right is InitializerExpressionSyntax) return (true, false);
                var setsOnly = assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && property.RefKind == RefKind.None;
                return (!setsOnly, true);
            }

            var parent = access.Parent;
            var increments = parent != null && (parent.IsKind(SyntaxKind.PostIncrementExpression)
                || parent.IsKind(SyntaxKind.PostDecrementExpression) || parent.IsKind(SyntaxKind.PreIncrementExpression)
                || parent.IsKind(SyntaxKind.PreDecrementExpression));
            return (true, increments);
        }

        private static IEnumerable<ISymbol> ImplementedMembers(ISymbol symbol)
        {
            switch (symbol)
            {
                case IMethodSymbol method:
                    for (var overridden = method.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
                        yield return overridden;
                    foreach (var implemented in method.ExplicitInterfaceImplementations) yield return implemented;
                    break;
                case IPropertySymbol property:
                    for (var overridden = property.OverriddenProperty; overridden != null; overridden = overridden.OverriddenProperty)
                        yield return overridden;
                    foreach (var implemented in property.ExplicitInterfaceImplementations) yield return implemented;
                    break;
                case IEventSymbol eventSymbol:
                    for (var overridden = eventSymbol.OverriddenEvent; overridden != null; overridden = overridden.OverriddenEvent)
                        yield return overridden;
                    foreach (var implemented in eventSymbol.ExplicitInterfaceImplementations) yield return implemented;
                    break;
            }
        }

        // The implemented member and the accessors this declaration provides. Its containing type is not checked
        // here: the base list that names a newer type is reported where it is written.
        private static IEnumerable<ISymbol> ImplementedRequirementSources(ISymbol symbol, ISymbol implemented)
        {
            yield return implemented.OriginalDefinition;
            if (symbol is IPropertySymbol property && implemented is IPropertySymbol baseProperty)
            {
                if (property.GetMethod != null && baseProperty.GetMethod != null) yield return baseProperty.GetMethod.OriginalDefinition;
                if (property.SetMethod != null && baseProperty.SetMethod != null) yield return baseProperty.SetMethod.OriginalDefinition;
            }

            if (implemented is IEventSymbol baseEvent)
            {
                if (baseEvent.AddMethod != null) yield return baseEvent.AddMethod.OriginalDefinition;
                if (baseEvent.RemoveMethod != null) yield return baseEvent.RemoveMethod.OriginalDefinition;
            }
        }
    }
}
