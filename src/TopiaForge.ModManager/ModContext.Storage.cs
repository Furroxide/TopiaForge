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
        private sealed class ModStorageService : IModStorageService
        {
            private const string StoryFlagPrefix = "story-flags/";
            private readonly string root;
            private readonly object sync = new object();

            public ModStorageService(string dataPath)
            {
                root = Path.Combine(dataPath, "storage");
            }

            public bool Contains(string key) => File.Exists(Resolve(key));

            public OperationResult<T> Load<T>(string key) where T : class
            {
                var file = Resolve(key);
                lock (sync)
                {
                    if (!File.Exists(file) && !File.Exists(file + JsonUtil.BackupSuffix))
                    {
                        return OperationResult<T>.Failure(ModErrorCode.NotFound, "Storage key '" + key + "' was not found.");
                    }

                    try
                    {
                        var value = JsonUtil.LoadPersistentFile<T?>(file, null);
                        return value != null
                            ? OperationResult<T>.Success(value)
                            : OperationResult<T>.Failure(ModErrorCode.Io, "The stored value was null.");
                    }
                    catch (Exception exception)
                    {
                        return OperationResult<T>.Failure(ModErrorCode.Io, exception.Message);
                    }
                }
            }

            public OperationResult<bool> Save<T>(string key, T value) where T : class
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                var file = Resolve(key);
                lock (sync)
                {
                    try
                    {
                        JsonUtil.SaveFile(file, value);
                        return OperationResult<bool>.Success(true);
                    }
                    catch (Exception exception)
                    {
                        return OperationResult<bool>.Failure(ModErrorCode.Io, exception.Message);
                    }
                }
            }

            public OperationResult<bool> Delete(string key)
            {
                var file = Resolve(key);
                lock (sync)
                {
                    try
                    {
                        if (File.Exists(file)) File.Delete(file);
                        if (File.Exists(file + JsonUtil.BackupSuffix)) File.Delete(file + JsonUtil.BackupSuffix);
                        return OperationResult<bool>.Success(true);
                    }
                    catch (Exception exception)
                    {
                        return OperationResult<bool>.Failure(ModErrorCode.Io, exception.Message);
                    }
                }
            }

            public bool TryGetStoryFlag(string key, out bool value)
            {
                var result = Load<StoryFlagValue>(StoryFlagPrefix + ValidateStoryFlagKey(key));
                if (result.TryGetValue(out var stored))
                {
                    value = stored.Value;
                    return true;
                }

                value = false;
                return false;
            }

            public OperationResult<bool> SetStoryFlag(string key, bool value) =>
                Save(StoryFlagPrefix + ValidateStoryFlagKey(key), new StoryFlagValue { Value = value });

            public OperationResult<bool> DeleteStoryFlag(string key) =>
                Delete(StoryFlagPrefix + ValidateStoryFlagKey(key));

            private static string ValidateStoryFlagKey(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new ArgumentException("A story flag key is required.", nameof(key));
                }

                return key;
            }

            [DataContract]
            private sealed class StoryFlagValue
            {
                [DataMember(Name = "value")]
                public bool Value { get; set; }
            }

            private string Resolve(string key)
            {
                if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A storage key is required.", nameof(key));
                try
                {
                    return PathSafety.CombineRelativeChild(root, key + ".json");
                }
                catch (InvalidOperationException exception)
                {
                    throw new ArgumentException("Storage keys cannot be absolute or traverse directories.", nameof(key), exception);
                }
            }
        }
    }
}
