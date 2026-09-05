using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class ManifestV5Tests
    {
        private static readonly byte[] ContractLockBytes = System.Text.Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":2,\"protocolVersion\":\"1.0.0\",\"contracts\":[]}");

        public static void Run(string root)
        {
            SchemaDispatchRejectsRetiredV4AndAllowsStandaloneV5();
            V5SessionMetadataRoundTrips();
            V5ModesHaveClosedSemantics();
            ProtocolAndSynchronizedPathsAreValidated();
            PackagedContentHashesAreEnforced(root);
            InstallerAndRegistryEnforceSynchronizedHashes(root);
            Console.WriteLine("ManifestV5Tests passed.");
        }

        private static void SchemaDispatchRejectsRetiredV4AndAllowsStandaloneV5()
        {
            Assert(ManifestSchemaDispatch.Resolve(ModManifest.ManifestV5SchemaVersion) == ManifestSchemaContract.V5,
                "the immutable V5 selector must dispatch to its dedicated reader branch");

            try
            {
                ModManifestJson.Deserialize(ManifestJson(4, string.Empty));
                throw new InvalidOperationException("retired schema V4 unexpectedly loaded");
            }
            catch (InvalidDataException exception)
            {
                Assert(exception.Message.Contains("retired") && exception.Message.Contains("schemaVersion 5"),
                    "schema V4 rejection must provide an actionable V5 migration message");
            }

            var standalone = ModManifestJson.Deserialize(ManifestJson(5, string.Empty));
            Assert(standalone.IsStandaloneOnly && !standalone.DeclaresMultiplayer,
                "schema V5 without multiplayer metadata must be standalone-only");
            Assert(ManifestValidator.Validate(standalone).Count == 0,
                "a canonical standalone V5 manifest must remain valid");

            Assert(ManifestSchemaDispatch.Resolve(ModManifest.ManifestV6SchemaVersion) == ManifestSchemaContract.V6,
                "the V6 selector must dispatch to its own reader branch, never to V5's");

            AssertThrows<InvalidDataException>(
                () => ModManifestJson.Deserialize(ManifestJson(7, string.Empty)),
                "unknown schema versions must fail without being treated as a supported one");

            var futureErrors = ManifestValidator.Validate(new ModManifest
            {
                SchemaVersion = 7,
                Multiplayer = new ModMultiplayerMetadata { Mode = "future-mode" }
            });
            Assert(futureErrors.Count == 1 && futureErrors[0].Contains("schemaVersion must be 5 or 6"),
                "an unsupported future schema must fail on its selector without interpreting supported fields");

            // Each version rejects the other's one distinguishing field by name. Known fields are
            // the union across schemas, so without this a V5 manifest would silently accept a
            // declaration set no V5 reader understands.
            AssertThrows<InvalidDataException>(
                () => ModManifestJson.Deserialize(ManifestJson(
                    6,
                    ",\"worldGamemodes\":[{\"id\":\"sample.mod.mode\",\"name\":\"Mode\"}]")),
                "a V6 manifest must not carry the retired worldGamemodes list");
            AssertThrows<InvalidDataException>(
                () => ModManifestJson.Deserialize(ManifestJson(
                    5,
                    ",\"contributions\":{\"gamemodes\":[{\"id\":\"sample.mod.mode\"," +
                    "\"name\":\"Mode\",\"implementation\":{\"type\":\"Sample.Mode\"}}]}")),
                "a V5 manifest must not declare contributions");
        }

        private static void V5SessionMetadataRoundTrips()
        {
            var manifest = ModManifestJson.Deserialize(ManifestJson(
                5,
                ",\"multiplayer\":{" +
                "\"mode\":\"session\",\"presence\":\"required\"," +
                "\"protocol\":{\"version\":\"1.2.3\",\"peerVersionRange\":\">=1.0.0 <2.0.0\"}," +
                "\"synchronizedFiles\":[\"Content/gameplay-rules.json\"]}"));

            Assert(manifest.DeclaresMultiplayer && !manifest.IsStandaloneOnly,
                "a valid V5 declaration must opt into multiplayer");
            Assert(manifest.Multiplayer!.Mode == ModMultiplayerMetadata.SessionMode,
                "session mode must deserialize");
            Assert(manifest.Multiplayer.Presence == ModMultiplayerMetadata.RequiredPresence,
                "session presence must deserialize");
            Assert(manifest.Multiplayer.Protocol!.Version == "1.2.3" &&
                   manifest.Multiplayer.Protocol.PeerVersionRange == ">=1.0.0 <2.0.0",
                "protocol compatibility must deserialize independently from package version");
            Assert(manifest.Multiplayer.Protocol.EffectivePeerVersionRange == ">=1.0.0 <2.0.0",
                "an explicit peer range must be the effective admission range");
            Assert(ManifestValidator.Validate(manifest).Count == 0,
                "source manifests must validate before pack-time hashes are generated");

            var roundTrip = ModManifestJson.Deserialize(JsonUtil.Serialize(manifest));
            Assert(roundTrip.Multiplayer!.SynchronizedFiles.SequenceEqual(
                    new[] { "Content/gameplay-rules.json" }),
                "synchronized file declarations must survive JSON round trips");
            Assert(roundTrip.Multiplayer.Protocol!.PeerVersionRange == ">=1.0.0 <2.0.0",
                "optional peer ranges must survive JSON round trips");

            var exactByDefault = new ModMultiplayerProtocol { Version = "2.3.4" };
            Assert(exactByDefault.EffectivePeerVersionRange == "2.3.4",
                "an omitted peer range must require exact protocol equality");
        }

        private static void V5ModesHaveClosedSemantics()
        {
            foreach (var mode in new[]
                     {
                         ModMultiplayerMetadata.ClientLocalMode,
                         ModMultiplayerMetadata.ServerOnlyMode,
                     })
            {
                var manifest = ModManifestJson.Deserialize(ManifestJson(
                    5,
                    ",\"multiplayer\":{\"mode\":\"" + mode + "\"}"));
                Assert(ManifestValidator.Validate(manifest).Count == 0,
                    mode + " must be valid without session-only metadata");
            }

            var clientWithSessionFields = ModManifestJson.Deserialize(ManifestJson(
                5,
                ",\"multiplayer\":{" +
                "\"mode\":\"client-local\",\"presence\":\"optional\"," +
                "\"protocol\":{\"version\":\"1.0.0\"},\"synchronizedFiles\":[]}"));
            var clientErrors = ManifestValidator.Validate(clientWithSessionFields);
            Assert(clientErrors.Any(error => error.Contains("presence is only valid")) &&
                   clientErrors.Any(error => error.Contains("protocol is only valid")) &&
                   clientErrors.Any(error => error.Contains("synchronizedFiles is only valid")),
                "client-local must reject every session-only field, including an explicitly empty list");

            AssertThrows<InvalidDataException>(
                () => ModManifestJson.Deserialize(ManifestJson(
                    5,
                    ",\"multiplayer\":{\"mode\":\"client-local\",\"protocol\":null}")),
                "session-only fields cannot bypass closed mode semantics with null");
            AssertThrows<InvalidDataException>(
                () => ModManifestJson.Deserialize(ManifestJson(
                    5,
                    ",\"multiplayer\":{" +
                    "\"mode\":\"session\",\"presence\":\"required\"," +
                    "\"protocol\":{\"version\":\"1.0.0\"},\"synchronizedFiles\":null}")),
                "synchronizedFiles must be an array when present");

            var incompleteSession = ModManifestJson.Deserialize(ManifestJson(
                5,
                ",\"multiplayer\":{\"mode\":\"session\"}"));
            var sessionErrors = ManifestValidator.Validate(incompleteSession);
            Assert(sessionErrors.Any(error => error.Contains("presence must be")) &&
                   sessionErrors.Any(error => error.Contains("protocol is required")),
                "session mode must require presence and protocol");
        }

        private static void ProtocolAndSynchronizedPathsAreValidated()
        {
            var invalid = ModManifestJson.Deserialize(ManifestJson(
                5,
                ",\"multiplayer\":{" +
                "\"mode\":\"session\",\"presence\":\"required\"," +
                "\"protocol\":{\"version\":\"1.0\",\"peerVersionRange\":\"nope\"}," +
                "\"synchronizedFiles\":[\"../escape.json\",\"Data/File.json\",\"data/file.json\"," +
                "\"topiaforge.mod.json\"]}"));
            var errors = ManifestValidator.Validate(invalid);
            Assert(errors.Any(error => error.Contains("protocol.version must be an exact semantic version")),
                "protocol versions must be exact SemVer");
            Assert(errors.Any(error => error.Contains("peerVersionRange must be a valid")),
                "peer compatibility must use a supported SemVer range");
            Assert(errors.Any(error => error.Contains("safe portable relative path")),
                "synchronized files must use portable package paths");
            Assert(errors.Any(error => error.Contains("portable-collision")),
                "synchronized files must reject cross-platform path collisions");
            Assert(errors.Any(error => error.Contains("generated package metadata")),
                "synchronized files must not create a self-referential manifest hash");
        }

        private static void PackagedContentHashesAreEnforced(string root)
        {
            var packageRoot = Path.Combine(root, "manifest-v5-package");
            Directory.CreateDirectory(Path.Combine(packageRoot, "Content"));
            var contentPath = Path.Combine(packageRoot, "Content", "gameplay-rules.json");
            File.WriteAllText(contentPath, "{\"difficulty\":2}");
            var contractLockPath = Path.Combine(packageRoot, ModMultiplayerMetadata.ContractLockFileName);
            File.WriteAllBytes(contractLockPath, ContractLockBytes);
            File.WriteAllText(Path.Combine(packageRoot, "Example.dll"), "fixture");
            File.WriteAllText(Path.Combine(packageRoot, "topiaforge.mod.json"), "{}");

            var manifest = ValidSessionManifest();
            Assert(ManifestValidator.Validate(manifest).Count == 0,
                "ordinary source validation must not require generated content hashes");
            Assert(ManifestContentValidator.Validate(packageRoot, manifest)
                    .Any(error => error.Contains("missing its pack-time SHA-256")),
                "packaged validation must require a hash for every synchronized file");

            manifest.Hashes["Content/gameplay-rules.json"] = Sha256(contentPath);
            manifest.Hashes[ModMultiplayerMetadata.ContractLockFileName] = Sha256(contractLockPath);
            Assert(ManifestContentValidator.Validate(packageRoot, manifest).Count == 0,
                "packaged validation must accept matching generated content hashes");

            var undeclaredLock = ValidSessionManifest();
            undeclaredLock.Multiplayer!.SynchronizedFiles.Remove(ModMultiplayerMetadata.ContractLockFileName);
            undeclaredLock.Hashes["Content/gameplay-rules.json"] = Sha256(contentPath);
            Assert(ManifestContentValidator.Validate(packageRoot, undeclaredLock)
                    .Any(error => error.Contains("canonical generated contract lock")),
                "hand-crafted session packages must not bypass contract-lock admission by omitting the lock");

            File.WriteAllText(contentPath, "{\"difficulty\":3}");
            Assert(ManifestContentValidator.Validate(packageRoot, manifest)
                    .Any(error => error.Contains("does not match")),
                "packaged validation must reject synchronized content changed after packing");

            manifest.Hashes["Content/gameplay-rules.json"] = Sha256(contentPath);
            var archive = Path.Combine(root, "manifest-v5.topiaforgemod");
            File.WriteAllText(archive, "archive fixture");
            var receipt = PackageInstallReceipt.Create(archive, packageRoot, manifest);
            Assert(receipt.Files.Single(file => file.Path == "Content/gameplay-rules.json").Critical,
                "synchronized content must be classified as critical installed payload");
        }

        private static void InstallerAndRegistryEnforceSynchronizedHashes(string root)
        {
            var paths = new ManagerPaths(Path.Combine(root, "manifest-v5-install", "BepInEx"));
            paths.EnsureCreated();
            var state = new ManagerState();
            var content = System.Text.Encoding.UTF8.GetBytes("{\"difficulty\":4}");

            var missingHashPackage = Path.Combine(root, "manifest-v5-missing-hash.topiaforgemod");
            WritePackage(missingHashPackage, ValidSessionManifest(), content);
            var missingHash = new PackageInstaller().Install(
                missingHashPackage,
                paths,
                state,
                restartRequired: false);
            Assert(!missingHash.Ok && missingHash.Errors.Any(error => error.Contains("pack-time SHA-256")),
                "package installation must reject synchronized content without its generated hash");

            var manifest = ValidSessionManifest();
            manifest.Hashes["Content/gameplay-rules.json"] = Sha256(content);
            manifest.Hashes[ModMultiplayerMetadata.ContractLockFileName] = Sha256(ContractLockBytes);
            var validPackage = Path.Combine(root, "manifest-v5-valid.topiaforgemod");
            WritePackage(validPackage, manifest, content);
            var installed = new PackageInstaller().Install(
                validPackage,
                paths,
                state,
                restartRequired: false);
            Assert(installed.Ok, "package installation must accept synchronized content with a matching hash");

            var installedContent = Path.Combine(
                paths.GetPackagePath(manifest.Id, manifest.Version),
                "Content",
                "gameplay-rules.json");
            File.WriteAllText(installedContent, "{\"difficulty\":5}");
            var scanned = new ModRegistry().Scan(paths, state)
                .Single(package => string.Equals(
                    package.Manifest?.Id,
                    manifest.Id,
                    StringComparison.Ordinal));
            Assert(!scanned.IsValid && scanned.Errors.Any(error => error.Contains("does not match")),
                "runtime scanning must reject synchronized content changed after installation");
        }

        private static void WritePackage(string path, ModManifest manifest, byte[] synchronizedContent)
        {
            const string fixtureAssembly = "TopiaForge.ValidTestMod.dll";
            manifest.EntryAssembly = fixtureAssembly;
            manifest.EntryType = "TopiaForge.ValidTestMod.ValidMod";
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "topiaforge.mod.json", System.Text.Encoding.UTF8.GetBytes(JsonUtil.Serialize(manifest)));
                WriteEntry(zip, "Content/gameplay-rules.json", synchronizedContent);
                WriteEntry(zip, ModMultiplayerMetadata.ContractLockFileName, ContractLockBytes);
                WriteEntry(zip, fixtureAssembly, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, fixtureAssembly)));
            }
        }

        private static void WriteEntry(ZipArchive zip, string path, byte[] contents)
        {
            var entry = zip.CreateEntry(path);
            using (var output = entry.Open())
            {
                output.Write(contents, 0, contents.Length);
            }
        }

        private static ModManifest ValidSessionManifest()
        {
            var manifest = new ModManifest
            {
                SchemaVersion = ModManifest.CurrentSchemaVersion,
                Id = "example.multiplayer",
                Name = "Example multiplayer",
                Version = "1.0.0",
                Author = new ModAuthor { Name = "TopiaForge Tests" },
                EntryAssembly = "Example.dll",
                EntryType = "Example.Mod",
                Multiplayer = new ModMultiplayerMetadata
                {
                    Mode = ModMultiplayerMetadata.SessionMode,
                    Presence = ModMultiplayerMetadata.RequiredPresence,
                    Protocol = new ModMultiplayerProtocol { Version = "1.0.0" },
                    SynchronizedFiles = new List<string>
                    {
                        "Content/gameplay-rules.json",
                        ModMultiplayerMetadata.ContractLockFileName
                    },
                },
            };
            return manifest;
        }

        private static string ManifestJson(int schemaVersion, string additionalFields)
        {
            return "{" +
                   "\"schemaVersion\":" + schemaVersion + "," +
                   "\"name\":\"example.manifest\"," +
                   "\"displayName\":\"Example\"," +
                   "\"version\":\"1.0.0\"," +
                   "\"author\":{\"name\":\"TopiaForge Tests\"}," +
                   "\"entryAssembly\":\"Example.dll\"," +
                   "\"entryType\":\"Example.Mod\"," +
                   "\"supportedGameVersionRange\":\"*\"," +
                   "\"supportedLoaderVersionRange\":\"*\"," +
                   "\"supportedSdkVersionRange\":\"*\"" +
                   additionalFields + "}";
        }

        private static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string Sha256(byte[] contents)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(contents)).Replace("-", string.Empty).ToLowerInvariant();
            }
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

            throw new InvalidOperationException("Manifest V5: " + message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Manifest V5: " + message);
            }
        }
    }
}
