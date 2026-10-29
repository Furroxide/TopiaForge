extern alias provider;

using System.Diagnostics;
using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A host and four players play for thirty virtual minutes over the deterministic network with real cryptography,
/// transport, engine and live flows. Commands and replicated state change throughout, players join, leave, lose their
/// links, reconnect and are kicked and banned, NATs rebind, frames stall, the host replaces its session, and the network
/// moves between realistic conditions. Every five minutes the workload holds still and every peer must match the host
/// exactly; every growing table is held to a ceiling all along. The run takes seconds, so every pass runs it twice, to
/// prove a seed repeats it exactly; <c>--soak</c> adds eight more seeds and a four-hour run.
/// </summary>
internal static class LiveSoakTests
{
    private const int Seed = 140;
    private const int Minutes = 30;

    internal static void ASoakConvergesStaysBoundedAndRepeats()
    {
        SoakLifetimeTests.Verify();
        var watch = Stopwatch.StartNew();
        var first = new SoakScenario(Seed, Minutes).Run();
        var cold = watch.Elapsed.TotalSeconds;
        var again = new SoakScenario(Seed, Minutes).Run();
        var warm = watch.Elapsed.TotalSeconds - cold;
        That(first.Summary == again.Summary, "a seed repeats the soak exactly:\n" + first.Summary + "\n" + again.Summary);
        Console.WriteLine("Soak, seed " + Seed + ", " + Minutes + " virtual minutes: " + cold.ToString("0.0") + " s real, then " +
                          warm.ToString("0.0") + " s for the repeat. " + first.Report);
        if (!HarnessOptions.ExtendedSoak) return;
        for (var seed = Seed + 1; seed <= Seed + 8; seed++) Extended(seed, Minutes);
        Extended(Seed + 9, 240);
    }

    private static void Extended(int seed, int minutes)
    {
        var watch = Stopwatch.StartNew();
        var outcome = new SoakScenario(seed, minutes).Run();
        Console.WriteLine("Soak, seed " + seed + ", " + minutes + " virtual minutes: " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s real. " + outcome.Report);
    }

    /// <summary>What a soak ends with: a summary two runs of one seed must share, and a report of what happened.</summary>
    internal sealed class SoakOutcome
    {
        internal SoakOutcome(string summary, string report)
        {
            Summary = summary;
            Report = report;
        }

        internal string Summary { get; }

        internal string Report { get; }
    }

    private sealed class SoakScenario
    {
        private static readonly (string Name, long Latency, long Jitter, double Loss, double Duplicates)[] Profiles =
        {
            ("lan", 1_000, 1_000, 0, 0),
            ("wifi", 6_000, 12_000, 0.01, 0.002),
            ("internet", 35_000, 20_000, 0.02, 0.005),
            ("congested", 80_000, 50_000, 0.05, 0.01),
            ("burst", 50_000, 40_000, 0.08, 0.01)
        };

        private const long CheckpointEvery = 300_000_000;
        private const long ReplacementEvery = 600_000_000;

        private readonly int seed;
        private readonly long end;
        private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
        private SoakRig rig = null!;
        private long nextPhaseAt;
        private long nextCheckpointAt;
        private long nextReplacementAt;
        private long nextHostStallAt;
        private long banAt;
        private long unbanAt;
        private SoakPeer? banned;
        private int replacements;

        internal SoakScenario(int seed, int minutes)
        {
            this.seed = seed;
            end = minutes * 60_000_000L;
        }

        internal SoakOutcome Run()
        {
            using (rig = new SoakRig(seed))
            {
                rig.Start();
                var start = rig.Network.Now;
                var stop = start + end;
                nextPhaseAt = start;
                nextCheckpointAt = start + CheckpointEvery;
                nextReplacementAt = start + Math.Min(end * 3 / 10, ReplacementEvery);
                nextHostStallAt = start + Seconds(40);
                banAt = start + (end * 4 / 10);
                unbanAt = start + (end * 55 / 100);
                foreach (var client in rig.Clients)
                {
                    client.Direct = client.Index % 2 == 0;
                    client.NextActionAt = start + (client.Index * 500_000L);
                }

                while (rig.Network.Now < stop)
                {
                    rig.Frame(Act);
                    Observe();
                    if (rig.Frames % 8 == 0) rig.Sample();
                    if (rig.Network.Now >= nextCheckpointAt && rig.Network.Now < stop - Seconds(20))
                    {
                        Checkpoint(allPlayers: false);
                        nextCheckpointAt = rig.Network.Now + CheckpointEvery;
                    }
                }

                Checkpoint(allPlayers: true);
                That(rig.Network.Spins == 0, "no endpoint ever reported a timer that was already due (" + rig.Network.Spins + " spins)");
                foreach (var peer in rig.Peers)
                {
                    That(peer.Diagnostics.Reports.Count == 0, peer.Name + " reported nothing wrong: " + string.Join("; ", peer.Diagnostics.Reports.Take(5)));
                }

                That(rig.TransportDiagnostics.Count(provider::TopiaForge.Multiplayer.Transport.TransportDiagnosticKind.ProtocolViolation) == 0,
                    "no endpoint saw a protocol violation from a correct peer");
                return Outcome();
            }
        }

        /// <summary>Runs before the peers pump each frame: the network's moods, the host's moves and every player's own.</summary>
        private void Act()
        {
            var now = rig.Network.Now;
            if (now >= nextPhaseAt) ChangeConditions(now);
            foreach (var client in rig.Clients)
            {
                if (client.HealAt <= now) Heal(client);
            }

            if (rig.Quiet) return;
            if (now >= nextHostStallAt)
            {
                rig.Host.StalledUntil = now + rig.Random.Next(200_000, 1_000_000);
                nextHostStallAt = now + Seconds(rig.Random.Next(60, 180));
                Count("host stalls");
            }

            if (now >= nextReplacementAt)
            {
                replacements++;
                rig.Host.Engine.ReplaceSession(new MultiplayerSessionId("soak:" + seed + ":" + replacements), new SessionSeed((ulong)(seed + replacements)));
                nextReplacementAt = rig.Network.Now + ReplacementEvery;
                Count("session replacements");
            }

            if (banned == null && now >= banAt && rig.Clients.FirstOrDefault(client => client.InSession && Connected(client)) is { } target)
            {
                banned = target;
                That(rig.Host.Provider.Live!.Ban(target.Engine.LocalParticipantId!.Value, target.Name), "the host bans " + target.Name);
                target.NextActionAt = now + Seconds(5);
                Count("bans");
            }

            if (banned != null && unbanAt > 0 && now >= unbanAt)
            {
                That(rig.Host.Provider.Live!.Host!.Bans.Remove(banned.Fingerprint), "the host lifts the ban");
                unbanAt = 0;
                Count("unbans");
            }

            foreach (var client in rig.Clients)
            {
                if (now >= client.NextActionAt) Decide(client, now);
            }

            foreach (var peer in rig.Peers)
            {
                if (now >= peer.StalledUntil) Play(peer, rig.Frames == 0 ? 0.016 : 0.021);
            }
        }

        private void ChangeConditions(long now)
        {
            var profile = Profiles[rig.Random.Next(Profiles.Length)];
            var conditions = rig.Network.Conditions;
            conditions.LatencyMicros = profile.Latency;
            conditions.JitterMicros = profile.Jitter;
            conditions.LossRate = profile.Loss;
            conditions.DuplicateRate = profile.Duplicates;
            nextPhaseAt = now + (profile.Name == "burst" ? Seconds(rig.Random.Next(5, 15)) : Seconds(rig.Random.Next(40, 150)));
            Count("network " + profile.Name);
        }

        /// <summary>A player's next move: join from solo play, or, in a session, one of the ways sessions are disturbed.</summary>
        private void Decide(SoakPeer client, long now)
        {
            client.NextActionAt = now + Seconds(rig.Random.Next(8, 40));
            if (client.Solo)
            {
                rig.Join(client);
                Count("join attempts");
                return;
            }

            if (!client.InSession || client.PartitionedFrom != null) return;
            var roll = rig.Random.Next(100);
            if (roll < 16)
            {
                client.Join!.Leave();
                client.NextActionAt = now + Seconds(rig.Random.Next(3, 45));
                Count("leaves");
            }
            else if (roll < 30) Partition(client, now, rig.Random.Next(500_000, 2_500_000), "blips");
            else if (roll < 44) Partition(client, now, rig.Random.Next(4_000_000, 9_000_000), "stalls");
            else if (roll < 56) Partition(client, now, rig.Random.Next(12_000_000, 24_000_000), "reconnecting cuts");
            else if (roll < 60) Partition(client, now, 50_000_000, "lost links");
            else if (roll < 70 && Connected(client))
            {
                That(rig.Host.Provider.Live!.Host!.Kick(client.Engine.LocalParticipantId!.Value, "Soak kick."), "the host kicks " + client.Name);
                client.NextActionAt = now + Seconds(rig.Random.Next(3, 45));
                Count("kicks");
            }
            else if (roll < 80 && client.Direct && client.Node != null)
            {
                client.Node.Rebind();
                Count("NAT rebinds");
            }
            else if (roll < 92)
            {
                client.StalledUntil = now + rig.Random.Next(200_000, 1_500_000);
                Count("client stalls");
            }
        }

        private void Partition(SoakPeer client, long now, long duration, string what)
        {
            client.PartitionedFrom = rig.Host.Node!;
            rig.Network.Partition(client.PartitionedFrom, client.Node!, cut: true);
            client.HealAt = now + duration;
            client.NextActionAt = Math.Max(client.NextActionAt, client.HealAt + Seconds(2));
            Count(what);
        }

        private void Heal(SoakPeer client)
        {
            foreach (var node in client.Factory.Networks.Select(network => network.Node)) rig.Network.Partition(client.PartitionedFrom!, node, cut: false);
            client.PartitionedFrom = null;
            client.HealAt = long.MaxValue;
        }

        /// <summary>Submits adds and nudges at a player's pace, in solo play or a session, with the odd burst and refusal.</summary>
        private void Play(SoakPeer peer, double seconds)
        {
            var random = rig.Random;
            if (random.NextDouble() < seconds * 2.5) Add(peer, random.Next(10) == 0 ? 0 : random.Next(1, 10));
            if (random.NextDouble() < seconds * 0.02)
            {
                for (var burst = 0; burst < 25; burst++) Add(peer, random.Next(1, 10));
                Count("bursts");
            }

            var local = peer.Engine.LocalParticipantId;
            foreach (var marker in peer.Workload.Markers)
            {
                var owned = local.HasValue && marker.OwnerId.HasValue && marker.OwnerId.Value.Equals(local.Value);
                if (random.NextDouble() < seconds * (owned ? 1.5 : 0.05)) Nudge(peer, marker, random.Next(-3, 4));
            }
        }

        private void Add(SoakPeer peer, int amount)
        {
            var nonce = ++peer.Nonce;
            var remote = peer.Engine.Role == provider::TopiaForge.Multiplayer.Engine.EngineRole.Client;
            var task = peer.Provider.SubmitAsync(SoakWorkload.AddType, new SoakOrder(peer.Index, nonce, amount));
            peer.Submissions.Add(new SoakSubmission(peer.Index, nonce, false, remote, task, () => Shape(task.Result.Result)));
            Count("adds submitted");
        }

        private void Nudge(SoakPeer peer, IReplicatedObject<SoakMarker, SoakNudge> marker, int delta)
        {
            var nonce = ++peer.Nonce;
            var remote = peer.Engine.Role == provider::TopiaForge.Multiplayer.Engine.EngineRole.Client;
            var task = marker.SubmitInputAsync(new SoakNudge(peer.Index, nonce, delta));
            peer.Submissions.Add(new SoakSubmission(peer.Index, nonce, true, remote, task, () => Shape(task.Result.Result)));
            Count("nudges submitted");
        }

        /// <summary>Counts how joins progress and end, and checks that a link that came back kept its participant.</summary>
        private void Observe()
        {
            foreach (var client in rig.Clients)
            {
                var phase = client.Join?.Phase ?? JoinPhase.Ended;
                if (phase == client.LastPhase) continue;
                if (phase == JoinPhase.Reconnecting)
                {
                    client.ParticipantBeforeLoss = rig.Host.Engine.Snapshot.Participants.FirstOrDefault(participant => participant.DisplayName == client.Name)?.Id;
                    client.LostAt = rig.Network.Now;
                    Count("reconnects");
                }
                else if (phase == JoinPhase.InSession && client.LastPhase == JoinPhase.Synchronizing && client.ParticipantBeforeLoss.HasValue)
                {
                    // The host keeps a departed player's place for 30 s from when it notices; well inside that, the
                    // player must come back as the same participant. Near the edge either answer is correct.
                    if (rig.Network.Now - client.LostAt < 20_000_000)
                    {
                        That(client.Engine.LocalParticipantId.Equals(client.ParticipantBeforeLoss), client.Name + " comes back as the same participant");
                        Count("resumed as the same participant");
                    }

                    client.ParticipantBeforeLoss = null;
                }
                else if (phase == JoinPhase.InSession)
                {
                    Count("joins");
                }
                else if (phase == JoinPhase.Ended)
                {
                    var failure = client.Join!.Failure;
                    That(failure == LiveFailure.Left || failure == LiveFailure.Kicked || failure == LiveFailure.Banned || failure == LiveFailure.ConnectionLost,
                        client.Name + "'s join ends only the ways the soak ends it (" + failure + ": " + client.Join.Detail + ")");
                    That(client.Engine.Role == provider::TopiaForge.Multiplayer.Engine.EngineRole.Host && client.Engine.Snapshot.Participants.Count == 1,
                        client.Name + " is back in solo play");
                    client.ParticipantBeforeLoss = null;
                    Count("ended " + failure);
                }

                client.LastPhase = phase;
            }
        }

        /// <summary>
        /// Holds the workload still and heals every cut, waits for every join in progress to settle and every peer to catch
        /// up, then compares every peer in the session with the host, value by value, and audits every submission.
        /// </summary>
        private void Checkpoint(bool allPlayers)
        {
            rig.Quiet = true;
            Count("checkpoints");
            foreach (var client in rig.Clients)
            {
                if (client.PartitionedFrom != null) Heal(client);
                client.StalledUntil = 0;
            }

            rig.Host.StalledUntil = 0;
            if (allPlayers)
            {
                if (banned != null && unbanAt > 0) rig.Host.Provider.Live!.Host!.Bans.Remove(banned.Fingerprint);
                unbanAt = 0;
                rig.Network.Conditions.LossRate = Math.Min(rig.Network.Conditions.LossRate, 0.02);
                foreach (var client in rig.Clients) client.NextActionAt = 0;
            }

            // Protocol timeouts do not scale with the run: a join in progress may need its whole reconnect window.
            var deadline = rig.Network.Now + 90_000_000;
            while (rig.Network.Now < deadline && !Settled(allPlayers))
            {
                rig.Frame(() =>
                {
                    if (!allPlayers) return;
                    foreach (var client in rig.Clients.Where(client => client.Solo && rig.Network.Now >= client.NextActionAt))
                    {
                        rig.Join(client);
                        client.NextActionAt = rig.Network.Now + Seconds(6);
                    }
                });
                Observe();
            }

            That(Settled(allPlayers), "every peer settles (" + Describe() + ")");
            rig.Sample();
            var expected = rig.Host.Workload.Describe();
            foreach (var client in rig.Clients.Where(client => client.InSession))
            {
                That(client.Workload.Describe() == expected, client.Name + " converges on the host's state:\n  host   " + expected + "\n  client " + client.Workload.Describe());
                That(Roster(client) == Roster(rig.Host), client.Name + " sees the host's players: " + Roster(client) + " vs " + Roster(rig.Host));
            }

            VerifyPresentation();
            Audit();
            rig.Quiet = false;
        }

        /// <summary>
        /// While every peer is settled and connected, the host emits a few flashes nobody else does, and each must reach
        /// every connected peer exactly once: a flash lost to a connected peer, or delivered to it twice, fails here.
        /// </summary>
        private void VerifyPresentation()
        {
            // Only once the host has every joined connection marked Ready does a flash reach all of them; a client can
            // report Ready a round trip before its ReadyAck reaches the host, and a flash sent in that gap would miss it.
            var settleBy = rig.Network.Now + 30_000_000;
            while (rig.Network.Now < settleBy && (rig.Host.Engine.ConnectionsAwaitingReady > 0 || rig.Host.Engine.HasPendingWork)) rig.Frame();

            var pings = new List<(int Peer, int Nonce)>();
            for (var count = 0; count < 3; count++) pings.Add(rig.Host.Workload.Ping());
            var deadline = rig.Network.Now + 30_000_000;
            while (rig.Network.Now < deadline && (rig.Host.Engine.HasPendingWork || !AllGot(pings))) rig.Frame();
            foreach (var peer in Connected())
            {
                foreach (var ping in pings)
                {
                    That(peer.Workload.GotFlash(ping.Peer, ping.Nonce), peer.Name + " got the checkpoint flash " + ping.Nonce + " while connected");
                }
            }
        }

        private bool AllGot(List<(int Peer, int Nonce)> pings) => Connected().All(peer => pings.All(ping => peer.Workload.GotFlash(ping.Peer, ping.Nonce)));

        /// <summary>The peers a flash must reach: the host, and every client still in the session.</summary>
        private IEnumerable<SoakPeer> Connected() => rig.Peers.Where(peer => peer == rig.Host || peer.InSession);

        private bool Settled(bool allPlayers)
        {
            if (rig.Host.Engine.HasPendingWork) return false;
            foreach (var client in rig.Clients)
            {
                if (client.InSession)
                {
                    if (client.Engine.Snapshot.State != MultiplayerSessionState.Ready || client.Engine.PendingSubmissionCount > 0) return false;
                    if (client.Workload.Describe() != rig.Host.Workload.Describe()) return false;
                    if (Roster(client) != Roster(rig.Host)) return false;
                }
                else if (!client.Solo || allPlayers)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Every submission has its one answer, and the ledgers agree with them and with the score.</summary>
        private void Audit()
        {
            var hostLedger = rig.Host.Workload.Ledger;
            foreach (var peer in rig.Peers)
            {
                That(peer.Workload.Ledger.Violations.Count == 0, "nothing ran twice on " + peer.Name + ": " + string.Join("; ", peer.Workload.Ledger.Violations));
                That(peer.Workload.FlashViolations.Count == 0, "no presentation event reached " + peer.Name + " twice: " + string.Join("; ", peer.Workload.FlashViolations));
                foreach (var submission in peer.Submissions)
                {
                    That(submission.Task.IsCompleted, peer.Name + "'s submission " + submission.Nonce + " has its answer");
                    var outcome = submission.Outcome;
                    var ledger = submission.Remote ? hostLedger : peer.Workload.Ledger;
                    var ran = submission.Nudge ? ledger.RanNudge(submission.Peer, submission.Nonce) : ledger.RanAdd(submission.Peer, submission.Nonce);
                    if (outcome.Succeeded) That(ran, peer.Name + "'s confirmed submission " + submission.Nonce + " ran where it was sent");
                    else if (outcome.ErrorCode != ModErrorCode.Cancelled) That(!ran, peer.Name + "'s refused submission " + submission.Nonce + " (" + outcome.ErrorCode + ") never ran");
                    Count(submission.Nudge ? "nudges " + outcome.ErrorCode : "adds " + outcome.ErrorCode);
                }

                peer.Submissions.Clear();
            }

            var totals = hostLedger.Session(rig.Host.Engine.Snapshot.Id.Value);
            var score = rig.Host.Workload.Score;
            That(score.Value.Value == totals.Sum && score.Version == (ulong)totals.Count,
                "the host's score is exactly the adds it ran this session (" + score.Value.Value + "@" + score.Version + " vs " + totals.Sum + "@" + totals.Count + ")");
        }

        private string Describe() =>
            string.Join(", ", rig.Clients.Select(client => client.Name + " " + (client.Join?.Phase.ToString() ?? "solo") + " " + client.Engine.Snapshot.State +
                                                          " pending " + client.Engine.PendingSubmissionCount)) + "; host pending " + rig.Host.Engine.HasPendingWork;

        private static string Roster(SoakPeer peer) =>
            string.Join(",", peer.Engine.Snapshot.Participants.OrderBy(participant => participant.Id.Value, StringComparer.Ordinal)
                .Select(participant => participant.Id + ":" + participant.DisplayName + (participant.IsConnected ? "+" : "-") + (participant.IsHost ? "h" : "")));

        private SoakOutcome Outcome()
        {
            var summary = rig.Network.TranscriptHash() + " at " + rig.Network.Now + "; " + rig.Host.Workload.Describe() + "; " +
                          string.Join(", ", counts.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key + " " + entry.Value));
            var report = rig.Network.Sent + " datagrams (" + rig.Network.Lost + " lost, " + rig.Network.Duplicated + " duplicated), " + rig.Frames + " frames; " +
                         string.Join(", ", counts.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key + " " + entry.Value)) +
                         ". Host ledger: " + rig.Host.Workload.Ledger.Adds + " adds and " + rig.Host.Workload.Ledger.Nudges + " nudges, each run once. Maxima: " + rig.Census + ".";
            return new SoakOutcome(summary, report);
        }

        /// <summary>Gets whether the host still counts the player as connected, so a kick or ban has someone to remove.</summary>
        private bool Connected(SoakPeer client) =>
            client.Engine.LocalParticipantId is { } id && rig.Host.Engine.IsConnectedParticipant(id);

        private void Count(string what) => counts[what] = counts.GetValueOrDefault(what) + 1;

        private static long Seconds(int value) => value * 1_000_000L;

        private static OperationResult<bool> Shape<T>(OperationResult<T> result) where T : notnull =>
            result.Succeeded ? OperationResult<bool>.Success(true) : OperationResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
    }
}
