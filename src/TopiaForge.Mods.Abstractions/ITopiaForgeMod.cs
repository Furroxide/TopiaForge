using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    public interface ITopiaForgeMod
    {
        void OnLoad(IModContext context);
        void OnUnload();
    }

    public interface IModContext
    {
        string ModId { get; }
        string ModName { get; }
        Version Version { get; }
        ModPaths Paths { get; }
        IModLogger Logger { get; }

        event Action<float> Update;
        event Action<string> SceneLoaded;

        T LoadConfig<T>(T defaultValue) where T : class;
        void SaveConfig<T>(T config) where T : class;
        T? GetService<T>() where T : class;
    }

    public interface IModServiceRegistry
    {
        IReadOnlyList<ModServiceRegistration> Services { get; }
        void Register<T>(string ownerModId, T service) where T : class;
        void UnregisterOwner(string ownerModId);
        T? Get<T>() where T : class;
    }

    public interface IModLogger
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Error(Exception exception, string message);
    }

    public sealed class ModPaths
    {
        public ModPaths(string packagePath, string configPath, string dataPath)
        {
            PackagePath = packagePath;
            ConfigPath = configPath;
            DataPath = dataPath;
        }

        public string PackagePath { get; }
        public string ConfigPath { get; }
        public string DataPath { get; }
    }

    public sealed class ModServiceRegistration
    {
        public ModServiceRegistration(string ownerModId, Type serviceType, object service)
        {
            OwnerModId = ownerModId;
            ServiceType = serviceType;
            Service = service;
        }

        public string OwnerModId { get; }
        public Type ServiceType { get; }
        public object Service { get; }
    }

    public interface IModFileService
    {
        string GetPackageFilePath(string relativePath);
        string GetDataFilePath(string relativePath);
        string GetConfigFilePath();
    }

}
