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
    /// RobotKit must not send exception payloads to logs or diagnostics. Like the microphone convention, this reads
    /// Unity-dependent sources as syntax and follows local assignments within each member, including callbacks.
    /// Message properties and ToString calls are conservatively private regardless of their receiver's type.
    /// Sink recognition is name-based; fields and calls between members are not a data-flow analysis.
    /// </summary>
    internal static class RobotKitLogPrivacyConventionTests
    {
        private static readonly HashSet<string> Sinks = new(StringComparer.Ordinal)
        {
            "Debug", "Info", "Warn", "Warning", "Error", "Report", "Log", "LogWarning", "LogError",
        };

        public static void Run()
        {
            CheckScanner();
            var root = Program.FindRepoRoot();
            var violations = new List<string>();
            var sinks = 0;
            var tokenSinks = 0;
            var dialogueSinks = 0;
            var diagnosticSinks = 0;
            var files = Directory.EnumerateFiles(Path.Combine(root, "mods", "TopiaForge.RobotKit"), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var result = Scan(File.ReadAllText(file), relative);
                sinks += result.Sinks;
                if (relative.EndsWith("/RoboApiClient.cs", StringComparison.Ordinal)) tokenSinks += result.Sinks;
                if (relative.EndsWith("/PlayerDialogueInputService.cs", StringComparison.Ordinal)) dialogueSinks += result.Sinks;
                if (relative.EndsWith("/RobotKitDiagnostics.cs", StringComparison.Ordinal)) diagnosticSinks += result.Sinks;
                violations.AddRange(result.Violations);
            }

            Assert(files.Length >= 25 && sinks > 50 && tokenSinks >= 5 && dialogueSinks >= 5 && diagnosticSinks >= 2,
                "the scan reads RobotKit logging and diagnostic calls (" + files.Length + " files, " + sinks + " calls, "
                + tokenSinks + " token, " + dialogueSinks + " dialogue, " + diagnosticSinks + " fallback)");
            Assert(violations.Count == 0, "no RobotKit log or diagnostic carries exception text: " + string.Join(", ", violations));
            Console.WriteLine("RobotKitLogPrivacyConventionTests passed: " + files.Length + " files, " + sinks
                + " logging and diagnostic calls (" + tokenSinks + " token, " + dialogueSinks + " dialogue, " + diagnosticSinks + " fallback).");
        }

        private static (int Sinks, List<string> Violations) Scan(string source, string relative)
        {
            var tree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), relative);
            Assert(!tree.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error), relative + " reads as C#");
            var violations = new List<string>();
            var sinks = 0;
            foreach (var member in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>()
                         .Where(candidate => candidate is not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax
                             and not DelegateDeclarationSyntax))
            {
                var tainted = Tainted(member);
                foreach (var call in member.DescendantNodes().Where(IsSink))
                {
                    sinks++;
                    if (Arguments(call).Any(argument => IsPrivate(argument.Expression, tainted)))
                        violations.Add(relative + ":" + (call.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                }
            }

            return (sinks, violations);
        }

        private static HashSet<string> Tainted(SyntaxNode member)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var caught in member.DescendantNodes().OfType<CatchDeclarationSyntax>())
                names.Add(caught.Identifier.ValueText);
            foreach (var parameter in member.DescendantNodes().OfType<ParameterSyntax>())
                if (IsExceptionType(parameter.Type)) names.Add(parameter.Identifier.ValueText);
            foreach (var declaration in member.DescendantNodes().OfType<VariableDeclarationSyntax>())
                if (IsExceptionType(declaration.Type))
                    foreach (var variable in declaration.Variables) names.Add(variable.Identifier.ValueText);

            bool grew;
            do
            {
                grew = false;
                foreach (var variable in member.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                    if (variable.Initializer != null && IsPrivate(variable.Initializer.Value, names))
                        grew |= names.Add(variable.Identifier.ValueText);
                foreach (var assignment in member.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    if (assignment.Left is IdentifierNameSyntax target && IsPrivate(assignment.Right, names))
                        grew |= names.Add(target.Identifier.ValueText);
            }
            while (grew);
            return names;
        }

        private static bool IsPrivate(SyntaxNode node, HashSet<string> tainted)
        {
            // Only this payload-free formatter is trusted. Its behaviour is exercised with hostile exceptions.
            if (node is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax { Identifier.ValueText: "RobotKitLog" },
                        Name.Identifier.ValueText: "ExceptionType",
                    },
                }) return false;
            if (node is IdentifierNameSyntax identifier && tainted.Contains(identifier.Identifier.ValueText)) return true;
            if (node is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText is "Message" or "ToString") return true;
            if (node is MemberBindingExpressionSyntax binding && binding.Name.Identifier.ValueText is "Message" or "ToString") return true;
            if (node is ObjectCreationExpressionSyntax creation && IsExceptionType(creation.Type)) return true;
            return node.ChildNodes().Any(child => IsPrivate(child, tainted));
        }

        private static bool IsExceptionType(TypeSyntax? type) => type switch
        {
            NullableTypeSyntax nullable => IsExceptionType(nullable.ElementType),
            QualifiedNameSyntax qualified => IsExceptionType(qualified.Right),
            SimpleNameSyntax simple => simple.Identifier.ValueText.EndsWith("Exception", StringComparison.Ordinal),
            _ => false,
        };

        private static bool IsSink(SyntaxNode node) => node switch
        {
            InvocationExpressionSyntax call => call.Expression switch
            {
                MemberAccessExpressionSyntax access => Sinks.Contains(access.Name.Identifier.ValueText)
                    || (access.Name.Identifier.ValueText is "WriteLine" or "Write"
                        && access.Expression.ToString() is "Console" or "Console.Error" or "Console.Out"
                            or "System.Console" or "System.Console.Error" or "System.Console.Out"),
                MemberBindingExpressionSyntax binding => Sinks.Contains(binding.Name.Identifier.ValueText),
                IdentifierNameSyntax name => Sinks.Contains(name.Identifier.ValueText),
                _ => false,
            },
            ObjectCreationExpressionSyntax creation => creation.Type.ToString().EndsWith("Diagnostic", StringComparison.Ordinal),
            _ => false,
        };

        private static IEnumerable<ArgumentSyntax> Arguments(SyntaxNode call) => call switch
        {
            InvocationExpressionSyntax invocation => invocation.ArgumentList.Arguments,
            ObjectCreationExpressionSyntax creation when creation.ArgumentList != null => creation.ArgumentList.Arguments,
            _ => Array.Empty<ArgumentSyntax>(),
        };

        private static void CheckScanner()
        {
            var rejected = new[]
            {
                "logger.Debug(ex.Message);",
                "logger?.Warn($\"Failure: {ex}\");",
                "logger.Error(ex, \"Failed\");",
                "logger.Debug(ex.ToString());",
                "var detail = ex.Message; var renamed = detail; logger.Info(renamed);",
                "string detail = \"\"; detail += ex.Message; logger?.Debug(detail);",
                "var alias = ex; logger.Debug(alias.ToString());",
                "logger.Debug(ex?.Message);",
                "Action<Exception> report = failure => logger.Debug(failure.Message);",
                "var detail = ex.Message; Console.Error.WriteLine(detail);",
                "var detail = ex.Message; Report(new ModDiagnostic(detail));",
                "string detail = ex.Message; detail = \"prefix\" + detail; logger.Debug(detail);",
            };
            foreach (var body in rejected)
            {
                var result = Scan("class Sample { void Run(Exception ex) { " + body + " } }", "sample.cs");
                Assert(result.Sinks > 0 && result.Violations.Count > 0, "scanner rejects private exception payloads");
                Assert(result.Violations.All(item => item == "sample.cs:1"), "violations name the relative file and line");
            }

            var safe = Scan("class Sample { void Run(Exception ex) { var category = RobotKitLog.ExceptionType(ex); "
                + "logger?.Debug(\"Failed: \" + category); Console.Error.WriteLine(\"Unavailable\"); } }", "sample.cs");
            Assert(safe.Sinks == 2 && safe.Violations.Count == 0, "fixed text and exception types remain useful");
            Assert(Scan("class Sample { }", "sample.cs").Sinks == 0, "empty input is counted as empty");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
        }
    }
}
