using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Session launch staging under concurrency and against V4: a take claims its file before it reads, a reader shares
    /// deletion, a channel write waits briefly for the launcher's reader, an untaken ticket is withdrawn, and every V4
    /// wire-profile fixture reads through the request readers V5 added exactly as the V4 readers read it.
    /// </summary>
    internal static class LaunchSessionStagingTests
    {
        private const string ServerId = "c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3";

        internal static void Run(string repositoryRoot)
        {
            var failures = new List<Exception>();
            foreach (var test in new (string Name, Action<ManagerPaths> Run)[]
            {
                ("take-claims", TakeClaims), ("read-shares-deletion", ReadSharesDeletion), ("channel-write-retry", ChannelWriteRetry),
                ("ticket-withdrawn", _ => TicketWithdrawn()), ("v4-fixtures", paths => V4Fixtures(repositoryRoot, paths))
            })
            {
                var owned = Directory.CreateTempSubdirectory("TopiaForgeSessionStaging-");
                try
                {
                    var paths = new ManagerPaths(owned.CreateSubdirectory("game").CreateSubdirectory("BepInEx").FullName);
                    paths.EnsureCreated();
                    test.Run(paths);
                    Console.WriteLine("Launch session staging " + test.Name + ": PASS");
                }
                catch (Exception error) { failures.Add(error); Console.WriteLine("Launch session staging " + test.Name + ": FAIL " + error.Message); }
                finally { owned.Delete(true); }
            }

            if (failures.Count != 0) throw new AggregateException("Launch session staging regressions failed.", failures);
        }

        private static SessionJoinRequest Join(string joinId) => new SessionJoinRequest("process-1", joinId, "0123456789abcdef",
            new LaunchSessionRequest("join", "intent-1", "Charl", new LaunchSessionTransport("relay-only", null, 0, false, true),
                target: LaunchSessionTarget.Listing("lan", ServerId, new string('a', 64), new string('b', 64))));

        /// <summary>Replaces <paramref name="path"/> as the launcher does: a sibling temporary file renamed over it.</summary>
        private static void Replace(string path, string text)
        {
            var temporary = path + ".tmp-test";
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, true);
        }

        private static void TakeClaims(ManagerPaths paths)
        {
            var plain = new LaunchStagingStore(paths);
            var path = plain.SessionJoinPath("process-1");
            File.WriteAllText(path, LaunchTransportJson.WriteSessionJoin(Join("join-1")));
            // The launcher stages a newer join between the claim and the read.
            var racing = new LaunchStagingStore(paths, null, () => Replace(path, LaunchTransportJson.WriteSessionJoin(Join("join-2"))), null, null);
            Assert(racing.TakeSessionJoin("process-1")!.JoinId == "join-1" && File.Exists(path),
                "A join staged during a take waits at its name instead of being removed unread.");
            Assert(plain.TakeSessionJoin("process-1")!.JoinId == "join-2" && !File.Exists(path), "The newer join is taken next.");
            Assert(!Directory.EnumerateFiles(paths.Staging).Any(file => Path.GetFileName(file).Contains(".taking-", StringComparison.Ordinal)),
                "No claimed file is left behind.");
        }

        private static void ReadSharesDeletion(ManagerPaths paths)
        {
            var older = new MultiplayerPreferencesProjection(3, 0, "Charl", 1, 0, false, true, false,
                new MultiplayerHostingDefaults("Charl's server", 8, "lan", 27250, false));
            var plain = new LaunchStagingStore(paths);
            File.WriteAllText(plain.PreferencesPath, LaunchTransportJson.WritePreferences(older));
            var aside = plain.PreferencesPath + ".taking-test";
            Exception? refused = null;
            // The launcher claims or removes the file while the game reads it. (A rename over it still waits for the read to
            // end on Windows whatever the sharing, so the launcher's replace retries.)
            var reading = new LaunchStagingStore(paths, null, null, path =>
            {
                try { File.Move(path, aside); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { refused = error; }
            }, null);
            var read = reading.ReadPreferences();
            Assert(refused == null, "Moving the file away while the game reads must succeed: " + refused?.Message);
            Assert(read!.Revision == 3 && !File.Exists(plain.PreferencesPath) && File.Exists(aside),
                "The reader keeps the bytes it opened, and the file moved.");
        }

        private static void ChannelWriteRetry(ManagerPaths paths)
        {
            var plain = new LaunchStagingStore(paths);
            var first = new MultiplayerPreferencesJournal(new[] { MultiplayerPreferenceChange.DisplayName(1, "Charl") });
            var second = new MultiplayerPreferencesJournal(new[] { MultiplayerPreferenceChange.DisplayName(2, "Charlie") });
            plain.WritePreferencesJournal(first);
            // The launcher reads without sharing deletion, as Dart's reader does.
            FileStream? held = new FileStream(plain.PreferencesJournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var retries = 0;
            var waiting = new LaunchStagingStore(paths, null, null, null, attempt =>
            {
                retries++;
                held?.Dispose();
                held = null;
            });
            try { waiting.WritePreferencesJournal(second); }
            finally { held?.Dispose(); }
            Assert(plain.ReadPreferencesJournal()!.Changes[0].Sequence == 2, "The write lands once the reader lets go.");
            if (!OperatingSystem.IsWindows()) return;
            Assert(retries == 1, "Windows refuses the first replace while the launcher reads.");

            using (new FileStream(plain.PreferencesJournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var stubborn = new LaunchStagingStore(paths, null, null, null, attempt => retries++);
                retries = 0;
                try
                {
                    stubborn.WritePreferencesJournal(first);
                    throw new InvalidOperationException("A write past a reader that never lets go must fail.");
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            }

            Assert(retries == LaunchStagingStore.ChannelWriteRetries && plain.ReadPreferencesJournal()!.Changes[0].Sequence == 2,
                "The write gives up after its retries and leaves the journal as it was.");
            Assert(!Directory.EnumerateFiles(paths.Staging).Any(file => Path.GetFileName(file).Contains(".tmp-", StringComparison.Ordinal)),
                "No temporary file is left behind.");
        }

        private static void TicketWithdrawn()
        {
            var session = new LaunchSessionRequest("join", "intent-1", "Charl", new LaunchSessionTransport("relay-only", null, 0, false, true),
                target: LaunchSessionTarget.Direct("192.168.1.20", 27250, ServerId, new string('a', 64), new string('b', 64)));
            using (ModManager.LaunchSessionOffer.Offer(session)) { }
            Assert(LaunchSessionTickets.Take() == null, "An untaken ticket is withdrawn once startup ends.");
            var taken = ModManager.LaunchSessionOffer.Offer(session);
            Assert(LaunchSessionTickets.Take() != null && !taken.Withdraw(), "A taken ticket is the provider's; nothing is withdrawn.");
            var earlier = ModManager.LaunchSessionOffer.Offer(session);
            var later = ModManager.LaunchSessionOffer.Offer(session);
            Assert(!earlier.Withdraw() && later.Withdraw() && LaunchSessionTickets.Take() == null,
                "An earlier offering never withdraws a later ticket.");
        }

        private static void V4Fixtures(string repositoryRoot, ManagerPaths paths)
        {
            var folder = Path.Combine(repositoryRoot, "tests", "fixtures", "gamemode-v6", "serialization", "transport");
            var files = Directory.GetFiles(folder, "wire-profile-*.json").OrderBy(file => file, StringComparer.Ordinal).ToArray();
            Assert(files.Length == 26, "The V4 wire-profile corpus has 26 cases.");
            var store = new LaunchStagingStore(paths);
            var accepted = 0;
            foreach (var file in files)
            {
                using var body = JsonDocument.Parse(File.ReadAllText(file));
                var root = body.RootElement;
                var id = root.GetProperty("id").GetString()!;
                var text = root.TryGetProperty("wireJson", out var wire) ? wire.GetString()! : root.GetProperty("payload").GetRawText();
                var v4 = Normalized(() => LaunchTransportJson.ReadProfile(text));
                var either = Normalized(() =>
                {
                    var consumed = LaunchTransportJson.ReadLaunchRequest(text);
                    Assert(consumed.Session == null && consumed.SchemaVersion == 4, id + " reads as V4.");
                    return consumed.Profile;
                });
                Assert(either == v4, id + ": the request reader differs from the V4 reader.");
                var requestId = v4 == null ? "refused-request" : LaunchTransportJson.ReadProfile(text).RequestId;
                var path = store.RequestPath(requestId);
                File.WriteAllText(path, text);
                var consumedV4 = Normalized(() => store.ConsumeRequest(path));
                var removedV4 = !File.Exists(path);
                File.WriteAllText(path, text);
                var consumedEither = Normalized(() => store.ConsumeLaunchRequest(path).Profile);
                var removedEither = !File.Exists(path);
                if (File.Exists(path)) File.Delete(path);
                Assert(consumedV4 == v4 && consumedEither == v4 && removedV4 == (v4 != null) && removedEither == (v4 != null),
                    id + ": consuming from staging differs from the V4 consumer.");
                var expected = root.GetProperty("expect").GetProperty("csharp").GetProperty("outcome").GetString();
                Assert((v4 == null ? "reject" : "accept") == expected, id + " keeps its verdict.");
                if (v4 != null) accepted++;
            }

            Assert(accepted == 2, "Two V4 cases are accepted and 24 refused.");
        }

        /// <summary>The V4 writer's bytes for what <paramref name="read"/> returns, or null when it refuses.</summary>
        private static string? Normalized(Func<ProfileLaunchConfigurationV4> read)
        {
            try { return LaunchTransportJson.WriteProfile(read()); }
            catch (InvalidDataException) { return null; }
        }

        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
