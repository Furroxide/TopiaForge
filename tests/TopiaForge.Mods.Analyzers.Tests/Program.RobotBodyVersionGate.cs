extern alias sdk;
extern alias robotkit;

using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace TopiaForge.Mods.Analyzers.Tests
{
    internal static partial class Program
    {
        private static void RobotBodyVersionGate()
        {
            var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = platform.Where(path => Path.GetFileName(path) is "netstandard.dll" or "System.Runtime.dll")
                .Append(typeof(sdk::TopiaForge.Mods.TopiaForgeMod).Assembly.Location)
                .Append(typeof(robotkit::TopiaForge.Mods.IRobotBodyService).Assembly.Location)
                .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
            var cases = new[]
            {
                "void Run(IRobotBodyService bodies, RobotBodyRequest request) { bodies.Spawn(request); }",
                "object Run(AvatarAppearance appearance) => new RobotBodyRequest(appearance, Vec3.Zero);",
                "object Run(IRobotBody body) => body.Appearance;",
                "Quat Run(IRobotBody body) => body.Rotation;"
            };
            foreach (var member in cases)
            {
                var source = "using TopiaForge.Mods; class Mod { " + member + " }";
                Assert(!Compile(source, references).Any(d => d.Severity == DiagnosticSeverity.Error), "shipped body API compiles");
                Assert(Analyze(source, VersionManifest(">=0.1.0 <0.3.0"), additionalReferences: references).Any(d => d.Id == "TF1010"), "body API requires stable 0.2");
                Assert(!Analyze(source, VersionManifest(">=0.2.0 <0.3.0"), additionalReferences: references).Any(d => d.Id == "TF1010"), "body API accepts stable 0.2");
            }
            Console.WriteLine("Persistent body version gates passed: 12 assertions against shipped SDK assemblies.");
        }
    }
}
