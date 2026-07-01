using System;
using System.IO;
using BepInEx.Logging;
using Robotopia.Mods;

namespace Robotopia.ModManager
{
    public sealed class ManagerFileLogger
    {
        private readonly object sync = new object();
        private readonly string logFile;
        private readonly ManualLogSource bepinExLogger;

        public ManagerFileLogger(string logFile, ManualLogSource bepinExLogger)
        {
            this.logFile = logFile;
            this.bepinExLogger = bepinExLogger;
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        }

        public IModLogger ForMod(string modId)
        {
            return new ModLogger(modId, this);
        }

        public void Debug(string message)
        {
            Write("DEBUG", "manager", message);
            bepinExLogger.LogDebug(message);
        }

        public void Info(string message)
        {
            Write("INFO", "manager", message);
            bepinExLogger.LogInfo(message);
        }

        public void Warn(string message)
        {
            Write("WARN", "manager", message);
            bepinExLogger.LogWarning(message);
        }

        public void Error(Exception exception, string message)
        {
            Write("ERROR", "manager", message + Environment.NewLine + exception);
            bepinExLogger.LogError(message + Environment.NewLine + exception);
        }

        public void Write(string level, string source, string message)
        {
            lock (sync)
            {
                File.AppendAllText(logFile, DateTime.Now.ToString("O") + " [" + level + "] [" + source + "] " + message + Environment.NewLine);
            }
        }

        private sealed class ModLogger : IModLogger
        {
            private readonly string modId;
            private readonly ManagerFileLogger parent;

            public ModLogger(string modId, ManagerFileLogger parent)
            {
                this.modId = modId;
                this.parent = parent;
            }

            public void Debug(string message) => parent.Write("DEBUG", modId, message);
            public void Info(string message) => parent.Write("INFO", modId, message);
            public void Warn(string message) => parent.Write("WARN", modId, message);
            public void Error(string message) => parent.Write("ERROR", modId, message);
            public void Error(Exception exception, string message) => parent.Write("ERROR", modId, message + Environment.NewLine + exception);
        }
    }
}
