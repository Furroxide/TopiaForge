using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TopiaForge.GameCompat;
using TopiaForge.GameCompat.Extractor;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
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

            AssertSafeSdkConsumersHaveNoBindingManifest(root);
            AssertBaselineIsCanonicalAndComplete(root, baseline);
            AssertRetiredFormatsRejected(baseline, manifests[0]);

            // THE gate: resolve every declared binding against the independently-captured full surface.
            var report = SurfaceDiffer.ResolveManifests(manifests, baseline);
            Assert(report.IndeterminateBindings == 0,
                "the checked-in baseline must contain enough metadata to verify every offline-verifiable binding");
            var errors = report.Findings.Where(f => f.Severity == Severity.Error).ToList();
            if (errors.Count > 0)
            {
                var detail = string.Join("\n  ", errors.Select(e => e.ModId + " / " + e.Feature + " (" + e.ChangeKind + "): " + e.Detail));
                Assert(false, "the checked-in baseline no longer satisfies " + errors.Count + " critical binding(s):\n  " + detail);
            }

            AssertDamageTypeOrdinalsMatchSdk(baseline);
            AssertDifferDetectsBreakage(baseline, manifests);
            AssertSimpleNameWalkValidatesMembers();
            AssertLinkedCompileSourcesAreAudited();

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
                "surface baseline missing — capture it with `dotnet run --project src/TopiaForge.GameCompat.Extractor -- baseline` and commit baselines/gamecode.surface.baseline.json");
            return SurfaceSnapshot.Parse(File.ReadAllText(path));
        }

        private static void AssertSafeSdkConsumersHaveNoBindingManifest(string root)
        {
            var bindingsDirectory = Path.Combine(root, "bindings");
            foreach (var modId in new[]
                     {
                         "io.github.furroxide.topiaforge.gravitygun",
                         "io.github.furroxide.topiaforge.sandbox",
                         "io.github.furroxide.topiaforge.zombies"
                     })
            {
                Assert(
                    !File.Exists(Path.Combine(bindingsDirectory, modId + ".gamebindings.json")),
                    modId + " is a safe SDK consumer and must not declare native game bindings");
            }
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

        private static void AssertRetiredFormatsRejected(
            SurfaceSnapshot baseline,
            BindingManifest manifest)
        {
            AssertFormatRejected(
                () => SurfaceSnapshot.Parse(
                    baseline.ToCanonicalJson().Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1")),
                "surface snapshots must reject schemaVersion 1");
            AssertFormatRejected(
                () => BindingManifest.Parse(
                    manifest.ToCanonicalJson().Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1")),
                "binding manifests must reject schemaVersion 1");
        }

        private static void AssertFormatRejected(Action action, string message)
        {
            try
            {
                action();
            }
            catch (FormatException)
            {
                return;
            }

            Assert(false, message);
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

        private static void AssertSimpleNameWalkValidatesMembers()
        {
            var manifest = new BindingManifest { ModId = "test.simple", ModName = "Simple-name test" };
            var binding = new GameBinding
            {
                Id = "test.simple.tick",
                Kind = BindingKind.Method,
                Assembly = "GameCode",
                DeclaringType = "Widget",
                Member = "Tick",
                ReturnType = "System.Boolean",
                MatchMode = MatchMode.SimpleNameWalk,
                Criticality = Criticality.Critical,
                Feature = "Simple-name member validation"
            };
            binding.Parameters.Add(new ParameterSpec("System.Int32", constrained: true));
            manifest.Bindings.Add(binding);

            SurfaceSnapshot SnapshotWith(MethodSurface? method)
            {
                var snapshot = new SurfaceSnapshot();
                snapshot.SimpleNameCounts["GameCode|Widget"] = 1;
                var type = new TypeSurface
                {
                    TypeKey = "GameCode|Game.Namespace.Widget",
                    Assembly = "GameCode",
                    FullName = "Game.Namespace.Widget",
                    SimpleName = "Widget",
                    Status = SurfaceStatus.Resolved
                };
                if (method != null)
                {
                    type.Methods.Add(method);
                }

                snapshot.Types[type.TypeKey] = type;
                return snapshot;
            }

            var healthyMethod = new MethodSurface { Name = "Tick", ReturnType = "System.Boolean" };
            healthyMethod.Parameters.Add("System.Int32");
            var healthy = SurfaceDiffer.ResolveManifests(new[] { manifest }, SnapshotWith(healthyMethod));
            Assert(healthy.Findings.Count == 0 && healthy.VerifiableBindings == 1,
                "a SimpleNameWalk member should resolve against a captured matching type surface");

            var missing = SurfaceDiffer.ResolveManifests(new[] { manifest }, SnapshotWith(method: null));
            Assert(missing.Findings.Any(f => f.BindingId == binding.Id && f.ChangeKind == ChangeKind.MissingMember),
                "SimpleNameWalk must report a missing member even when the simple type name still exists");

            var wrongReturn = new MethodSurface { Name = "Tick", ReturnType = "System.String" };
            wrongReturn.Parameters.Add("System.Int32");
            var mismatched = SurfaceDiffer.ResolveManifests(new[] { manifest }, SnapshotWith(wrongReturn));
            Assert(mismatched.Findings.Any(f =>
                    f.BindingId == binding.Id && f.ChangeKind == ChangeKind.SignatureMismatch),
                "SimpleNameWalk must validate the selected member signature, not just its name");

            binding.ReturnType = "System.Collections.Generic.IReadOnlyList<System.String>";
            var legacyGenericReturn = new MethodSurface
            {
                Name = "Tick",
                ReturnType = "IReadOnlyList`1[System.String]"
            };
            legacyGenericReturn.Parameters.Add("System.Int32");
            var equivalentGeneric = SurfaceDiffer.ResolveManifests(
                new[] { manifest },
                SnapshotWith(legacyGenericReturn));
            Assert(equivalentGeneric.Findings.Count == 0,
                "equivalent normalized and reflection-style generic type names should match");

            var wrongGenericReturn = new MethodSurface
            {
                Name = "Tick",
                ReturnType = "IReadOnlyList`1[System.Int32]"
            };
            wrongGenericReturn.Parameters.Add("System.Int32");
            var genericMismatch = SurfaceDiffer.ResolveManifests(
                new[] { manifest },
                SnapshotWith(wrongGenericReturn));
            Assert(genericMismatch.Findings.Any(f =>
                    f.BindingId == binding.Id && f.ChangeKind == ChangeKind.SignatureMismatch),
                "member signature validation must retain generic arguments across supported type-name spellings");

            var legacy = new SurfaceSnapshot();
            legacy.SimpleNameCounts["GameCode|Widget"] = 1;
            var indeterminate = SurfaceDiffer.ResolveManifests(new[] { manifest }, legacy);
            Assert(indeterminate.IndeterminateBindings == 1,
                "a count-only legacy snapshot must be indeterminate instead of falsely passing a member binding");
        }

        private static void AssertLinkedCompileSourcesAreAudited()
        {
            var root = Path.Combine(Path.GetTempPath(), "TopiaForgeGameCompatLinked-" + Guid.NewGuid().ToString("N"));
            var bindings = Path.Combine(root, "bindings");
            const string modId = "io.github.furroxide.topiaforge.linked";
            var mod = Path.Combine(root, "mods", "TopiaForge.Linked");
            var shared = Path.Combine(root, "mods", "Shared");
            Directory.CreateDirectory(bindings);
            Directory.CreateDirectory(mod);
            Directory.CreateDirectory(shared);

            try
            {
                var manifest = new BindingManifest { ModId = modId, ModName = "Linked source" };
                manifest.Bindings.Add(new GameBinding
                {
                    Id = "test.linked.field",
                    Kind = BindingKind.Field,
                    DeclaringType = "LinkedGameType",
                    Member = "LinkedMember",
                    MatchMode = MatchMode.StaticFullName,
                    Feature = "Linked source discovery"
                });
                File.WriteAllText(
                    Path.Combine(bindings, modId + ".gamebindings.json"),
                    manifest.ToCanonicalJson());
                File.WriteAllText(
                    Path.Combine(mod, "TopiaForge.Linked.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                    "<Compile Include=\"..\\Shared\\LinkedBridge.cs\" Link=\"Shared\\LinkedBridge.cs\" />" +
                    "</ItemGroup></Project>");
                var linkedSource = Path.Combine(shared, "LinkedBridge.cs");
                File.WriteAllText(
                    linkedSource,
                    "class LinkedBridge { void Bind() { " +
                    "System.Type.GetType(\"LinkedGameType, GameCode\")?.GetField(\"LinkedMember\"); } }");

                var healthy = GameReflectionAuditor.Audit(root);
                Assert(healthy.Count == 0,
                    "source-linked Compile Include files should satisfy declared type/member audit bindings");

                File.WriteAllText(linkedSource, "class LinkedBridge { }");
                var broken = GameReflectionAuditor.Audit(root);
                Assert(broken.Any(finding =>
                        finding.ModId == modId &&
                        finding.Kind == "stale" &&
                        finding.Detail.Contains("LinkedMember", StringComparison.Ordinal)),
                    "linked-source regression must fail when the linked member reference is actually removed");
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

        private static string Normalize(string value) => value.Replace("\r\n", "\n");

        private static string Short(string hash) => hash.Length >= 12 ? hash.Substring(0, 12) : hash;

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TopiaForge.slnx")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("GameCompat: could not locate repo root (TopiaForge.slnx) from " + AppContext.BaseDirectory);
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
