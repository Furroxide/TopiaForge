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
            try
            {
                var directory = Path.GetDirectoryName(logFile);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                // The plugin constructs this logger before its initialization try/catch. Fall back to BepInEx
                // instead of letting an invalid/read-only manager log path prevent every mod from loading.
                EmitToBepInEx(
                    "WARN",
                    "Robotopia manager log directory could not be created: " + ex.Message);
            }
        }

        public IModLogger ForMod(string modId)
        {
            return new ModLogger(modId, this);
        }

        public void Debug(string message)
        {
            Write("DEBUG", "manager", message);
        }

        public void Info(string message)
        {
            Write("INFO", "manager", message);
        }

        public void Warn(string message)
        {
            Write("WARN", "manager", message);
        }

        public void Error(string message)
        {
            Write("ERROR", "manager", message);
        }

        public void Error(Exception exception, string message)
        {
            Write("ERROR", "manager", message + Environment.NewLine + exception);
        }

        public void Write(string level, string source, string message)
        {
            var consoleMessage = string.Equals(source, "manager", StringComparison.Ordinal)
                ? message
                : "[" + source + "] " + message;

            try
            {
                lock (sync)
                {
                    File.AppendAllText(logFile, DateTime.Now.ToString("O") + " [" + level + "] [" + source + "] " + message + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                // The game/mod lifecycle must continue even if the log path becomes unwritable at runtime.
                EmitToBepInEx("WARN", "Robotopia manager file logging failed: " + ex.Message);
            }

            // BepInEx remains the observable fallback when the manager file cannot be written. Mod-scoped
            // messages include their owner so diagnostics do not lose attribution.
            EmitToBepInEx(level, consoleMessage);
        }

        private void EmitToBepInEx(string level, string message)
        {
            try
            {
                switch (level)
                {
                    case "DEBUG":
                        bepinExLogger.LogDebug(message);
                        break;
                    case "INFO":
                        bepinExLogger.LogInfo(message);
                        break;
                    case "WARN":
                        bepinExLogger.LogWarning(message);
                        break;
                    default:
                        bepinExLogger.LogError(message);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Never recurse through the file/BepInEx logger while reporting a logging failure. Console is
                // the final independent sink available during early startup and late shutdown.
                FallbackToConsole("Robotopia logging failed (" + level + "): " + ex.Message + ". Original: " + message);
            }
        }

        private static void FallbackToConsole(string message)
        {
            try
            {
                Console.Error.WriteLine(message);
            }
            catch
            {
                // There is no further independent sink. Logging must not abort mod loading or game shutdown.
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
