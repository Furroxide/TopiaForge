using System;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class RuntimeInfoTests
    {
        public static void Run()
        {
            var runtime = new RuntimeInfo("0.0.2309");
            runtime.ConfigureProviders(Array.Empty<ModPackage>());
            Assert(runtime.ProviderVersions.ContainsKey("topiaforge.core"),
                "runtime metadata should always expose the manager-owned core provider version");
            Assert(runtime.TryGetUnavailableCapability("robotkit", out var missing)
                && missing!.Contains("not installed", StringComparison.Ordinal),
                "missing specialist modules should have a stable plain-language reason");
            Assert(runtime.TryGetUnavailableCapability("multiplayer", out var missingMultiplayer)
                && missingMultiplayer!.Contains("not installed", StringComparison.Ordinal),
                "the multiplayer preview provider should participate in runtime availability metadata");

            var manifest = new ModManifest
            {
                SchemaVersion = ModManifest.CurrentSchemaVersion,
                Id = "io.github.furroxide.topiaforge.robotkit",
                Name = "RobotKit",
                Version = "1.2.3-beta.1+acceptance",
                EntryAssembly = "TopiaForge.RobotKit.dll",
                EntryType = "TopiaForge.RobotKit.RobotKitMod"
            };
            var package = new ModPackage(
                "/tmp/robotkit",
                manifest,
                new InstalledModState { Id = manifest.Id, Name = manifest.Name, Version = manifest.Version, Enabled = true },
                Array.Empty<string>());
            runtime.ConfigureProviders(new[] { package });
            Assert(runtime.TryGetUnavailableCapability("robotkit", out var loading)
                && loading!.Contains("not completed", StringComparison.Ordinal),
                "an installed provider must not be advertised before its load callback completes");

            runtime.MarkProviderLoaded(manifest);
            Assert(runtime.ProviderVersions.TryGetValue(manifest.Id, out var version)
                && version.ToString() == manifest.Version
                && !runtime.TryGetUnavailableCapability("robotkit", out _),
                "a loaded provider should preserve full SemVer and clear its unavailable reason");

            runtime.MarkProviderFailed(manifest, "synthetic failure");
            Assert(!runtime.ProviderVersions.ContainsKey(manifest.Id)
                && runtime.TryGetUnavailableCapability("robotkit", out var failure)
                && failure!.Contains("synthetic failure", StringComparison.Ordinal),
                "provider runtime failure should replace optimistic version metadata with an exact reason");

            var customProvider = new ModManifest
            {
                SchemaVersion = ModManifest.CurrentSchemaVersion,
                Id = "example.weather-provider",
                Name = "Weather Provider",
                Version = "1.4.0+provider.7",
                EntryAssembly = "Weather.dll",
                EntryType = "Weather.Mod"
            };
            runtime.MarkProviderLoaded(customProvider);
            Assert(runtime.ProviderVersions.TryGetValue(customProvider.Id, out var customVersion)
                   && customVersion.ToString() == customProvider.Version,
                "dependency-scoped third-party service providers should expose complete versions too");
            runtime.MarkProviderFailed(customProvider, "provider stopped");
            Assert(!runtime.ProviderVersions.ContainsKey(customProvider.Id),
                "a failed third-party provider should not leave stale version metadata");

            TestLoadedButUnavailableProvider(package, manifest);

            Console.WriteLine("RuntimeInfoTests passed.");
        }

        private static void TestLoadedButUnavailableProvider(ModPackage package, ModManifest manifest)
        {
            var runtime = new RuntimeInfo("0.0.2309");
            var registry = new ModServiceRegistry();
            var lifetime = new FakeModLifetime();
            var agents = new FakeRobotAgentService(lifetime)
            {
                IsAvailable = false,
                IsNavigationAvailable = true
            };
            var registered = registry.RegisterExtension<IRobotAgentService>(
                manifest.Id,
                agents,
                ExtensionCardinality.Singleton);
            Assert(registered.Succeeded, "the canonical RobotKit provider should register for availability probing");

            try
            {
                runtime.ConfigureProviders(new[] { package });
                runtime.MarkProviderLoaded(manifest);
                RuntimeCapabilityProbe.Refresh(runtime, registry);
                Assert(runtime.ProviderVersions.ContainsKey(manifest.Id)
                       && runtime.TryGetUnavailableCapability("robotkit", out var unavailable)
                       && unavailable!.Contains("game adapter", StringComparison.Ordinal),
                    "a loaded RobotKit package must remain unavailable while its game adapter is unavailable");

                agents.IsAvailable = true;
                agents.IsNavigationAvailable = false;
                RuntimeCapabilityProbe.Refresh(runtime, registry);
                Assert(!runtime.TryGetUnavailableCapability("robotkit", out _)
                       && runtime.TryGetUnavailableCapability("robotkit.navigation", out var navigation)
                       && navigation!.Contains("navigation", StringComparison.OrdinalIgnoreCase),
                    "availability refresh should clear the recovered provider and retain only its unavailable adapter");

                agents.IsNavigationAvailable = true;
                RuntimeCapabilityProbe.Refresh(runtime, registry);
                Assert(!runtime.TryGetUnavailableCapability("robotkit", out _)
                       && !runtime.TryGetUnavailableCapability("robotkit.navigation", out _),
                    "a recovered provider and adapter should clear their stale unavailable reasons");

                runtime.MarkProviderFailed(manifest, "provider stopped");
                Assert(runtime.TryGetUnavailableCapability("robotkit", out var failed)
                       && failed!.Contains("provider stopped", StringComparison.Ordinal)
                       && !runtime.TryGetUnavailableCapability("robotkit.navigation", out _),
                    "provider failure should replace dynamic status and remove provider-owned adapter reasons");
            }
            finally
            {
                registered.Value?.Dispose();
                lifetime.Dispose();
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
