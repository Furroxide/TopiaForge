using System;
using System.Collections.Generic;
using System.IO;
using Robotopia.ModManager.Core;
using Robotopia.Mods;

namespace Robotopia.ModManager
{
    public sealed class ModContext : IModContext
    {
        private readonly string configFile;
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
            services[typeof(IModServiceRegistry)] = serviceRegistry;
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
            if (!File.Exists(configFile))
            {
                SaveConfig(defaultValue);
                return defaultValue;
            }

            try
            {
                return JsonUtil.LoadFile(configFile, defaultValue);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to read config. Defaults will be used.");
                return defaultValue;
            }
        }

        public void SaveConfig<T>(T config) where T : class
        {
            JsonUtil.SaveFile(configFile, config);
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
                if (Path.IsPathRooted(relativePath))
                {
                    throw new InvalidOperationException("Path must be relative.");
                }

                var rootFullPath = Path.GetFullPath(root);
                var combined = Path.GetFullPath(Path.Combine(rootFullPath, relativePath));
                if (!combined.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Path escapes the mod directory.");
                }

                return combined;
            }
        }
    }
}
