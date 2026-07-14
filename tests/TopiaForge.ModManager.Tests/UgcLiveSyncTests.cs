using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.UgcLiveSync;

namespace TopiaForge.ModManager.Tests
{
    // Exercises the Unity-free UGC live-sync service state machine with a fake bridge (no GameCode/UnityEngine).
    // The service files are compiled into this assembly via <Compile Include> in the csproj.
    internal static class UgcLiveSyncTests
    {
        public static void Run()
        {
            TestValidationHelpers();
            TestEditorUrlParsing();
            TestSecureSyncServerPolicy();
            TestFindNewestSnapshot();
            TestStableBoundedSnapshotRead();
            TestLocalFirstThenSubsequent();
            TestGarbageAndOversizeRejected();
            TestApplyErrorKeepsWatching();
            TestLifecycleCleanup();
            TestActiveSessionStopsOnSceneExit();
            TestAutomergeSession();
            TestLauncherDeployedAutomergePayloadStartsSession();
            TestAutomaticConnectDefersWhenSceneIsHeld();
            TestAutomaticAutomergeDefersWhenSceneIsHeld();
            TestDeferredAutomaticTargetWithoutControllerKeepsYielding();
            TestDeferredLocalConnectRetriesAfterBlockerEnds();
            TestUserInitiatedConnectLoadsAndReleasesClaim();
            TestFailedSceneDispatchReleasesClaim();
            TestSupersededUserConnectFailsAfterSceneDispatch();
            TestSceneDispatchTimeoutReleasesClaim();
            TestAutomergeTimeoutCannotReconnectFromLateRevision();
            TestStatusFileRoundTrip();
            TestCommandFileRoundTrip();
            TestStatusAndCommandFileBounds();
            TestCommandPollGateThrottles();
            Console.WriteLine("All UGC live-sync service tests passed.");
        }

        private static void TestValidationHelpers()
        {
            var gzipBomb = Gzip(Bytes("{\"padding\":\"" + new string('x', 4096) + "\"}"));
            Assert(!UgcLiveSyncService.TryValidateExpandedSnapshot(gzipBomb, 256, out var expansionError)
                    && expansionError.Contains("expanded gzip"),
                "gzip expansion beyond the runtime cap should be rejected before the bridge");
            Assert(!UgcLiveSyncService.TryValidateExpandedSnapshot(new byte[] { 0x1f, 0x8b, 0x00 }, 256, out var gzipError)
                    && gzipError.Contains("invalid gzip"),
                "malformed gzip should be rejected before the bridge");
            Assert(!UgcLiveSyncService.TryValidateExpandedSnapshot(Gzip(Bytes("[1,2,3]")), 256, out var shapeError)
                    && shapeError.Contains("root must be an object"),
                "gzip content must expand to a project JSON object");
            Assert(UgcLiveSyncService.TryValidateExpandedSnapshot(
                    Gzip(new byte[] { 0xEF, 0xBB, 0xBF, (byte)' ', (byte)'{', (byte)'}' }), 256, out _),
                "gzip JSON should accept a UTF-8 BOM and leading whitespace");
            Assert(UgcLiveSyncService.TryValidateExpandedSnapshot(Bytes("{}"), 2, out _),
                "ordinary JSON should receive the same strict structural preflight");
            Assert(!UgcLiveSyncService.TryValidateExpandedSnapshot(Bytes("{}{}"), 16, out var trailingError)
                    && trailingError.Contains("trailing"),
                "multiple JSON roots must not be accepted");
        }

        private static void TestEditorUrlParsing()
        {
            Assert(UgcLiveSyncService.TryParseEditorUrl("https://editor.example/?project=automerge:abc123&scene=main", out var doc, out var scene),
                "editor url with project should parse");
            Assert(doc == "automerge:abc123", "document url should be the project param, got: " + doc);
            Assert(scene == "main", "scene id should be the scene param, got: " + scene);

            Assert(!UgcLiveSyncService.TryParseEditorUrl("https://editor.example/?foo=bar", out _, out _), "url without project should not parse");
            Assert(!UgcLiveSyncService.TryParseEditorUrl("not a url", out _, out _), "garbage should not parse");
            Assert(!UgcLiveSyncService.TryParseEditorUrl("https://editor.example/?project=%ZZ", out _, out _),
                "malformed URL escapes should fail without escaping the parser");
        }

        private static void TestSecureSyncServerPolicy()
        {
            Assert(UgcLiveSyncService.TryValidateSecureSyncServerUrl("https://sync.example/room", out _),
                "HTTPS sync servers should be accepted");
            Assert(UgcLiveSyncService.TryValidateSecureSyncServerUrl("wss://sync.example/room", out _),
                "WSS sync servers should be accepted");
            Assert(!UgcLiveSyncService.TryValidateSecureSyncServerUrl("http://sync.example/", out _),
                "plaintext HTTP sync servers must be rejected");
            Assert(!UgcLiveSyncService.TryValidateSecureSyncServerUrl("ws://sync.example/", out _),
                "plaintext WebSocket sync servers must be rejected");
            Assert(!UgcLiveSyncService.TryValidateSecureSyncServerUrl("wss://user:secret@sync.example/", out _),
                "sync URLs containing credentials must be rejected");

            var bridge = new FakeBridge();
            var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false);
            var started = service.StartAutomergeSession(new UgcLiveSyncRequest(
                documentUrl: "automerge:healthy",
                syncServerUrl: "wss://sync.example/"));
            Assert(started.Ok, "the secure baseline Automerge request should start");
            var stopCalls = bridge.StopAutomergeCalls;

            var rejected = service.StartAutomergeSession(new UgcLiveSyncRequest(
                documentUrl: "automerge:replacement",
                syncServerUrl: "http://sync.example/"));

            Assert(!rejected.Ok && rejected.Message.Contains("https:// or wss://"),
                "an insecure replacement request should fail with an actionable reason");
            Assert(service.PendingSession?.Target == "automerge:healthy",
                "an invalid replacement request must preserve the healthy pending session");
            Assert(bridge.StopAutomergeCalls == stopCalls,
                "validation failure must not tear down the healthy native Automerge request");
            service.Dispose();
        }

        private static void TestFindNewestSnapshot()
        {
            var dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "ignore.txt"), "x");
                var older = Path.Combine(dir, "older.json");
                var newer = Path.Combine(dir, "newer.JSON.GZ");
                File.WriteAllText(older, "{}");
                File.WriteAllText(newer, "{}");
                File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
                File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

                var found = UgcLiveSyncService.FindNewestSnapshot(dir);
                Assert(found == newer, "newest snapshot should accept case-insensitive .json.gz, got: " + found);

                var sameTime = DateTime.UtcNow.AddMinutes(1);
                var tieA = Path.Combine(dir, "tie-a.JSON");
                var tieB = Path.Combine(dir, "tie-b.json");
                File.WriteAllText(tieA, "{}");
                File.WriteAllText(tieB, "{}");
                File.SetLastWriteTimeUtc(tieA, sameTime);
                File.SetLastWriteTimeUtc(tieB, sameTime);
                Assert(UgcLiveSyncService.FindNewestSnapshot(dir) == tieB,
                    "equal timestamps should resolve deterministically by ordinal path");

                var linkTarget = Path.Combine(dir, "link-target.txt");
                var link = Path.Combine(dir, "linked.JSON");
                File.WriteAllText(linkTarget, "{}");
                if (TryCreateFileSymbolicLink(link, linkTarget))
                {
                    File.SetLastWriteTimeUtc(linkTarget, DateTime.UtcNow.AddMinutes(5));
                    Assert(UgcLiveSyncService.FindNewestSnapshot(dir) == tieB,
                        "snapshot discovery must ignore symbolic links/reparse points");
                }

                Assert(UgcLiveSyncService.FindNewestSnapshot(Path.Combine(dir, "missing")) == null, "missing folder returns null");

                var bounded = Path.Combine(dir, "bounded");
                Directory.CreateDirectory(bounded);
                File.WriteAllText(Path.Combine(bounded, "one.txt"), "x");
                File.WriteAllText(Path.Combine(bounded, "two.json"), "{}");
                File.WriteAllText(Path.Combine(bounded, "three.txt"), "x");
                var boundedResult = UgcLiveSyncService.ScanNewestSnapshot(bounded, maximumEntries: 2);
                Assert(boundedResult.Outcome == UgcLiveSyncService.SnapshotScanOutcome.Rejected
                    && boundedResult.Error.Contains("more than 2"),
                    "watch-folder enumeration must stop at a configured all-entry bound");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestStableBoundedSnapshotRead()
        {
            var dir = NewTempDir();
            try
            {
                Assert(UgcLiveSyncService.IsSnapshotPath("snapshot.JSON")
                    && UgcLiveSyncService.IsSnapshotPath("snapshot.JSON.GZ")
                    && !UgcLiveSyncService.IsSnapshotPath("snapshot.json.tmp"),
                    "snapshot extension matching should be exact and case-insensitive");

                var path = Path.Combine(dir, "stable.json");
                File.WriteAllText(path, "{\"version\":1}");
                var outcome = UgcLiveSyncService.ReadStableSnapshot(path, 1024, out var bytes, out var error);
                Assert(outcome == UgcLiveSyncService.SnapshotReadOutcome.Success
                    && Encoding.UTF8.GetString(bytes) == "{\"version\":1}",
                    "stable bounded read should return the complete snapshot: " + error);

                outcome = UgcLiveSyncService.ReadStableSnapshot(path, 4, out _, out error);
                Assert(outcome == UgcLiveSyncService.SnapshotReadOutcome.Rejected && error.Contains("limit"),
                    "bounded read should reject metadata larger than its cap");

                File.Delete(path);
                outcome = UgcLiveSyncService.ReadStableSnapshot(path, 1024, out _, out _);
                Assert(outcome == UgcLiveSyncService.SnapshotReadOutcome.Retry,
                    "a snapshot rotated away before open should be retried");

                var replacement = Path.Combine(dir, "replacement.json");
                File.WriteAllText(replacement, "{\"value\":1}");
                var originalWriteTime = File.GetLastWriteTimeUtc(replacement);
                outcome = UgcLiveSyncService.ReadStableSnapshot(
                    replacement,
                    1024,
                    () =>
                    {
                        File.WriteAllText(replacement, "{\"value\":2}");
                        File.SetLastWriteTimeUtc(replacement, originalWriteTime);
                    },
                    out _,
                    out error);
                Assert(outcome == UgcLiveSyncService.SnapshotReadOutcome.Retry
                    && (error.Contains("replaced") || error.Contains("changed")),
                    "same-size/same-timestamp replacement races must be detected by content verification");

                var target = Path.Combine(dir, "target.txt");
                var link = Path.Combine(dir, "linked.json");
                File.WriteAllText(target, "{}");
                if (TryCreateFileSymbolicLink(link, target))
                {
                    outcome = UgcLiveSyncService.ReadStableSnapshot(link, 1024, out _, out error);
                    Assert(outcome == UgcLiveSyncService.SnapshotReadOutcome.Rejected
                        && error.Contains("reparse"),
                        "bounded read must reject symbolic links/reparse points");
                }

                var service = new UgcLiveSyncService(new FakeBridge(), new NullLogger(), enableFileWatcher: false)
                {
                    CurrentMaxBytes = 0
                };
                Assert(service.CurrentMaxBytes > 0,
                    "a non-positive config must not disable the snapshot allocation bound");
                service.Dispose();
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
                var bigPath = Path.Combine(dir2, "big.json");
                Assert(UgcLiveSyncService.IsSnapshotTooLarge(bigPath, 16, out var fileLength) && fileLength > 16,
                    "the metadata preflight should identify an oversized snapshot before it is read");
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

        private static void TestActiveSessionStopsOnSceneExit()
        {
            var dir = NewTempDir();
            try
            {
                var bridge = new FakeBridge();
                var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false);
                var stopped = 0;
                service.SessionStopped += _ => stopped++;
                service.StartLocalSession(new UgcLiveSyncRequest(watchFolder: dir));

                bridge.ActiveSceneName = "ForeignScene";
                bridge.ImportControllerReady = false;
                service.NotifySceneLoaded("ForeignScene");

                Assert(service.Status == UgcLiveSyncStatus.Stopped && service.CurrentSession == null,
                    "an active live session must stop when a foreign active scene has no import controller");
                Assert(stopped == 1, "scene exit should raise SessionStopped exactly once");
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
            Assert(started == 0, "SessionStarted must wait for the Automerge controller to become ready");
            Assert(bridge.StartAutomergeDocument == "automerge:doc", "bridge should receive the parsed document, got " + bridge.StartAutomergeDocument);
            Assert(bridge.StartAutomergeLoadPlayScene, "an explicit Automerge connect should load the play scene");
            Assert(service.Status == UgcLiveSyncStatus.WaitingForScene, "status should wait for the play scene");
            Assert(service.CurrentSession == null && service.PendingSession?.Target == "automerge:doc",
                "the requested session should remain pending until native confirmation");

            service.NotifySceneLoaded("UgcPlay"); // bridge replays the live revision callback
            Assert(started == 1, "SessionStarted should fire once when the Automerge controller is ready");
            Assert(service.Status == UgcLiveSyncStatus.Connected, "status should become Connected after confirmation");
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
            Assert(started == 0, "launcher payload should wait to raise SessionStarted until the scene is ready");
            Assert(service.Status == UgcLiveSyncStatus.WaitingForScene,
                "launcher payload should wait for its play scene, got " + service.Status);
            Assert(bridge.StartAutomergeDocument == "automerge:captured-doc", "bridge should receive launcher documentUrl");
            Assert(bridge.StartAutomergeSyncServer == UgcLiveSyncConfig.DefaultSyncServerUrl, "bridge should receive launcher sync server");
            Assert(bridge.StartAutomergeScene == "neon-rooftops", "bridge should receive launcher sceneId");
            Assert(service.PendingSession?.Target == "automerge:captured-doc", "pending target should be the launcher documentUrl");
            Assert(service.PendingSession?.SceneId == "neon-rooftops", "pending scene should be the launcher sceneId");

            service.NotifySceneLoaded("UgcPlay");
            Assert(started == 1, "launcher payload should raise SessionStarted once after native confirmation");
            Assert(service.Status == UgcLiveSyncStatus.Connected, "launcher payload should become connected");
            Assert(service.CurrentSession?.Target == "automerge:captured-doc", "session target should be the launcher documentUrl");

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
            Assert(round.SchemaVersion == UgcLiveSyncStatusFile.CurrentSchemaVersion,
                "schemaVersion should use the current format, got " + round.SchemaVersion);
            AssertInvalidData(
                () => UgcLiveSyncStatusFile.FromJson(
                    original.ToJson().Replace("\"schemaVersion\":2", "\"schemaVersion\":1")),
                "status schemaVersion 1 should be rejected");

            round.ClearLiveSession(clearHistory: true);
            Assert(round.ConnectedDocumentUrl == string.Empty, "ClearLiveSession should clear document url");
            Assert(round.SceneId == string.Empty, "ClearLiveSession should clear scene");
            Assert(round.AvailableScenes.Length == 0, "ClearLiveSession should clear available scenes");

            // JSON keys are the cross-language contract with the Dart reader.
            var json = original.ToJson();
            foreach (var key in new[] { "schemaVersion", "status", "transport", "defaultWatchFolder", "connectedDocumentUrl", "sceneId", "availableScenes" })
            {
                Assert(json.Contains("\"" + key + "\""), "status JSON must contain key '" + key + "'");
            }

            Assert(UgcLiveSyncStatusFile.PathForConfig(@"C:\a\b\topiaforge.ugc.livesync.json")
                .EndsWith("topiaforge.ugc.livesync.status.json"), "status path should be a sibling *.status.json");
            Assert(UgcLiveSyncStatusFile.PathForConfig("") == string.Empty, "empty config path yields empty status path");

            var dir = NewTempDir();
            try
            {
                var statusPath = Path.Combine(dir, "topiaforge.ugc.livesync.status.json");
                original.WriteTo(statusPath);
                Assert(File.Exists(statusPath), "status file should be written");
                var reread = UgcLiveSyncStatusFile.FromJson(File.ReadAllText(statusPath));
                Assert(reread.ConnectedDocumentUrl == "automerge:abc123", "written status file should read back");
                original.Status = "Idle";
                original.WriteTo(statusPath);
                reread = UgcLiveSyncStatusFile.FromJson(File.ReadAllText(statusPath));
                Assert(reread.Status == "Idle", "status replacement should expose only the complete new document");
                Assert(Directory.GetFiles(dir, "*.tmp-*").Length == 0, "atomic status writes should clean temporary files");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestCommandFileRoundTrip()
        {
            var original = new UgcLiveSyncCommandFile
            {
                Command = UgcLiveSyncCommandFile.StopCommand,
                Cleanup = true,
                CreatedUtc = "2026-07-05T00:00:00Z",
            };
            var round = UgcLiveSyncCommandFile.FromJson(original.ToJson());
            Assert(round.IsStop, "stop command should round-trip");
            Assert(round.Cleanup, "cleanup flag should round-trip");
            Assert(round.SchemaVersion == UgcLiveSyncCommandFile.CurrentSchemaVersion,
                "command schemaVersion should use the current format");
            AssertInvalidData(
                () => UgcLiveSyncCommandFile.FromJson(
                    original.ToJson().Replace("\"schemaVersion\":2", "\"schemaVersion\":1")),
                "command schemaVersion 1 should be rejected");

            var json = original.ToJson();
            foreach (var key in new[] { "schemaVersion", "command", "cleanup", "createdUtc" })
            {
                Assert(json.Contains("\"" + key + "\""), "command JSON must contain key '" + key + "'");
            }

            Assert(UgcLiveSyncCommandFile.PathForConfig(@"C:\a\b\topiaforge.ugc.livesync.json")
                .EndsWith("topiaforge.ugc.livesync.command.json"), "command path should be a sibling *.command.json");
            Assert(UgcLiveSyncCommandFile.PathForConfig("") == string.Empty, "empty config path yields empty command path");
        }

        private static void TestStatusAndCommandFileBounds()
        {
            var status = new UgcLiveSyncStatusFile();
            for (var index = 0; index < UgcLiveSyncStatusFile.MaxAvailableScenes + 20; index++)
            {
                status.AddScene("scene-" + index);
            }

            Assert(status.AvailableScenes.Length == UgcLiveSyncStatusFile.MaxAvailableScenes,
                "status scene history should remain bounded");
            Assert(status.AvailableScenes[status.AvailableScenes.Length - 1] ==
                   "scene-" + (UgcLiveSyncStatusFile.MaxAvailableScenes + 19),
                "bounded status history should retain the latest scene");

            var now = DateTime.UtcNow;
            var command = new UgcLiveSyncCommandFile
            {
                Command = UgcLiveSyncCommandFile.StopCommand,
                CreatedUtc = now.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            };
            Assert(command.IsFresh(now), "a newly written command should be fresh");
            command.CreatedUtc = now.Subtract(UgcLiveSyncCommandFile.MaxCommandAge).AddSeconds(-1)
                .ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            Assert(!command.IsFresh(now), "an old command should not be replayed");
            command.CreatedUtc = "not-a-date";
            Assert(!command.IsFresh(now), "an invalid command timestamp should be rejected");

            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "command.json");
                command.CreatedUtc = now.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllText(path, command.ToJson());
                Assert(UgcLiveSyncCommandFile.ReadFrom(path).IsStop, "bounded command reader should accept a valid command");

                File.WriteAllBytes(path, new byte[UgcLiveSyncCommandFile.MaxFileBytes + 1]);
                var rejected = false;
                try
                {
                    UgcLiveSyncCommandFile.ReadFrom(path);
                }
                catch (InvalidDataException)
                {
                    rejected = true;
                }

                Assert(rejected, "oversized command file should be rejected before allocation/parsing");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestCommandPollGateThrottles()
        {
            var gate = new UgcCommandPollGate(0.35f);
            Assert(gate.Tick(0f), "command polling should run immediately on startup");
            Assert(!gate.Tick(0.10f) && !gate.Tick(0.10f) && !gate.Tick(0.15f),
                "command polling should stay closed during the interval");
            Assert(gate.Tick(0f), "command polling should reopen after the interval elapses");
            Assert(!gate.Tick(-1f), "negative delta time must not advance the gate");
            gate.Reset();
            Assert(gate.Tick(0f), "reset should make the next command check immediate");
        }

        private static void AssertInvalidData(Action action, string message)
        {
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                return;
            }

            Assert(false, message);
        }

        private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

        private static byte[] Gzip(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
            {
                gzip.Write(bytes, 0, bytes.Length);
            }

            return output.ToArray();
        }

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "UgcLiveSyncTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
        {
            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
                return true;
            }
            catch
            {
                // Windows requires Developer Mode or an elevated token. Other assertions still cover the
                // platform-independent reparse-point gate when link creation is unavailable on the test host.
                return false;
            }
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TopiaForge.slnx")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("Could not locate repo root (TopiaForge.slnx) from " + AppContext.BaseDirectory);
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

        // The scene-stomp regression: an automatic connect (auto-connect on start) that needs the play scene
        // must defer while another mod holds the scene (a live world session), then attach when the play
        // scene arrives on its own — never dispatch a scene load over the session.
        private static void TestAutomaticConnectDefersWhenSceneIsHeld()
        {
            var dir = NewTempDir();
            try
            {
                var bridge = new FakeBridge { ImportControllerReady = false };
                var coordinator = new SceneCoordinator();
                var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false)
                {
                    SceneCoordinator = coordinator,
                    SceneOwnerId = "io.github.furroxide.topiaforge.ugc.livesync"
                };

                // Another mod (the worlds provider) holds the scene for its session.
                var worldClaim = coordinator.RequestTransition(new SceneTransitionRequest(
                    "io.github.furroxide.topiaforge.worlds", "IntroSewer", SceneTransitionPriority.UserInitiated, "world session"));
                Assert(worldClaim.Approved, "the world session's user-initiated claim should be approved");

                var result = service.StartLocalSession(new UgcLiveSyncRequest(
                    SceneTransitionPriority.Automatic, watchFolder: dir));

                Assert(result.Ok, "a deferred automatic connect still starts: " + result.Message);
                Assert(bridge.EnsurePlaySceneLoadedCalls == 0, "a refused automatic connect must not load the play scene");
                Assert(service.Status == UgcLiveSyncStatus.WaitingForScene, "deferred connect should wait for the scene");

                // The play scene later arrives by other means (e.g. the player launches the sandbox): attach.
                bridge.ImportControllerReady = true;
                service.NotifySceneLoaded("UgcPlay");
                Assert(service.Status == UgcLiveSyncStatus.Watching, "deferred connect should attach when the scene arrives");

                worldClaim.Claim!.Dispose();
            }
            finally
            {
                TryDelete(dir);
            }
        }

        // The explicit path: a user-initiated connect loads the play scene even while another claim is
        // active, and releases its own claim once a scene arrives so automatic transitions unblock.
        private static void TestUserInitiatedConnectLoadsAndReleasesClaim()
        {
            var dir = NewTempDir();
            try
            {
                var bridge = new FakeBridge { ImportControllerReady = false };
                var coordinator = new SceneCoordinator();
                var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false)
                {
                    SceneCoordinator = coordinator,
                    SceneOwnerId = "io.github.furroxide.topiaforge.ugc.livesync"
                };

                var result = service.StartLocalSession(new UgcLiveSyncRequest(watchFolder: dir));
                Assert(result.Ok, "user-initiated connect should start: " + result.Message);
                Assert(bridge.EnsurePlaySceneLoadedCalls == 1, "user-initiated connect should load the play scene");
                Assert(coordinator.IsSceneBusy, "the in-flight play-scene load should hold a claim");

                bridge.ImportControllerReady = true;
                service.NotifySceneLoaded("UgcPlay");
                Assert(service.Status == UgcLiveSyncStatus.Watching, "connect should attach once the scene is up");
                Assert(coordinator.IsSceneBusy,
                    "the resolved claim must survive the full SceneLoaded dispatch so later mods can identify the takeover");
                var duringDispatch = coordinator.RequestTransition(new SceneTransitionRequest(
                    "automatic.mod", "OtherScene", SceneTransitionPriority.Automatic));
                Assert(!duringDispatch.Approved,
                    "the resolved claim should remain visible until the next main-thread pump");
                service.Pump(0f);
                Assert(!coordinator.IsSceneBusy, "the claim should release on the next pump after scene dispatch");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestAutomaticAutomergeDefersWhenSceneIsHeld()
        {
            var bridge = new FakeBridge();
            var coordinator = new SceneCoordinator();
            var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false)
            {
                SceneCoordinator = coordinator,
                SceneOwnerId = "io.github.furroxide.topiaforge.ugc.livesync"
            };
            var started = 0;
            service.SessionStarted += _ => started++;

            var worldClaim = coordinator.RequestTransition(new SceneTransitionRequest(
                "io.github.furroxide.topiaforge.worlds", "IntroSewer", SceneTransitionPriority.UserInitiated, "world session"));
            var result = service.StartAutomergeSession(new UgcLiveSyncRequest(
                SceneTransitionPriority.Automatic, documentUrl: "automerge:deferred"));

            Assert(result.Ok, "a deferred Automerge connect should be accepted: " + result.Message);
            Assert(!bridge.StartAutomergeLoadPlayScene,
                "a refused automatic Automerge connect must arm the request without loading a scene");
            Assert(service.Status == UgcLiveSyncStatus.WaitingForScene && service.CurrentSession == null,
                "deferred Automerge should remain pending");
            Assert(service.PendingSession?.Target == "automerge:deferred",
                "deferred Automerge should retain its target for status reporting");
            Assert(coordinator.ActiveClaims.Count == 1,
                "a refused transition must not add a UGC claim beside the world claim");

            worldClaim.Claim!.Dispose();
            service.Pump(0f);
            Assert(bridge.StartAutomergeCalls == 2 && bridge.StartAutomergeLoadPlayScene,
                "once the blocker ends, Automerge should refresh its launch request and load the play scene");
            Assert(coordinator.IsSceneBusy,
                "the resumed deferred play-scene load should hold its own transition claim");
            service.NotifySceneLoaded("UgcPlay");
            Assert(started == 1 && service.Status == UgcLiveSyncStatus.Connected,
                "the resumed Automerge session should activate when the play scene arrives");
            service.Dispose();
        }

        private static void TestDeferredLocalConnectRetriesAfterBlockerEnds()
        {
            var dir = NewTempDir();
            try
            {
                var bridge = new FakeBridge { ImportControllerReady = false };
                var coordinator = new SceneCoordinator();
                var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false)
                {
                    SceneCoordinator = coordinator,
                    SceneOwnerId = "io.github.furroxide.topiaforge.ugc.livesync"
                };
                var blocker = coordinator.RequestTransition(new SceneTransitionRequest(
                    "io.github.furroxide.topiaforge.worlds", "IntroSewer", SceneTransitionPriority.UserInitiated, "world session"));

                var result = service.StartLocalSession(new UgcLiveSyncRequest(
                    SceneTransitionPriority.Automatic, watchFolder: dir));
                Assert(result.Ok && bridge.EnsurePlaySceneLoadedCalls == 0,
                    "the automatic local connect should initially defer without a scene load");

                blocker.Claim!.Dispose();
                service.Pump(0f);
                Assert(bridge.EnsurePlaySceneLoadedCalls == 1,
                    "the deferred local connect should resume its scene load once the blocker ends");
                Assert(coordinator.IsSceneBusy,
                    "the resumed local scene load should be protected by a transition claim");
                service.Dispose();
                Assert(!coordinator.IsSceneBusy, "stopping a resumed pending connect should release its claim");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestDeferredAutomaticTargetWithoutControllerKeepsYielding()
        {
            var bridge = new FakeBridge
            {
                ActiveSceneName = "UgcPlay",
                ImportControllerReady = false
            };
            var coordinator = new SceneCoordinator();
            var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false)
            {
                SceneCoordinator = coordinator,
                SceneOwnerId = "io.github.furroxide.topiaforge.ugc.livesync"
            };
            var foreign = coordinator.RequestTransition(new SceneTransitionRequest(
                "io.github.furroxide.topiaforge.worlds", "UgcPlay", SceneTransitionPriority.UserInitiated, "custom world"));

            var result = service.StartAutomergeSession(new UgcLiveSyncRequest(
                SceneTransitionPriority.Automatic,
                documentUrl: "automerge:deferred-target"));
            Assert(result.Ok && !bridge.StartAutomergeLoadPlayScene,
                "automatic Automerge should initially yield to the foreign target-scene claim");

            service.NotifySceneLoaded("UgcPlay");
            Assert(service.Status == UgcLiveSyncStatus.WaitingForScene && service.PendingSession != null,
                "a foreign target scene without a controller must remain deferred, not fail or connect");
            Assert(coordinator.ActiveClaims.Count == 1
                && coordinator.ActiveClaims[0].OwnerModId == "io.github.furroxide.topiaforge.worlds",
                "the deferred UGC request must not manufacture a claim for the foreign arrival");

            foreign.Claim!.Dispose();
            service.Pump(0f);
            Assert(bridge.StartAutomergeCalls == 2 && bridge.StartAutomergeLoadPlayScene,
                "after the foreign owner releases, Automerge should refresh its process-wide request and retry");
            Assert(coordinator.ActiveClaims.Count == 1
                && coordinator.ActiveClaims[0].OwnerModId == "io.github.furroxide.topiaforge.ugc.livesync",
                "the retried scene dispatch should hold the UGC claim");
            service.Dispose();
        }

        private static void TestFailedSceneDispatchReleasesClaim()
        {
            var dir = NewTempDir();
            try
            {
                var bridge = new FakeBridge
                {
                    ImportControllerReady = false,
                    EnsurePlaySceneLoadedResult = false
                };
                var coordinator = new SceneCoordinator();
                var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false)
                {
                    SceneCoordinator = coordinator,
                    SceneOwnerId = "io.github.furroxide.topiaforge.ugc.livesync"
                };

                var result = service.StartLocalSession(new UgcLiveSyncRequest(watchFolder: dir));
                Assert(!result.Ok, "a failed play-scene dispatch must fail the start request");
                Assert(service.Status == UgcLiveSyncStatus.Error, "failed dispatch should leave an Error status");
                Assert(service.PendingSession == null && service.CurrentSession == null,
                    "failed dispatch should clear pending session state");
                Assert(!coordinator.IsSceneBusy, "failed dispatch must release its coordinator claim");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestSupersededUserConnectFailsAfterSceneDispatch()
        {
            var dir = NewTempDir();
            try
            {
                var bridge = new FakeBridge { ImportControllerReady = false };
                var coordinator = new SceneCoordinator();
                var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false)
                {
                    SceneCoordinator = coordinator,
                    SceneOwnerId = "io.github.furroxide.topiaforge.ugc.livesync"
                };
                var result = service.StartLocalSession(new UgcLiveSyncRequest(watchFolder: dir));
                Assert(result.Ok && coordinator.ActiveClaims.Count == 1,
                    "the user connect should dispatch under its UGC scene claim");
                var foreign = coordinator.RequestTransition(new SceneTransitionRequest(
                    "foreign.mod", "ForeignScene", SceneTransitionPriority.UserInitiated, "user takeover"));

                bridge.ActiveSceneName = "ForeignScene";
                service.NotifySceneLoaded("ForeignScene");
                Assert(coordinator.ActiveClaims.Count == 2,
                    "the superseded UGC claim should remain visible for the full SceneLoaded dispatch");
                service.Pump(0f);
                Assert(service.Status == UgcLiveSyncStatus.Error
                    && service.PendingSession == null
                    && service.CurrentSession == null,
                    "a superseded user connect must fail instead of staying WaitingForScene forever");
                Assert(coordinator.ActiveClaims.Count == 1
                    && coordinator.ActiveClaims[0].OwnerModId == "foreign.mod",
                    "failing the superseded connect should release only the UGC claim");
                foreign.Claim!.Dispose();
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestSceneDispatchTimeoutReleasesClaim()
        {
            var dir = NewTempDir();
            try
            {
                var bridge = new FakeBridge { ImportControllerReady = false };
                var coordinator = new SceneCoordinator();
                var service = new UgcLiveSyncService(bridge, new NullLogger(), enableFileWatcher: false)
                {
                    SceneCoordinator = coordinator,
                    SceneOwnerId = "io.github.furroxide.topiaforge.ugc.livesync"
                };
                service.StartLocalSession(new UgcLiveSyncRequest(watchFolder: dir));

                service.Pump(31f);

                Assert(service.Status == UgcLiveSyncStatus.Error && service.PendingSession == null,
                    "a silent UGC scene dispatch should fail after its timeout");
                Assert(!coordinator.IsSceneBusy,
                    "a timed-out UGC scene dispatch must release its coordinator claim");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestAutomergeTimeoutCannotReconnectFromLateRevision()
        {
            var bridge = new FakeBridge { ImportControllerReady = false };
            var coordinator = new SceneCoordinator();
            var service = new UgcLiveSyncService(bridge, new ThrowingLogger(), enableFileWatcher: false)
            {
                SceneCoordinator = coordinator,
                SceneOwnerId = "io.github.furroxide.topiaforge.ugc.livesync"
            };
            var started = 0;
            var imported = 0;
            service.SessionStarted += _ => started++;
            service.SnapshotImported += _ => imported++;
            service.SyncError += _ => throw new InvalidOperationException("subscriber failure");

            var result = service.StartAutomergeSession(new UgcLiveSyncRequest(
                documentUrl: "automerge:will-time-out"));
            Assert(result.Ok && bridge.StartAutomergeCalls == 1,
                "the Automerge timeout regression must begin from a dispatched request");
            var stopCallsAfterStart = bridge.StopAutomergeCalls;

            // Neither a throwing logger nor a throwing error subscriber may interrupt terminal teardown.
            var originalError = Console.Error;
            using var capturedError = new StringWriter();
            try
            {
                Console.SetError(capturedError);
                service.Pump(31f);
            }
            finally
            {
                Console.SetError(originalError);
            }

            Assert(capturedError.ToString().Contains("logger failed"),
                "terminal-state diagnostics should fall back to an independent sink when the mod logger fails");
            Assert(service.Status == UgcLiveSyncStatus.Error
                && service.PendingSession == null
                && service.CurrentSession == null,
                "a timed-out Automerge start should be fully torn down in Error");
            Assert(bridge.StopAutomergeCalls > stopCallsAfterStart,
                "failing a pending Automerge start must detach its native controller/request");
            Assert(!coordinator.IsSceneBusy,
                "the timed-out Automerge start must release its scene claim");

            // Simulate a native revision that was already queued before Stop detached the bridge callback.
            bridge.EmitLastAutomergeRevision();
            Assert(service.Status == UgcLiveSyncStatus.Error
                && service.CurrentSession == null
                && started == 0
                && imported == 0,
                "a late native revision must not resurrect a failed Automerge session");
            service.Dispose();
        }

        private sealed class FakeBridge : IUgcLiveSyncBridge
        {
            private int appliedSinceReset;
            private Action<UgcApplyOutcome>? onRevision;
            private Action<UgcApplyOutcome>? lastAutomergeRevision;
            private bool automergePending;
            private string automergeScene = string.Empty;

            public int ApplyCalls { get; private set; }
            public int StopAutomergeCalls { get; private set; }
            public bool ThrowOnApply { get; set; }
            public string StartAutomergeDocument { get; private set; } = string.Empty;
            public string StartAutomergeSyncServer { get; private set; } = string.Empty;
            public string StartAutomergeScene { get; private set; } = string.Empty;
            public bool ImportControllerReady { get; set; } = true;
            public int EnsurePlaySceneLoadedCalls { get; private set; }
            public bool EnsurePlaySceneLoadedResult { get; set; } = true;
            public bool StartAutomergeLoadPlayScene { get; private set; }
            public int StartAutomergeCalls { get; private set; }
            public string ActiveSceneName { get; set; } = string.Empty;

            public bool IsAvailable => true;
            public bool IsImportControllerReady() => ImportControllerReady;
            public string PlaySceneName => "UgcPlay";
            public bool IsActiveScene(string sceneName) =>
                string.Equals(sceneName, ActiveSceneName, StringComparison.OrdinalIgnoreCase);

            public bool EnsurePlaySceneLoaded()
            {
                EnsurePlaySceneLoadedCalls++;
                return EnsurePlaySceneLoadedResult;
            }
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

            public bool StartAutomerge(
                string documentUrl,
                string syncServerUrl,
                string sceneId,
                bool loadPlayScene,
                Action<UgcApplyOutcome> onRevisionCallback)
            {
                StartAutomergeCalls++;
                StartAutomergeDocument = documentUrl;
                StartAutomergeSyncServer = syncServerUrl;
                StartAutomergeScene = sceneId;
                StartAutomergeLoadPlayScene = loadPlayScene;
                onRevision = onRevisionCallback;
                lastAutomergeRevision = onRevisionCallback;
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
                if (automergePending && ImportControllerReady)
                {
                    automergePending = false;
                    onRevision?.Invoke(new UgcApplyOutcome("(live)", automergeScene, sceneName, 0, isFullRebuild: false, wasFirstSnapshot: true));
                }
            }

            public void EmitLastAutomergeRevision()
            {
                lastAutomergeRevision?.Invoke(new UgcApplyOutcome(
                    "(late)", automergeScene, "UgcPlay", 0, isFullRebuild: false, wasFirstSnapshot: true));
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

        private sealed class ThrowingLogger : IModLogger
        {
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) => throw new InvalidOperationException("logger failure");
            public void Error(string message) => throw new InvalidOperationException("logger failure");
            public void Error(Exception exception, string message) => throw new InvalidOperationException("logger failure");
        }
    }
}
