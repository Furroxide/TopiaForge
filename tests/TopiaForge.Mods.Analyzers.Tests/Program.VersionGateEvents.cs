using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.Mods.Analyzers.Tests
{
    internal static partial class Program
    {
        private static void RunVersionGateEventTests()
        {
            const string api = """
                using System;
                namespace TopiaForge.Mods {
                    [AttributeUsage(AttributeTargets.All)] public class RequiresTopiaForgeAttribute : Attribute {
                        public RequiresTopiaForgeAttribute(string version) { }
                    }
                    public class Events {
                        public static event Action LaterRemove { add { } [RequiresTopiaForge("0.2.0")] remove { } }
                        public static event Action LaterAdd { [RequiresTopiaForge("0.2.0")] add { } remove { } }
                        public event Action InstanceLaterRemove { add { } [RequiresTopiaForge("0.2.0")] remove { } }
                        public event Action InstanceLaterAdd { [RequiresTopiaForge("0.2.0")] add { } remove { } }
                    }
                }
                """;
            var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(api, ModParseOptions), null);
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            Assert(emitted.Success, "event metadata fixture compiles: " + string.Join("; ", emitted.Diagnostics));
            var reference = MetadataReference.CreateFromImage(stream.ToArray());
            var cases = new[] {
                ("Events.LaterRemove += Handler;", false),
                ("Events.LaterRemove -= Handler;", true),
                ("Events.LaterAdd += Handler;", true),
                ("Events.LaterAdd -= Handler;", false),
                ("events?.InstanceLaterRemove += Handler;", false),
                ("events?.InstanceLaterRemove -= Handler;", true),
                ("events?.InstanceLaterAdd += Handler;", true),
                ("events?.InstanceLaterAdd -= Handler;", false),
            };
            foreach (var (body, gated) in cases)
            {
                var source = "using TopiaForge.Mods; class Mod { void Handler() {} void Run(Events events) { " + body + " } }";
                Assert(!CompileForMod(source, reference).Any(d => d.Severity == DiagnosticSeverity.Error), "event use compiles: " + body);
                var diagnostics = GatesForMod(source, VersionManifest(">=0.1.0 <1.0.0"), reference);
                Assert((diagnostics.Length != 0) == gated, "TF1010 checks only the event accessor called: " + body
                    + " => " + string.Join(" | ", diagnostics.Select(d => d.GetMessage())));
            }
            Console.WriteLine("SDK event accessor checks passed (8 cases).");
        }
    }
}
