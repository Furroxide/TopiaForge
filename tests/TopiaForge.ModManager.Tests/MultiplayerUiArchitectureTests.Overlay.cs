using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The connecting overlay's bindings, read from its source as C# syntax: it is a kit view, which the offline tests
    /// cannot run, so these checks hold it to what the presenters' tests prove of the screen it shows, and no comment or
    /// change of layout can fool them.
    /// </summary>
    internal static partial class MultiplayerUiArchitectureTests
    {
        /// <summary>
        /// Seen in the game: a RETRY that failed at once kept the earlier failure's heading and advice under the new failure's
        /// status line, because the failure body was built only when the overlay's layout changed, and a failure that
        /// replaces another in one frame keeps the failure layout. The heading, the words and the host's words are bound
        /// on every frame instead, through the kit's dirty-checked setters, outside any condition.
        /// </summary>
        private static void TheOverlayShowsTheCurrentFailure(string root)
        {
            var bind = OverlayMethod(root, "Bind").Body!.Statements.Select(statement => statement.ToString()).ToArray();
            foreach (var bound in new[]
            {
                "failureTitle?.SetText(screen.FailureTitle);",
                "failureMessage?.SetText(screen.FailureMessage);",
                "hostSays?.SetText(screen.HostSays);"
            })
            {
                That(bind.Contains(bound, StringComparer.Ordinal), "the connecting overlay binds the current failure on every frame: " + bound);
            }
        }

        /// <summary>The connecting overlay's one method named <paramref name="name"/>, parsed from its source.</summary>
        private static MethodDeclarationSyntax OverlayMethod(string root, string name)
        {
            var path = Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "Multiplayer", "ConnectingOverlay.cs");
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
            var error = tree.GetDiagnostics().FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            That(error == null, "the connecting overlay's source reads as C# (" + error + ")");
            var methods = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(type => type.Identifier.ValueText == "ConnectingOverlay")
                .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>())
                .Where(method => method.Identifier.ValueText == name)
                .ToArray();
            That(methods.Length == 1, "the connecting overlay declares " + name + " once (" + methods.Length + ")");
            return methods[0];
        }
    }
}
