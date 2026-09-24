using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class LaunchResolutionModelTests
    {
        public static void Run(string fixtureRoot)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureRoot,
                "resolution", "auto-transition-prefers-scene-replacement.json")));
            var raw = document.RootElement.GetProperty("profile").GetProperty("packages")[0];
            var id = raw.GetProperty("id").GetString()!;
            var version = raw.GetProperty("version").GetString()!;
            ModManifest Manifest() => ModManifestJson.Deserialize(raw.GetProperty("manifest").GetRawText());
            ResolvedPackage Package() => new ResolvedPackage(id, version, Manifest());
            EffectiveProfile Profile(IReadOnlyList<ResolvedPackage> packages) => new EffectiveProfile("fixture", 1, packages);
            var request = new LaunchRequest(document.RootElement.GetProperty("request").GetProperty("targetId").GetString()!);
            foreach (var token in new[] { "", "../escape", "unicode-é", "newline\n", new string('x', 129) })
                Reject(() => new EffectiveProfile(token, 1, new[] { Package() }), "Invalid profile token");
            Reject(() => new EffectiveProfile("fixture", -1, new[] { Package() }), "Negative profile revision");
            Reject(() => new ResolvedPackage("other.mod", version, Manifest()), "Manifest identity mismatch");
            Reject(() => new ResolvedPackage(id, "9.0.0", Manifest()), "Manifest version mismatch");

            var selected = new List<ResolvedPackage> { Package() };
            var effective = Profile(selected);
            var plan = LaunchResolver.Resolve(effective, request).Plan!;
            var before = LaunchTransportJson.WritePlan(plan.Descriptor);
            selected.Clear();
            Assert(effective.Packages.Count == 1 && plan.Packages.Count == 1, "Profile retained caller collection");
            try { ((IList<PackageIdentity>)plan.Packages).Clear(); throw new InvalidOperationException("Plan collection is mutable"); }
            catch (NotSupportedException) { }
            try { ((IList<ResolvedPackage>)effective.Packages).Clear(); throw new InvalidOperationException("Profile collection is mutable"); }
            catch (NotSupportedException) { }
            Assert(before == LaunchTransportJson.WritePlan(plan.Descriptor), "Plan mutated after rejected mutation");

            var mutable = Manifest();
            var captured = new ResolvedPackage(id, version, mutable);
            mutable.Contributions!.Worlds.Clear(); mutable.Contributions.Gamemodes.Clear(); mutable.Contributions.LaunchTargets.Clear();
            var returned = captured.Manifest;
            returned.Contributions!.Worlds.Clear();
            Assert(LaunchResolver.Resolve(Profile(new[] { captured }), request).Resolved, "Package did not capture declarations defensively");
            var one = new PackageIdentity("base.mod", "1.0.0"); var two = new PackageIdentity("mode.mod", "2.3.4");
            Assert(PackageSetDigest.Of(Array.Empty<PackageIdentity>()) == "cbf29ce484222325", "Empty digest vector");
            Assert(PackageSetDigest.Of(new[] { one }) == "7f1662f4d34d2839", "Single digest vector");
            Assert(PackageSetDigest.Of(new[] { one, two }) == "6a4a1aa84ed69e17", "UTF16LE/LF digest vector");
            Assert(PackageSetDigest.Of(new[] { two, one }) == "6a4a1aa84ed69e17", "Digest input order");
            Assert(LaunchResolver.Revalidate(plan, new[] { new PackageIdentity(id, "9.0.0") }).Single().Code
                == LaunchBlockCode.PlanPackageSetMismatch, "Changed loaded identity was accepted");
            var changedManifest = Manifest(); changedManifest.Contributions!.LaunchTargets[0].Transition = "additive-arena";
            var changed = new ResolvedPackage(id, version, changedManifest);
            var loaded = Profile(new[] { changed });
            var bindings = new RuntimeBindingSnapshot("fixture", 1, plan.Digest, new[] { plan.WorldId }, new[] { plan.GamemodeId });
            Assert(LaunchResolver.Revalidate(plan, new[] { changed.Identity }).Count == 0, "Same identity should pass set revalidation");
            Assert(LaunchResolver.ResolveAgain(plan.Descriptor, loaded, bindings: bindings).Blocks.Single().Code
                == LaunchBlockCode.PlanResolutionMismatch, "Loaded manifest must resolve again");
            Assert(LaunchResolver.ResolveAgain(plan.Descriptor, effective).Blocks.Select(block => block.Code).SequenceEqual(
                new[] { LaunchBlockCode.GamemodeUnbound, LaunchBlockCode.WorldUnbound }), "Runtime resolution did not require fresh bindings");
            LaunchTransportModelTests.Run();
        }
        private static void Reject(Action action, string detail)
        {
            try { action(); } catch (ArgumentException) { return; }
            throw new InvalidOperationException(detail + " was accepted.");
        }
        private static void Assert(bool value, string detail)
        { if (!value) throw new InvalidOperationException(detail); }
    }
}
