using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext
    {
        private sealed class ModConfigService : IModConfigService
        {
            private readonly string path;
            private readonly IModLogger logger;
            private readonly object sync = new object();

            public ModConfigService(string path, IModLogger logger)
            {
                this.path = path;
                this.logger = logger;
            }

            public OperationResult<T> Load<T>(ConfigDefinition<T> definition) where T : class
            {
                if (definition == null) throw new ArgumentNullException(nameof(definition));
                lock (sync)
                {
                    if (!File.Exists(path) && !File.Exists(path + JsonUtil.BackupSuffix))
                    {
                        var defaults = CreateDefaults(definition);
                        if (!defaults.TryGetValue(out var defaultValue)) return defaults;
                        var saved = SaveCore(definition, defaultValue);
                        return saved.Succeeded
                            ? OperationResult<T>.Success(defaultValue)
                            : OperationResult<T>.Failure(saved.ErrorCode, saved.ErrorMessage);
                    }

                    try
                    {
                        var envelope = JsonUtil.LoadPersistentFile(path, new ConfigEnvelope<T>());
                        var storedVersion = envelope.SchemaVersion;
                        var value = envelope.Value;
                        if (value == null)
                        {
                            var fallback = definition.CreateDefault();
                            value = JsonUtil.LoadPersistentFile(path, fallback);
                            storedVersion = 0;
                        }

                        if (storedVersion > definition.SchemaVersion)
                        {
                            return OperationResult<T>.Failure(
                                ModErrorCode.InvalidState,
                                "Config schema " + storedVersion + " is newer than supported schema "
                                + definition.SchemaVersion + ".");
                        }

                        if (storedVersion < definition.SchemaVersion)
                        {
                            if (definition.Migrate == null)
                            {
                                return OperationResult<T>.Failure(
                                    ModErrorCode.InvalidState,
                                    "Config schema " + storedVersion + " requires a migration to schema "
                                    + definition.SchemaVersion + ".");
                            }

                            var migration = definition.Migrate(storedVersion, value);
                            if (!migration.Succeeded || migration.Value == null)
                            {
                                return OperationResult<T>.Failure(migration.ErrorCode, migration.ErrorMessage);
                            }

                            value = migration.Value;
                            var saved = SaveCore(definition, value);
                            if (!saved.Succeeded)
                            {
                                return OperationResult<T>.Failure(saved.ErrorCode, saved.ErrorMessage);
                            }
                        }

                        var validation = definition.Validate(value);
                        return validation != null && validation.Succeeded && validation.Value == true
                            ? OperationResult<T>.Success(value)
                            : OperationResult<T>.Failure(
                                validation == null || validation.Succeeded
                                    ? ModErrorCode.InvalidArgument
                                    : validation.ErrorCode,
                                validation == null
                                    ? "The config validator returned no result."
                                    : validation.Succeeded
                                        ? "The config validator rejected the value."
                                        : validation.ErrorMessage);
                    }
                    catch (Exception exception)
                    {
                        logger.Error(exception, "Failed to load typed mod configuration.");
                        return OperationResult<T>.Failure(ModErrorCode.Io, exception.Message);
                    }
                }
            }

            public OperationResult<bool> Save<T>(ConfigDefinition<T> definition, T value) where T : class
            {
                if (definition == null) throw new ArgumentNullException(nameof(definition));
                if (value == null) throw new ArgumentNullException(nameof(value));
                lock (sync)
                {
                    return SaveCore(definition, value);
                }
            }

            public OperationResult<T> Reset<T>(ConfigDefinition<T> definition) where T : class
            {
                if (definition == null) throw new ArgumentNullException(nameof(definition));
                lock (sync)
                {
                    var defaults = CreateDefaults(definition);
                    if (!defaults.TryGetValue(out var value)) return defaults;
                    var save = SaveCore(definition, value);
                    return save.Succeeded
                        ? OperationResult<T>.Success(value)
                        : OperationResult<T>.Failure(save.ErrorCode, save.ErrorMessage);
                }
            }

            private static OperationResult<T> CreateDefaults<T>(ConfigDefinition<T> definition) where T : class
            {
                var value = definition.CreateDefault();
                if (value == null)
                {
                    return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The config default factory returned null.");
                }

                var validation = definition.Validate(value);
                return validation != null && validation.Succeeded && validation.Value == true
                    ? OperationResult<T>.Success(value)
                    : OperationResult<T>.Failure(
                        validation == null || validation.Succeeded
                            ? ModErrorCode.InvalidArgument
                            : validation.ErrorCode,
                        validation == null
                            ? "The config validator returned no result."
                            : validation.Succeeded
                                ? "The config validator rejected the value."
                                : validation.ErrorMessage);
            }

            private OperationResult<bool> SaveCore<T>(ConfigDefinition<T> definition, T value) where T : class
            {
                try
                {
                    var validation = definition.Validate(value);
                    if (validation == null || !validation.Succeeded || validation.Value != true)
                    {
                        return OperationResult<bool>.Failure(
                            validation == null || validation.Succeeded
                                ? ModErrorCode.InvalidArgument
                                : validation.ErrorCode,
                            validation == null
                                ? "The config validator returned no result."
                                : validation.Succeeded
                                    ? "The config validator rejected the value."
                                    : validation.ErrorMessage);
                    }

                    JsonUtil.SaveFile(path, new ConfigEnvelope<T>
                    {
                        SchemaVersion = definition.SchemaVersion,
                        Value = value
                    });
                    return OperationResult<bool>.Success(true);
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Failed to save typed mod configuration.");
                    return OperationResult<bool>.Failure(ModErrorCode.Io, exception.Message);
                }
            }

            [DataContract]
            private sealed class ConfigEnvelope<T> where T : class
            {
                [DataMember(Name = "schemaVersion")]
                public int SchemaVersion { get; set; }

                [DataMember(Name = "value")]
                public T? Value { get; set; }
            }
        }
    }
}
