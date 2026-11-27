using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class LaunchTransportModelTests
    {
        internal static void Run()
        {
            var failures = new List<string>();
            void Reject(Action action, string detail)
            {
                try { action(); failures.Add(detail + " was accepted"); }
                catch (ArgumentException) { }
            }
            var package = new PackageIdentity("base.mod", "1.0.0");
            var digest = PackageSetDigest.Of(new[] { package });
            var request = new LaunchRequest("base.mod.menu");
            var blocks = Enumerable.Range(0, 4097).Select(i => new LaunchBlock(LaunchBlockCode.WorldUnavailable, "base.mod.world" + i)).ToArray();
            var ids = Enumerable.Range(0, 4097).Select(i => "base.mod.world" + i).ToArray();
            var packages = Enumerable.Range(0, 4097).Select(i => new PackageIdentity("base.mod" + i, "1.0.0")).ToArray();
            var discoveries = ids.Select(id => new DiscoveredWorldObservation(id + ".instance", id, "World")).ToArray();
            var availability = ids.Select(id => new DeclarationAvailability("world", id, new[] { blocks[0] })).ToArray();
            LaunchPlanDescriptor Plan(IEnumerable<PackageIdentity> selected) => new LaunchPlanDescriptor(
                "base.mod.menu", "base.mod.mode", "base.mod.world", "scene-replacement", request, selected);
            ProfileLaunchConfigurationV4 Profile(IEnumerable<string> enabled, IReadOnlyDictionary<string, string> versions) =>
                new ProfileLaunchConfigurationV4("profile", 0, "request", "main-menu", new[] { package }, digest,
                    false, true, enabled, versions);
            LaunchOutcome Outcome(IEnumerable<LaunchBlock> reasons) => new LaunchOutcome("launch", "request", 0,
                "preparing", "failed", reasons, command: "launch-target");
            RuntimeObservationEnvelope Observation(IEnumerable<DiscoveredWorldObservation> worlds, IEnumerable<DeclarationAvailability> reasons) =>
                new RuntimeObservationEnvelope("profile", 0, package, digest, 0, worlds, reasons);
            RuntimeBindingSnapshot Bindings(IEnumerable<string> worlds, IEnumerable<string> modes, IEnumerable<DeclarationAvailability> reasons) =>
                new RuntimeBindingSnapshot("profile", 0, digest, worlds, modes, reasons);
            var noIds = Array.Empty<string>();
            var noAvailability = Array.Empty<DeclarationAvailability>();
            var noDiscoveries = Array.Empty<DiscoveredWorldObservation>();
            var noVersions = new Dictionary<string, string>();
            Plan(packages.Take(4096));
            Profile(packages.Take(4096).Select(item => item.Id), noVersions);
            Profile(noIds, packages.Take(4096).ToDictionary(item => item.Id, item => item.Version));
            Outcome(blocks.Take(4096));
            Observation(discoveries.Take(4096), noAvailability);
            Observation(noDiscoveries, availability.Take(4096));
            Bindings(ids.Take(4096), ids.Take(4096), noAvailability);
            Bindings(noIds, noIds, availability.Take(4096));
            Reject(() => Plan(packages), "4097 plan packages");
            Reject(() => Profile(packages.Select(item => item.Id), noVersions), "4097 enabled ids");
            Reject(() => Profile(noIds, packages.ToDictionary(item => item.Id, item => item.Version)), "4097 selected versions");
            Reject(() => Outcome(blocks), "4097 outcome blocks");
            Reject(() => Outcome(Enumerable.Repeat(blocks[0], 4097)), "4097 duplicate outcome blocks");
            Reject(() => new DeclarationAvailability("world", ids[0], blocks), "4097 availability blocks");
            Reject(() => Observation(discoveries, noAvailability), "4097 discovered worlds");
            Reject(() => Observation(noDiscoveries, availability), "4097 availability records");
            Reject(() => Bindings(ids, noIds, noAvailability), "4097 world binding ids");
            Reject(() => Bindings(noIds, ids, noAvailability), "4097 mode binding ids");
            Reject(() => Bindings(noIds, noIds, availability), "4097 binding failures");
            Reject(() => Bindings(new[] { ids[0], ids[0].ToUpperInvariant() }, noIds, noAvailability), "duplicate world bindings");
            Reject(() => Bindings(noIds, new[] { ids[0], ids[0].ToUpperInvariant() }, noAvailability), "duplicate mode bindings");
            Reject(() => Bindings(noIds, noIds, new[] { availability[0], availability[0] }), "duplicate binding availability");
            Reject(() => new LaunchExecutionError("external", ""), "empty operation error");
            Reject(() => new LaunchBlock((LaunchBlockCode)999, ids[0]), "undefined block code");
            foreach (var suffix in new[] { "\n", "\r", "\t", "é" })
            {
                Reject(() => new PackageIdentity("base.mod", "1.0.0" + suffix), "version suffix " + ((int)suffix[0]));
                Reject(() => Profile(noIds, new Dictionary<string, string> { { "other.mod", "1.0.0" + suffix } }), "pin suffix " + ((int)suffix[0]));
                Reject(() => new LaunchProgress("request" + suffix, 0, "idle"), "token suffix " + ((int)suffix[0]));
            }
            if (failures.Count != 0) throw new InvalidOperationException("Launch transport model failures: " + string.Join("; ", failures));
        }
    }
}
