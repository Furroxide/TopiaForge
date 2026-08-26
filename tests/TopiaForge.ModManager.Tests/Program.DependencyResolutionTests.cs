using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class Program
    {
        private static void TestRequiredDependenciesHelper()
        {
            var manifest = new ModManifest
            {
                SchemaVersion = 5,
                Id = "eta.mod",
                Name = "Eta",
                Version = "1.0.0",
                EntryAssembly = "Eta.dll",
                EntryType = "Eta.Entry"
            };
            manifest.Dependencies.Add("framework.mod", ">=1.0.0");
            manifest.Dependencies.Add("hard.mod", "*");
            manifest.OptionalDependencies.Add("soft.mod", "*");

            var required = DependencyResolver.GetRequiredDependencies(manifest).Select(d => d.Id).ToList();
            Assert(required.Contains("framework.mod") && required.Contains("hard.mod"), "hard dependencies are required");
            Assert(!required.Contains("soft.mod"), "optional dependencies are not required");

            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FRAMEWORK.MOD" };
            Assert(DependencyResolver.FindFailedRequiredDependency(manifest, failed) == "framework.mod",
                "a failed required dependency should be found case-insensitively");
            Assert(DependencyResolver.FindFailedRequiredDependency(
                    manifest,
                    new List<string> { "FRAMEWORK.MOD" }) == "framework.mod",
                "failed-dependency matching must remain case-insensitive for ordinary case-sensitive collections");
            Assert(DependencyResolver.FindFailedRequiredDependency(manifest, new HashSet<string>()) == null,
                "no failures means no gating");
        }

        private static void TestDependencyOrder(string root)
        {
            var state = new ManagerState();
            var depManifest = new ModManifest
            {
                SchemaVersion = 5,
                Id = "dependency.mod",
                Name = "Dependency",
                Version = "1.0.0",
                EntryAssembly = "Dependency.dll",
                EntryType = "Dependency.Entry"
            };
            var mainManifest = new ModManifest
            {
                SchemaVersion = 5,
                Id = "main.mod",
                Name = "Main",
                Version = "1.0.0",
                EntryAssembly = "Main.dll",
                EntryType = "Main.Entry"
            };
            mainManifest.Dependencies.Add("dependency.mod", ">=1.0.0");

            var dependency = new ModPackage(Path.Combine(root, "dep"), depManifest, state.Upsert(depManifest, true, false), Array.Empty<string>());
            var main = new ModPackage(Path.Combine(root, "main"), mainManifest, state.Upsert(mainManifest, true, false), Array.Empty<string>());
            var result = new DependencyResolver().Resolve(new[] { main, dependency });

            Assert(result.OrderedPackages.Count == 2, "both mods should be loadable");
            Assert(result.OrderedPackages[0].Manifest!.Id == "dependency.mod", "dependency should load first");
            Assert(result.OrderedPackages[1].Manifest!.Id == "main.mod", "dependent mod should load second");
        }

        private static void TestFrameworkDependencyOrder(string root)
        {
            var state = new ManagerState();
            var worldsManifest = new ModManifest
            {
                SchemaVersion = 5,
                Id = "io.github.furroxide.topiaforge.worlds",
                Name = "TopiaForge Worlds",
                Version = "0.1.0-rc.1",
                EntryAssembly = "TopiaForge.Worlds.dll",
                EntryType = "TopiaForge.Worlds.WorldsMod"
            };
            var promptsManifest = new ModManifest
            {
                SchemaVersion = 5,
                Id = "io.github.furroxide.topiaforge.prompts",
                Name = "TopiaForge Prompts",
                Version = "0.1.0-rc.1",
                EntryAssembly = "TopiaForge.Prompts.dll",
                EntryType = "TopiaForge.Prompts.PromptsMod"
            };
            var consumerManifest = new ModManifest
            {
                SchemaVersion = 5,
                Id = "consumer.mod",
                Name = "Consumer",
                Version = "1.0.0",
                EntryAssembly = "Consumer.dll",
                EntryType = "Consumer.Entry"
            };
            consumerManifest.Dependencies.Add("io.github.furroxide.topiaforge.worlds", ">=0.1.0-rc.1 <0.2.0");
            consumerManifest.Dependencies.Add("io.github.furroxide.topiaforge.prompts", ">=0.1.0-rc.1 <0.2.0");
            consumerManifest.LoadAfter.Add("io.github.furroxide.topiaforge.worlds");
            consumerManifest.LoadAfter.Add("io.github.furroxide.topiaforge.prompts");

            var worlds = new ModPackage(Path.Combine(root, "worlds"), worldsManifest, state.Upsert(worldsManifest, true, false), Array.Empty<string>());
            var prompts = new ModPackage(Path.Combine(root, "prompts"), promptsManifest, state.Upsert(promptsManifest, true, false), Array.Empty<string>());
            var consumer = new ModPackage(Path.Combine(root, "consumer"), consumerManifest, state.Upsert(consumerManifest, true, false), Array.Empty<string>());
            var result = new DependencyResolver().Resolve(new[] { consumer, prompts, worlds });
            var orderedIds = result.OrderedPackages.Select(p => p.Manifest!.Id).ToList();

            Assert(orderedIds.Count == 3, "framework providers and consumer should all be loadable");
            Assert(orderedIds.IndexOf("io.github.furroxide.topiaforge.worlds") < orderedIds.IndexOf("consumer.mod"), "worlds provider should load before its consumer");
            Assert(orderedIds.IndexOf("io.github.furroxide.topiaforge.prompts") < orderedIds.IndexOf("consumer.mod"), "prompts provider should load before its consumer");
        }

        private static void TestDependencyFailurePropagation(string root)
        {
            var state = new ManagerState();

            var cycleA = TestManifest("cycle.a");
            var cycleB = TestManifest("cycle.b");
            var cycleDependent = TestManifest("cycle.consumer");
            var optionalConsumer = TestManifest("cycle.optional");
            cycleA.Dependencies.Add("cycle.b", ">=1.0.0");
            cycleB.Dependencies.Add("cycle.a", ">=1.0.0");
            cycleDependent.Dependencies.Add("cycle.a", ">=1.0.0");
            optionalConsumer.OptionalDependencies.Add("cycle.a", "*");

            var cycleResult = new DependencyResolver().Resolve(new[]
            {
                TestPackage(root, state, cycleDependent),
                TestPackage(root, state, optionalConsumer),
                TestPackage(root, state, cycleB),
                TestPackage(root, state, cycleA),
            });
            Assert(cycleResult.Errors.ContainsKey("cycle.a") && cycleResult.Errors.ContainsKey("cycle.b"),
                "every member of A -> B -> A must be blocked as a cycle");
            Assert(cycleResult.Errors.ContainsKey("cycle.consumer"),
                "a required dependent of a cycle member must also be blocked");
            Assert(!cycleResult.Errors.ContainsKey("cycle.optional")
                && cycleResult.OrderedPackages.Any(package => package.Manifest!.Id == "cycle.optional"),
                "an optional dependency on a blocked cycle must remain non-blocking");

            var missing = TestManifest("missing.leaf");
            var missingDependent = TestManifest("missing.consumer");
            missing.Dependencies.Add("not.installed", ">=1.0.0");
            missingDependent.Dependencies.Add("missing.leaf", ">=1.0.0");
            var missingResult = new DependencyResolver().Resolve(new[]
            {
                TestPackage(root, state, missingDependent),
                TestPackage(root, state, missing),
                ModPackage.Invalid(Path.Combine(root, "not-installed-invalid"), "invalid manifest")
            });
            Assert(missingResult.Errors.ContainsKey("missing.leaf")
                && missingResult.Errors.ContainsKey("missing.consumer"),
                "a missing/invalid dependency failure must propagate through required dependents");
            Assert(missingResult.OrderedPackages.Count == 0,
                "no package in a required missing-dependency chain may enter the load order");

            var oldProvider = TestManifest("old.provider");
            var incompatible = TestManifest("incompatible.consumer");
            var transitive = TestManifest("incompatible.transitive");
            incompatible.Dependencies.Add("old.provider", ">=2.0.0");
            transitive.Dependencies.Add("incompatible.consumer", ">=1.0.0");
            var versionResult = new DependencyResolver().Resolve(new[]
            {
                TestPackage(root, state, transitive),
                TestPackage(root, state, incompatible),
                TestPackage(root, state, oldProvider),
            });
            Assert(versionResult.Errors.ContainsKey("incompatible.consumer")
                && versionResult.Errors.ContainsKey("incompatible.transitive"),
                "an invalid required version must propagate to transitive dependents");
            Assert(versionResult.OrderedPackages.Count == 1
                && versionResult.OrderedPackages[0].Manifest!.Id == "old.provider",
                "an otherwise valid provider remains loadable when only its consumer requires a newer version");

            var duplicateFirst = TestManifest("duplicate.mod");
            var duplicateSecond = TestManifest("DUPLICATE.MOD");
            var duplicateConsumer = TestManifest("duplicate.consumer");
            duplicateConsumer.Dependencies.Add("duplicate.mod", ">=1.0.0");
            var duplicateResult = new DependencyResolver().Resolve(new[]
            {
                new ModPackage(
                    Path.Combine(root, "z-duplicate"),
                    duplicateSecond,
                    state.Upsert(duplicateSecond, true, false),
                    Array.Empty<string>()),
                TestPackage(root, state, duplicateConsumer),
                new ModPackage(
                    Path.Combine(root, "a-duplicate"),
                    duplicateFirst,
                    state.Upsert(duplicateFirst, true, false),
                    Array.Empty<string>()),
            });
            Assert(duplicateResult.Errors.TryGetValue("duplicate.mod", out var duplicateErrors)
                && duplicateErrors.Any(error => error.Contains("Multiple enabled packages")),
                "duplicate manifest ids should produce a deterministic resolver error instead of throwing");
            Assert(duplicateResult.Errors.ContainsKey("duplicate.consumer")
                && duplicateResult.OrderedPackages.Count == 0,
                "duplicate providers and their required consumers must be excluded from the load order");
            Assert(duplicateErrors![0].IndexOf("a-duplicate", StringComparison.OrdinalIgnoreCase)
                    < duplicateErrors[0].IndexOf("z-duplicate", StringComparison.OrdinalIgnoreCase),
                "duplicate package diagnostics should sort paths deterministically");
        }

        private static void TestDependencyVersionRangeSemantics(string root)
        {
            LoadOrderResult ResolveRange(string range)
            {
                var state = new ManagerState();
                var provider = TestManifest("range.provider");
                provider.Version = "1.2.4";
                var consumer = TestManifest("range.consumer");
                consumer.Dependencies.Add(provider.Id, range);
                return new DependencyResolver().Resolve(new[]
                {
                    TestPackage(root, state, consumer),
                    TestPackage(root, state, provider)
                });
            }

            var exact = ResolveRange("1.2.3");
            Assert(exact.Errors.TryGetValue("range.consumer", out var exactErrors)
                && exactErrors.Any(error => error.Contains("satisfy 1.2.3")),
                "a plain dependency versionRange should be exact, not a minimum");
            Assert(!exactErrors!.Any(error => error.Contains(">=1.2.3")),
                "dependency diagnostics should not rewrite an exact range as a minimum");

            Assert(!ResolveRange(">=1.2.0 <2.0.0").Errors.ContainsKey("range.consumer"),
                "dependency versionRange should accept comparator ranges");
            Assert(!ResolveRange("1.2.x").Errors.ContainsKey("range.consumer"),
                "dependency versionRange should accept wildcard ranges");

            foreach (var range in new[] { ">=1.2.0 <2.0.0", "1.2.x" })
            {
                var manifest = TestManifest("range.validation");
                manifest.Dependencies.Add("range.provider", range);
                Assert(!ManifestValidator.Validate(manifest).Any(error => error.Contains("invalid version")),
                    "manifest validation should accept dependency versionRange: " + range);
            }
        }

        private static void TestSoftDependencyCyclesDoNotBlock(string root)
        {
            LoadOrderResult Resolve(params ModManifest[] manifests)
            {
                var state = new ManagerState();
                return new DependencyResolver().Resolve(
                    manifests.Select(manifest => TestPackage(root, state, manifest)));
            }

            var loadAfterA = TestManifest("soft.loadafter.a");
            var loadAfterB = TestManifest("soft.loadafter.b");
            loadAfterA.LoadAfter.Add(loadAfterB.Id);
            loadAfterB.LoadAfter.Add(loadAfterA.Id);
            var loadAfter = Resolve(loadAfterB, loadAfterA);
            Assert(loadAfter.Errors.Count == 0 && loadAfter.OrderedPackages.Count == 2,
                "a mutual loadAfter hint must not block either mod");
            Assert(loadAfter.OrderedPackages.Select(package => package.Manifest!.Id).SequenceEqual(
                    new[] { loadAfterB.Id, loadAfterA.Id }),
                "mutual loadAfter ordering should keep the deterministic first edge");

            var loadBeforeA = TestManifest("soft.loadbefore.a");
            var loadBeforeB = TestManifest("soft.loadbefore.b");
            loadBeforeA.LoadBefore.Add(loadBeforeB.Id);
            var loadBefore = Resolve(loadBeforeB, loadBeforeA);
            Assert(loadBefore.Errors.Count == 0 && loadBefore.OrderedPackages.Select(package => package.Manifest!.Id)
                    .SequenceEqual(new[] { loadBeforeA.Id, loadBeforeB.Id }),
                "loadBefore should order its owner before the named mod without becoming a hard dependency");

            var optionalA = TestManifest("soft.optional.a");
            var optionalB = TestManifest("soft.optional.b");
            optionalA.OptionalDependencies.Add(optionalB.Id, "*");
            optionalB.OptionalDependencies.Add(optionalA.Id, "*");
            var optional = Resolve(optionalB, optionalA);
            Assert(optional.Errors.Count == 0 && optional.OrderedPackages.Count == 2,
                "a mutual optional-dependency hint must not block either mod");
            Assert(optional.OrderedPackages.Select(package => package.Manifest!.Id).SequenceEqual(
                    new[] { optionalB.Id, optionalA.Id }),
                "mutual optional ordering should be deterministic regardless of input order");

            var absentOptionalConsumer = TestManifest("soft.optional.absent-consumer");
            absentOptionalConsumer.OptionalDependencies.Add("soft.optional.absent-provider", ">=2.0.0 <3.0.0");
            var absentOptional = Resolve(absentOptionalConsumer);
            Assert(absentOptional.Errors.Count == 0 && absentOptional.OrderedPackages.Count == 1,
                "an absent optional provider must remain non-blocking for its consumer");

            var incompatibleOptionalConsumer = TestManifest("soft.optional.incompatible-consumer");
            var incompatibleOptionalProvider = TestManifest("soft.optional.incompatible-provider");
            incompatibleOptionalConsumer.OptionalDependencies.Add(
                incompatibleOptionalProvider.Id,
                ">=2.0.0 <3.0.0");
            var incompatibleOptional = Resolve(incompatibleOptionalProvider, incompatibleOptionalConsumer);
            Assert(incompatibleOptional.Errors.Count == 0 && incompatibleOptional.OrderedPackages.Count == 2,
                "an incompatible optional provider must not block either provider or consumer");

            var hardConsumer = TestManifest("soft.mixed.consumer");
            var hardProvider = TestManifest("soft.mixed.provider");
            hardConsumer.Dependencies.Add(hardProvider.Id, ">=1.0.0");
            hardProvider.LoadAfter.Add(hardConsumer.Id);
            var mixed = Resolve(hardConsumer, hardProvider);
            Assert(mixed.Errors.Count == 0 && mixed.OrderedPackages.Select(package => package.Manifest!.Id).SequenceEqual(
                    new[] { hardProvider.Id, hardConsumer.Id }),
                "a contradictory soft hint must yield to a hard dependency without blocking either mod");
        }

        private static void TestManifestDependencyIdsRejected()
        {
            var manifest = TestManifest("safe.mod");
            manifest.Dependencies.Add("../vpm", ">=1.0.0");
            manifest.Dependencies.Add("../required", "*");
            manifest.OptionalDependencies.Add(@"..\optional", "*");
            manifest.Conflicts.Add(new ModConflict { Id = "/conflict" });
            manifest.LoadAfter.Add("../load-after");

            var errors = ManifestValidator.Validate(manifest);
            foreach (var unsafeId in new[] { "../vpm", "../required", @"..\optional", "/conflict", "../load-after" })
            {
                Assert(errors.Any(error => error.Contains(unsafeId)),
                    "manifest validation should reject unsafe related id '" + unsafeId + "'");
            }
        }

        private static void TestRetiredEcosystemIdRootsRejected()
        {
            var retiredPrefixes = new[]
            {
                StringFromCodeUnits(114, 111, 98, 111, 116, 111, 112, 105, 97, 46),
                StringFromCodeUnits(99, 111, 109, 46, 114, 111, 98, 111, 116, 111, 112, 105, 97, 46),
                StringFromCodeUnits(113, 117, 97, 110, 116, 117, 109, 119, 111, 114, 107, 115, 46)
            };

            foreach (var prefix in retiredPrefixes)
            {
                var retiredId = prefix + "validation";
                var manifest = TestManifest(retiredId);
                manifest.Author.Name = "Tests";
                var manifestErrors = ManifestValidator.Validate(manifest);
                Assert(manifestErrors.Any(error => error.Contains("name must be 2-64 characters")),
                    "manifest validation should reject retired ecosystem root '" + retiredId + "'");

                var related = TestManifest("io.github.furroxide.topiaforge.validation");
                related.Author.Name = "Tests";
                related.Dependencies.Add(retiredId, "*");
                related.Conflicts.Add(new ModConflict { Id = retiredId });
                var relatedErrors = ManifestValidator.Validate(related);
                Assert(relatedErrors.Count(error => error.Contains(retiredId)) == 2,
                    "manifest validation should reject retired dependency and conflict root '" + retiredId + "'");
            }

            var canonical = TestManifest("io.github.furroxide.topiaforge.validation");
            canonical.Author.Name = "Tests";
            canonical.Dependencies.Add("io.github.furroxide.topiaforge.validation.required", "*");
            canonical.Conflicts.Add(new ModConflict
            {
                Id = "io.github.furroxide.topiaforge.validation.conflict"
            });
            Assert(ManifestValidator.Validate(canonical).Count == 0,
                "manifest validation should accept canonical TopiaForge manifest and related ids");
        }

    }
}
