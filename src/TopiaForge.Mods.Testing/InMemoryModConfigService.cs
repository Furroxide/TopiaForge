using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Stores and validates versioned typed configuration documents in memory.</summary>
    public sealed class InMemoryModConfigService : IModConfigService
    {
        private readonly Dictionary<Type, StoredConfig> values = new Dictionary<Type, StoredConfig>();

        /// <summary>Gets the number of configuration types currently stored.</summary>
        public int Count => values.Count;

        /// <summary>Seeds a historical schema value so a test can exercise migration.</summary>
        public void Seed<T>(int schemaVersion, T value) where T : class
        {
            if (schemaVersion < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(schemaVersion));
            }

            values[typeof(T)] = new StoredConfig(schemaVersion, value ?? throw new ArgumentNullException(nameof(value)));
        }

        /// <inheritdoc/>
        public OperationResult<T> Load<T>(ConfigDefinition<T> definition) where T : class
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (!values.TryGetValue(typeof(T), out var stored))
            {
                return Reset(definition);
            }

            if (!(stored.Value is T value))
            {
                return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The stored config has a different type.");
            }

            if (stored.SchemaVersion > definition.SchemaVersion)
            {
                return OperationResult<T>.Failure(
                    ModErrorCode.InvalidState,
                    "The stored config schema is newer than this definition.");
            }

            if (stored.SchemaVersion < definition.SchemaVersion)
            {
                if (definition.Migrate == null)
                {
                    return OperationResult<T>.Failure(ModErrorCode.InvalidState, "No config migrator is available.");
                }

                var migration = definition.Migrate(stored.SchemaVersion, value);
                if (!migration.Succeeded || migration.Value == null)
                {
                    return OperationResult<T>.Failure(migration.ErrorCode, migration.ErrorMessage);
                }

                value = migration.Value;
            }

            var validation = definition.Validate(value);
            if (!validation.Succeeded || validation.Value != true)
            {
                return OperationResult<T>.Failure(
                    validation.Succeeded ? ModErrorCode.InvalidArgument : validation.ErrorCode,
                    validation.Succeeded ? "The config validator rejected the value." : validation.ErrorMessage);
            }

            values[typeof(T)] = new StoredConfig(definition.SchemaVersion, value);
            return OperationResult<T>.Success(value);
        }

        /// <inheritdoc/>
        public OperationResult<bool> Save<T>(ConfigDefinition<T> definition, T value) where T : class
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var validation = definition.Validate(value);
            if (!validation.Succeeded || validation.Value != true)
            {
                return OperationResult<bool>.Failure(
                    validation.Succeeded ? ModErrorCode.InvalidArgument : validation.ErrorCode,
                    validation.Succeeded ? "The config validator rejected the value." : validation.ErrorMessage);
            }

            values[typeof(T)] = new StoredConfig(definition.SchemaVersion, value);
            return OperationResult<bool>.Success(true);
        }

        /// <inheritdoc/>
        public OperationResult<T> Reset<T>(ConfigDefinition<T> definition) where T : class
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            var value = definition.CreateDefault();
            if (value == null)
            {
                return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The default factory returned null.");
            }

            var saved = Save(definition, value);
            return saved.Succeeded
                ? OperationResult<T>.Success(value)
                : OperationResult<T>.Failure(saved.ErrorCode, saved.ErrorMessage);
        }

        /// <summary>Removes the stored value for a configuration type.</summary>
        public bool Remove<T>() where T : class => values.Remove(typeof(T));

        /// <summary>Removes every stored configuration document.</summary>
        public void Clear() => values.Clear();

        private sealed class StoredConfig
        {
            public StoredConfig(int schemaVersion, object value)
            {
                SchemaVersion = schemaVersion;
                Value = value;
            }

            public int SchemaVersion { get; }
            public object Value { get; }
        }
    }
}
