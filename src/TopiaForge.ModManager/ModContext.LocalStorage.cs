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
        private sealed class LocalModStorageService : ILocalModStorageService
        {
            private readonly string root;
            private readonly object sync = new object();

            public LocalModStorageService(string dataPath)
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
