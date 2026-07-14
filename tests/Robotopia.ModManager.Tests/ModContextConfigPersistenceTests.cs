using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Text.Json;
using Robotopia.ModManager.Core;
using Robotopia.Mods;

namespace Robotopia.ModManager.Tests
{
    internal static class ModContextConfigPersistenceTests
    {
        internal static void Run(string root)
        {
            PreservesUnknownContractMembers(root);
            RecoversUnknownMembersFromBackup(root);
            ReplacesUnrecoverableMalformedConfig(root);
            RecoversFromOversizedConfig(root);
            FailedOversizedSaveIsAtomic(root);
            RejectsExcessiveJsonDepth();
            Console.WriteLine("ModContextConfigPersistenceTests passed.");
        }

        private static void PreservesUnknownContractMembers(string root)
        {
            var fixture = CreateFixture(root, "config-forward-fields");
            File.WriteAllText(
                fixture.ConfigPath,
                "{\"known\":1,\"nested\":{\"knownNested\":\"old\",\"futureNested\":{\"flag\":true}},"
                + "\"map\":{\"removeMe\":3},\"optional\":\"remove-me\","
                + "\"futureTop\":{\"mode\":\"next\"}}");

            var config = fixture.Context.LoadConfig(new TestConfig());
            Assert(config.Known == 1 && config.Nested?.KnownNested == "old",
                "typed config should still deserialize its known members");
            config.Known = 2;
            config.Nested!.KnownNested = "updated";
            config.Map = new Dictionary<string, int>(StringComparer.Ordinal) { ["fresh"] = 9 };
            config.Optional = null;
            fixture.Context.SaveConfig(config);

            using var document = JsonDocument.Parse(File.ReadAllText(fixture.ConfigPath));
            var json = document.RootElement;
            Assert(json.GetProperty("known").GetInt32() == 2,
                "saving should update a known top-level member");
            Assert(json.GetProperty("nested").GetProperty("knownNested").GetString() == "updated",
                "saving should update a known nested member");
            Assert(json.GetProperty("nested").GetProperty("futureNested").GetProperty("flag").GetBoolean(),
                "saving should retain unknown fields inside a typed nested object");
            Assert(json.GetProperty("futureTop").GetProperty("mode").GetString() == "next",
                "saving should retain unknown top-level fields");
            Assert(!json.TryGetProperty("optional", out _),
                "a known EmitDefaultValue=false member omitted by serialization must be removed");
            Assert(json.GetProperty("map").TryGetProperty("fresh", out _)
                && !json.GetProperty("map").TryGetProperty("removeMe", out _),
                "dictionary data should be replaced, not recursively retained as schema fields");
        }

        private static void RecoversUnknownMembersFromBackup(string root)
        {
            var fixture = CreateFixture(root, "config-backup-merge");
            File.WriteAllText(fixture.ConfigPath, "{\"known\":");
            File.WriteAllText(
                fixture.ConfigPath + JsonUtil.BackupSuffix,
                "{\"known\":4,\"nested\":{\"knownNested\":\"backup\",\"futureNested\":17},"
                + "\"futureTop\":\"retained-from-backup\"}");

            var config = fixture.Context.LoadConfig(new TestConfig());
            Assert(config.Known == 4, "typed load should recover from the valid atomic backup");
            config.Known = 5;
            fixture.Context.SaveConfig(config);

            using var document = JsonDocument.Parse(File.ReadAllText(fixture.ConfigPath));
            var json = document.RootElement;
            Assert(json.GetProperty("known").GetInt32() == 5,
                "the recovered config should persist its updated known value");
            Assert(json.GetProperty("futureTop").GetString() == "retained-from-backup"
                && json.GetProperty("nested").GetProperty("futureNested").GetInt32() == 17,
                "raw unknown members should be recovered from the same backup used for typed loading");
            using var backupDocument = JsonDocument.Parse(File.ReadAllText(
                fixture.ConfigPath + JsonUtil.BackupSuffix));
            Assert(backupDocument.RootElement.GetProperty("futureTop").GetString() == "retained-from-backup",
                "replacing a corrupt primary must retain the last valid backup instead of rotating corruption over it");
        }

        private static void ReplacesUnrecoverableMalformedConfig(string root)
        {
            var fixture = CreateFixture(root, "config-both-malformed");
            File.WriteAllText(fixture.ConfigPath, "{broken");
            File.WriteAllText(fixture.ConfigPath + JsonUtil.BackupSuffix, "[also-broken]");

            var config = fixture.Context.LoadConfig(new TestConfig { Known = 7 });
            Assert(config.Known == 7 && fixture.Logger.Errors == 1,
                "an unreadable primary and backup should return defaults and log the load failure");
            config.Known = 8;
            fixture.Context.SaveConfig(config);

            using var document = JsonDocument.Parse(File.ReadAllText(fixture.ConfigPath));
            Assert(document.RootElement.GetProperty("known").GetInt32() == 8,
                "a later save should recover an unrecoverably malformed config with validated typed data");
            Assert(fixture.Logger.Warnings == 1,
                "destructive recovery of malformed raw content should be explicit in the mod log");
            AssertNoTemps(fixture.ConfigPath);
        }

        private static void RecoversFromOversizedConfig(string root)
        {
            var fixture = CreateFixture(root, "config-oversized-read");
            using (var stream = new FileStream(fixture.ConfigPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(JsonUtil.MaxPersistedFileBytes + 1);
            }

            var config = fixture.Context.LoadConfig(new TestConfig { Known = 11 });
            Assert(config.Known == 11, "an oversized config should be rejected before deserialization");
            config.Known = 12;
            fixture.Context.SaveConfig(config);

            Assert(new FileInfo(fixture.ConfigPath).Length < JsonUtil.MaxPersistedFileBytes,
                "oversized corrupt input should be replaced by a bounded document");
            using var document = JsonDocument.Parse(File.ReadAllText(fixture.ConfigPath));
            Assert(document.RootElement.GetProperty("known").GetInt32() == 12,
                "oversized-input recovery should persist the requested typed config");
            AssertNoTemps(fixture.ConfigPath);
        }

        private static void FailedOversizedSaveIsAtomic(string root)
        {
            var fixture = CreateFixture(root, "config-oversized-save");
            var futureBlob = new string('u', 3 * 1024 * 1024);
            File.WriteAllText(
                fixture.ConfigPath,
                "{\"known\":1,\"futureBlob\":\"" + futureBlob + "\"}");
            var original = File.ReadAllText(fixture.ConfigPath);
            var config = new TestConfig
            {
                Known = 2,
                Payload = new string('p', 2 * 1024 * 1024)
            };

            Throws<InvalidDataException>(
                () => fixture.Context.SaveConfig(config),
                "a merged config larger than the persistence bound should be rejected");
            Assert(File.ReadAllText(fixture.ConfigPath) == original,
                "a rejected oversized merge must not replace the prior complete config");
            AssertNoTemps(fixture.ConfigPath);

            config.Payload = new string('x', checked((int)JsonUtil.MaxPersistedFileBytes + 1024));
            Throws<InvalidDataException>(
                () => fixture.Context.SaveConfig(config),
                "typed serialization itself should be bounded before merge allocation");
            Assert(File.ReadAllText(fixture.ConfigPath) == original,
                "failed bounded serialization must also preserve the prior config");
            AssertNoTemps(fixture.ConfigPath);
        }

        private static void RejectsExcessiveJsonDepth()
        {
            var nested = new string('[', 129) + "0" + new string(']', 129);
            Throws<FormatException>(
                () => JsonObjectMerge.ValidateObject("{\"future\":" + nested + "}"),
                "strict raw-field validation should bound nesting before recursive merge");
        }

        private static Fixture CreateFixture(string root, string name)
        {
            var paths = new ManagerPaths(Path.Combine(root, name, "BepInEx"));
            paths.EnsureCreated();
            var manifest = new ModManifest
            {
                SchemaVersion = 2,
                Id = "test.config",
                Name = "Config test",
                Version = "1.0.0",
                EntryAssembly = "Test.dll",
                EntryType = "Test.Entry"
            };
            var logger = new RecordingLogger();
            var context = new ModContext(
                manifest,
                paths,
                Path.Combine(root, name, "package"),
                logger,
                new ModServiceRegistry());
            return new Fixture(context, paths.GetConfigPath(manifest.Id), logger);
        }

        private static void AssertNoTemps(string configPath)
        {
            var directory = Path.GetDirectoryName(configPath)!;
            Assert(Directory.GetFiles(
                    directory,
                    Path.GetFileName(configPath) + ".tmp-*",
                    SearchOption.TopDirectoryOnly).Length == 0,
                "config persistence should clean failed atomic-write temp files");
        }

        private static void Throws<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class Fixture
        {
            public Fixture(ModContext context, string configPath, RecordingLogger logger)
            {
                Context = context;
                ConfigPath = configPath;
                Logger = logger;
            }

            public ModContext Context { get; }
            public string ConfigPath { get; }
            public RecordingLogger Logger { get; }
        }

        private sealed class RecordingLogger : IModLogger
        {
            public int Warnings { get; private set; }
            public int Errors { get; private set; }

            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) => Warnings++;
            public void Error(string message) => Errors++;
            public void Error(Exception exception, string message) => Errors++;
        }

        [DataContract]
        private sealed class TestConfig
        {
            [DataMember(Name = "known")]
            public int Known { get; set; }

            [DataMember(Name = "nested")]
            public NestedConfig? Nested { get; set; } = new NestedConfig();

            [DataMember(Name = "map")]
            public Dictionary<string, int> Map { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

            [DataMember(Name = "payload")]
            public string Payload { get; set; } = string.Empty;

            [DataMember(Name = "optional", EmitDefaultValue = false)]
            public string? Optional { get; set; }
        }

        [DataContract]
        private sealed class NestedConfig
        {
            [DataMember(Name = "knownNested")]
            public string KnownNested { get; set; } = string.Empty;
        }
    }
}
