using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The plugin's partial class files, read as C# syntax. The plugin is a MonoBehaviour this harness cannot run, so its
    /// wiring is checked in its source. Reading syntax rather than text means that layout, spacing, braces, comments and
    /// a <c>this.</c> or <c>UnityEngine.</c> qualifier cannot change what a check finds.
    /// </summary>
    internal sealed class PluginSourceSyntax
    {
        private const string Plugin = "TopiaForgeModManagerPlugin";
        private readonly IReadOnlyList<MethodDeclarationSyntax> methods;

        private PluginSourceSyntax(IReadOnlyList<MethodDeclarationSyntax> methods) => this.methods = methods;

        internal static PluginSourceSyntax Read()
        {
            var folder = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.ModManager");
            var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
            var methods = new List<MethodDeclarationSyntax>();
            foreach (var file in Directory.GetFiles(folder, Plugin + "*.cs").OrderBy(path => path, StringComparer.Ordinal))
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, Path.GetFileName(file));
                var error = tree.GetDiagnostics().FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                if (error != null) throw new InvalidOperationException("The plugin's source could not be read as C#: " + error);
                methods.AddRange(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Where(type => type.Identifier.ValueText == Plugin)
                    .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>()));
            }

            return new PluginSourceSyntax(methods);
        }

        /// <summary>The top-level statements of the plugin's one parameterless method of this name.</summary>
        internal SyntaxList<StatementSyntax> Statements(string method)
        {
            var declared = methods.Where(candidate => candidate.Identifier.ValueText == method && candidate.Arity == 0
                && candidate.ParameterList.Parameters.Count == 0).ToArray();
            if (declared.Length != 1)
                throw new InvalidOperationException("The plugin must declare " + method + "() once, not " + declared.Length + " times.");
            return declared[0].Body?.Statements
                ?? throw new InvalidOperationException("The plugin's " + method + "() must have a block body.");
        }

        /// <summary>Whether the code names the plugin's own member, as <c>member</c> or <c>this.member</c>.</summary>
        internal static bool Mentions(SyntaxNode code, string member) =>
            code.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(name => name.Identifier.ValueText == member
                && name.Parent switch
                {
                    MemberAccessExpressionSyntax access when access.Name == name => access.Expression is ThisExpressionSyntax,
                    MemberBindingExpressionSyntax or NameColonSyntax => false,
                    _ => true,
                });

        /// <summary>Whether the code calls <c>method()</c> on the plugin's own field, as <c>field.</c> or <c>field?.</c>.</summary>
        internal static bool CallsOn(SyntaxNode code, string field, string method) =>
            code.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(call => call.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText == method && IsOwn(access.Expression, field),
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText == method
                    && call.Parent is ConditionalAccessExpressionSyntax conditional && conditional.WhenNotNull == call
                    && IsOwn(conditional.Expression, field),
                _ => false,
            });

        /// <summary>Whether the code calls the plugin's own method, as <c>method()</c> or <c>this.method()</c>.</summary>
        internal static bool Calls(SyntaxNode code, string method) =>
            code.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(call => IsOwn(call.Expression, method));

        /// <summary>Whether the code asks Unity to quit: <c>Application.Quit</c>.</summary>
        internal static bool CallsApplicationQuit(SyntaxNode code) =>
            code.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(call =>
                call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "Quit"
                && LastName(access.Expression) == "Application");

        /// <summary>Whether the statement is <c>Application.wantsToQuit -= handler;</c>, the plugin's own handler.</summary>
        internal static bool Unsubscribes(StatementSyntax statement, string handler) =>
            statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
            && assignment.IsKind(SyntaxKind.SubtractAssignmentExpression)
            && assignment.Left is MemberAccessExpressionSyntax target && target.Name.Identifier.ValueText == "wantsToQuit"
            && LastName(target.Expression) == "Application"
            && IsOwn(assignment.Right, handler);

        /// <summary>Whether the code sets the plugin's own field to <c>false</c>.</summary>
        internal static bool AssignsFalse(SyntaxNode code, string field) =>
            code.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>().Any(assignment =>
                assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && IsOwn(assignment.Left, field)
                && assignment.Right.IsKind(SyntaxKind.FalseLiteralExpression));

        /// <summary>Whether the code can leave the method it is in: a return or a throw, outside lambdas and local functions.</summary>
        internal static bool CanLeave(SyntaxNode code) =>
            code.DescendantNodesAndSelf(node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                .Any(node => node is ReturnStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax);

        private static bool IsOwn(ExpressionSyntax expression, string member)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
            return expression switch
            {
                IdentifierNameSyntax name => name.Identifier.ValueText == member,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access => access.Name.Identifier.ValueText == member,
                _ => false,
            };
        }

        private static string? LastName(ExpressionSyntax expression) => expression switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => null,
        };
    }
}
