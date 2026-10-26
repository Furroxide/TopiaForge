extern alias sdk;

using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace TopiaForge.Mods.Analyzers.Tests
{
    internal static partial class Program
    {
        private static void BlockerVersionGate()
        {
            const string source = "using TopiaForge.Mods; class Mod { void Run(IPhysicsService physics) { physics.AddBlocker(null); } }";
            var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = platform.Where(path => Path.GetFileName(path) is "netstandard.dll" or "System.Runtime.dll")
                .Append(typeof(sdk::TopiaForge.Mods.IPhysicsService).Assembly.Location).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
            Assert(!Compile(source, references).Any(d => d.Severity == DiagnosticSeverity.Error), "real blocker API fixture compiles");
            var gates = Analyze(source, VersionManifest(">=0.1.0 <0.3.0"), additionalReferences: references).Where(d => d.Id == "TF1010").ToArray();
            Assert(gates.Length == 1 && gates[0].GetMessage().Contains("AddBlocker", StringComparison.Ordinal), "TF1010 gates the shipped AddBlocker member");
            Assert(!Analyze(source, VersionManifest(">=0.2.0 <0.3.0"), additionalReferences: references).Any(d => d.Id == "TF1010"), "blocker API accepts a 0.2 floor");
            Console.WriteLine("Blocker version gate passed: 3 assertions against the shipped SDK assembly.");
        }
    }
}
