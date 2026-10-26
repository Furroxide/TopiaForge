using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static class DiagnosticPathRedactorTests
    {
        internal static void Run()
        {
            var paths = new List<string>
            {
                "Q:" + '\\' + "Private Folder" + '\\' + "secret",
                "q:" + '/' + "Private Folder/secret",
                new string('\\', 2) + "private-server" + '\\' + "share" + '\\' + "secret",
                new string('\\', 2) + "?" + '\\' + "Q:" + '\\' + "secret",
                new string('\\', 2) + "." + '\\' + "pipe" + '\\' + "secret",
                "/" + "private folder/secret",
                "\\" + "Private Folder" + '\\' + "secret",
                "file:" + new string('/', 3) + "private%20folder/secret",
                "/" + "private/日本語/secret"
            };
            foreach (var path in paths)
            {
                Check(DiagnosticPathRedactor.Redact("Cannot read '" + path + "'; try again.") == "Cannot read '[path]'; try again.", "quoted path and advice");
                Check(DiagnosticPathRedactor.Redact("Cannot read " + path + " with spaces") == "Cannot read [path]", "unquoted path with spaces");
                Check(MultiplayerDiagnosticRedactor.Redact("Cannot read '" + path + "'; try again.") == "Cannot read '[path]'; try again.", "multiplayer path");
            }

            const string frames = "System.IO.IOException: blocked\n   at Example.Save() in ";
            var stack = frames + paths[0] + ":line 7\n   at Example.Load()";
            Check(DiagnosticPathRedactor.Redact(stack) == frames + "[path]\n   at Example.Load()", "stack retains methods and lines without paths");
            Check(DiagnosticPathRedactor.Redact("folder=private HOME/data", "private home") == "folder=[path]", "home match ignoring case");
            Check(DiagnosticPathRedactor.Redact("folder=private\\HOME/data", "private/HOME") == "folder=[path]", "home separator variants");
            Check(DiagnosticPathRedactor.Redact(null) == string.Empty, "null");
            foreach (var text in new[] { "retry in 10 ms", "System.IO.IOException", "io.example.mod", "version 1.2.3", "relative/file", "enabled/disabled", "https://example.invalid/help" })
                Check(DiagnosticPathRedactor.Redact(text) == text, "non-path diagnostics unchanged");
            foreach (var text in new[] { "Wave 3 / 10 cleared", "Capture \\ Hold", "a trailing /" })
                Check(DiagnosticPathRedactor.Redact(text) == text, "a slash on its own is punctuation: " + text);
            var once = DiagnosticPathRedactor.Redact(stack);
            Check(DiagnosticPathRedactor.Redact(once) == once, "redaction is idempotent");
            var hostile = "'" + paths[0] + new string('x', 200000) + "'";
            Check(DiagnosticPathRedactor.Redact(hostile) == "'[path]'", "long input");
            var checks = 43 + ApostrophesBelongToThePath(paths[0]) + PathsNeverExposeAnAddress() + LabelsKeepTheirSlashes()
                + PluginWritesGoThroughTheFilter();
            Console.WriteLine("DiagnosticPathRedactorTests passed: " + checks + " checks.");
        }

        // An apostrophe inside a name, as in a profile or folder named Sam's, is part of the path, so nothing after it
        // leaks, quoted or not; a closing quote followed by punctuation still ends the path.
        private static int ApostrophesBelongToThePath(string plain)
        {
            var named = new[]
            {
                "Q:" + '\\' + "Users" + '\\' + "Sam's" + '\\' + "AppData" + '\\' + "secret",
                "Q:" + '\\' + "Users" + '\\' + "Sam" + '\\' + "Sam's mods" + '\\' + "secret",
                "/" + "home/sam's/.config/secret"
            };
            foreach (var path in named)
            {
                Check(DiagnosticPathRedactor.Redact("Access to the path '" + path + "' is denied.") == "Access to the path '[path]' is denied.",
                    "an apostrophe in a quoted path");
                Check(DiagnosticPathRedactor.Redact("Sharing violation on path " + path) == "Sharing violation on path [path]",
                    "an apostrophe in an unquoted path");
                Check(MultiplayerDiagnosticRedactor.Redact("Cannot read '" + path + "' now.") == "Cannot read '[path]' now.",
                    "an apostrophe in a quoted multiplayer path");
            }

            Check(DiagnosticPathRedactor.Redact("Could not find file '" + plain + "'.") == "Could not find file '[path]'.", "a closing quote ends the path");
            return named.Length * 3 + 1;
        }

        // Paths and addresses are both found in the text as it came: removing a path never leaves part of an address
        // standing, and the combined filter keeps nothing that either filter alone hides.
        private static int PathsNeverExposeAnAddress()
        {
            var cases = new[]
            {
                ("endpoint myhost:27250=file:///x failed", "myhost"),
                ("peer 2001:db8::5:/10.1.2.3 left", "2001:db8"),
                ("relay=play.example.org-file:///x", "example.org")
            };
            foreach (var (text, address) in cases)
                Check(MultiplayerDiagnosticRedactor.Redact(text).IndexOf(address, StringComparison.OrdinalIgnoreCase) < 0, "removing a path keeps " + address + " hidden");

            var pieces = new[]
            {
                "10.1.2.3:27250", "[2001:db8::5]:27250", "fe80::1%eth0", "play.example.org", "myhost:27250", "udp://10.1.2.3:27250",
                "Q:" + '\\', "/", new string('\\', 2), "file:///", "Users" + '\\' + "Sam", "/home/sam", "Sam's", "x", ":", "=", ",", " ", "'", "\"", "(",
                "[", "\n", "-"
            };
            var cores = new[] { "10.1.2.3", "2001:db8::5", "fe80::1", "play.example.org", "myhost", "Users" + '\\' + "Sam", "home/sam" };
            var random = new Random(320);
            for (var item = 0; item < 5000; item++)
            {
                var text = string.Empty;
                for (var part = random.Next(2, 9); part > 0; part--) text += pieces[random.Next(pieces.Length)];
                var both = MultiplayerDiagnosticRedactor.Redact(text);
                var addresses = MultiplayerDiagnosticRedactor.RedactAddresses(text);
                var paths = DiagnosticPathRedactor.Redact(text);
                foreach (var core in cores)
                {
                    if (both.IndexOf(core, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Check(addresses.IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0 && paths.IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0,
                        "the combined filter keeps nothing either filter hides: " + text.Replace("\n", "\\n"));
                }
            }

            return cases.Length + 1;
        }

        // Words another player wrote keep their slashes on a label, while their addresses still go; a diagnostic still
        // withholds what reads as a path, such as /help.
        private static int LabelsKeepTheirSlashes()
        {
            var names = new[] { "EU / NA PvP", "Type /help for the rules", "Capture \\ Hold" };
            foreach (var name in names)
            {
                Check(HostText.ForDisplay(name, HostText.LineChars) == name, "a label keeps the slashes of " + name);
                Check(HostText.HostSays(name) == HostText.HostSaysPrefix + name, "a host message keeps the slashes of " + name);
            }

            Check(MultiplayerDiagnosticRedactor.Redact(names[1]) == "Type [path]", "a diagnostic still withholds what reads as a path");
            Check(HostText.ForDisplay("Join 10.1.2.3 now") == "Join [address] now", "a label still loses addresses");
            return names.Length * 2 + 2;
        }

        // The plugin writes to BepInEx's own log only before the manager log exists, or after it has gone, and those
        // writes go through the same filter as the manager log's.
        private static int PluginWritesGoThroughTheFilter()
        {
            const string Filtered = "TopiaForge.Mods.Internal.DiagnosticPathRedactor.Redact(";
            var runtime = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.ModManager");
            var writes = 0;
            foreach (var file in Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                var source = File.ReadAllText(file);
                foreach (Match call in Regex.Matches(source, @"\bLogger\.Log[A-Za-z]*\("))
                {
                    writes++;
                    Check(string.CompareOrdinal(source, call.Index + call.Length, Filtered, 0, Filtered.Length) == 0,
                        "a direct BepInEx write in " + Path.GetFileName(file) + " goes through the path filter");
                }
            }

            Check(writes >= 2, "the plugin's own BepInEx writes were found");
            return writes + 1;
        }

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Path redaction failed: " + name);
        }
    }
}
