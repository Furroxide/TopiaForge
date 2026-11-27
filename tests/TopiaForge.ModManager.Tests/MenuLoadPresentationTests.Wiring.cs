using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class MenuLoadPresentationTests
    {
        // Supplemental UI wiring checks; native-operation and reply behavior run in the other test files.
        private static void CallerWiring()
        {
            string Read(string path) => File.ReadAllText(Path.Combine(Program.FindRepoRoot(), path));
            var tab = CSharpSyntaxTree.ParseText(Read("src/TopiaForge.ModManager/Overlay/GamemodesTab.cs")).GetRoot();
            var type = tab.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(item => item.Identifier.ValueText == "GamemodesTab");
            Check(type.BaseList!.Types.Any(item => item.Type.ToString() == "IManagerLiveTab"), "menu notice uses the existing live tab lifecycle");
            var build = type.Members.OfType<MethodDeclarationSyntax>().Single(item => item.Identifier.ValueText == "Build");
            var button = build.DescendantNodes().OfType<InvocationExpressionSyntax>().Single(call =>
                call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Button"
                && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal && literal.Token.ValueText == "MAIN MENU");
            var action = ((ParenthesizedLambdaExpressionSyntax)button.ArgumentList.Arguments[1].Expression).Body.ToString();
            var opening = action.IndexOf("menuNotice?.SetText(MenuLoadPresentation.OpeningText)", StringComparison.Ordinal);
            var native = action.IndexOf("context.Plugin.ReturnToMainMenu()", StringComparison.Ordinal);
            var waiting = action.IndexOf("await pending", StringComparison.Ordinal);
            Check(opening >= 0 && native > opening && waiting > native
                && action.Contains("MenuNoticeBinding.CanFinish(currentBuild, build, context.Plugin.MenuNoticeOwner, owner, context.Plugin.MenuNotice, presentation, attempt)", StringComparison.Ordinal)
                && action.Contains("TopiaForgeToasts.ShowPlain", StringComparison.Ordinal),
                "actual menu caller sets literal pending text before native call and guards delayed feedback");
            var notice = build.DescendantNodes().OfType<AssignmentExpressionSyntax>().Single(assignment => assignment.Left.ToString() == "menuNotice");
            Check(notice.Right.ToString().EndsWith(".PlainText()", StringComparison.Ordinal), "persistent menu notice renders fixed text literally");
            var tick = type.Members.OfType<MethodDeclarationSyntax>().Single(item => item.Identifier.ValueText == "Tick").ToString();
            var hidden = type.Members.OfType<MethodDeclarationSyntax>().Single(item => item.Identifier.ValueText == "OnHidden").ToString();
            Check(tick.Contains("menuBinding.TryRead", StringComparison.Ordinal) && tick.Contains("menuButton?.SetEnabled(!pending)", StringComparison.Ordinal)
                && new[] { "plugin = null", "menuNotice = null", "menuButton = null", "currentBuild = null", "menuBinding.Reset()" }
                    .All(part => hidden.Contains(part, StringComparison.Ordinal)), "live tab dirties only changed state and drops hidden UI references");

            var menu = CSharpSyntaxTree.ParseText(Read("src/TopiaForge.ModManager/GamemodeSessionOrchestrator.MainMenu.cs")).GetRoot();
            var replace = menu.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(item => item.Identifier.ValueText == "ReplaceWorldAsync");
            Check(replace.ParameterList.Parameters.Last().Default?.Value.ToString() == "false",
                "non-menu world replacement keeps notice opt-in disabled");
            var plugin = CSharpSyntaxTree.ParseText(Read("src/TopiaForge.ModManager/TopiaForgeModManagerPlugin.Gamemodes.cs")).GetRoot();
            var source = plugin.DescendantNodes().OfType<PropertyDeclarationSyntax>().Single(item => item.Identifier.ValueText == "MenuNotice");
            Check(source.ExpressionBody!.Expression.ToString() == "ready ? runtime?.Sessions.MenuPresentation : null",
                "disposed plugin reads no runtime notice into its old view");
            var owner = plugin.DescendantNodes().OfType<PropertyDeclarationSyntax>().Single(item => item.Identifier.ValueText == "MenuNoticeOwner");
            Check(owner.ExpressionBody!.Expression.ToString() == "(object?)runtime ?? this",
                "null-notice replies still use the actual runtime or initializing plugin identity");
        }
    }
}
