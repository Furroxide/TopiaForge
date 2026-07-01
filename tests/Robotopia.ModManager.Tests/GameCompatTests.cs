using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Robotopia.GameCompat;
using Robotopia.Mods;

namespace Robotopia.ModManager.Tests
{
    // The offline, deterministic CI gate for game-update breaking changes. It never loads GameCode.dll (the harness
    // is net8.0 and cannot), so it runs anywhere. Instead it diffs the checked-in per-mod binding manifests against
    // the checked-in surface BASELINE — a snapshot the extractor captured from the real game DLL, carrying every
    // referenced type's COMPLETE member list. Because the baseline is an independent oracle (not a projection of the
    // manifests), "does this declared binding actually exist?" has a real answer here, so the gate is not circular.
    //
    // What this catches with zero external inputs: a manifest that drifted from reality (declares a critical symbol
    // the last-known-good surface never had), a hand-edited/nondeterministic baseline, and an enum reorder at the
    // SDK<->game seam. What it CANNOT catch (no DLL): whether a brand-new GameCode.dll still has the symbols — that
    // is the extractor's live `verify` job, run where a game install exists.
    internal static class GameCompatTests
    {
        public static void Run()
        {
            var root = FindRepoRoot();
            var manifests = LoadManifests(root);
            var baseline = LoadBaseline(root);

            AssertBaselineIsCanonicalAndComplete(root, baseline);

            // THE gate: resolve every declared binding against the independently-captured full surface.
            var report = SurfaceDiffer.ResolveManifests(manifests, baseline);
            var errors = report.Findings.Where(f => f.Severity == Severity.Error).ToList();
            if (errors.Count > 0)
            {
                var detail = string.Join("\n  ", errors.Select(e => e.ModId + " / " + e.Feature + " (" + e.ChangeKind + "): " + e.Detail));
                Assert(false, "the checked-in baseline no longer satisfies " + errors.Count + " critical binding(s):\n  " + detail);
            }

            AssertDamageTypeOrdinalsMatchSdk(baseline);
            AssertDifferDetectsBreakage(baseline, manifests);

            Console.WriteLine("GameCompat: " + manifests.Count + " manifest(s), " + report.TotalBindings + " binding(s) (" +
                report.VerifiableBindings + " verifiable, " + report.UncheckableBindings + " uncheckable-offline, " +
                report.IndeterminateBindings + " indeterminate), " + report.WarningCount + " warning(s). Baseline surface " +
                Short(baseline.ComputeContentHash()) + ".");
        }

        private static List<BindingManifest> LoadManifests(string root)
        {
            var bindingsDir = Path.Combine(root, "bindings");
            Assert(Directory.Exists(bindingsDir), "bindings/ directory should exist");

            var files = Directory.GetFiles(bindingsDir, "*.gamebindings.json").OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert(files.Count > 0, "at least one *.gamebindings.json manifest should exist");

            var manifests = new List<BindingManifest>();
            foreach (var file in files)
            {
                BindingManifest manifest;
                try
                {
                    manifest = BindingManifest.Parse(File.ReadAllText(file));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("GameCompat: manifest '" + Path.GetFileName(file) + "' failed to parse: " + ex.Message);
                }

                var problems = manifest.Validate().ToList();
                Assert(problems.Count == 0, "manifest '" + Path.GetFileName(file) + "' is invalid: " + string.Join("; ", problems));

                // Filename convention keeps modId honest: <modId>.gamebindings.json.
                var expectedId = Path.GetFileName(file).Substring(0, Path.GetFileName(file).Length - ".gamebindings.json".Length);
                Assert(manifest.ModId == expectedId,
                    "manifest '" + Path.GetFileName(file) + "' should declare modId '" + expectedId + "' but declares '" + manifest.ModId + "'");

                manifests.Add(manifest);
            }

            return manifests;
        }

        private static SurfaceSnapshot LoadBaseline(string root)
        {
            var path = Path.Combine(root, "baselines", "gamecode.surface.baseline.json");
            Assert(File.Exists(path),
                "surface baseline missing — capture it with `dotnet run --project src/Robotopia.GameCompat.Extractor -- baseline` and commit baselines/gamecode.surface.baseline.json");
            return SurfaceSnapshot.Parse(File.ReadAllText(path));
        }

        private static void AssertBaselineIsCanonicalAndComplete(string root, SurfaceSnapshot baseline)
        {
            var path = Path.Combine(root, "baselines", "gamecode.surface.baseline.json");
            var onDisk = File.ReadAllText(path);

            // Canonical round-trip: re-serializing the parsed baseline must reproduce the file, so a hand-edit or a
            // nondeterministic write is caught. Normalize line endings a checkout may have rewritten.
            Assert(Normalize(baseline.ToCanonicalJson()) == Normalize(onDisk),
                "baseline is not canonical (re-serialization differs) — regenerate with `gamecompat baseline`, never hand-edit");

            // A known-good baseline must have been captured in a complete environment (nothing left unreadable).
            var unreadable = baseline.UnreadableTypes().ToList();
            Assert(unreadable.Count == 0,
                "baseline was captured with unreadable types (incomplete Managed dir): " + string.Join(", ", unreadable) + " — recapture on a full install");
        }

        // The SDK's RobotDamageType is cast by `(int)` into the game's DamageType (RobotKit ApplyDamage). If the game
        // reorders DamageType, that cast silently targets the wrong damage type. Assert the baseline-captured game
        // ordinals still line up with the SDK enum at the exact seam where the bug would live.
        private static void AssertDamageTypeOrdinalsMatchSdk(SurfaceSnapshot baseline)
        {
            var damageType = baseline.FindType("GameCode|DamageType");
            if (damageType == null || damageType.Status != SurfaceStatus.Resolved || !damageType.IsEnum)
            {
                return; // no DamageType binding in the manifests / baseline; nothing to cross-check
            }

            foreach (RobotDamageType value in Enum.GetValues(typeof(RobotDamageType)))
            {
                var name = value.ToString();
                Assert(damageType.EnumMembers.TryGetValue(name, out var ordinal),
                    "game DamageType no longer defines '" + name + "', which SDK RobotDamageType maps to");
                Assert(ordinal == (int)value,
                    "DamageType." + name + " is now ordinal " + ordinal + " but SDK RobotDamageType." + name + " is " + (int)value +
                    " — every (int) cast into the game damage pipeline would be wrong");
            }
        }

        // Proves the differ is not "compat theater": it must actually turn a simulated game break into findings.
        // We clone the real baseline, injure it the way a game update would, and assert the differ notices — and
        // that the healthy baseline produces none of those findings (the control). If it can't detect a break, the
        // whole subsystem is worthless, so this is a hard gate.
        private static void AssertDifferDetectsBreakage(SurfaceSnapshot baseline, List<BindingManifest> manifests)
        {
            var healthy = SurfaceDiffer.ResolveManifests(manifests, baseline);
            Assert(healthy.ErrorCount == 0, "the healthy baseline should resolve without errors before the breakage self-test");

            var proofs = 0;

            // (a) enum ordinal drift — the silent (int)-cast corruption class.
            var enumClone = SurfaceSnapshot.Parse(baseline.ToCanonicalJson());
            var damageType = enumClone.FindType("GameCode|DamageType");
            if (damageType != null && damageType.EnumMembers.ContainsKey("Fire"))
            {
                damageType.EnumMembers["Fire"] = 99;
                var report = SurfaceDiffer.ResolveManifests(manifests, enumClone);
                Assert(report.Findings.Any(f => f.ChangeKind == ChangeKind.EnumOrdinalMismatch),
                    "differ FAILED to detect a DamageType ordinal shift — it would miss enum drift");
                Assert(!healthy.Findings.Any(f => f.ChangeKind == ChangeKind.EnumOrdinalMismatch),
                    "control failed: the healthy baseline should have no ordinal mismatch");
                proofs++;
            }

            // (b) a removed critical symbol must become an Error.
            var critical = manifests.SelectMany(m => m.Bindings).FirstOrDefault(b =>
                b.Criticality == Criticality.Critical && b.MatchMode == MatchMode.StaticFullName &&
                baseline.FindType(b.TypeKey)?.Status == SurfaceStatus.Resolved);
            if (critical != null)
            {
                var clone = SurfaceSnapshot.Parse(baseline.ToCanonicalJson());
                clone.Types.Remove(critical.TypeKey); // simulate the game update deleting the type
                var report = SurfaceDiffer.ResolveManifests(manifests, clone);
                Assert(report.Findings.Any(f => f.BindingId == critical.Id && f.Severity == Severity.Error),
                    "differ FAILED to flag a removed critical symbol (" + critical.Id + ") as an error");
                proofs++;
            }

            Assert(proofs > 0, "breakage self-test could not run (no suitable bindings found) — cannot certify the differ detects breaks");
        }

        private static string Normalize(string value) => value.Replace("\r\n", "\n");

        private static string Short(string hash) => hash.Length >= 12 ? hash.Substring(0, 12) : hash;

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "RobotopiaModManager.slnx")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("GameCompat: could not locate repo root (RobotopiaModManager.slnx) from " + AppContext.BaseDirectory);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("GameCompat: " + message);
            }
        }
    }
}
