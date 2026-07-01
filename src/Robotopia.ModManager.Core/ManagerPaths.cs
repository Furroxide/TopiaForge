using System.IO;

namespace Robotopia.ModManager.Core
{
    public sealed class ManagerPaths
    {
        public ManagerPaths(string bepinExRoot)
        {
            BepInExRoot = Path.GetFullPath(bepinExRoot);
            Root = Path.Combine(BepInExRoot, "RobotopiaModManager");
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
            return Path.Combine(Packages, id, version);
        }

        public string GetConfigPath(string id)
        {
            return Path.Combine(Config, id + ".json");
        }

        public string GetDataPath(string id)
        {
            return Path.Combine(Data, id);
        }
    }
}
