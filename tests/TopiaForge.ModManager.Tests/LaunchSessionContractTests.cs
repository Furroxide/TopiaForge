using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Request V5 and the session documents beyond the shared fixtures: V4 untouched, the host rules the launcher shares,
    /// consumption from staging, correlation of a rejected V5 request, V1-to-V2 publication, the channels, and privacy.
    /// </summary>
    internal static class LaunchSessionContractTests
    {
        private const string PublicHost = "203.0.113.77";
        private const int PublicPort = 47819;
        private const string ServerId = "c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3";

        // The game's journal, and one the launcher took in before it.
        private const string Epoch = "5f0c2a9e8d7b6a5f4e3d2c1b0a998877";
        private const string OtherEpoch = "0d1c2b3a49586776a5b4c3d2e1f00f1e";

        // V4 exactly as the launcher writes it, from the Dart golden test.
        private const string V4Menu = "{\"schemaVersion\":4,\"profileId\":\"default\",\"profileRevision\":7,"
            + "\"requestId\":\"fedcba9876543210fedcba9876543210\",\"command\":\"main-menu\",\"safeMode\":false,"
            + "\"inheritManagerModState\":true,\"enabledMods\":[\"example.alpha\"],\"selectedVersions\":{\"example.gamma\":\"2.0.0\"},"
            + "\"packages\":[{\"id\":\"example.alpha\",\"version\":\"1.2.0\"},{\"id\":\"example.beta\",\"version\":\"0.3.0-rc.1+build.7\"}],"
            + "\"digest\":\"d64fc177cff1cde2\"}";

        internal static void Run(string repositoryRoot)
        {
            var failures = new List<Exception>();
            foreach (var test in new (string Name, Action Run)[]
            {
                ("v4-untouched", V4Untouched), ("hosts", () => Hosts(repositoryRoot)), ("session-rules", SessionRules),
                ("consume", Consume), ("correlation", Correlation), ("publication", Publication), ("channels", Channels),
                ("journal-append", JournalAppend), ("privacy", Privacy), ("offer", Offer)
            })
            {
                try { test.Run(); Console.WriteLine("Launch session contract " + test.Name + ": PASS"); }
                catch (Exception error) { failures.Add(error); Console.WriteLine("Launch session contract " + test.Name + ": FAIL " + error.Message); }
            }

            if (failures.Count != 0) throw new AggregateException("Launch session contract regressions failed.", failures);
        }

        private static LaunchSessionRequest Join(LaunchSessionTarget target, bool direct = false) => new LaunchSessionRequest("join", "intent-1", "Sam",
            new LaunchSessionTransport(direct ? "direct" : "relay-only", direct ? new LaunchDirectConsent("intent-1", 1) : null, 0, false, true),
            target: target);

        private static LaunchSessionTarget Direct(string host, int port = PublicPort) => LaunchSessionTarget.Direct(host, port, ServerId, new string('a', 64), new string('b', 64));

        private static ProfileLaunchConfigurationV5 Request(LaunchSessionRequest session, string requestId = "request-5") =>
            new ProfileLaunchConfigurationV5("mp-0123456789abcdef", 0, requestId, session.Command, Array.Empty<PackageIdentity>(),
                PackageSetDigest.Of(Array.Empty<PackageIdentity>()), false, Array.Empty<string>(), new Dictionary<string, string>(), session);

        private static void V4Untouched()
        {
            var profile = LaunchTransportJson.ReadProfile(V4Menu);
            var consumed = LaunchTransportJson.ReadLaunchRequest(V4Menu);
            Assert(consumed.SchemaVersion == 4 && consumed.Session == null && consumed.Profile.RequestId == profile.RequestId
                && consumed.Profile.Digest == "d64fc177cff1cde2" && consumed.Profile.InheritManagerModState,
                "A V4 request reads as V4 with no session.");
            Assert(consumed.Describe() == "Using one-shot V4 launch profile default request fedcba9876543210fedcba9876543210.",
                "The V4 log line is unchanged.");
            Rejects(() => LaunchTransportJson.ReadProfileV5(V4Menu), "The V5 reader refuses V4.");
        }

        private static void Hosts(string repositoryRoot)
        {
            // The launcher's links fixture holds the canonical spelling of every valid direct host.
            using var links = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "tests", "fixtures", "multiplayer", "links", "cases.json")));
            foreach (var item in links.RootElement.GetProperty("valid").EnumerateArray())
            {
                if (item.GetProperty("kind").GetString() != "direct") continue;
                var input = item.GetProperty("input").GetString()!;
                var parts = input.Substring("topiaforge://join/direct/".Length + 65);
                var host = parts.Substring(0, parts.LastIndexOf('/'));
                Assert(LaunchSessionHosts.TryNormalize(host) == item.GetProperty("host").GetString(), "A link host normalizes as the launcher does.");
            }

            foreach (var host in new[] { "192.168.001.20", "999.1.1.1", "1.2.3", "[::1]", "bad_host.example", "-bad.example", "example.com.",
                "Kexample.com", "::ffff:1.2.3.04", "1::2::3", "", new string('a', 254) })
            {
                Assert(LaunchSessionHosts.TryNormalize(host) == null, "Not a host.");
            }

            foreach (var (host, local) in new[]
            {
                ("10.1.2.3", true), ("172.16.0.1", true), ("172.31.255.255", true), ("172.32.0.1", false), ("192.168.1.1", true),
                ("169.254.0.9", true), ("127.0.0.1", true), ("100.64.0.1", false), ("8.8.8.8", false), ("::1", true), ("fe80::1", true),
                ("fd00::7", true), ("fc00::1", true), ("2001:db8::1", false), ("localhost", true), ("peer.localhost", true),
                ("gameroom.local", true), ("play.example.org", false), ("local", false)
            })
            {
                Assert(LaunchSessionHosts.IsLocalNetwork(host) == local, "Local-network classification matches the launcher.");
            }
        }

        private static void SessionRules()
        {
            Assert(Join(Direct("192.168.1.20")).Target!.OnLocalNetwork, "Relay-only allows a LAN direct target.");
            Rejects(() => Join(Direct(PublicHost)), "Relay-only refuses a public direct target without consent.");
            Rejects(() => Join(Direct("play.example.org")), "Relay-only never looks up a name beyond the local network.");
            Assert(Join(Direct(PublicHost), direct: true).Transport.AllowsDirect, "Consent allows a public direct target.");
            Rejects(() => new LaunchSessionRequest("join", "intent-1", "Sam", new LaunchSessionTransport("direct", null, 0, false, true),
                target: Direct(PublicHost)), "Direct privacy requires consent.");
            Rejects(() => new LaunchSessionRequest("join", "intent-1", "Sam",
                new LaunchSessionTransport("direct", new LaunchDirectConsent("intent-2", 1), 0, false, true), target: Direct(PublicHost)),
                "A consent covers only its own session.");
            Rejects(() => new LaunchDirectConsent("intent-1", 0), "A consent follows the disclosure.");
            Rejects(() => new LaunchSessionRequest("join", "intent-1", "Sam",
                new LaunchSessionTransport("relay-only", new LaunchDirectConsent("intent-1", 1), 0, false, true), target: Direct("10.0.0.1")),
                "Relay-only carries no consent.");
            var host = new LaunchSessionRequest("host", "intent-1", "Sam", new LaunchSessionTransport("relay-only", null, 27250, true, true),
                host: new LaunchHostOptions("Sam's Sandbox", 8, "lan", true, true, false));
            Assert(host.Command == "host-session" && host.Host!.PasswordRequired, "A host asks for its password in game.");
            // The JSON reader refuses U+FFFE and U+FFFF, so no writer may produce them.
            foreach (var text in new[] { "Sam\uFFFF", "Sam\uFFFE" })
            {
                Rejects(() => new LaunchSessionRequest("host", "intent-1", text, new LaunchSessionTransport("relay-only", null, 0, false, true),
                    host: new LaunchHostOptions("Sandbox", 8, "lan", false, false, false)), "Display names hold no noncharacters.");
                Rejects(() => new LaunchHostOptions(text, 8, "lan", false, false, false), "Server names hold no noncharacters.");
                Rejects(() => new LaunchOutcomeMultiplayer("join", "intent-1", detail: text), "Details hold no noncharacters.");
                Rejects(() => new MultiplayerHostingDefaults(text, 8, "lan", 27250, false), "Hosting names hold no noncharacters.");
            }

            Rejects(() => new LaunchSessionRequest("host", "intent-1", "Sam", new LaunchSessionTransport("relay-only", null, 0, false, true),
                host: new LaunchHostOptions("Sandbox", 8, "lan", false, false, true)), "Only developer mode serves packages.");
            Assert(new LaunchSessionRequest("host", "intent-1", "Sam", new LaunchSessionTransport("relay-only", null, 0, false, true),
                host: new LaunchHostOptions("Sandbox", 8, "lan", false, false, true), developerMode: true).Host!.ServePackages,
                "Developer mode serves packages.");
            Rejects(() => new ProfileLaunchConfigurationV5("p", 0, "r", "join-session", Array.Empty<PackageIdentity>(),
                PackageSetDigest.Of(Array.Empty<PackageIdentity>()), false, Array.Empty<string>(), new Dictionary<string, string>(), host),
                "The command agrees with the role.");
            Rejects(() => new ProfileLaunchConfigurationV5("p", 0, "r", "join-session", Array.Empty<PackageIdentity>(),
                PackageSetDigest.Of(Array.Empty<PackageIdentity>()), true, Array.Empty<string>(), new Dictionary<string, string>(),
                Join(Direct("10.0.0.1"))), "A join never inherits the manager's mod state.");
        }

        private static void Consume()
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeSessionConsume-");
            try
            {
                var paths = new ManagerPaths(owned.CreateSubdirectory("game").CreateSubdirectory("BepInEx").FullName);
                paths.EnsureCreated();
                var store = new LaunchStagingStore(paths);
                var request = Request(Join(Direct(PublicHost), direct: true));
                var path = store.RequestPath(request.RequestId);
                File.WriteAllText(path, LaunchTransportJson.WriteProfileV5(request));
                var consumed = store.ConsumeLaunchRequest(path);
                Assert(consumed.SchemaVersion == 5 && consumed.Session!.Target!.Host == PublicHost && !File.Exists(path),
                    "A V5 request is consumed once with its session, and its address leaves staging with it.");
                var foreign = store.RequestPath("other-request");
                File.WriteAllText(foreign, LaunchTransportJson.WriteProfileV5(request));
                Rejects(() => store.ConsumeLaunchRequest(foreign), "A request is consumed only under its own filename.");
                Assert(File.Exists(foreign), "A foreign request stays untouched.");
                var v4 = store.RequestPath("fedcba9876543210fedcba9876543210");
                File.WriteAllText(v4, V4Menu);
                Assert(store.ConsumeLaunchRequest(v4).SchemaVersion == 4 && !File.Exists(v4), "V4 is consumed as before.");
            }
            finally { owned.Delete(true); }
        }

        private static void Correlation()
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeSessionCorrelation-");
            try
            {
                var paths = new ManagerPaths(owned.CreateSubdirectory("game").CreateSubdirectory("BepInEx").FullName);
                paths.EnsureCreated();
                var store = new LaunchStagingStore(paths);
                var valid = LaunchTransportJson.WriteProfileV5(Request(Join(Direct("10.0.0.1"))));
                var path = store.RequestPath("request-5");
                // A password member makes the request invalid; its correlation still names the session it was.
                File.WriteAllText(path, valid.Replace("\"intentId\":\"intent-1\"", "\"intentId\":\"intent-1\",\"password\":\"x\""));
                Rejects(() => store.ConsumeLaunchRequest(path), "A staged password is refused.");
                var correlation = store.TryReadCorrelation(path)!;
                Assert(correlation.Command == "join-session" && correlation.IntentId == "intent-1" && correlation.ExecutableCommand == "main-menu"
                    && correlation.Session!.Role == "join" && correlation.Session.Command == "join-session",
                    "A rejected V5 request is reported as the session launch it was.");
                File.WriteAllText(path, V4Menu.Replace("fedcba9876543210fedcba9876543210", "request-5"));
                var v4 = store.TryReadCorrelation(path)!;
                Assert(v4.IntentId == null && v4.Session == null && v4.ExecutableCommand == "main-menu", "V4 correlation is unchanged.");
            }
            finally { owned.Delete(true); }
        }

        private static void Publication()
        {
            var identity = new LaunchSessionIdentity("request-5", "join", "intent-1");
            var progress = LaunchSessionPublication.ToSessionProgress(new LaunchProgress("request-5", 3, "loading-world", "session-1", true), identity);
            Assert(progress.Version == 2 && progress.Session!.State == "pending" && progress.Phase == "loading-world" && progress.Sequence == 3,
                "Progress keeps its phase and sequence with the session still pending.");
            var failed = new LaunchOutcome("launch", "request-5", 4, "idle", "failed", new[] { new LaunchBlock(LaunchBlockCode.PlanPackageSetMismatch, "p") },
                command: "main-menu", error: new LaunchExecutionError("invalidState", "Changed."));
            var published = LaunchSessionPublication.ToSessionOutcome(failed, identity)!;
            Assert(published.Version == 2 && published.Command == "join-session" && published.Multiplayer!.IntentId == "intent-1"
                && published.Blocks.Count == 1 && published.Error!.Code == "invalidState",
                "A failure before the session started is the session launch's outcome.");
            var parsed = LaunchTransportJson.ReadOutcome(LaunchTransportJson.WriteOutcome(published));
            Assert(parsed.Command == "join-session" && parsed.Multiplayer!.Role == "join", "The published outcome reads back.");
            Assert(LaunchSessionPublication.ToSessionOutcome(new LaunchOutcome("launch", "request-5", 5, "idle", "succeeded",
                Array.Empty<LaunchBlock>(), command: "main-menu"), identity) == null, "Reaching the main menu is not yet a join's success.");
            Assert(LaunchSessionPublication.ToSessionOutcome(new LaunchOutcome("session", "request-5", 6, "idle", "succeeded",
                Array.Empty<LaunchBlock>(), sessionId: "session-1"), identity) == null, "A gamemode session's end is not the live session's.");
        }

        private static void Channels()
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeSessionChannels-");
            try
            {
                var paths = new ManagerPaths(owned.CreateSubdirectory("game").CreateSubdirectory("BepInEx").FullName);
                paths.EnsureCreated();
                var store = new LaunchStagingStore(paths);
                var handoff = new SessionHandoff("process-1", "handoff-1", LaunchHandoffTarget.Current());
                store.WriteSessionHandoff(handoff);
                Assert(File.ReadAllText(store.SessionHandoffPath("process-1")) == LaunchTransportJson.WriteSessionHandoff(handoff),
                    "A handoff is staged under the process's request.");
                var listing = LaunchSessionTarget.Listing("lan", ServerId, new string('a', 64), new string('b', 64));
                var join = new SessionJoinRequest("process-1", "join-1", "0123456789abcdef", Join(listing));
                File.WriteAllText(store.SessionJoinPath("process-1"), LaunchTransportJson.WriteSessionJoin(join));
                Assert(store.TakeSessionJoin("process-2") == null && File.Exists(store.SessionJoinPath("process-1")), "Another process's join is not taken.");
                var taken = store.TakeSessionJoin("process-1")!;
                Assert(taken.JoinId == "join-1" && !File.Exists(store.SessionJoinPath("process-1")) && store.TakeSessionJoin("process-1") == null,
                    "A live join is taken once.");
                File.WriteAllText(store.SessionJoinPath("process-1"), "{\"schemaVersion\":1}");
                Assert(store.TakeSessionJoin("process-1") == null && !File.Exists(store.SessionJoinPath("process-1")),
                    "A malformed join is removed without being acted on.");
                Rejects(() => new SessionJoinRequest("process-1", "join-2", "0123456789abcdef", Join(Direct("10.0.0.1"))),
                    "A live join never carries an address.");
                Assert(store.ReadPreferences() == null && store.ReadPreferencesJournal() == null, "Absent preferences read as none.");
                File.WriteAllText(store.PreferencesPath, "{\"schemaVersion\":1}");
                Assert(store.ReadPreferences() == null, "Damaged preferences read as none.");
                var projection = new MultiplayerPreferencesProjection(3, 1, "Sam", 1, 1, true, true, false,
                    new MultiplayerHostingDefaults("Sam's server", 8, "lan", 27250, false), "https://mp.topiaforge.dev", Epoch);
                File.WriteAllText(store.PreferencesPath, LaunchTransportJson.WritePreferences(projection));
                var read = store.ReadPreferences()!;
                Assert(read.Revision == 3 && read.DisplayName == "Sam" && read.ServiceOrigin == "https://mp.topiaforge.dev"
                    && read.JournalEpoch == Epoch && read.JournalApplied == 1, "Preferences read back.");
            }
            finally { owned.Delete(true); }
        }

        private static void JournalAppend()
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeSessionJournal-");
            try
            {
                var paths = new ManagerPaths(owned.CreateSubdirectory("game").CreateSubdirectory("BepInEx").FullName);
                paths.EnsureCreated();
                var store = new LaunchStagingStore(paths);
                var journal = new MultiplayerPreferencesJournal(Epoch, new[]
                {
                    MultiplayerPreferenceChange.DisplayName(1, "Sam"), MultiplayerPreferenceChange.Disclosure(2, 1)
                });
                var projection = Counting(1, Epoch);
                var next = journal.Append(MultiplayerPreferenceChange.Voice(journal.NextSequence(projection), 1, true), projection);
                Assert(next.Epoch == Epoch && next.Changes.Select(change => change.Sequence).SequenceEqual(new[] { 2, 3 }),
                    "Appending keeps the epoch and drops what the launcher applied from this journal.");
                var other = Counting(9, OtherEpoch);
                Assert(journal.NextSequence(other) == 3 && journal.NextSequence(Counting(0, null)) == 3
                    && journal.Append(MultiplayerPreferenceChange.Voice(3, 1, true), other).Changes.Count == 3,
                    "A position in another journal, or none, neither numbers nor trims this one.");
                store.WritePreferencesJournal(next);
                var read = store.ReadPreferencesJournal()!;
                Assert(read.Epoch == Epoch && read.Changes.Count == 2 && read.Changes[1].Kind == "voice" && read.Changes[1].NewVoiceEnabled,
                    "The journal reads back with its epoch.");
                Rejects(() => MultiplayerPreferenceChange.Voice(4, 0, true), "Voice stays off until the player consents.");
                Rejects(() => new MultiplayerPreferencesJournal(Epoch, new[] { MultiplayerPreferenceChange.Disclosure(2, 1), MultiplayerPreferenceChange.Disclosure(2, 1) }),
                    "Sequences increase strictly.");
                foreach (var epoch in new[] { Epoch.ToUpperInvariant(), Epoch.Substring(1), Epoch + "0", new string('g', 32), string.Empty })
                {
                    Rejects(() => new MultiplayerPreferencesJournal(epoch, Array.Empty<MultiplayerPreferenceChange>()), "An epoch is 32 lowercase hexadecimal digits.");
                    Rejects(() => Counting(1, epoch), "A position names its journal by a valid epoch.");
                }

                Rejects(() => Counting(1, null), "A position names the journal it counts.");
                var started = MultiplayerPreferencesJournal.Start();
                Assert(started.Changes.Count == 0 && started.Epoch.Length == 32 && started.Epoch.All(Uri.IsHexDigit)
                    && started.Epoch == started.Epoch.ToLowerInvariant() && started.Epoch != MultiplayerPreferencesJournal.Start().Epoch,
                    "A new journal starts empty, under a fresh random epoch.");

                // The launcher takes the journal in only before a session launch and after the game exits, so one session's
                // edits all land in it. Only the newest change of each preference matters, so the journal never fills.
                var session = new MultiplayerPreferencesJournal(Epoch, Array.Empty<MultiplayerPreferenceChange>());
                for (var edit = 0; edit < 200; edit++)
                {
                    var sequence = session.NextSequence(projection);
                    session = session.Append((edit % 3) switch
                    {
                        0 => MultiplayerPreferenceChange.Voice(sequence, 1, edit % 2 == 0),
                        1 => MultiplayerPreferenceChange.Network(sequence, edit % 2 == 0, false),
                        _ => MultiplayerPreferenceChange.DisplayName(sequence, "Sam " + (edit % 10)),
                    }, projection);
                }

                Assert(session.Changes.Select(change => change.Kind + "@" + change.Sequence)
                    .SequenceEqual(new[] { "displayName@199", "voice@200", "network@201" }),
                    "Two hundred edits keep only the newest change of each preference.");
                Assert(session.Changes[0].NewDisplayName == "Sam 7" && session.Changes[1].NewVoiceEnabled && !session.Changes[2].NewLanDiscovery,
                    "The kept changes hold the newest values.");
                var acknowledged = new MultiplayerPreferencesJournal(Epoch, Array.Empty<MultiplayerPreferenceChange>())
                    .Append(MultiplayerPreferenceChange.Disclosure(2, 2), null).Append(MultiplayerPreferenceChange.Disclosure(3, 1), null);
                Assert(acknowledged.Changes.Select(change => change.NewDisclosure).SequenceEqual(new[] { 2, 1 }),
                    "An acknowledgement of less never supersedes one of more, since the launcher never lowers the version.");
                Assert(acknowledged.Append(MultiplayerPreferenceChange.Disclosure(4, 2), null).Changes.Select(change => change.Sequence)
                    .SequenceEqual(new[] { 4 }), "One that acknowledges at least as much supersedes them.");
            }
            finally { owned.Delete(true); }
        }

        /// <summary>A projection at revision 4 that counts <paramref name="applied"/> in the journal <paramref name="epoch"/>.</summary>
        private static MultiplayerPreferencesProjection Counting(int applied, string? epoch) => new MultiplayerPreferencesProjection(4, applied,
            "Sam", 0, 0, false, true, false, new MultiplayerHostingDefaults("Sam's server", 8, "lan", 27250, false), null, epoch);

        private static void Privacy()
        {
            var session = Join(Direct(PublicHost), direct: true);
            var request = Request(session);
            var consumed = LaunchTransportJson.ReadLaunchRequest(LaunchTransportJson.WriteProfileV5(request));
            foreach (var text in new[] { session.ToString(), session.Target!.ToString(), request.ToString(), consumed.Describe() })
            {
                Assert(!text.Contains(PublicHost, StringComparison.Ordinal) && !text.Contains(PublicPort.ToString(), StringComparison.Ordinal),
                    "No description names the address.");
            }

            Assert(consumed.Describe() == "Using one-shot V5 launch profile mp-0123456789abcdef request request-5 to join a session.",
                "The V5 log line names the role only.");
            var staged = LaunchTransportJson.WriteProfileV5(request);
            foreach (var invalid in new[]
            {
                staged.Replace("\"" + PublicHost + "\"", "\"203.0.113.077\""),
                staged.Replace("\"privacy\":\"direct\"", "\"privacy\":\"relay-only\"").Replace("\"directConsent\":{\"disclosure\":1,\"intentId\":\"intent-1\"},", string.Empty)
            })
            {
                try { LaunchTransportJson.ReadProfileV5(invalid); throw new InvalidOperationException("Accepted an invalid request."); }
                catch (InvalidDataException error)
                {
                    Assert(!error.ToString().Contains("203.0.113", StringComparison.Ordinal) && !error.ToString().Contains(PublicPort.ToString(), StringComparison.Ordinal),
                        "No refusal repeats the address.");
                }
            }

            var progress = LaunchTransportJson.WriteProgress(new LaunchProgress("request-5", 1, "connecting", null, null,
                new LaunchProgressSession("join", "intent-1", "connecting", ServerId)));
            var outcome = LaunchTransportJson.WriteOutcome(new LaunchOutcome("launch", "request-5", 2, "connecting", "failed", Array.Empty<LaunchBlock>(),
                command: "join-session", multiplayer: new LaunchOutcomeMultiplayer("join", "intent-1", ServerId, "timeout", "The host did not answer.")));
            var handoff = LaunchTransportJson.WriteSessionHandoff(new SessionHandoff("request-5", "handoff-1", LaunchHandoffTarget.Current()));
            foreach (var text in new[] { progress, outcome, handoff })
            {
                Assert(!text.Contains(PublicHost, StringComparison.Ordinal) && !text.Contains(PublicPort.ToString(), StringComparison.Ordinal),
                    "Only the launch request holds the address.");
            }
        }

        /// <summary>
        /// The session the runtime offers the provider: the request's session as the internal launch session, with direct play
        /// only under the player's consent for this session, and its target pinned by the server and descriptor the launcher
        /// verified. Neither the session nor its target prints the address.
        /// </summary>
        private static void Offer()
        {
            var session = new LaunchSessionRequest("join", "intent-1", "Sam",
                new LaunchSessionTransport("direct", new LaunchDirectConsent("intent-1", 1), 0, false, true), "https://mp.topiaforge.dev",
                Direct(PublicHost), devInstance: new LaunchDevInstance(1, 2));
            var offered = ModManager.LaunchSessionOffer.ToSession(session);
            Assert(offered.IntentId == "intent-1" && offered.AllowDirect && offered.DisplayName == "Sam" && offered.Host == null
                && offered.Target!.Kind == TopiaForge.Mods.Internal.InternalJoinTargetKind.Direct && offered.Target.ServerId == ServerId
                && offered.Target.Host == PublicHost && offered.Target.Port == PublicPort && offered.Target.DescriptorSha256 == new string('a', 64),
                "The offered session carries the launch, its target pinned by the verified server and descriptor.");
            Assert(!offered.ToString().Contains(PublicHost, StringComparison.Ordinal) && !offered.Target!.ToString().Contains(PublicHost, StringComparison.Ordinal)
                && !offered.ToString().Contains(PublicPort.ToString(), StringComparison.Ordinal),
                "A launch session never shows its address.");
            var relayOnly = ModManager.LaunchSessionOffer.ToSession(new LaunchSessionRequest("join", "intent-2", "Sam",
                new LaunchSessionTransport("relay-only", null, 0, false, true), "https://mp.topiaforge.dev",
                LaunchSessionTarget.Invite("ABCDE12345", ServerId, new string('a', 64), new string('b', 64))));
            Assert(!relayOnly.AllowDirect && relayOnly.Target!.Kind == TopiaForge.Mods.Internal.InternalJoinTargetKind.Invite && relayOnly.Target.InviteCode == "ABCDE12345"
                && relayOnly.Target.Host == null, "A relay-only launch is relay-only: nothing turns direct play on.");
            var host = ModManager.LaunchSessionOffer.ToSession(new LaunchSessionRequest("host", "intent-3", "Sam",
                new LaunchSessionTransport("relay-only", null, 27260, true, true),
                host: new LaunchHostOptions("Sam's Sandbox", 6, "lan", true, true, false)));
            Assert(host.Host!.MaxPlayers == 6 && host.Host.PasswordRequired && host.Host.Visibility == TopiaForge.Mods.Internal.InternalSessionVisibility.Lan
                && host.Target == null && host.Port == 27260 && host.RouterForwarding && !host.AllowDirect,
                "A host's session carries its options and its game port; its password is typed in game.");
        }

        private static void Rejects(Action operation, string message)
        {
            try { operation(); }
            catch (Exception error) when (error is ArgumentException || error is InvalidDataException) { return; }
            throw new InvalidOperationException(message);
        }

        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
