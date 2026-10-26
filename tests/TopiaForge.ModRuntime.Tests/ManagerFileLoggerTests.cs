using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using TopiaForge.ModManager;

namespace TopiaForge.ModRuntime.Tests
{
    internal static class ManagerFileLoggerTests
    {
        internal static void Run()
        {
            var root = Directory.CreateTempSubdirectory("TopiaForgeLogPrivacy-");
            try
            {
                FileAndBepInEx(root.FullName);
                StartupAndWriteFailure(root.FullName);
                ConsoleFallback(root.FullName);
                Console.WriteLine("ManagerFileLoggerTests passed: file, BepInEx, startup, write failure, console fallback.");
            }
            finally { root.Delete(true); }
        }

        private static void FileAndBepInEx(string root)
        {
            using var source = new ManualLogSource("Privacy test");
            var observed = new List<string>();
            source.LogEvent += (_, args) => observed.Add(args.Data.ToString()!);
            var file = Path.Combine(root, "manager.log");
            using (var log = new ManagerFileLogger(file, source))
            {
                var privatePath = Path.Combine(root, "private folder", "secret");
                var message = "Could not read '" + privatePath + "'; try again.";
                log.Debug(message);
                log.Info(message);
                log.Warn(message);
                log.Error(message);
                log.Error(Failure(privatePath), message);
                var mod = log.ForMod("example.mod");
                mod.Debug(message);
                mod.Info(message);
                mod.Warn(message);
                mod.Error(message);
                mod.Error(Failure(privatePath), message);
                log.Write(privatePath, privatePath, message);
            }

            var contents = File.ReadAllText(file);
            Check(!contents.Contains(root, StringComparison.OrdinalIgnoreCase), "file contains no root");
            Check(contents.Contains("[example.mod]") && contents.Contains("[path]"), "file keeps attribution and replacement");
            Check(contents.Contains("InvalidOperationException") && contents.Contains("IOException") && contents.Contains(nameof(Failure)), "file keeps exception chain and frames");
            Check(observed.Count == 11, "every entry reaches BepInEx");
            foreach (var line in observed)
                Check(!line.Contains(root, StringComparison.OrdinalIgnoreCase) && line.Contains("[path]"), "BepInEx contains no root");
        }

        private static Exception Failure(string path)
        {
            try { throw new InvalidOperationException("Blocked '" + path + "'.", new IOException("Inner '" + path + "'.")); }
            catch (Exception failure) { return failure; }
        }

        private static void StartupAndWriteFailure(string root)
        {
            using var source = new ManualLogSource("Failure test");
            var observed = new List<string>();
            source.LogEvent += (_, args) => observed.Add(args.Data.ToString()!);
            // A directory at the file location fails on both Windows and Unix without permission changes.
            var destination = Directory.CreateDirectory(Path.Combine(root, "occupied"));
            using var log = new ManagerFileLogger(destination.FullName, source);
            log.Info("The mod keeps running.");
            Check(observed.Count == 3, "constructor warning, write warning and original entry");
            Check(observed[0].Contains("directory could not be created") && observed[0].Contains("[path]"), "startup redaction");
            Check(observed[1].Contains("file logging failed") && observed[1].Contains("[path]"), "write-failure redaction");
            foreach (var line in observed) Check(!line.Contains(root, StringComparison.OrdinalIgnoreCase), "failure contains no root");
        }

        private static void ConsoleFallback(string root)
        {
            using var source = new ManualLogSource("Throwing listener");
            source.LogEvent += (_, _) => throw new IOException("Listener failed '" + root + "'.");
            var original = Console.Error;
            using var capture = new StringWriter();
            try
            {
                Console.SetError(capture);
                using var log = new ManagerFileLogger(Path.Combine(root, "fallback.log"), source);
                log.Info("Original '" + root + "'.");
            }
            finally { Console.SetError(original); }
            var output = capture.ToString();
            Check(output.Contains("Listener failed '[path]'") && output.Contains("Original '[path]'"), "console redacts listener and original");
            Check(!output.Contains(root, StringComparison.OrdinalIgnoreCase), "console contains no root");
        }

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Logger privacy failed: " + name);
        }
    }
}
