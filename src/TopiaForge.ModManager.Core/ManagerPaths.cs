using System;
using System.IO;

namespace TopiaForge.ModManager.Core
{
    public sealed class ManagerPaths
    {
        private const string FirstPartyIdPrefix = "io.github.furroxide.topiaforge.";
        private const string FirstPartyConfigPrefix = "topiaforge.";

        public ManagerPaths(string bepinExRoot)
        {
            BepInExRoot = Path.GetFullPath(bepinExRoot);
            Root = Path.Combine(BepInExRoot, "TopiaForge");
            Packages = Path.Combine(Root, "packages");
            PackageInbox = Path.Combine(Root, "package-inbox");
            Config = Path.Combine(Root, "config");
            Data = Path.Combine(Root, "data");
            Logs = Path.Combine(Root, "logs");
            Staging = Path.Combine(Root, "staging");
            StateFile = Path.Combine(Root, "state.json");
            ManagerLogFile = Path.Combine(Logs, "manager.log");
        }

        public string BepInExRoot { get; }
        public string Root { get; }
        public string Packages { get; }
        public string PackageInbox { get; }
        public string Config { get; }
        public string Data { get; }
        public string Logs { get; }
        public string Staging { get; }
        public string StateFile { get; }
        public string ManagerLogFile { get; }

        public void EnsureCreated()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Packages);
            Directory.CreateDirectory(PackageInbox);
            Directory.CreateDirectory(Config);
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Logs);
            Directory.CreateDirectory(Staging);
        }

        public string GetPackagePath(string id, string version)
        {
            if (!VersionUtil.TryParse(version, out _))
            {
                throw new InvalidDataException("Package version is not a safe semantic version: " + version);
            }

            return PathSafety.CombineRelativeChild(GetPackageIdPath(id), version);
        }

        public string GetPackageIdPath(string id)
        {
            if (!TryGetPackageIdPath(id, out var path))
            {
                throw new InvalidDataException("Package id is not safe: " + id);
            }

            return path;
        }

        public bool TryGetPackageIdPath(string? id, out string path)
        {
            path = string.Empty;
            if (!ManifestValidator.IsValidId(id))
            {
                return false;
            }

            try
            {
                path = PathSafety.CombineRelativeChild(Packages, id!);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is InvalidOperationException)
            {
                path = string.Empty;
                return false;
            }
        }

        public string GetConfigPath(string id)
        {
            EnsureSafeModId(id);
            var fileStem = id.StartsWith(FirstPartyIdPrefix, StringComparison.Ordinal)
                ? FirstPartyConfigPrefix + id.Substring(FirstPartyIdPrefix.Length)
                : id;
            return PathSafety.CombineRelativeChild(Config, fileStem + ".json");
        }

        public string GetDataPath(string id)
        {
            EnsureSafeModId(id);
            return PathSafety.CombineRelativeChild(Data, id);
        }

        private static void EnsureSafeModId(string id)
        {
            if (!ManifestValidator.IsValidId(id))
            {
                throw new InvalidDataException("Mod id is not safe: " + id);
            }
        }
    }
}
