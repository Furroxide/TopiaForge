using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text.Json;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class ModContextConfigPersistenceTests
    {
        internal static void Run(string root)
        {
            CreatesValidatedVersionedDefaults(root);
            MigratesAndPersistsOlderSchemas(root);
            RejectsInvalidValuesWithoutReplacingTheFile(root);
            RecoversFromTheAtomicBackup(root);
            ReportsMalformedAndOversizedDocuments(root);
            FailedOversizedSaveIsAtomic(root);
            RejectsExcessiveJsonDepth();
            Console.WriteLine("ModContextConfigPersistenceTests passed.");
        }

        private static void CreatesValidatedVersionedDefaults(string root)
        {
            var fixture = CreateFixture(root, "config-defaults");
            var definition = Definition(defaultKnown: 7);

            var loaded = fixture.Context.Config.Load(definition);

            Assert(loaded.TryGetValue(out var config) && config.Known == 7,
                "a missing config should produce validated defaults");
            using var document = JsonDocument.Parse(File.ReadAllText(fixture.ConfigPath));
            Assert(document.RootElement.GetProperty("schemaVersion").GetInt32() == 2
                && document.RootElement.GetProperty("value").GetProperty("known").GetInt32() == 7,
                "defaults should be persisted in the V1 versioned envelope");
        }

        private static void MigratesAndPersistsOlderSchemas(string root)
        {
            var fixture = CreateFixture(root, "config-migration");
            File.WriteAllText(
                fixture.ConfigPath,
                "{\"schemaVersion\":1,\"value\":{\"known\":4,\"label\":\"legacy\"}}");

            var loaded = fixture.Context.Config.Load(Definition());

            Assert(loaded.TryGetValue(out var config)
                && config.Known == 5
                && config.Label == "migrated:legacy",
                "the declared migrator should receive and upgrade an older schema");
            using var document = JsonDocument.Parse(File.ReadAllText(fixture.ConfigPath));
            Assert(document.RootElement.GetProperty("schemaVersion").GetInt32() == 2
                && document.RootElement.GetProperty("value").GetProperty("known").GetInt32() == 5,
                "a successful migration should be written back at the current schema");
        }

        private static void RejectsInvalidValuesWithoutReplacingTheFile(string root)
        {
            var fixture = CreateFixture(root, "config-validation");
            var definition = Definition(defaultKnown: 3);
            Assert(fixture.Context.Config.Load(definition).Succeeded,
                "the valid initial config should load");
            var original = File.ReadAllText(fixture.ConfigPath);

            var saved = fixture.Context.Config.Save(
                definition,
                new TestConfig { Known = -1, Label = "invalid" });

            Assert(!saved.Succeeded && saved.ErrorCode == ModErrorCode.InvalidArgument,
                "validation failures should use the stable invalid-argument error");
            Assert(File.ReadAllText(fixture.ConfigPath) == original,
                "a rejected value must not replace the last valid document");
        }

        private static void RecoversFromTheAtomicBackup(string root)
        {
            var fixture = CreateFixture(root, "config-backup");
            var definition = Definition();
            File.WriteAllText(
                fixture.ConfigPath + JsonUtil.BackupSuffix,
                "{\"schemaVersion\":2,\"value\":{\"known\":9,\"label\":\"backup\"}}");
            File.WriteAllText(fixture.ConfigPath, "{broken");

            var loaded = fixture.Context.Config.Load(definition);

            Assert(loaded.TryGetValue(out var config)
                && config.Known == 9
                && config.Label == "backup",
                "typed config loading should recover through the bounded atomic backup");
        }

        private static void ReportsMalformedAndOversizedDocuments(string root)
        {
            var malformed = CreateFixture(root, "config-malformed");
            File.WriteAllText(malformed.ConfigPath, "{broken");
            File.WriteAllText(malformed.ConfigPath + JsonUtil.BackupSuffix, "[also-broken]");

            var malformedResult = malformed.Context.Config.Load(Definition());
            Assert(!malformedResult.Succeeded
                && malformedResult.ErrorCode == ModErrorCode.Io
                && malformed.Logger.Errors == 1,
                "unrecoverable malformed config should be an attributed I/O failure");

            var oversized = CreateFixture(root, "config-oversized-read");
            using (var stream = new FileStream(
                oversized.ConfigPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                stream.SetLength(JsonUtil.MaxPersistedFileBytes + 1);
            }

            var oversizedResult = oversized.Context.Config.Load(Definition());
            Assert(!oversizedResult.Succeeded && oversizedResult.ErrorCode == ModErrorCode.Io,
                "oversized config should be rejected before deserialization");
        }

        private static void FailedOversizedSaveIsAtomic(string root)
        {
            var fixture = CreateFixture(root, "config-oversized-save");
            var definition = Definition(defaultKnown: 1);
            Assert(fixture.Context.Config.Load(definition).Succeeded,
                "the initial bounded config should load");
            var original = File.ReadAllText(fixture.ConfigPath);

            var result = fixture.Context.Config.Save(
                definition,
                new TestConfig
                {
                    Known = 2,
                    Label = new string('x', checked((int)JsonUtil.MaxPersistedFileBytes + 1024))
                });

            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.Io,
                "oversized typed serialization should report a stable I/O failure");
            Assert(File.ReadAllText(fixture.ConfigPath) == original,
                "a failed bounded save must preserve the previous complete document");
            AssertNoTemps(fixture.ConfigPath);
        }

        private static void RejectsExcessiveJsonDepth()
        {
            var nested = new string('[', 129) + "0" + new string(']', 129);
            Throws<FormatException>(
                () => JsonObjectMerge.ValidateObject("{\"future\":" + nested + "}"),
                "strict JSON validation should bound nesting before recursive parsing");
        }

        private static ConfigDefinition<TestConfig> Definition(int defaultKnown = 1)
        {
            return new ConfigDefinition<TestConfig>(
                schemaVersion: 2,
                createDefault: () => new TestConfig { Known = defaultKnown, Label = "default" },
                validate: value => value.Known >= 0
                    ? OperationResult<bool>.Success(true)
                    : OperationResult<bool>.Failure(
                        ModErrorCode.InvalidArgument,
                        "Known must be non-negative."),
                migrate: (storedVersion, value) => storedVersion == 1
                    ? OperationResult<TestConfig>.Success(new TestConfig
                    {
                        Known = value.Known + 1,
                        Label = "migrated:" + value.Label
                    })
                    : OperationResult<TestConfig>.Failure(
                        ModErrorCode.InvalidState,
                        "Only schema 1 can be migrated to schema 2."));
        }

        private static Fixture CreateFixture(string root, string name)
        {
            var paths = new ManagerPaths(Path.Combine(root, name, "BepInEx"));
            paths.EnsureCreated();
            var manifest = new ModManifest
            {
                SchemaVersion = ModManifest.CurrentSchemaVersion,
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

            [DataMember(Name = "label")]
            public string Label { get; set; } = string.Empty;
        }
    }
}
