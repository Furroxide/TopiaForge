using System;
using System.Collections.Generic;
using TopiaForge.ModManager;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class ModServiceRegistryTests
    {
        public static void Run()
        {
            TestSingletonRegistration();
            TestDependencyScopedDeterministicResolution();
            TestOwnerBoundFacadeIsolationAndFailureCleanup();
            TestContextFiltersIncompatibleOptionalProviders();
            TestCoreContractsAreReserved();
            TestSpecialistContractsAreReservedForCanonicalProviders();
            Console.WriteLine("All mod service registry tests passed.");
        }

        private static void TestSingletonRegistration()
        {
            var registry = new ModServiceRegistry();
            var provider = new FakeService("first");
            var registration = registry.RegisterExtension<ITestService>(
                "provider.mod",
                provider,
                ExtensionCardinality.Singleton);

            Assert(registration.TryGetValue(out var lease), "the first singleton provider should register");
            Assert(ReferenceEquals(registry.Get<ITestService>(), provider), "the singleton should resolve");

            var duplicate = registry.RegisterExtension<ITestService>(
                "other.mod",
                new FakeService("second"),
                ExtensionCardinality.Singleton);
            Assert(!duplicate.Succeeded && duplicate.ErrorCode == ModErrorCode.Conflict,
                "a second singleton provider should fail with Conflict");

            lease!.Dispose();
            Assert(registry.Get<ITestService>() == null, "disposing the registration should remove its provider");
        }

        private static void TestDependencyScopedDeterministicResolution()
        {
            var registry = new ModServiceRegistry();
            registry.RegisterExtension<ITestService>(
                "z.provider",
                new FakeService("z"),
                ExtensionCardinality.Multiple);
            registry.RegisterExtension<ITestService>(
                "a.provider",
                new FakeService("a"),
                ExtensionCardinality.Multiple);

            var accessible = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "a.provider",
                "z.provider"
            };
            var providers = registry.GetExtensions<ITestService>(accessible);
            Assert(providers.Count == 2 && providers[0].Name == "a" && providers[1].Name == "z",
                "multi-provider resolution should be normalized by provider id");

            registry.UnregisterOwner("A.PROVIDER");
            providers = registry.GetExtensions<ITestService>(accessible);
            Assert(providers.Count == 1 && providers[0].Name == "z",
                "owner cleanup should be case-insensitive and leave other providers intact");
        }

        private static void TestCoreContractsAreReserved()
        {
            var registry = new ModServiceRegistry();
            var result = registry.RegisterExtension<IModLogger>(
                "hostile.mod",
                new FakeLogger(),
                ExtensionCardinality.Singleton);
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.InvalidArgument,
                "mods must not replace manager-owned core service contracts");
        }

        private static void TestSpecialistContractsAreReservedForCanonicalProviders()
        {
            var registry = new ModServiceRegistry();
            var providerContext = new FakeModContext(new ModIdentity(
                "provider",
                "Provider",
                SemanticVersion.Parse("1.0.0")));
            var provider = new FakePromptOverrideRegistry(providerContext);
            var hostile = registry.RegisterExtension<IPromptOverrideRegistry>(
                "hostile.mod",
                provider,
                ExtensionCardinality.Singleton);
            Assert(!hostile.Succeeded && hostile.ErrorCode == ModErrorCode.InvalidArgument,
                "unrelated mods must not preempt a reserved specialist service contract");

            var canonical = registry.RegisterExtension<IPromptOverrideRegistry>(
                "io.github.furroxide.topiaforge.prompts",
                provider,
                ExtensionCardinality.Singleton);
            Assert(canonical.Succeeded,
                "the canonical specialist package must be able to publish its reserved contract");
            canonical.Value?.Dispose();
            providerContext.Dispose();
        }

        private static void TestOwnerBoundFacadeIsolationAndFailureCleanup()
        {
            var registry = new ModServiceRegistry();
            var provider = new OwnerBoundFakeService();
            Assert(registry.RegisterExtension<ITestService>(
                    "provider.mod",
                    provider,
                    ExtensionCardinality.Singleton).Succeeded,
                "owner-bound provider should register");

            var firstLifetime = new OwnerModLifetime();
            var firstExtensions = new OwnerExtensionService(
                "consumer.one",
                new[] { "provider.mod" },
                firstLifetime,
                registry);
            Assert(firstExtensions.TryGet<ITestService>(out var first) &&
                   firstExtensions.TryGet<ITestService>(out var firstAgain) &&
                   ReferenceEquals(first, firstAgain),
                "one consumer receives a cached owner facade");

            var secondLifetime = new OwnerModLifetime();
            var secondExtensions = new OwnerExtensionService(
                "consumer.two",
                new[] { "provider.mod" },
                secondLifetime,
                registry);
            Assert(secondExtensions.TryGet<ITestService>(out var second) &&
                   !ReferenceEquals(first, second) &&
                   first!.Name == "consumer.one" && second!.Name == "consumer.two",
                "different consumers receive isolated facades authenticated by runtime identity");

            var inaccessibleLifetime = new OwnerModLifetime();
            var inaccessible = new OwnerExtensionService(
                "unrelated.mod",
                Array.Empty<string>(),
                inaccessibleLifetime,
                registry);
            Assert(!inaccessible.TryGet<ITestService>(out _),
                "a consumer cannot resolve a provider outside its dependency scope");

            firstLifetime.Dispose();
            Assert(!((OwnerBoundFakeService.Facade)first!).IsActive &&
                   ((OwnerBoundFakeService.Facade)second!).IsActive,
                "partial-load cleanup releases only the failed consumer's facade resources");
            secondLifetime.Dispose();
            inaccessibleLifetime.Dispose();
            Assert(provider.ActiveFacadeResources == 0,
                "unload releases every consumer facade resource without provider-global reset");
        }

        private static void TestContextFiltersIncompatibleOptionalProviders()
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "TopiaForgeOptionalScope-" + Guid.NewGuid().ToString("N"));
            try
            {
                var paths = new TopiaForge.ModManager.Core.ManagerPaths(root);
                paths.EnsureCreated();
                var registry = new ModServiceRegistry();
                var providerManifest = new TopiaForge.ModManager.Core.ModManifest
                {
                    SchemaVersion = 4,
                    Id = "provider.optional",
                    Name = "Optional Provider",
                    Version = "1.0.0",
                    EntryAssembly = "Provider.dll",
                    EntryType = "Provider.Entry"
                };
                var registration = registry.RegisterExtension<ITestService>(
                    providerManifest.Id,
                    new FakeService("provider"),
                    ExtensionCardinality.Singleton);
                Assert(registration.Succeeded, "the optional provider should register for the scope test");

                var consumerManifest = new TopiaForge.ModManager.Core.ModManifest
                {
                    SchemaVersion = 4,
                    Id = "consumer.optional",
                    Name = "Optional Consumer",
                    Version = "1.0.0",
                    EntryAssembly = "Consumer.dll",
                    EntryType = "Consumer.Entry"
                };
                consumerManifest.OptionalDependencies.Add(providerManifest.Id, ">=2.0.0 <3.0.0");
                var context = new ModContext(
                    consumerManifest,
                    paths,
                    System.IO.Path.Combine(root, "package"),
                    new FakeLogger(),
                    registry,
                    runtimeInfo: null,
                    gameplayFactory: null,
                    availableManifests: new[] { providerManifest, consumerManifest });
                try
                {
                    Assert(!context.Extensions.TryGet<ITestService>(out _),
                        "an incompatible optional provider service must not be exposed through the context");
                }
                finally
                {
                    context.DisposeLifetime();
                    registration.Value?.Dispose();
                }
            }
            finally
            {
                if (System.IO.Directory.Exists(root))
                {
                    System.IO.Directory.Delete(root, recursive: true);
                }
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private interface ITestService
        {
            string Name { get; }
        }

        private sealed class FakeService : ITestService
        {
            public FakeService(string name)
            {
                Name = name;
            }

            public string Name { get; }
        }

        private sealed class OwnerBoundFakeService : ITestService, IOwnerBoundExtensionFactory
        {
            public string Name => "raw-provider";
            public int ActiveFacadeResources { get; private set; }

            public object CreateOwnerFacade(Type contractType, string consumerId, IModLifetime lifetime)
            {
                if (contractType != typeof(ITestService))
                {
                    throw new ArgumentException("Unexpected contract.", nameof(contractType));
                }

                ActiveFacadeResources++;
                var resource = new FacadeResource(() => ActiveFacadeResources--);
                lifetime.Track(resource);
                return new Facade(consumerId, resource);
            }

            public sealed class Facade : ITestService
            {
                private readonly FacadeResource resource;

                internal Facade(string name, FacadeResource resource)
                {
                    Name = name;
                    this.resource = resource;
                }

                public string Name { get; }
                public bool IsActive => resource.IsActive;
            }

            internal sealed class FacadeResource : IDisposable
            {
                private Action? release;

                public FacadeResource(Action release)
                {
                    this.release = release;
                }

                public bool IsActive => release != null;

                public void Dispose()
                {
                    var callback = release;
                    release = null;
                    callback?.Invoke();
                }
            }
        }

        private sealed class FakeLogger : IModLogger
        {
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
        }
    }
}
