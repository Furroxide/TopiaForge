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
    /// No log line names a microphone. A device's name is free text the player's system gives it, such as "Sam's headset",
    /// and logs go into diagnostic bundles, which redact the home folder and secrets but not free text. The microphone code
    /// references UnityEngine, so it cannot be compiled here; its sources are read as C# syntax instead. In RobotKit and the
    /// voice code, no argument of a logging or diagnostic call may mention Unity's <c>Microphone</c>, a device name, or a
    /// local that either reaches, through assignments within the same member.
    /// </summary>
    internal static class MicrophoneLogPrivacyConventionTests
    {
        private static readonly string[] Folders =
        {
            Path.Combine("mods", "TopiaForge.RobotKit"),
            Path.Combine("mods", "TopiaForge.Multiplayer", "Voice"),
            Path.Combine("src", "TopiaForge.ModManager", "Multiplayer"),
        };

        private static readonly HashSet<string> Sinks = new(StringComparer.Ordinal)
        {
            "Debug", "Info", "Warn", "Warning", "Error", "Report", "Log", "LogWarning", "LogError",
        };

        private static readonly HashSet<string> DeviceNames = new(StringComparer.Ordinal)
        {
            "device", "devices", "deviceName", "deviceNames", "PickDevice",
        };

        public static void Run()
        {
            var root = Program.FindRepoRoot();
            var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
            var violations = new List<string>();
            var sinks = 0;
            var dialogueSinks = 0;
            foreach (var folder in Folders)
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories)
                             .Where(path => !IsBuildOutput(path)).OrderBy(path => path, StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, relative);
                    var error = tree.GetDiagnostics().FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                    if (error != null) throw new InvalidOperationException("Assertion failed: " + relative + " reads as C# (" + error + ")");
                    foreach (var member in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>()
                                 .Where(candidate => candidate is not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax
                                     and not DelegateDeclarationSyntax))
                    {
                        var tainted = Tainted(member);
                        foreach (var call in member.DescendantNodes().Where(IsSink))
                        {
                            sinks++;
                            if (relative.EndsWith("/PlayerDialogueInputService.cs", StringComparison.Ordinal)) dialogueSinks++;
                            if (call.DescendantNodes().Any(node => NamesMicrophone(node, tainted)))
                            {
                                var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                violations.Add(relative + ":" + line);
                            }
                        }
                    }
                }
            }

            // The scan sees the dialogue capture's own log lines, so it cannot pass by reading nothing.
            Assert(sinks > 50 && dialogueSinks >= 5, "the scan reads the logging calls of RobotKit and the voice code ("
                + sinks + " in all, " + dialogueSinks + " in the dialogue capture)");
            Assert(violations.Count == 0, "no log line names a microphone: " + string.Join(", ", violations));
            Console.WriteLine("MicrophoneLogPrivacyConventionTests passed: " + sinks + " logging and diagnostic calls read, "
                + dialogueSinks + " of them in the dialogue capture.");
        }

        /// <summary>The locals of <paramref name="member"/> that hold a device name: assigned one, directly or through each other.</summary>
        private static HashSet<string> Tainted(SyntaxNode member)
        {
            var tainted = new HashSet<string>(DeviceNames, StringComparer.Ordinal);
            bool grew;
            do
            {
                grew = false;
                foreach (var declarator in member.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                {
                    if (declarator.Initializer != null && Mentions(declarator.Initializer.Value, tainted))
                        grew |= tainted.Add(declarator.Identifier.ValueText);
                }

                foreach (var assignment in member.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    if (assignment.Left is IdentifierNameSyntax target && Mentions(assignment.Right, tainted))
                        grew |= tainted.Add(target.Identifier.ValueText);
                }

                foreach (var loop in member.DescendantNodes().OfType<ForEachStatementSyntax>())
                {
                    if (Mentions(loop.Expression, tainted)) grew |= tainted.Add(loop.Identifier.ValueText);
                }
            }
            while (grew);

            return tainted;
        }

        private static bool Mentions(SyntaxNode code, HashSet<string> tainted) =>
            code.DescendantNodesAndSelf().Any(node => NamesMicrophone(node, tainted));

        private static bool NamesMicrophone(SyntaxNode node, HashSet<string> tainted) => node switch
        {
            IdentifierNameSyntax name => tainted.Contains(name.Identifier.ValueText),
            MemberAccessExpressionSyntax access => access.Expression switch
            {
                IdentifierNameSyntax type => type.Identifier.ValueText == "Microphone",
                MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.ValueText == "Microphone"
                    && qualified.Expression is IdentifierNameSyntax { Identifier.ValueText: "UnityEngine" },
                _ => false,
            },
            _ => false,
        };

        /// <summary>A call that writes a log line or reports a diagnostic, and a diagnostic made to be reported.</summary>
        private static bool IsSink(SyntaxNode node) => node switch
        {
            InvocationExpressionSyntax call => call.Expression switch
            {
                MemberAccessExpressionSyntax access => Sinks.Contains(access.Name.Identifier.ValueText),
                MemberBindingExpressionSyntax binding => Sinks.Contains(binding.Name.Identifier.ValueText),
                IdentifierNameSyntax name => Sinks.Contains(name.Identifier.ValueText),
                _ => false,
            },
            ObjectCreationExpressionSyntax creation => TypeName(creation.Type).EndsWith("Diagnostic", StringComparison.Ordinal),
            _ => false,
        };

        private static string TypeName(TypeSyntax type) => type switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => type.ToString(),
        };

        private static bool IsBuildOutput(string path)
        {
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parts.Contains("obj") || parts.Contains("bin");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
        }
    }
}
