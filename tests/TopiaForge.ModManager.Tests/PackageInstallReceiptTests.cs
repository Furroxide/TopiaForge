using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class PackageInstallReceiptTests
    {
        public static void Run(string root)
        {
            TestLauncherStyleReceiptPassesRuntimeScan(root);

            var directory = Path.Combine(root, "install-receipt", "package");
            Directory.CreateDirectory(directory);
            var archive = Path.Combine(root, "install-receipt", "example.topiaforgemod");
            File.WriteAllText(archive, "archive bytes");
            var manifest = new ModManifest
            {
                SchemaVersion = 4,
                Id = "example.mod",
                Name = "Example",
                Version = "1.0.0",
                EntryAssembly = "Example.dll",
                EntryType = "Example.Mod"
            };
            File.WriteAllText(Path.Combine(directory, "topiaforge.mod.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "Example.dll"), "managed fixture placeholder");
            Directory.CreateDirectory(Path.Combine(directory, "content"));
            File.WriteAllText(Path.Combine(directory, "content", "data.txt"), "original");

            var receipt = PackageInstallReceipt.Create(archive, directory, manifest);
            JsonUtil.SaveFile(Path.Combine(directory, PackageInstallReceipt.FileName), receipt);

            Assert(receipt.SchemaVersion == 2, "new receipts should use schema v2");
            Assert(receipt.Source == PackageInstallReceipt.LocalSource,
                "direct installs should record local provenance separately from sourceFile");
            Assert(receipt.SourceFile == Path.GetFileName(archive),
                "sourceFile should retain only the archive basename");
            Assert(PackageInstallReceipt.Verify(directory, manifest).Count == 0,
                "newly receipted bytes should verify");

            TestProvenanceRoundTripAndRedaction(root, archive, directory, manifest);

            File.WriteAllText(Path.Combine(directory, "content", "data.txt"), "tampered");
            var errors = PackageInstallReceipt.Verify(directory, manifest);
            Assert(errors.Any(error => error.Contains("changed", StringComparison.OrdinalIgnoreCase)),
                "post-install byte changes should be detected");

            File.WriteAllText(Path.Combine(directory, "content", "data.txt"), "original");
            File.WriteAllText(Path.Combine(directory, "content", "extra.txt"), "unreceipted");
            errors = PackageInstallReceipt.Verify(directory, manifest);
            Assert(errors.Any(error => error.Contains("unreceipted", StringComparison.OrdinalIgnoreCase)),
                "new post-install files should be detected");
            File.Delete(Path.Combine(directory, "content", "extra.txt"));

            receipt.ValidatorVersion = "future";
            receipt.Files.Single(file => file.Path == "Example.dll").Critical = false;
            JsonUtil.SaveFile(Path.Combine(directory, PackageInstallReceipt.FileName), receipt);
            errors = PackageInstallReceipt.Verify(directory, manifest);
            Assert(errors.Any(error => error.Contains("validatorVersion", StringComparison.OrdinalIgnoreCase)),
                "unsupported receipt validators should fail closed");
            Assert(errors.Any(error => error.Contains("critical-file", StringComparison.OrdinalIgnoreCase)),
                "critical-file classification tampering should be detected");

            receipt.ValidatorVersion = PackageInstallReceipt.CurrentValidatorVersion;
            receipt.Files.Single(file => file.Path == "Example.dll").Critical = true;
            receipt.Trust = "signature-trusted";
            JsonUtil.SaveFile(Path.Combine(directory, PackageInstallReceipt.FileName), receipt);
            errors = PackageInstallReceipt.Verify(directory, manifest);
            Assert(errors.Any(error => error.Contains("trust result", StringComparison.OrdinalIgnoreCase)),
                "trust values outside the shared launcher/runtime vocabulary should fail closed");

            receipt.Trust = PackageInstallReceipt.LocalUnverifiedTrust;
            receipt.Source = "registry:https://user:password@example.test?token=secret";
            JsonUtil.SaveFile(Path.Combine(directory, PackageInstallReceipt.FileName), receipt);
            errors = PackageInstallReceipt.Verify(directory, manifest);
            Assert(errors.Any(error => error.Contains("source provenance", StringComparison.OrdinalIgnoreCase)),
                "unsafe source provenance should fail closed");

            receipt.Source = PackageInstallReceipt.LocalSource;
            receipt.InstalledAtUtc = "not-a-timestamp";
            receipt.Files.Reverse();
            JsonUtil.SaveFile(Path.Combine(directory, PackageInstallReceipt.FileName), receipt);
            errors = PackageInstallReceipt.Verify(directory, manifest);
            Assert(errors.Any(error => error.Contains("installedAtUtc", StringComparison.OrdinalIgnoreCase)),
                "invalid installation timestamps should fail closed");
            Assert(errors.Any(error => error.Contains("not sorted", StringComparison.OrdinalIgnoreCase)),
                "receipt inventories should remain deterministically sorted");

            receipt.InstalledAtUtc = DateTime.UtcNow.ToString("O");
            receipt.Files = receipt.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();

            receipt.SchemaVersion = 1;
            receipt.Source = string.Empty;
            var receiptPath = Path.Combine(directory, PackageInstallReceipt.FileName);
            JsonUtil.SaveFile(receiptPath, receipt);
            var legacyDocument = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
            legacyDocument.Remove("source");
            File.WriteAllText(receiptPath, legacyDocument.ToJsonString());
            var receiptBackupPath = receiptPath + JsonUtil.BackupSuffix;
            if (File.Exists(receiptBackupPath))
            {
                File.Delete(receiptBackupPath);
            }
            errors = PackageInstallReceipt.Verify(directory, manifest);
            Assert(errors.Count == 0,
                "schema-v1 receipts without source provenance should remain readable: " +
                string.Join(" | ", errors));
        }

        private static void TestProvenanceRoundTripAndRedaction(
            string root,
            string archive,
            string directory,
            ModManifest manifest)
        {
            var receipt = PackageInstallReceipt.Create(
                archive,
                directory,
                manifest,
                "registry:Official.Source",
                PackageInstallReceipt.Sha256VerifiedTrust);
            var roundTripPath = Path.Combine(root, "install-receipt", "receipt-roundtrip.json");
            JsonUtil.SaveFile(roundTripPath, receipt);
            var loaded = JsonUtil.LoadFile(roundTripPath, new PackageInstallReceipt());
            Assert(loaded.Source == "registry:official.source",
                "registry source ids should round-trip in normalized form");
            Assert(loaded.SourceFile == Path.GetFileName(archive),
                "source provenance must not replace sourceFile");
            Assert(loaded.Trust == PackageInstallReceipt.Sha256VerifiedTrust,
                "the stable verified trust result should round-trip");

            const string secret = "super-secret-token";
            var redacted = PackageInstallReceipt.Create(
                archive,
                directory,
                manifest,
                "registry:https://user:" + secret + "@example.test?token=" + secret);
            Assert(redacted.Source == "registry",
                "unsafe registry identifiers should degrade to their non-secret origin kind");
            Assert(redacted.Source.Length <= 160 && !redacted.Source.Contains(secret, StringComparison.Ordinal),
                "source provenance should be bounded and redact credentials or query material");
            JsonUtil.SaveFile(roundTripPath, redacted);
            Assert(!File.ReadAllText(roundTripPath).Contains(secret, StringComparison.Ordinal),
                "serialized source provenance should not retain rejected secret material");

            var invalidTrustRejected = false;
            try
            {
                PackageInstallReceipt.Create(
                    archive,
                    directory,
                    manifest,
                    PackageInstallReceipt.LocalSource,
                    "signature-trusted");
            }
            catch (ArgumentException)
            {
                invalidTrustRejected = true;
            }

            Assert(invalidTrustRejected, "receipt creation should reject unknown trust values");
        }

        private static void TestLauncherStyleReceiptPassesRuntimeScan(string root)
        {
            const string fixtureAssembly = "TopiaForge.ValidTestMod.dll";
            var paths = new ManagerPaths(Path.Combine(root, "launcher-receipt", "BepInEx"));
            paths.EnsureCreated();
            var manifest = new ModManifest
            {
                SchemaVersion = 4,
                Id = "launcher.receipt",
                Name = "Launcher receipt",
                Version = "1.0.0",
                Author = new ModAuthor { Name = "TopiaForge" },
                EntryAssembly = fixtureAssembly,
                EntryType = "TopiaForge.ValidTestMod.ValidMod",
                SupportedGameVersionRange = "*",
                SupportedLoaderVersionRange = "*",
                SupportedSdkVersionRange = "*"
            };
            var packageRoot = paths.GetPackagePath(manifest.Id, manifest.Version);
            Directory.CreateDirectory(packageRoot);
            JsonUtil.SaveFile(Path.Combine(packageRoot, "topiaforge.mod.json"), manifest);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, fixtureAssembly),
                Path.Combine(packageRoot, fixtureAssembly),
                true);
            Directory.CreateDirectory(Path.Combine(packageRoot, "content"));
            File.WriteAllText(Path.Combine(packageRoot, "content", "data.txt"), "launcher payload");

            var archive = Path.Combine(root, "launcher-receipt", "launcher.receipt-1.0.0.topiaforgemod");
            File.WriteAllText(archive, "launcher source archive fixture");
            var files = Directory.GetFiles(packageRoot, "*", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(packageRoot, path), StringComparer.Ordinal)
                .Select(path =>
                {
                    var relative = Path.GetRelativePath(packageRoot, path).Replace(Path.DirectorySeparatorChar, '/');
                    return new PackageFileReceipt
                    {
                        Path = relative,
                        Length = new FileInfo(path).Length,
                        Sha256 = ComputeSha256(path),
                        Critical = string.Equals(relative, "topiaforge.mod.json", StringComparison.Ordinal) ||
                                   string.Equals(relative, fixtureAssembly, StringComparison.Ordinal)
                    };
                })
                .ToList();
            var launcherReceipt = new PackageInstallReceipt
            {
                SchemaVersion = PackageInstallReceipt.CurrentSchemaVersion,
                ModId = manifest.Id,
                Version = manifest.Version,
                SourceFile = Path.GetFileName(archive),
                Source = PackageInstallReceipt.LocalSource,
                SourceSha256 = ComputeSha256(archive),
                InstalledAtUtc = DateTime.UtcNow.ToString("O"),
                ValidatorVersion = PackageInstallReceipt.CurrentValidatorVersion,
                Trust = "local-unverified",
                Files = files
            };
            JsonUtil.SaveFile(Path.Combine(packageRoot, PackageInstallReceipt.FileName), launcherReceipt);

            Assert(PackageInstallReceipt.Verify(packageRoot, manifest).Count == 0,
                "a launcher-authored current receipt should pass the C# integrity verifier");
            var state = new ManagerState();
            state.Upsert(manifest, enabled: true, restartRequired: false);
            var scanned = new ModRegistry().Scan(paths, state).Single();
            Assert(scanned.IsValid && scanned.Manifest?.Id == manifest.Id,
                "a launcher-receipted package should pass the complete runtime registry scan");
        }

        private static string ComputeSha256(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(input)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Package receipt test failed: " + message);
            }
        }
    }
}
