using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using Robotopia.ModManager.Core;
using Robotopia.Mods;

namespace Robotopia.ModManager
{
    public sealed class ModContext : IModContext
    {
        private readonly string configFile;
        private readonly object configSync = new object();
        private readonly IModServiceRegistry serviceRegistry;
        private readonly Dictionary<Type, object> services = new Dictionary<Type, object>();

        public ModContext(ModManifest manifest, ManagerPaths managerPaths, string packagePath, IModLogger logger, IModServiceRegistry serviceRegistry)
        {
            this.serviceRegistry = serviceRegistry;
            ModId = manifest.Id;
            ModName = manifest.Name;
            VersionUtil.TryParse(manifest.Version, out var version);
            Version = version;
            Paths = new ModPaths(packagePath, managerPaths.GetConfigPath(manifest.Id), managerPaths.GetDataPath(manifest.Id));
            Logger = logger;
            configFile = Paths.ConfigPath;
            Directory.CreateDirectory(Paths.DataPath);
            services[typeof(IModServiceRegistry)] = new OwnerBoundModServiceRegistry(ModId, serviceRegistry);
            services[typeof(IModFileService)] = new ModFileService(Paths);
        }

        public string ModId { get; }
        public string ModName { get; }
        public Version Version { get; }
        public ModPaths Paths { get; }
        public IModLogger Logger { get; }

        public event Action<float>? Update;
        public event Action<string>? SceneLoaded;

        public T LoadConfig<T>(T defaultValue) where T : class
        {
            if (defaultValue == null)
            {
                throw new ArgumentNullException(nameof(defaultValue));
            }

            lock (configSync)
            {
                if (!File.Exists(configFile) && !File.Exists(configFile + JsonUtil.BackupSuffix))
                {
                    SaveConfigCore(defaultValue);
                    return defaultValue;
                }

                try
                {
                    return JsonUtil.LoadPersistentFile(configFile, defaultValue);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to read config. Defaults will be used.");
                    return defaultValue;
                }
            }
        }

        public void SaveConfig<T>(T config) where T : class
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            lock (configSync)
            {
                SaveConfigCore(config);
            }
        }

        private void SaveConfigCore<T>(T config) where T : class
        {
            var replacementJson = JsonUtil.SerializeBounded(config);
            if (!IsObjectJson(replacementJson))
            {
                // Preserve the historical API behavior for reference types such as arrays and strings. Forward
                // object-member retention does not apply to a non-object JSON root.
                JsonUtil.SaveFile(configFile, config);
                return;
            }

            string existingJson;
            try
            {
                existingJson = JsonUtil.LoadPersistentJsonObject(configFile, "{}");
            }
            catch (InvalidDataException ex) when (IsInvalidConfigContent(ex))
            {
                // Both primary and backup are malformed/oversized (or no valid backup exists). Retaining raw
                // members is impossible, but a validated typed config can safely recover the mod on this save.
                Logger.Warn("Existing config is malformed or oversized and cannot be merged; replacing it. " + ex.Message);
                existingJson = "{}";
            }

            var merged = JsonObjectMerge.MergeSerializedContract(existingJson, replacementJson, typeof(T));
            JsonUtil.SaveJsonObject(configFile, merged);
        }

        private static bool IsObjectJson(string json)
        {
            for (var index = 0; index < json.Length; index++)
            {
                var character = json[index];
                if (character != ' ' && character != '\t' && character != '\r' && character != '\n')
                {
                    return character == '{';
                }
            }

            return false;
        }

        private static bool IsInvalidConfigContent(Exception exception)
        {
            if (exception is AggregateException aggregate)
            {
                return aggregate.InnerExceptions.Count > 0
                    && aggregate.InnerExceptions.All(IsInvalidConfigContent);
            }

            if (exception is InvalidDataException invalidData)
            {
                return invalidData.InnerException == null || IsInvalidConfigContent(invalidData.InnerException);
            }

            return exception is FormatException
                || exception is SerializationException
                || exception is XmlException
                || exception is DecoderFallbackException;
        }

        public T? GetService<T>() where T : class
        {
            if (services.TryGetValue(typeof(T), out var service))
            {
                return service as T;
            }

            return serviceRegistry.Get<T>();
        }

        public void RaiseUpdate(float deltaTime)
        {
            Update?.Invoke(deltaTime);
        }

        public void RaiseSceneLoaded(string sceneName)
        {
            SceneLoaded?.Invoke(sceneName);
        }

        private sealed class ModFileService : IModFileService
        {
            private readonly ModPaths paths;

            public ModFileService(ModPaths paths)
            {
                this.paths = paths;
            }

            public string GetPackageFilePath(string relativePath)
            {
                return SafeCombine(paths.PackagePath, relativePath);
            }

            public string GetDataFilePath(string relativePath)
            {
                Directory.CreateDirectory(paths.DataPath);
                return SafeCombine(paths.DataPath, relativePath);
            }

            public string GetConfigFilePath()
            {
                var directory = Path.GetDirectoryName(paths.ConfigPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                return paths.ConfigPath;
            }

            public static string SafeCombine(string root, string relativePath)
            {
                return PathSafety.CombineRelativeChild(root, relativePath);
            }
        }
    }
}
