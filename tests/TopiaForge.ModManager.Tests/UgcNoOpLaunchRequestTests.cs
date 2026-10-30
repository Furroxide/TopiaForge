using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TopiaForge.Mods.GameBridge;

namespace TopiaForge.ModManager.Tests
{
    internal static class UgcNoOpLaunchRequestTests
    {
        public static void Run()
        {
            var firstName = "TopiaForgeNoOpTest-" + Guid.NewGuid().ToString("N");
            var secondName = firstName + "-other";
            string? firstPath = null;
            string? secondPath = null;
            try
            {
                Assert(UgcNoOpLaunchRequest.TryQueue(
                        typeof(FakeLastRun), typeof(FakeLaunchRequest), firstName, _ => { }, ValidateExport),
                    "the no-op request should be queued");
                firstPath = FakeLaunchRequest.Last?.ImportFolderPath;
                Assert(!string.IsNullOrWhiteSpace(firstPath) && Directory.Exists(firstPath),
                    "the queued request should point at an existing folder");
                var requiredFirstPath = firstPath!;

                File.WriteAllText(Path.Combine(requiredFirstPath, "stale.json"), "{}");
                Directory.CreateDirectory(Path.Combine(requiredFirstPath, "nested"));
                File.WriteAllText(Path.Combine(requiredFirstPath, "nested", "stale.json"), "{}");

                Assert(UgcNoOpLaunchRequest.TryQueue(
                        typeof(FakeLastRun), typeof(FakeLaunchRequest), firstName, _ => { }, ValidateExport),
                    "re-queuing the no-op request should succeed");
                Assert(string.Equals(firstPath, FakeLaunchRequest.Last?.ImportFolderPath, StringComparison.Ordinal),
                    "one caller should reuse its stable empty folder");
                Assert(Directory.GetFiles(requiredFirstPath).Length == 1 && Directory.GetDirectories(requiredFirstPath).Length == 0,
                    "the reused folder retains only the validated generated export");

                Assert(UgcNoOpLaunchRequest.TryQueue(
                        typeof(FakeLastRun), typeof(FakeLaunchRequest), secondName, _ => { }, ValidateExport),
                    "a second caller should also queue successfully");
                secondPath = FakeLaunchRequest.Last?.ImportFolderPath;
                Assert(!string.Equals(firstPath, secondPath, StringComparison.Ordinal),
                    "different callers must not share a no-op import folder");
                var queued = FakeLaunchRequest.Last;
                Assert(queued != null && queued.Mode == "SelectedFile" && File.Exists(queued.SelectedExportFilePath),
                    "SelectedFile must carry a real nonempty selected export before native Awake");
                var before = queued;
                Assert(!UgcNoOpLaunchRequest.TryQueue(typeof(FakeLastRun), typeof(FakeLaunchRequest), firstName, _ => { }, _ => false)
                    && ReferenceEquals(before, FakeLaunchRequest.Last), "native parser rejection never arms a request");
                Assert(!UgcNoOpLaunchRequest.TryQueue(typeof(FakeLastRun), typeof(FakeLaunchRequest), firstName, _ => { })
                    && ReferenceEquals(before, FakeLaunchRequest.Last), "missing validator never arms an unchecked request");
                string? debug = null;
                Assert(!UgcNoOpLaunchRequest.TryQueue(typeof(FakeLastRun), typeof(FakeLaunchRequest), firstName, message => debug = message, _ => throw new Exception("private native text"))
                    && ReferenceEquals(before, FakeLaunchRequest.Last), "validator faults remain pre-dispatch failures");
                Assert(debug != null && !debug.Contains("private native text", StringComparison.Ordinal), "preflight diagnostics omit native text");
                Console.WriteLine("UgcNoOpLaunchRequestTests passed: real selected empty export, stale cleanup, independent folders, validator rejection/fault and privacy.");
            }
            finally
            {
                TryDelete(firstPath);
                TryDelete(secondPath);
            }
        }

        private static bool ValidateExport(string path)
        {
            Assert(File.Exists(path) && Path.GetExtension(path) == ".roboworld", "generated export exists before validation");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert(root.GetProperty("version").GetString() == "1.0" && !root.GetProperty("assets").EnumerateObject().Any()
                && !root.GetProperty("local-assets").EnumerateObject().Any(), "bootstrap has no assets or external sources");
            var scenes = root.GetProperty("scenes").EnumerateObject().ToArray();
            Assert(scenes.Length == 1 && scenes[0].Name == scenes[0].Value.GetProperty("id").GetString()
                && !scenes[0].Value.GetProperty("entities").EnumerateObject().Any(), "one resolvable empty scene imports no user content");
            return true;
        }
        private static void TryDelete(string? path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        public sealed class FakeLastRun
        {
            public string Mode = string.Empty;
            public string ImportFolderPath = string.Empty;
            public string SelectedExportFilePath = string.Empty;
        }

        public static class FakeLaunchRequest
        {
            public static FakeLastRun? Last { get; private set; }

            public static void Create(FakeLastRun values)
            {
                Assert(values.Mode == "SelectedFile" && !string.IsNullOrWhiteSpace(values.SelectedExportFilePath)
                    && File.Exists(values.SelectedExportFilePath), "the native SelectedFile contract must never see an empty selection");
                Last = values;
            }
        }
    }
}
