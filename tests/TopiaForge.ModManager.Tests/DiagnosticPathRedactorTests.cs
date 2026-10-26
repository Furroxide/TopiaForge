using System;
using System.Collections.Generic;
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
            var once = DiagnosticPathRedactor.Redact(stack);
            Check(DiagnosticPathRedactor.Redact(once) == once, "redaction is idempotent");
            var hostile = "'" + paths[0] + new string('x', 200000) + "'";
            Check(DiagnosticPathRedactor.Redact(hostile) == "'[path]'", "long input");
            Console.WriteLine("DiagnosticPathRedactorTests passed: 40 checks.");
        }

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Path redaction failed: " + name);
        }
    }
}
