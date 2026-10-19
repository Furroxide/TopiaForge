using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Session launch staging under concurrency and against V4: a take claims its file before it reads, a reader shares
    /// deletion, a channel write waits briefly for the launcher's reader, the game records preference changes under its
    /// journal's epoch, stale claims are swept and live ones kept, an untaken session is withdrawn, and every V4 wire-profile
    /// fixture reads through the request readers V5 added exactly as the V4 readers read it.
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
                ("journal-record", JournalRecord), ("claims-swept", ClaimsSwept),
                ("session-withdrawn", _ => SessionWithdrawn()), ("v4-fixtures", paths => V4Fixtures(repositoryRoot, paths))
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
            new LaunchSessionRequest("join", "intent-1", "Sam", new LaunchSessionTransport("relay-only", null, 0, false, true),
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
            var older = new MultiplayerPreferencesProjection(3, 0, "Sam", 1, 0, false, true, false,
                new MultiplayerHostingDefaults("Sam's server", 8, "lan", 27250, false));
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
            var epoch = MultiplayerPreferencesJournal.Start().Epoch;
            var first = new MultiplayerPreferencesJournal(epoch, new[] { MultiplayerPreferenceChange.DisplayName(1, "Sam") });
            var second = new MultiplayerPreferencesJournal(epoch, new[] { MultiplayerPreferenceChange.DisplayName(2, "Charlie") });
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

        private static void JournalRecord(ManagerPaths paths)
        {
            var store = new LaunchStagingStore(paths);
            var first = store.RecordPreferenceChange(sequence => MultiplayerPreferenceChange.DisplayName(sequence, "Sam"));
            Assert(first.Changes.Single().Sequence == 1 && first.Epoch.Length == 32, "With no journal file, the game starts one under a fresh epoch.");
            var second = store.RecordPreferenceChange(sequence => MultiplayerPreferenceChange.Network(sequence, false, true));
            Assert(second.Epoch == first.Epoch && second.Changes.Select(change => change.Sequence).SequenceEqual(new[] { 1, 2 })
                && store.ReadPreferencesJournal()!.Epoch == first.Epoch, "A journal keeps its epoch as it grows.");

            // The launcher took both changes in, then a projection counting another journal arrives.
            File.WriteAllText(store.PreferencesPath, LaunchTransportJson.WritePreferences(Projection(2, first.Epoch)));
            var third = store.RecordPreferenceChange(sequence => MultiplayerPreferenceChange.Voice(sequence, 1, true));
            Assert(third.Changes.Select(change => change.Sequence).SequenceEqual(new[] { 3 }), "What the launcher applied from this journal is dropped.");
            File.WriteAllText(store.PreferencesPath, LaunchTransportJson.WritePreferences(Projection(9, new string('e', 32))));
            var fourth = store.RecordPreferenceChange(sequence => MultiplayerPreferenceChange.DisplayName(sequence, "Robin"));
            Assert(fourth.Epoch == first.Epoch && fourth.Changes.Select(change => change.Sequence).SequenceEqual(new[] { 3, 4 }),
                "A position in another journal neither numbers nor trims this one.");

            // A journal the launcher could never apply, such as one without its epoch, is replaced by a new one.
            File.WriteAllText(store.PreferencesJournalPath, "{\"changes\":[],\"schemaVersion\":1}");
            var restarted = store.RecordPreferenceChange(sequence => MultiplayerPreferenceChange.DisplayName(sequence, "Sam"));
            Assert(restarted.Epoch != first.Epoch && restarted.Changes.Single().Sequence == 1,
                "A damaged journal is replaced by a new one under a fresh epoch.");
            if (!OperatingSystem.IsWindows()) return;

            // A journal another process holds without sharing is waited for, then refused; never replaced by a new journal.
            FileStream? locker = new FileStream(store.PreferencesJournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var patient = new LaunchStagingStore(paths, null, null, null, attempt => { locker?.Dispose(); locker = null; });
            try
            {
                var kept = patient.RecordPreferenceChange(sequence => MultiplayerPreferenceChange.DisplayName(sequence, "Alex"));
                Assert(kept.Epoch == restarted.Epoch && kept.Changes.Select(change => change.Sequence).SequenceEqual(new[] { 2 }),
                    "A briefly locked journal keeps its epoch and gains the change.");
            }
            finally { locker?.Dispose(); }

            var written = File.ReadAllText(store.PreferencesJournalPath);
            var retries = 0;
            var refused = new LaunchStagingStore(paths, null, null, null, attempt => retries++);
            using (new FileStream(store.PreferencesJournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    refused.RecordPreferenceChange(sequence => MultiplayerPreferenceChange.DisplayName(sequence, "Lost"));
                    throw new InvalidOperationException("A journal that stays locked must refuse the change.");
                }
                catch (IOException) { }
            }

            Assert(retries == LaunchStagingStore.ChannelWriteRetries && File.ReadAllText(store.PreferencesJournalPath) == written,
                "The locked journal keeps its epoch and its changes.");
        }

        private static void ClaimsSwept(ManagerPaths paths)
        {
            var store = new LaunchStagingStore(paths);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var hour = (long)TimeSpan.FromHours(1).TotalMilliseconds;
            string Claim(string channel, long claimedAt)
            {
                var path = channel + ".taking-" + claimedAt.ToString(CultureInfo.InvariantCulture) + "-" + new string('c', 32);
                File.WriteAllText(path, "{}");
                return path;
            }

            var stale = Claim(store.SessionJoinPath("crashed"), now - (2 * hour));
            var live = Claim(store.SessionJoinPath("running"), now - 1000);
            var launcher = Claim(store.SessionHandoffPath("crashed"), now - (2 * hour));
            var undated = store.SessionJoinPath("other") + ".taking-" + new string('c', 32);
            File.WriteAllText(undated, "{}");
            var held = Claim(store.SessionJoinPath("held"), now - (2 * hour));
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // The launcher stages a join; taking it sweeps first.
                File.WriteAllText(store.SessionJoinPath("process-1"), LaunchTransportJson.WriteSessionJoin(Join("join-1")));
                Assert(store.TakeSessionJoin("process-1")!.JoinId == "join-1", "The join is taken.");
                Assert(!File.Exists(stale), "A stale claim of this game's is swept.");
                Assert(File.Exists(live), "A live take keeps its claim.");
                Assert(File.Exists(launcher), "The launcher's claims are the launcher's to sweep.");
                Assert(File.Exists(undated), "Only dated claims go.");
                if (OperatingSystem.IsWindows()) Assert(File.Exists(held), "A claim another handle holds, even sharing deletion, is left.");
            }

            var swept = store.SweepStaleClaims();
            Assert(!File.Exists(held) && (!OperatingSystem.IsWindows() || swept == 1), "Once released, the held claim goes too.");
        }

        private static MultiplayerPreferencesProjection Projection(int applied, string epoch) => new MultiplayerPreferencesProjection(3, applied,
            "Sam", 1, 0, false, true, false, new MultiplayerHostingDefaults("Sam's server", 8, "lan", 27250, false), null, epoch);

        /// <summary>
        /// The provider takes the offered session through its control as mods finish loading, once; a session no provider took
        /// is withdrawn, and either way nothing keeps the session, whose direct target is an address, afterwards.
        /// </summary>
        private static void SessionWithdrawn()
        {
            var request = new LaunchSessionRequest("join", "intent-1", "Sam", new LaunchSessionTransport("relay-only", null, 0, false, true),
                target: LaunchSessionTarget.Direct("192.168.1.20", 27250, ServerId, new string('a', 64), new string('b', 64)));
            var identity = new LaunchSessionIdentity("request-1", "join", "intent-1");
            ModManager.SessionLaunch Offered() => new ModManager.SessionLaunch(identity, ModManager.LaunchSessionOffer.ToSession(request), startsGame: false);

            var untaken = Offered();
            Assert(untaken.Withdraw() && !untaken.IsOffered && untaken.RefusalCode == TopiaForge.Mods.ModErrorCode.Unavailable
                && untaken.Refusal == ModManager.SessionLaunch.ProviderMissing && !untaken.Withdraw(),
                "A session no provider took is withdrawn once startup ends, and its launch fails as unavailable.");
            Assert(!untaken.Take(new Tests.RecordingSessionControl()) && untaken.Plan == null, "A withdrawn session is never taken later.");

            var missing = Offered();
            Assert(!missing.Take(null) && missing.Refusal == ModManager.SessionLaunch.ProviderMissing, "Without the provider's control the launch fails at once.");

            var control = new Tests.RecordingSessionControl();
            var taken = Offered();
            Assert(taken.Take(control) && taken.Plan != null && control.Planned.Count == 1 && !taken.Withdraw() && !taken.IsRefused,
                "The provider takes the offered session once, through its control; nothing is withdrawn after.");
            Assert(!taken.Take(control) && control.Planned.Count == 1, "A session is taken once.");

            control.PlanResult = TopiaForge.Mods.OperationResult<IInternalLaunchPlan>.Failure(TopiaForge.Mods.ModErrorCode.Unavailable,
                "The relay at 203.0.113.7:3478 is down.");
            var refused = Offered();
            Assert(!refused.Take(control) && refused.RefusalCode == TopiaForge.Mods.ModErrorCode.Unavailable && !refused.Refusal.Contains("203.0.113", StringComparison.Ordinal),
                "A provider's refusal is the launch's, redacted (" + refused.Refusal + ").");

            foreach (var launch in new[] { untaken, missing, taken, refused })
            {
                foreach (var field in typeof(ModManager.SessionLaunch).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
                {
                    Assert(field.FieldType != typeof(InternalLaunchSession) || field.GetValue(launch) == null,
                        "Once taken or withdrawn, nothing keeps the session or its address (" + field.Name + ").");
                }
            }
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
