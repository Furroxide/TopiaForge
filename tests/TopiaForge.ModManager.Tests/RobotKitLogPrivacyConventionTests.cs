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
    /// RobotKit must not send exception payloads to logs, diagnostics or the failure results it hands to mods, which
    /// RobotKit's own disposal logging and other mods log in turn. Like the microphone convention, this reads
    /// Unity-dependent sources as syntax and follows local assignments within each member, including callbacks.
    /// Message properties, ToString calls and members named like an exception (InnerException, Task.Exception) are
    /// conservatively private regardless of their receiver's type. So are implicitly typed lambda parameters, the way
    /// RobotKit writes its failure callbacks (give one an explicit type when it is not an exception), and <c>out var</c>
    /// variables passed where a RobotKit method declares an exception out parameter. Sink recognition is name-based;
    /// fields and other calls between members are not a data-flow analysis.
    /// </summary>
    internal static class RobotKitLogPrivacyConventionTests
    {
        private static readonly HashSet<string> Sinks = new(StringComparer.Ordinal)
        {
            "Debug", "Info", "Warn", "Warning", "Error", "Report", "Log", "LogWarning", "LogError",
            // Unity's and BepInEx's own loggers, which RobotKit can reach directly as a native mod.
            "LogException", "LogFormat", "LogWarningFormat", "LogErrorFormat", "LogAssertion", "LogAssertionFormat",
            "LogInfo", "LogDebug", "LogMessage", "LogFatal",
            // OperationResult failures: their messages reach mods and RobotKit's own logs from other members.
            "Failure",
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
            var trees = files.Select(file => Parse(File.ReadAllText(file), Path.GetRelativePath(root, file).Replace('\\', '/'))).ToArray();
            var outExceptions = OutExceptionParameters(trees);
            foreach (var tree in trees)
            {
                var relative = tree.FilePath;
                var result = Scan(tree, outExceptions);
                sinks += result.Sinks;
                if (relative.EndsWith("/RoboApiClient.cs", StringComparison.Ordinal)) tokenSinks += result.Sinks;
                if (relative.EndsWith("/PlayerDialogueInputService.cs", StringComparison.Ordinal)) dialogueSinks += result.Sinks;
                if (relative.EndsWith("/RobotKitDiagnostics.cs", StringComparison.Ordinal)) diagnosticSinks += result.Sinks;
                violations.AddRange(result.Violations);
            }

            Assert(files.Length >= 25 && sinks > 50 && tokenSinks >= 5 && dialogueSinks >= 5 && diagnosticSinks >= 2,
                "the scan reads RobotKit logging, diagnostic and failure-result calls (" + files.Length + " files, " + sinks + " calls, "
                + tokenSinks + " token, " + dialogueSinks + " dialogue, " + diagnosticSinks + " fallback)");
            Assert(violations.Count == 0, "no RobotKit log, diagnostic or failure result carries exception text: " + string.Join(", ", violations));
            Console.WriteLine("RobotKitLogPrivacyConventionTests passed: " + files.Length + " files, " + sinks
                + " logging, diagnostic and failure-result calls (" + tokenSinks + " token, " + dialogueSinks + " dialogue, " + diagnosticSinks + " fallback).");
        }

        private static (int Sinks, List<string> Violations) Scan(string source, string relative)
        {
            var tree = Parse(source, relative);
            return Scan(tree, OutExceptionParameters(new[] { tree }));
        }

        private static (int Sinks, List<string> Violations) Scan(SyntaxTree tree, Dictionary<string, List<(int Index, string Name)>> outExceptions)
        {
            var violations = new List<string>();
            var sinks = 0;
            foreach (var member in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>()
                         .Where(candidate => candidate is not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax
                             and not DelegateDeclarationSyntax))
            {
                var tainted = Tainted(member, outExceptions);
                foreach (var call in member.DescendantNodes().Where(IsSink))
                {
                    sinks++;
                    if (Arguments(call).Any(argument => IsPrivate(argument.Expression, tainted)))
                        violations.Add(tree.FilePath + ":" + (call.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                }
            }

            return (sinks, violations);
        }

        private static SyntaxTree Parse(string source, string relative)
        {
            var tree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), relative);
            Assert(!tree.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error), relative + " reads as C#");
            return tree;
        }

        // The exception out parameters RobotKit's own methods declare, by method name, so that an `out var` passed there
        // is followed: ApplyFromRegistry(..., out var resolutionFailure) shows no type at the call.
        private static Dictionary<string, List<(int Index, string Name)>> OutExceptionParameters(IEnumerable<SyntaxTree> trees)
        {
            var found = new Dictionary<string, List<(int Index, string Name)>>(StringComparer.Ordinal);
            foreach (var method in trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()))
            {
                var parameters = method.ParameterList.Parameters;
                var extension = parameters.Count > 0 && parameters[0].Modifiers.Any(SyntaxKind.ThisKeyword);
                for (var index = 0; index < parameters.Count; index++)
                {
                    if (!parameters[index].Modifiers.Any(SyntaxKind.OutKeyword) || !IsExceptionType(parameters[index].Type)) continue;
                    if (!found.TryGetValue(method.Identifier.ValueText, out var outs))
                        found[method.Identifier.ValueText] = outs = new List<(int Index, string Name)>();
                    outs.Add((index, parameters[index].Identifier.ValueText));
                    // Called as an extension method, the receiver takes the first parameter's place.
                    if (extension) outs.Add((index - 1, parameters[index].Identifier.ValueText));
                }
            }

            return found;
        }

        private static string InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            SimpleNameSyntax name => name.Identifier.ValueText,
            _ => string.Empty,
        };

        private static HashSet<string> Tainted(SyntaxNode member, Dictionary<string, List<(int Index, string Name)>> outExceptions)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var caught in member.DescendantNodes().OfType<CatchDeclarationSyntax>())
                names.Add(caught.Identifier.ValueText);
            // An implicitly typed lambda parameter has no type to read: RobotKit's failure callbacks are written that way.
            foreach (var parameter in member.DescendantNodes().OfType<ParameterSyntax>())
                if (parameter.Type == null || IsExceptionType(parameter.Type)) names.Add(parameter.Identifier.ValueText);
            foreach (var declaration in member.DescendantNodes().OfType<VariableDeclarationSyntax>())
                if (IsExceptionType(declaration.Type))
                    foreach (var variable in declaration.Variables) names.Add(variable.Identifier.ValueText);
            // `out Exception failure`, `is IOException io` and `case Exception e` name their type; `out var failure` does
            // not, so it is followed when the RobotKit method it is passed to declares that out parameter as an exception.
            foreach (var declared in member.DescendantNodes().OfType<DeclarationExpressionSyntax>())
                if (IsExceptionType(declared.Type)) AddDesignated(declared.Designation, names);
            foreach (var invocation in member.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!outExceptions.TryGetValue(InvokedName(invocation), out var outs)) continue;
                var arguments = invocation.ArgumentList.Arguments;
                for (var index = 0; index < arguments.Count; index++)
                {
                    var argument = arguments[index];
                    var named = argument.NameColon?.Name.Identifier.ValueText;
                    if (!argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                        || !outs.Any(item => named == null ? item.Index == index : item.Name == named)) continue;
                    if (argument.Expression is DeclarationExpressionSyntax declared) AddDesignated(declared.Designation, names);
                    if (argument.Expression is IdentifierNameSyntax target) names.Add(target.Identifier.ValueText);
                }
            }
            foreach (var pattern in member.DescendantNodes().OfType<DeclarationPatternSyntax>())
                if (IsExceptionType(pattern.Type)) AddDesignated(pattern.Designation, names);
            foreach (var pattern in member.DescendantNodes().OfType<RecursivePatternSyntax>())
                if (pattern.Designation != null && IsExceptionType(pattern.Type)) AddDesignated(pattern.Designation, names);

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
                foreach (var loop in member.DescendantNodes().OfType<ForEachStatementSyntax>())
                    if (IsExceptionType(loop.Type) || IsPrivate(loop.Expression, names))
                        grew |= names.Add(loop.Identifier.ValueText);
            }
            while (grew);
            return names;
        }

        private static void AddDesignated(VariableDesignationSyntax designation, HashSet<string> names)
        {
            if (designation is SingleVariableDesignationSyntax single) names.Add(single.Identifier.ValueText);
            if (designation is ParenthesizedVariableDesignationSyntax tuple)
                foreach (var inner in tuple.Variables) AddDesignated(inner, names);
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
            if (node is MemberAccessExpressionSyntax access && IsPayloadMember(access.Name.Identifier.ValueText)) return true;
            if (node is MemberBindingExpressionSyntax binding && IsPayloadMember(binding.Name.Identifier.ValueText)) return true;
            if (node is ObjectCreationExpressionSyntax creation && IsExceptionType(creation.Type)) return true;
            return node.ChildNodes().Any(child => IsPrivate(child, tainted));
        }

        // Message and ToString format a payload. A member named like an exception (InnerException, Task.Exception,
        // GetBaseException, InnerExceptions) hands one over, and concatenation formats it with its ToString.
        private static bool IsPayloadMember(string name) => name is "Message" or "ToString"
            || name.EndsWith("Exception", StringComparison.Ordinal) || name.EndsWith("Exceptions", StringComparison.Ordinal);

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
                // Whole exceptions whose type the syntax does not show, as RobotKit's own callbacks and out parameters pass them.
                "Action<Exception> report = failure => logger.Warn(\"Failed: \" + failure);",
                "Compose(out var failure); logger.Warn(\"Failed: \" + failure);",
                "if (ex is IOException io) logger.Debug($\"Failed: {io}\");",
                "logger.Debug(\"Failed: \" + task.Exception);",
                "foreach (var inner in aggregate.InnerExceptions) logger.Debug(\"Inner: \" + inner);",
                "UnityEngine.Debug.LogException(ex);",
                "var result = OperationResult<bool>.Failure(ModErrorCode.External, ex.Message);",
            };
            // Compose declares an exception out parameter, as BrainQueryDirectiveComposer.ApplyFromRegistry does.
            var declarations = " static void Compose(out Exception failure) => failure = null; }";
            foreach (var body in rejected)
            {
                var result = Scan("class Sample { void Run(Exception ex) { " + body + " }" + declarations, "sample.cs");
                Assert(result.Sinks > 0 && result.Violations.Count > 0, "scanner rejects private exception payloads: " + body);
                Assert(result.Violations.All(item => item == "sample.cs:1"), "violations name the relative file and line");
            }

            var safe = Scan("class Sample { void Run(Exception ex) { var category = RobotKitLog.ExceptionType(ex); "
                + "logger?.Debug(\"Failed: \" + category); Console.Error.WriteLine(\"Unavailable\"); "
                + "Action<string> say = (string text) => logger.Info(text); values.TryGetValue(\"name\", out var name); "
                + "logger.Info(\"Name: \" + name); OperationResult<bool>.Failure(ModErrorCode.External, \"Failed: \" + category); }"
                + declarations, "sample.cs");
            Assert(safe.Sinks == 5 && safe.Violations.Count == 0, "fixed text, exception types and other values remain useful");
            Assert(Scan("class Sample { }", "sample.cs").Sinks == 0, "empty input is counted as empty");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
        }
    }
}
