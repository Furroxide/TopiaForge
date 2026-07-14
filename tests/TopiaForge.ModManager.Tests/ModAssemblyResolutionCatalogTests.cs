using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class ModAssemblyResolutionCatalogTests
    {
        internal static void Run(string root)
        {
            var testRoot = Path.Combine(root, "assembly-catalog");
            var provider = Package(testRoot, "catalog.provider");
            var consumer = Package(testRoot, "catalog.consumer", provider.Manifest!.Id);
            var unrelated = Package(testRoot, "catalog.unrelated");
            Directory.CreateDirectory(provider.PackagePath);
            Directory.CreateDirectory(consumer.PackagePath);
            Directory.CreateDirectory(unrelated.PackagePath);

            var coreAssembly = typeof(ModManifest).Assembly.Location;
            var definition = AssemblyName.GetAssemblyName(coreAssembly);
            var providerAssembly = Path.Combine(provider.PackagePath, definition.Name + ".dll");
            File.Copy(coreAssembly, providerAssembly);

            var catalog = new ModAssemblyResolutionCatalog(
                new[] { provider, consumer, unrelated },
                pluginDirectory: string.Empty);
            Assert(catalog.ValidateScopes().Count == 0,
                "a unique dependency-owned assembly should pass collision preflight");
            Assert(catalog.IsOwnerVisible(consumer.Manifest!.Id, provider.Manifest!.Id),
                "a declared dependency owner should be visible to its consumer");
            Assert(!catalog.IsOwnerVisible(unrelated.Manifest!.Id, provider.Manifest!.Id),
                "an undeclared mod owner must remain isolated");
            Assert(catalog.FindCandidate(consumer.Manifest.Id, definition) == providerAssembly,
                "consumer should resolve a strict-identity candidate from its declared dependency");
            Assert(catalog.FindCandidate(unrelated.Manifest.Id, definition) == null,
                "unrelated requester must not search another mod's folder");
            Assert(catalog.TryGetOwner(providerAssembly, out var owner) && owner == provider.Manifest.Id,
                "assembly path should map back to its package owner");

            var wrongVersion = new AssemblyName(definition.FullName)
            {
                Version = new Version(99, 0, 0, 0)
            };
            var mismatchRejected = false;
            try
            {
                catalog.FindCandidate(consumer.Manifest.Id, wrongVersion);
            }
            catch (FileLoadException)
            {
                mismatchRejected = true;
            }

            Assert(mismatchRejected, "same-name/different-version assembly must not be silently unified");

            // A consumer bundling different bytes under the same requested filename would make resolution order
            // process-dependent. Preflight must reject it before any mod executes.
            File.Copy(typeof(ModAssemblyResolutionCatalogTests).Assembly.Location,
                Path.Combine(consumer.PackagePath, definition.Name + ".dll"));
            var collisions = catalog.ValidateScopes();
            Assert(collisions.ContainsKey(consumer.Manifest.Id),
                "conflicting private/dependency assembly files should block the consumer scope");
            Console.WriteLine("ModAssemblyResolutionCatalogTests passed.");
        }

        private static ModPackage Package(string root, string id, string? dependency = null)
        {
            var manifest = new ModManifest
            {
                SchemaVersion = 3,
                Id = id,
                Name = id,
                Version = "1.0.0",
                Author = new ModAuthor { Name = "Tests" },
                EntryAssembly = id + ".dll",
                EntryType = id + ".Entry"
            };
            if (dependency != null)
            {
                manifest.Dependencies.Add(new ModDependency { Id = dependency });
            }

            var state = new ManagerState();
            return new ModPackage(
                Path.Combine(root, id),
                manifest,
                state.Upsert(manifest, enabled: true, restartRequired: false),
                Array.Empty<string>());
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assembly catalog: " + message);
            }
        }
    }
}
