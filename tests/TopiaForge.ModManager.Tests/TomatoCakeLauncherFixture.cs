using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A throwaway %LOCALAPPDATA%\Tomato Cake\launcher state directory plus a separate directory the official
    /// launcher can move the game to, laid out as observed with build 2545. Tests pass <see cref="State"/>
    /// explicitly, so the Windows lookup runs against real directories on every host.
    /// </summary>
    internal sealed class TomatoCakeLauncherFixture
    {
        private TomatoCakeLauncherFixture(string root)
        {
            Root = root;
            State = Path.Combine(root, "LocalAppData", "Tomato Cake", "launcher");
            RelocatedParent = Path.Combine(root, "Games", "Tomato Cake");
            Directory.CreateDirectory(State);
        }

        internal string Root { get; }

        /// <summary>The launcher's state directory, which keeps installed-build.json.</summary>
        internal string State { get; }

        /// <summary>The game_dir a player picked in the official launcher.</summary>
        internal string RelocatedParent { get; }

        internal string DefaultGameRoot => Path.Combine(State, "Robotopia");

        internal string RelocatedGameRoot => Path.Combine(RelocatedParent, "Robotopia");

        internal string ConfigPath => Path.Combine(State, "launcher-config.json");

        internal string StateMarker => Path.Combine(State, "installed-build.json");

        internal static TomatoCakeLauncherFixture Create(string parent, string name)
        {
            return new TomatoCakeLauncherFixture(Path.Combine(parent, name));
        }

        internal void WriteConfig(string json)
        {
            WriteConfigBytes(new UTF8Encoding(false).GetBytes(json));
        }

        internal void WriteConfigBytes(byte[] bytes)
        {
            File.WriteAllBytes(ConfigPath, bytes);
        }

        internal void WriteGameDirectory(string gameDirectory)
        {
            WriteConfig("{\"game_dir\":" + Json(gameDirectory) + "}");
        }

        /// <summary>Writes the marker the way build 2545 left it, extra field included.</summary>
        internal void WriteStateMarker(int buildId)
        {
            File.WriteAllText(StateMarker, "{\"id\":" + buildId + ",\"dev\":false}");
        }

        /// <summary>A stale manifest from an earlier build, which nothing may read.</summary>
        internal void WriteStaleStateManifest()
        {
            File.WriteAllText(Path.Combine(State, "filelist.json"), "stale manifest from an earlier build");
        }

        internal static string Json(string value)
        {
            return System.Text.Json.JsonSerializer.Serialize(value);
        }

        /// <summary>Creates a directory link: a junction on Windows, which needs no privilege, else a symlink.</summary>
        internal static void LinkDirectory(string link, string target)
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return;
            }

            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("cmd.exe did not start.");
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("mklink /J failed: " + output);
            }
        }
    }
}
