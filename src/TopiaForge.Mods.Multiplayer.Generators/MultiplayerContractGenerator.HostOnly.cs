using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace TopiaForge.Mods.Multiplayer.Generators
{
    public sealed partial class MultiplayerContractGenerator
    {
        private static readonly DiagnosticDescriptor HostOnlyPresentation = new DiagnosticDescriptor(
            "TFMP015",
            "Server-only handler uses local presentation",
            "'{0}' runs only on the session authority, but it uses '{1}', which acts on the host's own process rather than on the player who sent it; publish a presentation event or address the player through IPlayer.Ui",
            "TopiaForge.Multiplayer",
            DiagnosticSeverity.Warning,
            true,
            helpLinkUri: DocsRoot + "TFMP015");

        /// <summary>
        /// Warns when a handler that never runs on the sender (Prediction = None) drives local UI, input, player,
        /// audio, or Unity state, which in a session would affect the host instead of the player who acted.
        /// </summary>
        private static void ReportHostOnlyPresentation(
            SourceProductionContext context,
            Compilation compilation,
            IMethodSymbol method)
        {
            method = method.PartialImplementationPart ?? method;
            var reportedTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var syntaxReference in method.DeclaringSyntaxReferences)
            {
                var syntax = syntaxReference.GetSyntax();
                var model = compilation.GetSemanticModel(syntax.SyntaxTree);
                var root = GetPredictedMethodOperation(model, syntax);
                if (root == null) continue;

                foreach (var operation in DescendantsAndSelf(root))
                {
                    var symbol = ReferencedSymbol(operation);
                    var containingType = symbol?.ContainingType?.ToDisplayString();
                    if (containingType == null || !IsHostOnlyPresentationType(containingType)) continue;
                    if (!reportedTypes.Add(containingType)) continue;
                    context.ReportDiagnostic(Diagnostic.Create(
                        HostOnlyPresentation,
                        operation.Syntax.GetLocation(),
                        method.Name,
                        symbol!.ToDisplayString()));
                }
            }
        }

        private static bool IsHostOnlyPresentationType(string type) =>
            type == "TopiaForge.Mods.IUiService" ||
            type == "TopiaForge.Mods.IUiSurface" ||
            type == "TopiaForge.Mods.IUiModal" ||
            type == "TopiaForge.Mods.IInputService" ||
            type == "TopiaForge.Mods.IInputAction" ||
            type == "TopiaForge.Mods.ILocalPlayerService" ||
            type == "TopiaForge.Mods.IAudioService" ||
            type.StartsWith("UnityEngine.", StringComparison.Ordinal);
    }
}
