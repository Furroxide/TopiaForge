using System;
using System.IO;
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
                        typeof(FakeLastRun), typeof(FakeLaunchRequest), firstName, _ => { }),
                    "the no-op request should be queued");
                firstPath = FakeLaunchRequest.Last?.ImportFolderPath;
                Assert(!string.IsNullOrWhiteSpace(firstPath) && Directory.Exists(firstPath),
                    "the queued request should point at an existing folder");
                var requiredFirstPath = firstPath!;
                Assert(FakeLaunchRequest.Last!.Mode == "SelectedFile", "the native launch must use its supported local-file mode");
                var selected = FakeLaunchRequest.Last.SelectedExportFilePath;
                Assert(selected == Path.Combine(requiredFirstPath, UgcNoOpLaunchRequest.EmptyExportName) && File.Exists(selected),
                    "the native bootstrap rejects a missing selected export; supply the original empty scene");
                using (var project = JsonDocument.Parse(File.ReadAllText(selected)))
                {
                    var root = project.RootElement;
                    Assert(root.GetProperty("assets").GetRawText() == "{}" && root.GetProperty("local-assets").GetRawText() == "{}",
                        "the bootstrap fixture must not contain user or remote assets");
                    Assert(root.GetProperty("scenes").GetProperty("topiaforge-empty").GetProperty("entities").GetRawText() == "{}",
                        "the bootstrap must supply one valid empty scene");
                }

                File.WriteAllText(Path.Combine(requiredFirstPath, "stale.json"), "{}");
                Directory.CreateDirectory(Path.Combine(requiredFirstPath, "nested"));
                File.WriteAllText(Path.Combine(requiredFirstPath, "nested", "stale.json"), "{}");

                Assert(UgcNoOpLaunchRequest.TryQueue(
                        typeof(FakeLastRun), typeof(FakeLaunchRequest), firstName, _ => { }),
                    "re-queuing the no-op request should succeed");
                Assert(string.Equals(firstPath, FakeLaunchRequest.Last?.ImportFolderPath, StringComparison.Ordinal),
                    "one caller should reuse its stable empty folder");
                Assert(Directory.GetFileSystemEntries(requiredFirstPath).Length == 1 && File.ReadAllText(selected) == UgcNoOpLaunchRequest.EmptyProjectJson,
                    "the reused folder must contain only the regenerated empty scene");
                Assert(!UgcNoOpLaunchRequest.TryQueue(typeof(FakeLastRun), typeof(FakeLaunchRequest), "..", _ => { }),
                    "parent traversal must be rejected before cleaning temporary storage");

                Assert(UgcNoOpLaunchRequest.TryQueue(
                        typeof(FakeLastRun), typeof(FakeLaunchRequest), secondName, _ => { }),
                    "a second caller should also queue successfully");
                secondPath = FakeLaunchRequest.Last?.ImportFolderPath;
                Assert(!string.Equals(firstPath, secondPath, StringComparison.Ordinal),
                    "different callers must not share a no-op import folder");
            }
            finally
            {
                TryDelete(firstPath);
                TryDelete(secondPath);
            }
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
                Last = values;
            }
        }
    }
}
