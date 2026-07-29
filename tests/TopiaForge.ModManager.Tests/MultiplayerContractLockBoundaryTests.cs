using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class MultiplayerContractLockBoundaryTests
    {
        private static readonly byte[] SynchronizedContent =
            System.Text.Encoding.UTF8.GetBytes("{\"difficulty\":7}");

        private static readonly byte[] ContractLockContent =
            System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":2,\"protocolVersion\":\"1.0.0\",\"contracts\":[]}");

        public static void Run(string root)
        {
            InstallerRejectsUndeclaredCanonicalLock(root);
            RegistryRejectsUndeclaredCanonicalLock(root);
            Console.WriteLine("MultiplayerContractLockBoundaryTests passed.");
        }

        private static void InstallerRejectsUndeclaredCanonicalLock(string root)
        {
            var paths = new ManagerPaths(Path.Combine(root, "contract-lock-install", "BepInEx"));
            paths.EnsureCreated();
            var manifest = HandcraftedSessionManifest();
            var package = Path.Combine(root, "contract-lock-omitted.topiaforgemod");
            WritePackage(package, manifest);

            var state = new ManagerState();
            var result = new PackageInstaller().Install(package, paths, state, restartRequired: false);

            Assert(!result.Ok, "installer accepted a session package whose lock was not synchronized");
            Assert(result.Errors.Any(IsCanonicalLockError),
                "installer did not explain that the canonical generated contract lock is mandatory");
            Assert(state.Find(manifest.Id) == null,
                "a rejected handcrafted session package must not enter manager state");
            Assert(!Directory.Exists(paths.GetPackagePath(manifest.Id, manifest.Version)),
                "a rejected handcrafted session package must not be committed to the package store");
        }

        private static void RegistryRejectsUndeclaredCanonicalLock(string root)
        {
            var paths = new ManagerPaths(Path.Combine(root, "contract-lock-scan", "BepInEx"));
            paths.EnsureCreated();
            var manifest = HandcraftedSessionManifest();
            var installedRoot = paths.GetPackagePath(manifest.Id, manifest.Version);
            WriteExtractedPackage(installedRoot, manifest);

            var state = new ManagerState();
            state.Upsert(manifest, enabled: true, restartRequired: false);
            var scanned = new ModRegistry().Scan(paths, state).Single();

            Assert(!scanned.IsValid, "registry accepted an installed session package whose lock was not synchronized");
            Assert(scanned.Errors.Any(IsCanonicalLockError),
                "registry scan did not explain that the canonical generated contract lock is mandatory");
        }

        private static ModManifest HandcraftedSessionManifest()
        {
            return new ModManifest
            {
                SchemaVersion = ModManifest.CurrentSchemaVersion,
                Id = "example.contract-lock-boundary",
                Name = "Contract lock boundary",
                Version = "1.0.0",
                Author = new ModAuthor { Name = "TopiaForge Tests" },
                EntryAssembly = "TopiaForge.ValidTestMod.dll",
                EntryType = "TopiaForge.ValidTestMod.ValidMod",
                SupportedGameVersionRange = "*",
                SupportedLoaderVersionRange = "*",
                SupportedSdkVersionRange = "*",
                Hashes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Content/gameplay-rules.json"] = Sha256(SynchronizedContent),
                    [ModMultiplayerMetadata.ContractLockFileName] = Sha256(ContractLockContent),
                },
                Multiplayer = new ModMultiplayerMetadata
                {
                    Mode = ModMultiplayerMetadata.SessionMode,
                    Presence = ModMultiplayerMetadata.RequiredPresence,
                    Protocol = new ModMultiplayerProtocol { Version = "1.0.0" },
                    // A handcrafted package may contain and hash the lock while omitting it from this
                    // admission-critical list. The package boundary must reject that bypass.
                    SynchronizedFiles = new List<string> { "Content/gameplay-rules.json" },
                },
            };
        }

        private static void WritePackage(string path, ModManifest manifest)
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "topiaforge.mod.json", System.Text.Encoding.UTF8.GetBytes(JsonUtil.Serialize(manifest)));
                WriteEntry(archive, "Content/gameplay-rules.json", SynchronizedContent);
                WriteEntry(archive, ModMultiplayerMetadata.ContractLockFileName, ContractLockContent);
                WriteEntry(
                    archive,
                    manifest.EntryAssembly,
                    File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, manifest.EntryAssembly)));
            }
        }

        private static void WriteExtractedPackage(string root, ModManifest manifest)
        {
            Directory.CreateDirectory(Path.Combine(root, "Content"));
            File.WriteAllText(Path.Combine(root, "topiaforge.mod.json"), JsonUtil.Serialize(manifest));
            File.WriteAllBytes(Path.Combine(root, "Content", "gameplay-rules.json"), SynchronizedContent);
            File.WriteAllBytes(Path.Combine(root, ModMultiplayerMetadata.ContractLockFileName), ContractLockContent);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, manifest.EntryAssembly),
                Path.Combine(root, manifest.EntryAssembly));
        }

        private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
        {
            var entry = archive.CreateEntry(path);
            using (var stream = entry.Open())
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private static bool IsCanonicalLockError(string error) =>
            error.Contains("canonical generated contract lock", StringComparison.OrdinalIgnoreCase) &&
            error.Contains(ModMultiplayerMetadata.ContractLockFileName, StringComparison.Ordinal);

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Multiplayer contract lock boundary: " + message);
            }
        }
    }
}
