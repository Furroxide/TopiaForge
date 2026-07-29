using System;
using System.IO;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class StartupJournalTests
    {
        public static void Run(string root)
        {
            TestInterruptedModLoadIsQuarantined(root);
            TestAmbiguousStartupFailureUsesSafeMode(root);
            TestCompletedStartupCrashUsesSafeModeWithoutBlame(root);
            TestCleanExitNeedsNoRecovery(root);
            TestUnknownStateFailsSafe(root);
        }

        private static void TestInterruptedModLoadIsQuarantined(string root)
        {
            var path = Path.Combine(root, "startup-journal", "loading.json");
            var first = StartupJournal.Begin(path, out var initial);
            Assert(!initial.SafeMode && initial.QuarantineModId.Length == 0, "first launch should need no recovery");
            first.MarkLoading("example.mod");

            StartupJournal.Begin(path, out var recovery);

            Assert(!recovery.SafeMode, "a precisely identified load failure should not disable unrelated mods");
            Assert(recovery.QuarantineModId == "example.mod", "the interrupted loading owner should be quarantined");
        }

        private static void TestAmbiguousStartupFailureUsesSafeMode(string root)
        {
            var path = Path.Combine(root, "startup-journal", "ambiguous.json");
            var first = StartupJournal.Begin(path, out _);
            first.MarkLoading("example.mod");
            first.MarkLoaded("example.mod");

            StartupJournal.Begin(path, out var recovery);

            Assert(recovery.SafeMode, "an interruption between mods and startup completion should use safe mode");
            Assert(recovery.QuarantineModId.Length == 0, "an ambiguous interruption must not blame the last loaded mod");
        }

        private static void TestCompletedStartupCrashUsesSafeModeWithoutBlame(string root)
        {
            var path = Path.Combine(root, "startup-journal", "ready.json");
            var first = StartupJournal.Begin(path, out _);
            first.MarkLoading("example.mod");
            first.MarkLoaded("example.mod");
            first.MarkStartupComplete();

            StartupJournal.Begin(path, out var recovery);

            Assert(recovery.SafeMode,
                "an unclean exit after successful startup should use one-shot safe mode");
            Assert(recovery.QuarantineModId.Length == 0,
                "a crash after successful startup must not be attributed to a loader callback");
        }

        private static void TestCleanExitNeedsNoRecovery(string root)
        {
            var path = Path.Combine(root, "startup-journal", "clean.json");
            var first = StartupJournal.Begin(path, out _);
            first.MarkStartupComplete();
            first.MarkCleanExit();

            StartupJournal.Begin(path, out var recovery);

            Assert(!recovery.SafeMode && recovery.QuarantineModId.Length == 0,
                "a clean exit should need no recovery action");
        }

        private static void TestUnknownStateFailsSafe(string root)
        {
            var path = Path.Combine(root, "startup-journal", "unknown.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                "{\"schemaVersion\":1,\"sessionId\":\"old\",\"state\":\"future-or-tampered\",\"currentModId\":\"\"}");

            StartupJournal.Begin(path, out var recovery);

            Assert(recovery.SafeMode && recovery.QuarantineModId.Length == 0,
                "an unknown journal state should fail safe without assigning blame");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Startup journal test failed: " + message);
            }
        }
    }
}
