using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The loader-owned kit is not one of the eight Unity-free SDK assemblies. Pin the public composition handles in
    /// source so this check needs no Unity runtime; the gallery build and chat contract compile their consumers too.
    /// </summary>
    internal static class SdkUnityUiCompositionBaselineTests
    {
        private const string FileName = "topiaforge.mods.unityui.composition.api.txt";

        internal static void Run()
        {
            var expected = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "baselines", FileName)).Replace("\r\n", "\n");
            if (expected != Create()) throw new InvalidOperationException("The kit composition API changed; review and update " + FileName + ".");
            Console.WriteLine("Kit composition API baseline passed (2 public handles).");
        }

        internal static void Update(string root) => File.WriteAllText(Path.Combine(root, "baselines", FileName), Create());

        private static string Create()
        {
            var result = "# Kit composition API (source declarations, not the complete UnityUi assembly)\n"
                + "# Regenerate with: --update-sdk-api-baselines .\n";
            foreach (var name in new[] { "TopiaForgeWidget", "TopiaForgeInputField" })
            {
                var source = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi", "Widgets", name + ".cs"));
                var type = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Single(item => item.Identifier.ValueText == name);
                result += "\n" + string.Join(" ", type.Modifiers.Select(token => token.Text)) + " class " + name
                    + (type.BaseList == null ? "" : " " + type.BaseList.WithoutTrivia().NormalizeWhitespace().ToFullString()) + "\n";
                foreach (var member in type.Members.Where(item => item.Modifiers.Any(SyntaxKind.PublicKeyword)))
                {
                    MemberDeclarationSyntax signature = member switch
                    {
                        MethodDeclarationSyntax method => method.WithBody(null).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                        PropertyDeclarationSyntax property => property.WithExpressionBody(null).WithInitializer(null).WithSemicolonToken(default)
                            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(property.AccessorList?.Accessors
                                .Select(accessor => accessor.WithBody(null).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))
                                ?? new[] { SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)) }))),
                        _ => throw new InvalidOperationException("Extend the composition baseline for " + member.Kind()),
                    };
                    result += "  " + signature.WithoutTrivia().NormalizeWhitespace().ToFullString().Replace("\r\n", " ").Replace("\n", " ") + "\n";
                }
            }
            return result;
        }
    }
}
