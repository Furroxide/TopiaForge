using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Robotopia.ModManager.Core;
using Robotopia.Mods;
using Robotopia.UgcLiveSync;

namespace Robotopia.ModManager.Tests
{
    // Exercises the Unity-free UGC live-sync service state machine with a fake bridge (no GameCode/UnityEngine).
    // The service files are compiled into this assembly via <Compile Include> in the csproj.
    internal static class UgcLiveSyncTests
    {
        public static void Run()
        {
            TestValidationHelpers();
            TestEditorUrlParsing();
            TestFindNewestSnapshot();
            TestLocalFirstThenSubsequent();
            TestGarbageAndOversizeRejected();
            TestApplyErrorKeepsWatching();
            TestLifecycleCleanup();
            TestAutomergeSession();
            TestLauncherDeployedAutomergePayloadStartsSession();
            TestStatusFileRoundTrip();
            Console.WriteLine("All UGC live-sync service tests passed.");
        }

        private static void TestValidationHelpers()
        {
            Assert(UgcLiveSyncService.LooksLikeProjectJson(Bytes("{\"a\":1}")), "object json should pass");
            Assert(UgcLiveSyncService.LooksLikeProjectJson(Bytes("   \n\t {\"a\":1}")), "leading whitespace json should pass");
            Assert(UgcLiveSyncService.LooksLikeProjectJson(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{' }), "BOM + brace should pass");
            Assert(UgcLiveSyncService.LooksLikeProjectJson(new byte[] { 0x1f, 0x8b, 0x08, 0x00 }), "gzip magic should pass");
            Assert(!UgcLiveSyncService.LooksLikeProjectJson(Bytes("nonsense")), "non-json should fail");
            Assert(!UgcLiveSyncService.LooksLikeProjectJson(Bytes("[1,2,3]")), "json array should fail (project is an object)");
            Assert(!UgcLiveSyncService.LooksLikeProjectJson(Array.Empty<byte>()), "empty should fail");
        }

        private static void TestEditorUrlParsing()
        {
            Assert(UgcLiveSyncService.TryParseEditorUrl("https://editor.example/?project=automerge:abc123&scene=main", out var doc, out var scene),
                "editor url with project should parse");
            Assert(doc == "automerge:abc123", "document url should be the project param, got: " + doc);
            Assert(scene == "main", "scene id should be the scene param, got: " + scene);

            Assert(!UgcLiveSyncService.TryParseEditorUrl("https://editor.example/?foo=bar", out _, out _), "url without project should not parse");
            Assert(!UgcLiveSyncService.TryParseEditorUrl("not a url", out _, out _), "garbage should not parse");
        }

        private static void TestFindNewestSnapshot()
        {
            var dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "ignore.txt"), "x");
                var older = Path.Combine(dir, "older.json");
                var newer = Path.Combine(dir, "newer.json.gz");
                File.WriteAllText(older, "{}");
                File.WriteAllText(newer, "{}");
                File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
                File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

                var found = UgcLiveSyncService.FindNewestSnapshot(dir);
                Assert(found == newer, "newest snapshot should be the .json.gz, got: " + found);
                Assert(UgcLiveSyncService.FindNewestSnapshot(Path.Combine(dir, "missing")) == null, "missing folder returns null");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestLocalFirstThenSubsequent()
        {
            var dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "snap.json"), "{\"version\":\"1.0\"}");
                var bridge = new FakeBridge();
                var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false);

                var imported = 0;
                var patched = 0;
                var started = 0;
                service.SnapshotImported += _ => imported++;
                service.PatchApplied += _ => patched++;
                service.SessionStarted += _ => started++;

                var result = service.StartLocalSession(new UgcLiveSyncRequest(watchFolder: dir));
                Assert(result.Ok, "local session should start: " + result.Message);
                Assert(started == 1, "SessionStarted should fire once");

                service.Pump(1f); // processes the seeded snapshot (first => ImportProject)
                Assert(imported == 1, "first snapshot should raise SnapshotImported, got " + imported);
                Assert(patched == 0, "first snapshot should not raise PatchApplied");
                Assert(bridge.ApplyCalls == 1, "bridge should apply once");

                service.MarkDirty();
                service.Pump(1f); // subsequent => Diff/patch
                Assert(imported == 1, "still one SnapshotImported");
                Assert(patched == 1, "second snapshot should raise PatchApplied, got " + patched);
                Assert(bridge.ApplyCalls == 2, "bridge should apply twice");

                service.Dispose();
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestGarbageAndOversizeRejected()
        {
            // Garbage content is rejected before reaching the bridge.
            var dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "bad.json"), "this is not json");
                var bridge = new FakeBridge();
                var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false);
                var errors = 0;
                service.SyncError += _ => errors++;
                service.StartLocalSession(new UgcLiveSyncRequest(watchFolder: dir));
                service.Pump(1f);
                Assert(errors == 1, "garbage snapshot should raise one SyncError, got " + errors);
                Assert(bridge.ApplyCalls == 0, "garbage should never reach the bridge");
                service.Dispose();
            }
            finally
            {
                TryDelete(dir);
            }

            // Oversize content is rejected by the size cap.
            var dir2 = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir2, "big.json"), "{\"padding\":\"" + new string('x', 200) + "\"}");
                var bridge = new FakeBridge();
                var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false) { CurrentMaxBytes = 16 };
                var errors = 0;
                service.SyncError += _ => errors++;
                service.StartLocalSession(new UgcLiveSyncRequest(watchFolder: dir2));
                service.Pump(1f);
                Assert(errors == 1, "oversize snapshot should raise one SyncError, got " + errors);
                Assert(bridge.ApplyCalls == 0, "oversize should never reach the bridge");
                service.Dispose();
            }
            finally
            {
                TryDelete(dir2);
            }
        }

        private static void TestApplyErrorKeepsWatching()
        {
            var dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "snap.json"), "{\"version\":\"1.0\"}");
                var bridge = new FakeBridge { ThrowOnApply = true };
                var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false);
                var errors = 0;
                service.SyncError += _ => errors++;
                service.StartLocalSession(new UgcLiveSyncRequest(watchFolder: dir));
                service.Pump(1f);
                Assert(errors == 1, "apply failure should raise one SyncError");
                Assert(service.Status == UgcLiveSyncStatus.Watching, "service should keep watching after an apply error, got " + service.Status);
                service.Dispose();
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestLifecycleCleanup()
        {
            var dir = NewTempDir();
            try
            {
                var bridge = new FakeBridge();
                var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false);
                var stopped = 0;
                service.SessionStopped += _ => stopped++;

                service.StartLocalSession(new UgcLiveSyncRequest(watchFolder: dir));
                Assert(service.Status == UgcLiveSyncStatus.Watching, "should be watching");

                service.Stop();
                Assert(stopped == 1, "SessionStopped should fire once on Stop");
                Assert(service.Status == UgcLiveSyncStatus.Stopped, "status should be Stopped");
                Assert(bridge.StopAutomergeCalls >= 1, "Stop should tear down the bridge");

                service.Dispose(); // must be safe after Stop
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestAutomergeSession()
        {
            var bridge = new FakeBridge();
            var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false);
            var imported = 0;
            var started = 0;
            service.SnapshotImported += _ => imported++;
            service.SessionStarted += _ => started++;

            var result = service.StartAutomergeSession(new UgcLiveSyncRequest(editorUrl: "https://h/?project=automerge:doc&scene=s"));
            Assert(result.Ok, "automerge session should start: " + result.Message);
            Assert(started == 1, "SessionStarted should fire for automerge");
            Assert(bridge.StartAutomergeDocument == "automerge:doc", "bridge should receive the parsed document, got " + bridge.StartAutomergeDocument);
            Assert(service.Status == UgcLiveSyncStatus.Connected, "status should be Connected");

            service.NotifySceneLoaded("UgcPlay"); // bridge replays the live revision callback
            Assert(imported == 1, "automerge live confirmation should raise SnapshotImported once, got " + imported);

            service.Dispose();
        }

        // Simulates the full runtime config JSON the launcher writes from the developer UGC "Go Live" flow after
        // the Automerge sidecar reports a live document URL. This catches drift between the Dart payload shape and
        // the game-side config/request path before we get to manual in-game smoke testing.
        private static void TestLauncherDeployedAutomergePayloadStartsSession()
        {
            var payload = File.ReadAllText(Path.Combine(
                FindRepoRoot(),
                "tests",
                "fixtures",
                "ugc",
                "live-sync-app-automerge-config.json"));
            var config = JsonUtil.Deserialize<UgcLiveSyncConfig>(payload);
            Assert(config.UsesAutomerge, "launcher payload should select the Automerge channel");
            Assert(config.AutoConnectOnStart, "launcher payload should request auto-connect");
            Assert(config.DocumentUrl == "automerge:captured-doc", "documentUrl should deserialize from launcher payload");
            Assert(config.SceneId == "neon-rooftops", "sceneId should deserialize from launcher payload");
            Assert(config.MaxSnapshotBytes == 4194304, "maxSnapshotBytes should deserialize from launcher payload");
            Assert(config.DebounceMilliseconds == 350, "debounceMilliseconds should deserialize from launcher payload");

            var bridge = new FakeBridge();
            var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false);
            var started = 0;
            service.SessionStarted += _ => started++;

            var request = new UgcLiveSyncRequest(
                watchFolder: config.WatchFolder,
                editorUrl: config.EditorUrl,
                documentUrl: config.DocumentUrl,
                syncServerUrl: config.SyncServerUrl,
                sceneId: config.SceneId,
                debounceMilliseconds: config.DebounceMilliseconds);
            var result = service.StartAutomergeSession(request);

            Assert(result.Ok, "launcher Automerge payload should start: " + result.Message);
            Assert(started == 1, "launcher payload should raise SessionStarted once");
            Assert(service.Status == UgcLiveSyncStatus.Connected, "launcher payload should leave service connected, got " + service.Status);
            Assert(bridge.StartAutomergeDocument == "automerge:captured-doc", "bridge should receive launcher documentUrl");
            Assert(bridge.StartAutomergeSyncServer == UgcLiveSyncConfig.DefaultSyncServerUrl, "bridge should receive launcher sync server");
            Assert(bridge.StartAutomergeScene == "neon-rooftops", "bridge should receive launcher sceneId");
            Assert(service.CurrentSession?.Target == "automerge:captured-doc", "session target should be the launcher documentUrl");
            Assert(service.CurrentSession?.SceneId == "neon-rooftops", "session scene should be the launcher sceneId");

            service.Dispose();
        }

        // The status handshake the launcher reads: round-trips through the same DataContractJsonSerializer the
        // config uses, derives a sibling *.status.json path, and writes atomically.
        private static void TestStatusFileRoundTrip()
        {
            var original = new UgcLiveSyncStatusFile
            {
                Status = "Connected",
                Transport = "automerge",
                DefaultWatchFolder = @"C:\game\ugc",
                ConnectedDocumentUrl = "automerge:abc123",
                SceneId = "main",
                ModVersion = "1.2.3",
            };
            original.AddScene("main");
            original.AddScene("main"); // dedupe
            original.AddScene("lobby");
            Assert(original.AvailableScenes.Length == 2, "AddScene should dedupe, got " + original.AvailableScenes.Length);

            var round = UgcLiveSyncStatusFile.FromJson(original.ToJson());
            Assert(round.Status == "Connected", "status should round-trip");
            Assert(round.Transport == "automerge", "transport should round-trip");
            Assert(round.DefaultWatchFolder == @"C:\game\ugc", "defaultWatchFolder should round-trip");
            Assert(round.ConnectedDocumentUrl == "automerge:abc123", "connectedDocumentUrl should round-trip");
            Assert(round.SceneId == "main", "sceneId should round-trip");
            Assert(round.AvailableScenes.Length == 2, "availableScenes should round-trip, got " + round.AvailableScenes.Length);
            Assert(round.SchemaVersion == 1, "schemaVersion should default to 1, got " + round.SchemaVersion);

            // JSON keys are the cross-language contract with the Dart reader.
            var json = original.ToJson();
            foreach (var key in new[] { "schemaVersion", "status", "transport", "defaultWatchFolder", "connectedDocumentUrl", "sceneId", "availableScenes" })
            {
                Assert(json.Contains("\"" + key + "\""), "status JSON must contain key '" + key + "'");
            }

            Assert(UgcLiveSyncStatusFile.PathForConfig(@"C:\a\b\robotopia.ugc.livesync.json")
                .EndsWith("robotopia.ugc.livesync.status.json"), "status path should be a sibling *.status.json");
            Assert(UgcLiveSyncStatusFile.PathForConfig("") == string.Empty, "empty config path yields empty status path");

            var dir = NewTempDir();
            try
            {
                var statusPath = Path.Combine(dir, "robotopia.ugc.livesync.status.json");
                original.WriteTo(statusPath);
                Assert(File.Exists(statusPath), "status file should be written");
                var reread = UgcLiveSyncStatusFile.FromJson(File.ReadAllText(statusPath));
                Assert(reread.ConnectedDocumentUrl == "automerge:abc123", "written status file should read back");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "UgcLiveSyncTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "RobotopiaModManager.slnx")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("Could not locate repo root (RobotopiaModManager.slnx) from " + AppContext.BaseDirectory);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
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
                throw new InvalidOperationException("UGC live-sync test failed: " + message);
            }
        }

        private sealed class FakeBridge : IUgcLiveSyncBridge
        {
            private int appliedSinceReset;
            private Action<UgcApplyOutcome>? onRevision;
            private bool automergePending;
            private string automergeScene = string.Empty;

            public int ApplyCalls { get; private set; }
            public int StopAutomergeCalls { get; private set; }
            public bool ThrowOnApply { get; set; }
            public string StartAutomergeDocument { get; private set; } = string.Empty;
            public string StartAutomergeSyncServer { get; private set; } = string.Empty;
            public string StartAutomergeScene { get; private set; } = string.Empty;

            public bool IsAvailable => true;
            public bool IsImportControllerReady() => true;
            public bool EnsurePlaySceneLoaded() => true;
            public string GetDefaultWatchFolder() => string.Empty;
            public void ResetApplyState() => appliedSinceReset = 0;
            public void ApplyAssetOverrides(IReadOnlyList<UgcAssetOverride> overrides) { }
            public void ClearAssetOverrides() { }

            public UgcApplyOutcome ApplyLocalSnapshot(byte[] bytes, string sceneId, string label)
            {
                ApplyCalls++;
                if (ThrowOnApply)
                {
                    throw new InvalidOperationException("simulated apply failure");
                }

                appliedSinceReset++;
                var first = appliedSinceReset == 1;
                return new UgcApplyOutcome("Sample", sceneId, "Main Scene", 7, isFullRebuild: false, wasFirstSnapshot: first);
            }

            public bool StartAutomerge(string documentUrl, string syncServerUrl, string sceneId, Action<UgcApplyOutcome> onRevisionCallback)
            {
                StartAutomergeDocument = documentUrl;
                StartAutomergeSyncServer = syncServerUrl;
                StartAutomergeScene = sceneId;
                onRevision = onRevisionCallback;
                automergeScene = sceneId ?? string.Empty;
                automergePending = true;
                return true;
            }

            public void StopAutomerge()
            {
                StopAutomergeCalls++;
                automergePending = false;
                onRevision = null;
            }

            public void NotifySceneLoaded(string sceneName)
            {
                if (automergePending)
                {
                    automergePending = false;
                    onRevision?.Invoke(new UgcApplyOutcome("(live)", automergeScene, sceneName, 0, isFullRebuild: false, wasFirstSnapshot: true));
                }
            }
        }

        private sealed class NullLogger : IModLogger
        {
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
        }
    }
}
