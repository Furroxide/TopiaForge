extern alias sdk;
using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace TopiaForge.Mods.Analyzers.Tests
{
    internal static partial class Program
    {
        private static void WorldWidgetVersionGate()
        {
            var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = platform.Where(path => Path.GetFileName(path) is "netstandard.dll" or "System.Runtime.dll")
                .Append(typeof(sdk::TopiaForge.Mods.IUiService).Assembly.Location)
                .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
            var bodies = new[] {
                "object Run() => new UiProgress(0.5f);",
                "object Run() => new UiCountdown(30);",
                "object Run(IUiService ui) => ui.Labels;",
                "double Run(IUiService ui) => ui.ClockTime;",
                "object Run() => UiWorldLabelRequest.At(Vec3.Zero, \"Marker\");",
                "void Run(IUiWorldLabel label) => label.SetText(\"Marker\");"
            };
            foreach (var body in bodies)
            {
                var source = "using TopiaForge.Mods; class Mod { " + body + " }";
                Assert(!Compile(source, references).Any(d => d.Severity == DiagnosticSeverity.Error), "real world-widget API compiles: " + body);
                Assert(Analyze(source, VersionManifest(">=0.1.0 <0.3.0"), additionalReferences: references).Any(d => d.Id == "TF1010"),
                    "world-widget API requires a 0.2 floor: " + body);
                Assert(!Analyze(source, VersionManifest(">=0.2.0 <0.3.0"), additionalReferences: references).Any(d => d.Id == "TF1010"),
                    "world-widget API accepts a 0.2 floor: " + body);
            }
            Console.WriteLine("World-widget version gates passed: six shipped-API cases, 18 assertions.");
        }
    }
}
