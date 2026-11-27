using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.GameBridge;

namespace TopiaForge.ModManager.Tests
{
    internal static class UgcImportProblemsTests
    {
        private const string PrivateText = "C:/Users/someone/private.prefab";

        internal static void Run()
        {
            EmptyReportLogsNothing();
            ProblemsAreCountedWithoutTheirText();
            OtherCollectionShapesAreCounted();
            UnreadableReportsSayTheyAreUnreadable();
            LoggerFailuresStayInside();
            Console.WriteLine("UGC import problem report tests passed (5 cases).");
        }

        private static void EmptyReportLogsNothing()
        {
            var logger = new RecordingLogger();
            UgcImportProblems.Warn(logger, "Worlds", "importing a world", new ListReport());
            Assert(UgcImportProblems.Count(new ListReport()) == 0 && logger.Warnings.Count == 0,
                "An empty native report must not produce a log line.");
        }

        private static void ProblemsAreCountedWithoutTheirText()
        {
            var report = new ListReport();
            report.Add(PrivateText, "detail naming " + PrivateText);
            report.Add("second", PrivateText);
            var logger = new RecordingLogger();
            UgcImportProblems.Warn(logger, "Worlds", "importing a world", report);
            Assert(logger.Warnings.Count == 1, "A report with problems must produce exactly one warning.");
            var line = logger.Warnings[0];
            Assert(line.StartsWith("Worlds: The game reported 2 problems while importing a world.", StringComparison.Ordinal),
                "The warning must name the source, the count and the operation: " + line);
            Assert(!line.Contains("private.prefab", StringComparison.Ordinal) && !line.Contains("detail", StringComparison.Ordinal),
                "Native problem text must never reach the log: " + line);
            var single = new ListReport();
            single.Add("one", "one");
            Assert(UgcImportProblems.Summary("building a composed robot", single)!.Contains("1 problem while", StringComparison.Ordinal),
                "A single problem must read in the singular.");
        }

        private static void OtherCollectionShapesAreCounted()
        {
            Assert(UgcImportProblems.Count(new EnumerableReport(3)) == 3,
                "A Problems list that is only enumerable must still be counted.");
            Assert(UgcImportProblems.Count(new EnumerableReport(int.MaxValue)) == 10000,
                "An unbounded Problems enumerable must stop at the counting budget.");
        }

        private static void UnreadableReportsSayTheyAreUnreadable()
        {
            foreach (var report in new object?[] { null, new object(), new NullReport(), new ThrowingReport() })
            {
                Assert(UgcImportProblems.Count(report) == UgcImportProblems.Unreadable,
                    "A report without a readable Problems list must count as unreadable: " + (report?.GetType().Name ?? "null"));
                Assert(UgcImportProblems.Summary("importing a world", report) ==
                    "The game's problem report for importing a world could not be read.",
                    "An unreadable report must say so in fixed text.");
            }
        }

        private static void LoggerFailuresStayInside()
        {
            var report = new ListReport();
            report.Add("one", "one");
            UgcImportProblems.Warn(new ThrowingLogger(), "RobotKit", "building a composed robot", report);
            UgcImportProblems.Warn(null, "RobotKit", "building a composed robot", report);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("UgcImportProblems: " + message);
        }

        // Stand-ins shaped like build 2640's UgcImportReport: a public read-only Problems list.
        private readonly struct Problem
        {
            public readonly string Title;
            public readonly string Detail;
            public Problem(string title, string detail) { Title = title; Detail = detail; }
        }

        private sealed class ListReport
        {
            private readonly List<Problem> problems = new List<Problem>();
            public IReadOnlyList<Problem> Problems => problems;
            public void Add(string title, string detail) => problems.Add(new Problem(title, detail));
        }

        private sealed class EnumerableReport
        {
            private readonly int count;
            public EnumerableReport(int count) { this.count = count; }
            public IEnumerable<Problem> Problems
            {
                get { for (var i = 0; i < count; i++) yield return new Problem("title", "detail"); }
            }
        }

        private sealed class NullReport
        {
            public IReadOnlyList<Problem>? Problems => null;
        }

        private sealed class ThrowingReport
        {
            public IReadOnlyList<Problem> Problems => throw new InvalidOperationException(PrivateText);
        }

        private sealed class RecordingLogger : IModLogger
        {
            public readonly List<string> Warnings = new List<string>();
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) => Warnings.Add(message);
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
        }

        private sealed class ThrowingLogger : IModLogger
        {
            public void Debug(string message) => throw new InvalidOperationException();
            public void Info(string message) => throw new InvalidOperationException();
            public void Warn(string message) => throw new InvalidOperationException();
            public void Error(string message) => throw new InvalidOperationException();
            public void Error(Exception exception, string message) => throw new InvalidOperationException();
        }
    }
}
