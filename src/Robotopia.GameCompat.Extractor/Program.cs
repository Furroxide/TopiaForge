using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Robotopia.GameCompat;

namespace Robotopia.GameCompat.Extractor
{
    internal static class Program
    {
        private const string ExtractorVersion = "1.1.0";
        private const int MaxSurfaceSnapshotBytes = 16 * 1024 * 1024;

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    return Usage();
                }

                var command = args[0];
                var options = ParseOptions(args.Skip(1));

                switch (command)
                {
                    case "extract":
                        return Extract(options);
                    case "baseline":
                        return Baseline(options);
                    case "verify":
                        return Verify(options);
                    case "audit":
                        return Audit(options);
                    case "help":
                    case "--help":
                    case "-h":
                        return Usage();
                    default:
                        Console.Error.WriteLine("Unknown command '" + command + "'.");
                        return Usage();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("gamecompat: " + ex.Message);
                return 3;
            }
        }

        private static int Usage()
        {
            Console.WriteLine(@"Robotopia GameCompat extractor — detect game-update breaking changes in the mod reflection surface.

Usage:
  gamecompat extract  [--managed <dir>] [--out <file>]
  gamecompat baseline [--managed <dir>] [--out <file>]
  gamecompat verify   [--managed <dir>] [--against <baseline>] [--format text|json]
  gamecompat audit    [--strict]

Commands:
  extract   Snapshot the current game surface (for the bindings the manifests reference) to JSON.
  baseline  Capture/refresh the checked-in known-good baseline; refuses to write a partial (unreadable) capture,
            and prints the surface diff vs the previous baseline for review.
  verify    Resolve every manifest binding against the installed game and (if a baseline exists) diff against it.
            Exit code 1 when a critical binding is broken. Skips cleanly (exit 0) when no game install is found.
  audit     Offline source-vs-manifest drift check (no game DLL needed).

Managed dir resolution order: --managed, $RobotopiaManagedDir, the platform-specific layout under
$RobotopiaGameDir, then the default launcher install path.");
            return 0;
        }

        // ---- commands ----
        private static int Extract(Options options)
        {
            var managed = RequireManagedDir(options);
            if (managed == null)
            {
                return 0; // no install: nothing to extract, treated as a clean skip
            }

            var repoRoot = RequireRepoRoot();
            var manifests = ManifestLoader.Manifests(repoRoot);
            var snapshot = Capture(managed, manifests);
            WriteOrPrint(options.Out, snapshot.ToCanonicalJson());
            return 0;
        }

        private static int Baseline(Options options)
        {
            var managed = RequireManagedDir(options);
            if (managed == null)
            {
                Console.Error.WriteLine("baseline: no game install found. Point --managed at a Managed dir with GameCode.dll.");
                return 2;
            }

            var repoRoot = RequireRepoRoot();
            var manifests = ManifestLoader.Manifests(repoRoot);
            var snapshot = Capture(managed, manifests);

            var unreadable = snapshot.UnreadableTypes().ToList();
            if (unreadable.Count > 0)
            {
                Console.Error.WriteLine("baseline: refusing to write — " + unreadable.Count +
                    " type(s) could not be read in this environment (missing referenced assemblies). A partial capture is not a valid known-good baseline:");
                foreach (var key in unreadable)
                {
                    Console.Error.WriteLine("  - " + key);
                }

                return 2;
            }

            var outPath = options.Out ?? Path.Combine(repoRoot, ManifestLoader.BaselineRelativePath);

            // Show what a refresh actually changes, so a baseline bump is a reviewed act, not an opaque blob.
            if (TryReadSurfaceSnapshot(outPath, out var previous))
            {
                var diff = SurfaceDiffer.DiffSurfaces(previous, snapshot, manifests);
                Console.WriteLine("Baseline refresh — changes vs the previous baseline:");
                PrintReport(diff);
                if (previous.ComputeContentHash() == snapshot.ComputeContentHash())
                {
                    Console.WriteLine("(surface content is identical; only provenance metadata will change)");
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, snapshot.ToCanonicalJson());
            Console.WriteLine("Wrote baseline: " + outPath);
            Console.WriteLine("  surface hash : " + snapshot.ComputeContentHash());
            Console.WriteLine("  gamecode mvid: " + snapshot.GameCodeMvid + " (advisory)");
            Console.WriteLine("  types        : " + snapshot.Types.Count + ", simple-name lookups: " + snapshot.SimpleNameCounts.Count);
            return 0;
        }

        private static int Verify(Options options)
        {
            var managed = RequireManagedDir(options);
            if (managed == null)
            {
                if (options.Format == "json")
                {
                    Console.WriteLine(new JsonObject().Set("status", "skipped").Set("reason", "no game install detected").ToCanonical());
                }
                else
                {
                    Console.WriteLine("GameCode compat: skipped (no game install detected).");
                }

                return 0;
            }

            var repoRoot = RequireRepoRoot();
            var manifests = ManifestLoader.Manifests(repoRoot);
            var candidate = Capture(managed, manifests);

            var resolve = SurfaceDiffer.ResolveManifests(manifests, candidate);

            var baselinePath = options.Against ?? Path.Combine(repoRoot, ManifestLoader.BaselineRelativePath);
            CompatReport? diff = null;
            if (TryReadSurfaceSnapshot(baselinePath, out var baseline))
            {
                diff = SurfaceDiffer.DiffSurfaces(baseline, candidate, manifests);
            }

            if (options.Format == "json" || options.Out != null)
            {
                var root = new JsonObject()
                    .Set("status", resolve.HasBreakingChanges ? "broken" : "ok")
                    .Set("gameVersionLabel", candidate.GameVersionLabel)
                    .Set("gameVersion", candidate.GameVersion)
                    .Set("surfaceHash", candidate.ComputeContentHash())
                    .Set("gameCodeMvid", candidate.GameCodeMvid)
                    .Set("resolve", resolve.ToJson());
                if (diff != null)
                {
                    root.Set("diff", diff.ToJson());
                }

                var json = root.ToCanonical();
                if (options.Out != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Out))!);
                    File.WriteAllText(options.Out, json);
                    Console.WriteLine("Wrote compat report: " + options.Out + " (status=" + (resolve.HasBreakingChanges ? "broken" : "ok") + ")");
                }
                else
                {
                    Console.WriteLine(json);
                }
            }
            else
            {
                Console.WriteLine("GameCode compatibility — resolving " + resolve.TotalBindings + " declared binding(s) against the installed game:");
                PrintReport(resolve);
                if (diff != null && diff.Findings.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Changes vs the checked-in baseline:");
                    PrintReport(diff);
                }
            }

            return resolve.HasBreakingChanges ? 1 : 0;
        }

        private static int Audit(Options options)
        {
            var repoRoot = RequireRepoRoot();
            var findings = GameReflectionAuditor.Audit(repoRoot);

            // Manifest self-validation (no DLL needed) piggybacks on the audit.
            var manifestProblems = new List<string>();
            foreach (var (manifest, path) in ManifestLoader.LoadAll(repoRoot))
            {
                foreach (var problem in manifest.Validate())
                {
                    manifestProblems.Add(Path.GetFileName(path) + ": " + problem);
                }
            }

            foreach (var problem in manifestProblems)
            {
                Console.WriteLine("[manifest] " + problem);
            }

            foreach (var finding in findings.OrderBy(f => f.ModId, StringComparer.Ordinal).ThenBy(f => f.Kind, StringComparer.Ordinal))
            {
                Console.WriteLine("[" + finding.Kind + "] " + finding.ModId + ": " + finding.Detail);
            }

            var undeclared = findings.Count(f => f.Kind == "undeclared");
            Console.WriteLine();
            Console.WriteLine("Audit: " + manifestProblems.Count + " manifest problem(s), " +
                undeclared + " undeclared binding(s), " + findings.Count(f => f.Kind == "stale") + " stale binding(s).");

            // Manifest problems and undeclared static bindings are hard errors; stale is advisory unless --strict.
            if (manifestProblems.Count > 0 || undeclared > 0)
            {
                return 1;
            }

            return options.Strict && findings.Count > 0 ? 1 : 0;
        }

        // ---- helpers ----
        private static SurfaceSnapshot Capture(string managedDir, IReadOnlyList<BindingManifest> manifests)
        {
            using var reader = new GameCodeSurfaceReader(managedDir);
            var versionLabel = GameVersionLabelReader.Read(managedDir);
            var gameVersion = GameVersionLabelReader.ReadCanonicalVersion(managedDir);
            var capturedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            return reader.Extract(manifests, ExtractorVersion, versionLabel, capturedUtc, gameVersion);
        }

        private static void PrintReport(CompatReport report)
        {
            Console.WriteLine("  coverage: " + report.VerifiableBindings + " verifiable, " +
                report.UncheckableBindings + " uncheckable-offline, " + report.IndeterminateBindings + " indeterminate; " +
                report.ErrorCount + " error(s), " + report.WarningCount + " warning(s).");

            foreach (var finding in report.Findings
                .OrderByDescending(f => (int)f.Severity)
                .ThenBy(f => f.ModId, StringComparer.Ordinal))
            {
                if (finding.Severity == Severity.Info && finding.ChangeKind == ChangeKind.Indeterminate)
                {
                    continue; // keep the summary readable; indeterminate is counted above
                }

                var tag = finding.Severity == Severity.Error ? "ERROR" : finding.Severity == Severity.Warning ? "WARN " : "info ";
                Console.WriteLine("  [" + tag + "] " + finding.ModId + " / " + finding.Feature + " (" + finding.ChangeKind + "): " + finding.Detail);
            }
        }

        private static void WriteOrPrint(string? outPath, string content)
        {
            if (outPath == null)
            {
                Console.WriteLine(content);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, content);
            Console.WriteLine("Wrote " + outPath);
        }

        private static bool TryReadSurfaceSnapshot(string path, out SurfaceSnapshot snapshot)
        {
            try
            {
                snapshot = SurfaceSnapshot.Parse(ExtractorFileIo.ReadStableUtf8(
                    path,
                    MaxSurfaceSnapshotBytes,
                    "GameCode surface snapshot"));
                return true;
            }
            catch (FileNotFoundException)
            {
                snapshot = null!;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                snapshot = null!;
                return false;
            }
        }

        private static string RequireRepoRoot() =>
            ManifestLoader.FindDataRoot() ?? throw new InvalidOperationException("could not locate bindings/ (neither a repo root with RobotopiaModManager.slnx nor a bundled bindings/ next to the tool)");

        private static string? RequireManagedDir(Options options)
        {
            foreach (var candidate in ManagedDirCandidates(options.Managed))
            {
                if (candidate != null && Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "GameCode.dll")))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<string?> ManagedDirCandidates(string? explicitDir)
        {
            yield return explicitDir;
            yield return Environment.GetEnvironmentVariable("RobotopiaManagedDir");

            var gameDir = Environment.GetEnvironmentVariable("RobotopiaGameDir");
            if (!string.IsNullOrEmpty(gameDir))
            {
                yield return Path.Combine(gameDir, "Robotopia_Data", "Managed");
                // macOS installs are an app bundle; the managed assemblies sit inside Contents/.
                yield return Path.Combine(gameDir, "Contents", "Resources", "Data", "Managed");
                // The launcher and GameLayout also treat the directory containing Robotopia.app as a game root.
                yield return Path.Combine(gameDir, "Robotopia.app", "Contents", "Resources", "Data", "Managed");
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return Path.Combine(localAppData, "Tomato Cake", "launcher", "Robotopia", "Robotopia_Data", "Managed");
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, "Library", "Application Support", "Tomato Cake", "launcher",
                    "Robotopia.app", "Contents", "Resources", "Data", "Managed");
            }
        }

        private sealed class Options
        {
            public string? Managed;
            public string? Out;
            public string? Against;
            public string Format = "text";
            public bool Strict;
        }

        private static Options ParseOptions(IEnumerable<string> args)
        {
            var options = new Options();
            var list = args.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                switch (list[i])
                {
                    case "--managed":
                        options.Managed = Next(list, ref i);
                        break;
                    case "--out":
                        options.Out = Next(list, ref i);
                        break;
                    case "--against":
                        options.Against = Next(list, ref i);
                        break;
                    case "--format":
                        options.Format = Next(list, ref i) ?? "text";
                        break;
                    case "--strict":
                        options.Strict = true;
                        break;
                    default:
                        throw new ArgumentException("unknown option '" + list[i] + "'");
                }
            }

            return options;
        }

        private static string? Next(List<string> list, ref int i)
        {
            if (i + 1 >= list.Count)
            {
                throw new ArgumentException("option '" + list[i] + "' expects a value");
            }

            return list[++i];
        }
    }
}
