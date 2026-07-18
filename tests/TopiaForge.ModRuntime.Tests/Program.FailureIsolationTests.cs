using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using RuntimeUnderTest = TopiaForge.ModManager.ModRuntime;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestRequiredDependencyRuntimeFailure(string root)
        {
            var fixture = NewFixture(root, "dependency-failure", "TopiaForge.ValidTestMod.RuntimeDependentMod");
            fixture.Manifest.Dependencies.Add("tests.missing-provider", ">=1.0.0 <2.0.0");
            var invalidProvider = ModPackage.Invalid(
                Path.Combine(root, "dependency-failure", "tests.missing-provider"),
                "synthetic corrupt provider");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { invalidProvider, fixture.Package });

            Assert(!runtime.IsLoaded(fixture.Manifest.Id)
                && runtime.GetLoadFailure(fixture.Manifest.Id)?.Contains("required dependency", StringComparison.Ordinal) == true,
                "runtime failure of a required dependency must block its consumer");
            AssertTrace(fixture.TracePath);
            runtime.UnloadAll();
        }

        private static void TestOptionalDependencyRuntimeFailureDoesNotBlock(string root)
        {
            var fixture = NewFixture(root, "optional-dependency-failure", "TopiaForge.ValidTestMod.RuntimeDependentMod");
            fixture.Manifest.OptionalDependencies.Add("tests.optional-provider", ">=1.0.0 <2.0.0");
            var invalidProvider = ModPackage.Invalid(
                Path.Combine(root, "optional-dependency-failure", "tests.optional-provider"),
                "synthetic corrupt optional provider");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { invalidProvider, fixture.Package });

            Assert(runtime.IsLoaded(fixture.Manifest.Id),
                "a corrupt optional dependency must not block an otherwise healthy consumer");
            runtime.UnloadAll();
            AssertTrace(fixture.TracePath, "dependent-load");
        }

        private static void TestConstructorFailure(string root)
        {
            var fixture = NewFixture(root, "constructor-failure", "TopiaForge.ValidTestMod.RuntimeThrowingConstructorMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(!runtime.IsLoaded(fixture.Manifest.Id)
                && runtime.GetLoadFailure(fixture.Manifest.Id)?.Contains("constructor failure", StringComparison.Ordinal) == true,
                "constructor failures must not strand a loaded mod");
            AssertTrace(fixture.TracePath, "constructor");
            Assert(fixture.Observer.Events.Count == 0,
                "the startup journal boundary must not blame OnLoad when construction failed first");
            runtime.UnloadAll();
        }

        private static void TestRuntimeCompatibilityDefense(string root)
        {
            var fixture = NewFixture(root, "runtime-incompatible", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            fixture.Manifest.Platforms.Add("macos");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(!runtime.IsLoaded(fixture.Manifest.Id) &&
                   runtime.GetLoadFailure(fixture.Manifest.Id)?.Contains(
                       "host platform windows",
                       StringComparison.Ordinal) == true,
                "runtime loading must recheck compatibility even for a preconstructed valid package");
            AssertTrace(fixture.TracePath);
            Assert(fixture.Observer.Events.Count == 0,
                "runtime compatibility rejection must happen before assembly activation and OnLoad journaling");
            runtime.UnloadAll();
        }

        private static void TestReceiptRecheckedImmediatelyBeforeLoad(string root)
        {
            var fixture = NewFixture(root, "load-time-tamper", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            File.AppendAllText(
                Path.Combine(fixture.Package.PackagePath, FixtureAssembly),
                "changed-after-registry-scan");

            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(!runtime.IsLoaded(fixture.Manifest.Id)
                && runtime.GetLoadFailure(fixture.Manifest.Id)?.Contains(
                    "package integrity changed before load",
                    StringComparison.Ordinal) == true,
                "the runtime must reverify installed bytes at the last safe point before Assembly.LoadFrom");
            AssertTrace(fixture.TracePath);
            Assert(fixture.Observer.Events.Count == 0,
                "receipt rejection must occur before activation and the OnLoad startup-journal boundary");
            runtime.UnloadAll();
        }

        private static void TestResolverLifetime(string root)
        {
            var reference = CreateUnloadedRuntimeReference(root);
            for (var attempt = 0; attempt < 3 && reference.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert(!reference.IsAlive,
                "UnloadAll must detach the AppDomain assembly resolver and release the runtime instance");
        }
    }
}
