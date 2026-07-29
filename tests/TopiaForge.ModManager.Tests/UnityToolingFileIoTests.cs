using System;
using System.IO;
using System.Linq;
using System.Text;
using TopiaForge.UgcCompanion.Editor;
using TopiaForge.VpmResolver;
using TopiaForge.WorldCompanion.Editor;

namespace TopiaForge.ModManager.Tests
{
    internal static class UnityToolingFileIoTests
    {
        public static void Run(string root)
        {
            var directory = Path.Combine(root, "unity-tooling-file-io");
            Directory.CreateDirectory(directory);
            WorldConfigSchemaRejectsPriorFormats();
            UgcSeedSchemaRejectsPriorFormats();
            VpmPackageIdsRejectRetiredRoots();
            StableReadersRejectHostileInputs(directory);
            WorldPublicationRollsBackAsOneTransaction(directory);
            SeedWritesAreAtomicAndRollbackSafe(directory);
            SourceContractsRemainWired();
            Console.WriteLine("UnityToolingFileIoTests passed.");
        }

        private static void WorldConfigSchemaRejectsPriorFormats()
        {
            WorldCompanionFileIo.RequireCurrentWorldConfigSchema(2);
            AssertThrows<InvalidDataException>(
                () => WorldCompanionFileIo.RequireCurrentWorldConfigSchema(1),
                "world config schemaVersion 1 must be rejected");
            AssertThrows<InvalidDataException>(
                () => WorldCompanionFileIo.RequireCurrentWorldConfigSchema(0),
                "world config with no schemaVersion must be rejected");
        }

        private static void UgcSeedSchemaRejectsPriorFormats()
        {
            UgcCompanionSeedFileIo.RequireCurrentSeedSchema(2);
            AssertThrows<InvalidDataException>(
                () => UgcCompanionSeedFileIo.RequireCurrentSeedSchema(1),
                "UGC seed schemaVersion 1 must be rejected");
            AssertThrows<InvalidDataException>(
                () => UgcCompanionSeedFileIo.RequireCurrentSeedSchema(0),
                "UGC seed with no schemaVersion must be rejected");
        }

        private static void VpmPackageIdsRejectRetiredRoots()
        {
            Assert(
                VpmSafeFileReader.IsValidPackageId("io.github.furroxide.topiaforge.world-companion"),
                "canonical TopiaForge VPM package id should be valid");
            foreach (var retired in new[]
            {
                "robo" + "topia.example",
                "com." + "robo" + "topia.example",
                "quantum" + "works.example",
            })
            {
                Assert(!VpmSafeFileReader.IsValidPackageId(retired),
                    "retired VPM package root must be rejected");
            }

            Assert(!VpmSafeFileReader.IsValidPackageId("Uppercase.Package"),
                "Unity package ids must use lowercase reverse-domain syntax");
            foreach (var malformed in new[] { "single", "a.", "a..b", "a_-b", ".a.b" })
            {
                Assert(!VpmSafeFileReader.IsValidPackageId(malformed),
                    "Unity package ids must contain non-empty reverse-domain segments");
            }

            var maximumLength = "a." + new string('b', 212);
            Assert(VpmSafeFileReader.IsValidPackageId(maximumLength),
                "Unity package ids at the 214-character contract limit should be valid");
            Assert(!VpmSafeFileReader.IsValidPackageId(maximumLength + "c"),
                "Unity package ids beyond the 214-character contract limit must be rejected");
        }

        private static void StableReadersRejectHostileInputs(string directory)
        {
            var valid = Path.Combine(directory, "valid.json");
            File.WriteAllText(valid, "{\"value\":1}", new UTF8Encoding(false));
            Assert(WorldCompanionFileIo.ReadStableUtf8(valid, 1024, "world config") == "{\"value\":1}",
                "world config reader should accept bounded strict UTF-8");
            Assert(VpmSafeFileReader.ReadStableUtf8(valid, 1024, "VPM manifest") == "{\"value\":1}",
                "VPM reader should accept bounded strict UTF-8");
            Assert(UgcCompanionSeedFileIo.ReadStableUtf8(valid, 1024, "UGC seed") == "{\"value\":1}",
                "UGC seed reader should accept bounded strict UTF-8");

            var invalidUtf8 = Path.Combine(directory, "invalid-utf8.json");
            File.WriteAllBytes(invalidUtf8, new byte[] { 0xC3, 0x28 });
            AssertThrows<InvalidDataException>(
                () => WorldCompanionFileIo.ReadStableUtf8(invalidUtf8, 1024, "world config"),
                "world config must reject malformed UTF-8");
            AssertThrows<InvalidDataException>(
                () => VpmSafeFileReader.ReadStableUtf8(invalidUtf8, 1024, "VPM manifest"),
                "VPM manifest must reject malformed UTF-8");
            AssertThrows<InvalidDataException>(
                () => UgcCompanionSeedFileIo.ReadStableUtf8(invalidUtf8, 1024, "UGC seed"),
                "UGC seed must reject malformed UTF-8");

            var oversized = Path.Combine(directory, "oversized.json");
            File.WriteAllBytes(oversized, new byte[1025]);
            AssertThrows<InvalidDataException>(
                () => WorldCompanionFileIo.ReadStableUtf8(oversized, 1024, "world config"),
                "world config must be bounded before allocation");

            AssertThrows<InvalidDataException>(
                () => VpmSafeFileReader.ReadStableUtf8(directory, 1024, "VPM manifest"),
                "directories must not be accepted as manifest files");

            var link = Path.Combine(directory, "linked.json");
            if (TryCreateSymbolicLink(link, valid))
            {
                AssertThrows<InvalidDataException>(
                    () => WorldCompanionFileIo.ReadStableUtf8(link, 1024, "world config"),
                    "world config must reject symbolic links");
                AssertThrows<InvalidDataException>(
                    () => VpmSafeFileReader.ReadStableUtf8(link, 1024, "VPM manifest"),
                    "VPM manifest must reject symbolic links");
                AssertThrows<InvalidDataException>(
                    () => UgcCompanionSeedFileIo.ReadStableUtf8(link, 1024, "UGC seed"),
                    "UGC seed must reject symbolic links");
            }

            var race = Path.Combine(directory, "replacement-race.json");
            File.WriteAllText(race, "{\"value\":1}", new UTF8Encoding(false));
            AssertThrows<IOException>(
                () => WorldCompanionFileIo.ReadStableUtf8(
                    race,
                    1024,
                    "world config",
                    () => File.WriteAllText(race, "{\"value\":2}", new UTF8Encoding(false))),
                "world config reader must detect same-length replacement races");
            File.WriteAllText(race, "{\"value\":1}", new UTF8Encoding(false));
            AssertThrows<IOException>(
                () => VpmSafeFileReader.ReadStableUtf8(
                    race,
                    1024,
                    "VPM manifest",
                    () => File.WriteAllText(race, "{\"value\":2}", new UTF8Encoding(false))),
                "VPM reader must detect same-length replacement races");
            File.WriteAllText(race, "{\"value\":1}", new UTF8Encoding(false));
            AssertThrows<IOException>(
                () => UgcCompanionSeedFileIo.ReadStableUtf8(
                    race,
                    1024,
                    "UGC seed",
                    () => File.WriteAllText(race, "{\"value\":2}", new UTF8Encoding(false))),
                "UGC seed reader must detect same-length replacement races");
        }

        private static void WorldPublicationRollsBackAsOneTransaction(string directory)
        {
            var source = Path.Combine(directory, "built.bundle");
            var bundle = Path.Combine(directory, "world.bundle");
            var manifest = Path.Combine(directory, "world.manifest.json");
            File.WriteAllBytes(source, Encoding.ASCII.GetBytes("new bundle bytes"));
            File.WriteAllText(bundle, "old bundle");
            File.WriteAllText(manifest, "old manifest");

            AssertThrows<InvalidOperationException>(
                () => WorldCompanionFileIo.PublishPairAtomic(
                    source,
                    bundle,
                    manifest,
                    hash => "{\"sha256\":\"" + hash + "\"}",
                    () => throw new InvalidOperationException("injected provenance failure")),
                "a provenance failure should surface to the world build");
            Assert(File.ReadAllText(bundle) == "old bundle" && File.ReadAllText(manifest) == "old manifest",
                "bundle and provenance must both roll back after a partial publication");
            AssertNoTransactionFiles(directory);

            var sha = WorldCompanionFileIo.PublishPairAtomic(
                source,
                bundle,
                manifest,
                hash => "{\"sha256\":\"" + hash + "\"}");
            Assert(File.ReadAllBytes(bundle).SequenceEqual(File.ReadAllBytes(source)),
                "successful publication must expose the exact staged bundle bytes");
            Assert(File.ReadAllText(manifest).Contains(sha, StringComparison.Ordinal),
                "provenance must hash the exact staged bundle that was committed");
            AssertNoTransactionFiles(directory);
        }

        private static void SeedWritesAreAtomicAndRollbackSafe(string directory)
        {
            var seed = Path.Combine(directory, "TopiaForgeUgcCompanion.json");
            File.WriteAllText(seed, "old seed");
            AssertThrows<InvalidOperationException>(
                () => UgcCompanionSeedFileIo.RewriteAtomicUtf8(
                    seed,
                    "old seed",
                    "new seed",
                    1024,
                    "UGC seed",
                    () => { },
                    () => throw new InvalidOperationException("injected post-commit failure")),
                "post-commit seed failures should surface");
            Assert(File.ReadAllText(seed) == "old seed",
                "post-commit failure must restore the previous seed");

            AssertThrows<InvalidDataException>(
                () => UgcCompanionSeedFileIo.RewriteAtomicUtf8(
                    seed, "old seed", new string('x', 1025), 1024, "UGC seed"),
                "oversized seed rewrites must fail before replacement");
            Assert(File.ReadAllText(seed) == "old seed",
                "rejected seed content must preserve the previous file");

            UgcCompanionSeedFileIo.RewriteAtomicUtf8(seed, "old seed", "new seed", 1024, "UGC seed");
            Assert(File.ReadAllText(seed) == "new seed", "valid seed rewrites should commit atomically");

            AssertThrows<IOException>(
                () => UgcCompanionSeedFileIo.RewriteAtomicUtf8(
                    seed,
                    "new seed",
                    "applied seed",
                    1024,
                    "UGC seed",
                    () => File.WriteAllText(seed, "newer CLI seed"),
                    () => { }),
                "seed rewrite must refuse to overwrite a concurrent CLI update");
            Assert(File.ReadAllText(seed) == "newer CLI seed",
                "concurrent seed replacement must win instead of being silently clobbered");
            AssertNoTransactionFiles(directory);
        }

        private static void SourceContractsRemainWired()
        {
            var root = Program.FindRepoRoot();
            var world = File.ReadAllText(Path.Combine(
                root,
                "templates", "TopiaForge.UnityWorldTemplate", "Packages",
                "io.github.furroxide.topiaforge.world-companion", "Editor", "WorldBundleBuilder.cs"));
            var vpm = File.ReadAllText(Path.Combine(
                root,
                "templates", "TopiaForge.UnityWorldTemplate", "Packages",
                "io.github.furroxide.topiaforge.vpm-resolver", "Editor", "VpmResolver.cs"));
            var seed = File.ReadAllText(Path.Combine(
                root,
                "templates", "unity-companion", "Packages",
                "io.github.furroxide.topiaforge.ugc-companion", "Editor", "UgcCompanionSeed.cs"));

            Assert(world.Contains("WorldCompanionFileIo.ReadStableUtf8", StringComparison.Ordinal)
                   && world.Contains("WorldCompanionFileIo.RequireCurrentWorldConfigSchema(config.schemaVersion)", StringComparison.Ordinal)
                   && world.Contains("WorldCompanionFileIo.PublishPairAtomic", StringComparison.Ordinal)
                   && !world.Contains("File.Copy(built, target", StringComparison.Ordinal)
                   && !world.Contains("File.ReadAllText(path)", StringComparison.Ordinal),
                "WorldBundleBuilder must stay wired to secure read and transactional publication helpers");
            Assert(vpm.Contains("VpmSafeFileReader.ReadStableUtf8", StringComparison.Ordinal)
                   && vpm.Contains("VpmSafeFileReader.IsValidPackageId", StringComparison.Ordinal)
                   && !vpm.Contains("PackageIdPattern.IsMatch", StringComparison.Ordinal)
                   && !vpm.Contains("File.ReadAllText", StringComparison.Ordinal),
                "embedded VPM inspection must use the safe regular-file reader");
            Assert(seed.Contains("UgcCompanionSeedFileIo.ReadStableUtf8", StringComparison.Ordinal)
                   && seed.Contains("UgcCompanionSeedFileIo.RequireCurrentSeedSchema(seed.schemaVersion)", StringComparison.Ordinal)
                   && seed.Contains("UgcCompanionSeedFileIo.RewriteAtomicUtf8", StringComparison.Ordinal)
                   && seed.Contains("Debug.LogError", StringComparison.Ordinal)
                   && seed.Contains("throw new InvalidOperationException", StringComparison.Ordinal)
                   && !seed.Contains("File.ReadAllText", StringComparison.Ordinal)
                   && !seed.Contains("File.WriteAllText", StringComparison.Ordinal),
                "UGC seed failures must be secure, atomic, and surfaced");
        }

        private static bool TryCreateSymbolicLink(string link, string target)
        {
            try
            {
                File.CreateSymbolicLink(link, target);
                return true;
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException
                                              || exception is UnauthorizedAccessException
                                              || exception is IOException)
            {
                return false;
            }
        }

        private static void AssertNoTransactionFiles(string directory)
        {
            Assert(!Directory.EnumerateFiles(directory).Any(path =>
                    Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains(".bak-", StringComparison.Ordinal)),
                "successful/rolled-back writes must not leave transaction files");
        }

        private static void AssertThrows<T>(Action action, string message) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }

            throw new InvalidOperationException("Unity tooling file I/O: " + message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Unity tooling file I/O: " + message);
            }
        }
    }
}
