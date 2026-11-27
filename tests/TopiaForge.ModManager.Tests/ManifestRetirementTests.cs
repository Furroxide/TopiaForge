using System;
using System.IO;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class ManifestRetirementTests
    {
        internal static void Run()
        {
            foreach (var version in new[] { 4, 5 })
            {
                try
                {
                    ManifestSchemaDispatch.Resolve(version);
                    throw new InvalidOperationException("Retired schemaVersion " + version + " was accepted by dispatch.");
                }
                catch (InvalidDataException error)
                {
                    AssertGuidance(error.Message, version);
                }
                try
                {
                    ModManifestJson.Deserialize("{\"schemaVersion\":" + version + "}");
                    throw new InvalidOperationException("Retired schemaVersion " + version + " reached field binding.");
                }
                catch (InvalidDataException error)
                {
                    AssertGuidance(error.Message, version);
                }
                var manifest = new ModManifest
                {
                    SchemaVersion = version,
                    Multiplayer = new ModMultiplayerMetadata { Mode = "unknown" }
                };
                var errors = ManifestValidator.Validate(manifest);
                if (errors.Count != 1) throw new InvalidOperationException("Retirement must precede other model validation.");
                AssertGuidance(errors.Single(), version);
                if (ModManifest.IsSupportedSchemaVersion(version) || manifest.DeclaresMultiplayer)
                    throw new InvalidOperationException("Retired model must not declare usable multiplayer content.");
            }
            Console.WriteLine("Manifest retirement tests passed.");
        }
        private static void AssertGuidance(string message, int version)
        {
            if (!message.Contains("schemaVersion " + version) || !message.Contains("retired") ||
                !message.Contains("schemaVersion 6") || !message.Contains("topiaforge migrate-manifest --project <path>"))
                throw new InvalidOperationException("Retirement must name the original version and actionable V6 migration: " + message);
        }
    }
}
