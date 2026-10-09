using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A game the official Tomato Cake launcher moved, as observed with build 2545: launcher-config.json names the
    /// new parent, the game moves to game_dir\Robotopia, and installed-build.json stays in the launcher's state
    /// directory beside a stale filelist.json.
    /// </summary>
    internal static class TomatoCakeLauncherStateTests
    {
        internal static void Run(string root)
        {
            var testRoot = Path.Combine(root, "tomato-cake-state");
            ReadsAbsentAndValidRecords(TomatoCakeLauncherFixture.Create(testRoot, "records"));
            RefusesMalformedRecords(testRoot);
            RefusesLinkedGameDirectories(TomatoCakeLauncherFixture.Create(testRoot, "links"));
            AttributesTheStateMarkerOnlyToTheRelocatedGame(TomatoCakeLauncherFixture.Create(testRoot, "attribution"));
            ChecksGameDirectoryShapes();
            RuntimeReaderFollowsTheRelocation(testRoot);
            Console.WriteLine("TomatoCakeLauncherStateTests passed.");
        }

        private static void ReadsAbsentAndValidRecords(TomatoCakeLauncherFixture fixture)
        {
            Assert(!TomatoCakeLauncherState.TryReadGameDirectory(fixture.State, out _, out var error) && error.Length == 0,
                "an absent record should be reported without an error");
            Assert(!TomatoCakeLauncherState.TryReadGameDirectory(null, out _, out error) && error.Length == 0,
                "a host without a state directory should have no record");
            Assert(TomatoCakeLauncherState.RelocatedGameRoot(fixture.State) == null,
                "an absent record should offer no relocated game");

            Directory.CreateDirectory(fixture.RelocatedGameRoot);
            fixture.WriteConfig("{\"game_dir\":" + TomatoCakeLauncherFixture.Json(
                fixture.RelocatedParent + Path.DirectorySeparatorChar) + ",\"future_setting\":{\"nested\":true}}");
            Assert(TomatoCakeLauncherState.TryReadGameDirectory(fixture.State, out var gameDirectory, out error)
                   && gameDirectory == fixture.RelocatedParent,
                "a valid record with unknown properties and a trailing separator should be read: " + error);
            Assert(TomatoCakeLauncherState.RelocatedGameRoot(fixture.State) == fixture.RelocatedGameRoot,
                "a valid record should offer game_dir\\Robotopia");

            Directory.Delete(fixture.RelocatedGameRoot);
            Assert(TomatoCakeLauncherState.RelocatedGameRoot(fixture.State) == null,
                "a relocated parent without a Robotopia folder should offer nothing");
        }

        private static void RefusesMalformedRecords(string testRoot)
        {
            var cases = new List<(string Name, Func<TomatoCakeLauncherFixture, byte[]> Bytes)>
            {
                ("truncated JSON", fixture => Utf8("{\"game_dir\":")),
                ("a duplicate game_dir", fixture => Utf8(
                    "{\"game_dir\":" + Parent(fixture) + ",\"game_dir\":" + Parent(fixture) + "}")),
                ("a duplicate nested property", fixture => Utf8(
                    "{\"game_dir\":" + Parent(fixture) + ",\"extra\":{\"a\":1,\"a\":2}}")),
                ("a duplicate property inside an array", fixture => Utf8(
                    "{\"game_dir\":" + Parent(fixture) + ",\"extra\":[{\"a\":1,\"a\":2}]}")),
                ("trailing content", fixture => Utf8("{\"game_dir\":" + Parent(fixture) + "} x")),
                ("a trailing comma", fixture => Utf8("{\"game_dir\":" + Parent(fixture) + ",}")),
                ("a comment", fixture => Utf8("{\"game_dir\":" + Parent(fixture) + " /* moved */}")),
                ("an array root", fixture => Utf8("[" + Parent(fixture) + "]")),
                ("a missing game_dir", fixture => Utf8("{\"gameDir\":\"ignored\"}")),
                ("a numeric game_dir", fixture => Utf8("{\"game_dir\":7}")),
                ("an empty game_dir", fixture => Utf8("{\"game_dir\":\"\"}")),
                ("a relative game_dir", fixture => Utf8("{\"game_dir\":\"Games/Tomato Cake\"}")),
                ("a dot-dot segment", fixture => Utf8("{\"game_dir\":" + TomatoCakeLauncherFixture.Json(
                    Path.Combine(fixture.RelocatedParent, "..", "x")) + "}")),
                ("a padded game_dir", fixture => Utf8("{\"game_dir\":" + TomatoCakeLauncherFixture.Json(
                    " " + fixture.RelocatedParent) + "}")),
                ("a stale game_dir that no longer exists", fixture => Utf8("{\"game_dir\":" +
                    TomatoCakeLauncherFixture.Json(Path.Combine(fixture.Root, "moved-away")) + "}")),
                ("a byte order mark", fixture => new byte[] { 0xEF, 0xBB, 0xBF }
                    .Concat(Utf8("{\"game_dir\":" + Parent(fixture) + "}")).ToArray()),
                ("invalid UTF-8", fixture => Utf8("{\"game_dir\":\"").Concat(new byte[] { 0xC3, 0x28 })
                    .Concat(Utf8("\"}")).ToArray()),
                ("an empty file", fixture => Array.Empty<byte>()),
                ("an oversized file", fixture => Utf8("{\"game_dir\":" + Parent(fixture) + "}" +
                    new string(' ', TomatoCakeLauncherState.MaxConfigBytes)))
            };

            var index = 0;
            foreach (var (name, bytes) in cases)
            {
                var fixture = TomatoCakeLauncherFixture.Create(testRoot, "malformed-" + index++);
                Directory.CreateDirectory(fixture.RelocatedGameRoot);
                fixture.WriteStateMarker(2545);
                fixture.WriteConfigBytes(bytes(fixture));
                AssertRefused(fixture, name);
            }

            var directoryRecord = TomatoCakeLauncherFixture.Create(testRoot, "malformed-directory");
            Directory.CreateDirectory(directoryRecord.RelocatedGameRoot);
            Directory.CreateDirectory(directoryRecord.ConfigPath);
            AssertRefused(directoryRecord, "a directory in place of the record");
        }

        private static void RefusesLinkedGameDirectories(TomatoCakeLauncherFixture fixture)
        {
            var real = Path.Combine(fixture.Root, "real");
            Directory.CreateDirectory(Path.Combine(real, "Robotopia"));
            Directory.CreateDirectory(Path.Combine(real, "nested", "Robotopia"));
            var alias = Path.Combine(fixture.Root, "alias");
            TomatoCakeLauncherFixture.LinkDirectory(alias, real);

            fixture.WriteGameDirectory(alias);
            AssertRefused(fixture, "a game_dir that is a link", Path.Combine(alias, "Robotopia"));
            fixture.WriteGameDirectory(Path.Combine(alias, "nested"));
            AssertRefused(fixture, "a game_dir beneath a link", Path.Combine(alias, "nested", "Robotopia"));

            var elsewhere = Path.Combine(fixture.Root, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            Directory.CreateDirectory(fixture.RelocatedParent);
            TomatoCakeLauncherFixture.LinkDirectory(fixture.RelocatedGameRoot, elsewhere);
            fixture.WriteGameDirectory(fixture.RelocatedParent);
            Assert(TomatoCakeLauncherState.TryReadGameDirectory(fixture.State, out _, out var error),
                "a real game_dir should be accepted even when its Robotopia folder is a link: " + error);
            Assert(TomatoCakeLauncherState.RelocatedGameRoot(fixture.State) == null,
                "a Robotopia folder that is a link should never be offered");
            Assert(TomatoCakeLauncherState.InstalledBuildMarkerFor(fixture.RelocatedGameRoot, fixture.State) == null,
                "a Robotopia folder that is a link should never receive the launcher marker");
        }

        private static void AttributesTheStateMarkerOnlyToTheRelocatedGame(TomatoCakeLauncherFixture fixture)
        {
            Directory.CreateDirectory(fixture.RelocatedGameRoot);
            Directory.CreateDirectory(fixture.DefaultGameRoot);
            var sibling = Path.Combine(fixture.RelocatedParent, "Robotopia Backup");
            var other = Path.Combine(fixture.Root, "other", "Robotopia");
            Directory.CreateDirectory(sibling);
            Directory.CreateDirectory(other);
            fixture.WriteGameDirectory(fixture.RelocatedParent);

            Assert(TomatoCakeLauncherState.InstalledBuildMarkerFor(fixture.RelocatedGameRoot, fixture.State)
                   == fixture.StateMarker,
                "the launcher marker should apply to game_dir\\Robotopia");
            Assert(TomatoCakeLauncherState.InstalledBuildMarkerFor(fixture.RelocatedGameRoot, null) == null,
                "no launcher marker should apply without a state directory");
            foreach (var unrelated in new[]
                     {
                         sibling,
                         other,
                         fixture.DefaultGameRoot,
                         Path.Combine(fixture.RelocatedParent, "missing", "Robotopia")
                     })
            {
                Assert(TomatoCakeLauncherState.InstalledBuildMarkerFor(unrelated, fixture.State) == null,
                    "the launcher marker should never apply to " + unrelated);
            }

            if (OperatingSystem.IsWindows())
            {
                fixture.WriteGameDirectory(fixture.RelocatedParent.ToUpperInvariant());
                Assert(TomatoCakeLauncherState.InstalledBuildMarkerFor(fixture.RelocatedGameRoot, fixture.State)
                       == fixture.StateMarker,
                    "Windows path comparison should ignore letter case");
            }
        }

        private static void ChecksGameDirectoryShapes()
        {
            foreach (var value in new[] { @"D:\Users\player\Local\Tomato Cake\launcher", "d:/Games/Tomato Cake/", @"D:\" })
            {
                Assert(TomatoCakeLauncherState.GameDirectoryProblem(value, windowsPaths: true) == null,
                    "Windows shape should be accepted: " + value);
            }

            foreach (var value in new[]
                     {
                         @"\\server\share\Games", "//server/share/Games", @"\\?\D:\Games", @"\\.\D:\Games",
                         "D:Games", @"\Games", @"Games\Tomato Cake", @"D:\Games\..\Other", @"D:\Games\.\Tomato Cake",
                         @"D:\Games\\Tomato Cake", @"D:\Games:stream", @"D:\Games\Tomato*", @"D:\Games.\Tomato Cake",
                         @"D:\Games \Tomato Cake", @" D:\Games", "D:\\Games\u0001", string.Empty
                     })
            {
                Assert(TomatoCakeLauncherState.GameDirectoryProblem(value, windowsPaths: true) != null,
                    "Windows shape should be refused: " + value);
            }

            foreach (var value in new[] { "/home/player/Games", "/", "/srv/games/" })
            {
                Assert(TomatoCakeLauncherState.GameDirectoryProblem(value, windowsPaths: false) == null,
                    "POSIX shape should be accepted: " + value);
            }

            foreach (var value in new[]
                     {
                         "Games", @"D:\Games", "/home/../etc", "/home/./player", "//server/share", "/home//player",
                         string.Empty
                     })
            {
                Assert(TomatoCakeLauncherState.GameDirectoryProblem(value, windowsPaths: false) != null,
                    "POSIX shape should be refused: " + value);
            }
        }

        private static void RuntimeReaderFollowsTheRelocation(string testRoot)
        {
            var defaultLayout = TomatoCakeLauncherFixture.Create(testRoot, "runtime-default");
            Directory.CreateDirectory(defaultLayout.DefaultGameRoot);
            defaultLayout.WriteStateMarker(2545);
            Assert(ReadVersion(defaultLayout.DefaultGameRoot, defaultLayout.State) == "0.0.2545",
                "runtime should keep reading the marker beside the default game folder");

            var relocated = TomatoCakeLauncherFixture.Create(testRoot, "runtime-relocated");
            Directory.CreateDirectory(relocated.RelocatedGameRoot);
            relocated.WriteStateMarker(2545);
            relocated.WriteStaleStateManifest();
            Assert(ReadVersion(relocated.RelocatedGameRoot, relocated.State) == string.Empty,
                "runtime should not attribute the launcher marker without launcher-config.json");
            relocated.WriteGameDirectory(relocated.RelocatedParent);
            Assert(ReadVersion(relocated.RelocatedGameRoot, relocated.State) == "0.0.2545",
                "runtime should read the launcher marker for the relocated game");
            Assert(ReadVersion(relocated.RelocatedGameRoot, null) == string.Empty,
                "runtime should not consult the launcher without a state directory");

            var other = Path.Combine(relocated.Root, "other", "Robotopia");
            Directory.CreateDirectory(other);
            Assert(!InstalledGameVersionReader.TryRead(other, relocated.State, out _, out var error)
                   && error.Contains("not found", StringComparison.Ordinal),
                "runtime should not lend the launcher marker to an install outside game_dir: " + error);

            relocated.WriteStateMarker(2544);
            var beside = Path.Combine(relocated.RelocatedParent, "installed-build.json");
            File.WriteAllText(beside, "{\"id\":2545}");
            Assert(ReadVersion(relocated.RelocatedGameRoot, relocated.State) == "0.0.2545",
                "a marker beside the game should outrank a stale launcher marker");
            File.WriteAllText(beside, "{\"id\":0}");
            Assert(!InstalledGameVersionReader.TryRead(relocated.RelocatedGameRoot, relocated.State, out _, out error)
                   && error.Contains("rejected", StringComparison.Ordinal),
                "an invalid marker beside the game should fail closed instead of falling back: " + error);
            File.Delete(beside);

            var value = TomatoCakeLauncherFixture.Json(relocated.RelocatedParent);
            relocated.WriteConfig("{\"game_dir\":" + value + ",\"game_dir\":" + value + "}");
            Assert(ReadVersion(relocated.RelocatedGameRoot, relocated.State) == string.Empty,
                "a malformed launcher-config.json should lend no marker");
        }

        private static string ReadVersion(string gameRoot, string? state)
        {
            return InstalledGameVersionReader.TryRead(gameRoot, state, out var version, out _) ? version : string.Empty;
        }

        private static void AssertRefused(TomatoCakeLauncherFixture fixture, string name, string? gameRoot = null)
        {
            Assert(!TomatoCakeLauncherState.TryReadGameDirectory(fixture.State, out var gameDirectory, out var error)
                   && gameDirectory.Length == 0
                   && error.StartsWith("launcher-config.json was rejected: ", StringComparison.Ordinal),
                "launcher-config.json with " + name + " should be refused with a reason, got '" + error + "'");
            Assert(TomatoCakeLauncherState.RelocatedGameRoot(fixture.State) == null,
                "launcher-config.json with " + name + " should offer no relocated game");
            Assert(TomatoCakeLauncherState.InstalledBuildMarkerFor(gameRoot ?? fixture.RelocatedGameRoot, fixture.State)
                   == null,
                "launcher-config.json with " + name + " should lend no marker");
        }

        private static string Parent(TomatoCakeLauncherFixture fixture)
        {
            return TomatoCakeLauncherFixture.Json(fixture.RelocatedParent);
        }

        private static byte[] Utf8(string text)
        {
            return new UTF8Encoding(false).GetBytes(text);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Tomato Cake launcher state: " + message);
            }
        }
    }
}
