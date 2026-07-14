using System;
using System.Collections.Generic;
using System.IO;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class ModServiceRegistryTests
    {
        public static void Run()
        {
            var registry = new ModServiceRegistry();
            var framework = new FakeSceneCoordinator();
            registry.RegisterFramework<ISceneCoordinator>("io.github.furroxide.topiaforge.modmanager", framework);

            Assert(ReferenceEquals(registry.Get<ISceneCoordinator>(), framework),
                "the manager-owned coordinator should be resolved");
            AssertThrows<InvalidOperationException>(
                () => registry.Register<ISceneCoordinator>("hostile.mod", new FakeSceneCoordinator()),
                "a mod must not replace a framework interface registration");
            AssertThrows<InvalidOperationException>(
                () => registry.Register<FakeSceneCoordinator>("hostile.mod", new FakeSceneCoordinator()),
                "a mod must not shadow a framework service through its concrete implementation type");

            // The public API accepts an owner string supplied by the caller. Even spoofing the internal owner
            // must neither replace nor remove a framework registration.
            AssertThrows<InvalidOperationException>(
                () => registry.Register<ISceneCoordinator>("io.github.furroxide.topiaforge.modmanager", new FakeSceneCoordinator()),
                "spoofing the framework owner must not replace the coordinator");
            registry.UnregisterOwner("io.github.furroxide.topiaforge.modmanager");
            Assert(ReferenceEquals(registry.Get<ISceneCoordinator>(), framework),
                "public owner cleanup must not remove manager-owned services");

            var ordinary = new FakeOrdinaryService();
            registry.Register<IOrdinaryService>("ordinary.mod", ordinary);
            Assert(ReferenceEquals(registry.Get<IOrdinaryService>(), ordinary),
                "ordinary mod services should still resolve");
            registry.UnregisterOwner("ordinary.mod");
            Assert(registry.Get<IOrdinaryService>() == null,
                "ordinary owner cleanup should still remove mod services");

            TestContextOwnerBoundary();
        }

        private static void TestContextOwnerBoundary()
        {
            var root = Path.Combine(Path.GetTempPath(), "TopiaForgeOwnerRegistry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var rawRegistry = new ModServiceRegistry();
                var manifest = new ModManifest
                {
                    SchemaVersion = 3,
                    Id = "owner.mod",
                    Name = "Owner",
                    Version = "1.0.0",
                    EntryAssembly = "Owner.dll",
                    EntryType = "Owner.Entry"
                };
                var paths = new ManagerPaths(Path.Combine(root, "BepInEx"));
                var packagePath = paths.GetPackagePath(manifest.Id, manifest.Version);
                Directory.CreateDirectory(packagePath);
                var context = new ModContext(
                    manifest,
                    paths,
                    packagePath,
                    new FakeLogger(),
                    rawRegistry);
                var exposed = context.GetService<IModServiceRegistry>();

                Assert(exposed != null && !ReferenceEquals(exposed, rawRegistry),
                    "ModContext must expose an owner-bound registry rather than the raw mutable registry");

                var owned = new FakeOrdinaryService();
                exposed!.Register<IOrdinaryService>(context.ModId, owned);
                Assert(ReferenceEquals(rawRegistry.Get<IOrdinaryService>(), owned),
                    "the facade should register services under its real context owner");

                AssertThrows<InvalidOperationException>(
                    () => exposed.Register<IHostileService>("victim.mod", new FakeHostileService()),
                    "a context must not register services under another owner");
                Assert(rawRegistry.Get<IHostileService>() == null,
                    "a rejected owner-spoofed registration must not reach the raw registry");

                var victim = new FakeVictimService();
                rawRegistry.Register<IVictimService>("victim.mod", victim);
                AssertThrows<InvalidOperationException>(
                    () => exposed.UnregisterOwner("victim.mod"),
                    "a context must not unregister another owner's services");
                Assert(ReferenceEquals(rawRegistry.Get<IVictimService>(), victim),
                    "rejected cross-owner cleanup must preserve the victim's service");

                exposed.UnregisterOwner(context.ModId.ToUpperInvariant());
                Assert(rawRegistry.Get<IOrdinaryService>() == null,
                    "the facade should allow case-insensitive cleanup of its own owner");
                Assert(ReferenceEquals(rawRegistry.Get<IVictimService>(), victim),
                    "owner cleanup through the facade must leave other owners intact");

                exposed.Register<IOrdinaryService>(context.ModId, owned);
                rawRegistry.UnregisterOwner(context.ModId);
                Assert(rawRegistry.Get<IOrdinaryService>() == null,
                    "runtime lifecycle cleanup must remove services registered through the facade");
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                    // Test cleanup only.
                }
            }
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class FakeSceneCoordinator : ISceneCoordinator
        {
            public bool IsSceneBusy => false;
            public IReadOnlyList<SceneClaimInfo> ActiveClaims => Array.Empty<SceneClaimInfo>();

            public SceneTransitionDecision RequestTransition(SceneTransitionRequest request)
            {
                return SceneTransitionDecision.Refuse("test");
            }

            public void ReleaseOwner(string ownerModId)
            {
            }
        }

        private interface IOrdinaryService
        {
        }

        private sealed class FakeOrdinaryService : IOrdinaryService
        {
        }

        private interface IHostileService
        {
        }

        private sealed class FakeHostileService : IHostileService
        {
        }

        private interface IVictimService
        {
        }

        private sealed class FakeVictimService : IVictimService
        {
        }

        private sealed class FakeLogger : IModLogger
        {
            public void Debug(string message)
            {
            }

            public void Info(string message)
            {
            }

            public void Warn(string message)
            {
            }

            public void Error(string message)
            {
            }

            public void Error(Exception exception, string message)
            {
            }
        }
    }
}
