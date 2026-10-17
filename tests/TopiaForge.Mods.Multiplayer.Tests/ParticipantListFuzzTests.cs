using System.Diagnostics;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A seeded fuzz of the participant list a client shows. One client engine plays against scripted hosts: three
/// identities run one process at a time, the third headless, and the first runs a second process alongside, as two copies
/// of the game that share a data folder would. Every process numbers its players itself and gives the client an id with
/// each new record, so ids differ between processes and records, and often agree. Hosts answer joins, split
/// their snapshots over several frames, list their players, replace their sessions in place, forget records, restart,
/// end sessions and refuse joins; links drop and stall; the player goes back to solo play. Every snapshot the client
/// reports, and the one it shows after each step, must list exactly the players of the host process that sent the list,
/// or nobody while a join withholds it, labelled only with the ids that process gave.
/// </summary>
internal static class ParticipantListFuzzTests
{
    private const int Steps = 300;

    internal static void EverySnapshotShowsTheListOfTheHostThatSentIt()
    {
        var runs = FuzzMutator.Iterations(1_000);
        var totals = new ParticipantListFuzz.Totals();
        var watch = Stopwatch.StartNew();
        for (var seed = 0; seed < runs; seed++)
        {
            var fuzz = new ParticipantListFuzz(seed, totals);
            try
            {
                fuzz.Run(Steps);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    "Assertion failed: participant-list seed " + seed + ": " + exception.Message + "\n  " + string.Join("\n  ", fuzz.Trace.TakeLast(40)),
                    exception);
            }
        }

        Console.WriteLine("Participant lists, " + runs + " seeded runs of " + Steps + " steps in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s: " + totals + ".");
        That(totals.Kept > 0 && totals.Withheld > 0 && totals.KeptByTheOtherCopy > 0 && totals.WithheldForOtherIds > 0 && totals.WithheldForANewRecord > 0 &&
             totals.Unanswered > 0 && totals.WindowSnapshots > 0 && totals.LinksLostWithAList > 0,
            "the runs reach every case (" + totals + ")");
    }
}

/// <summary>One run: the client, its scripted hosts, and the model of what the client must show.</summary>
internal sealed partial class ParticipantListFuzz
{
    private static readonly string[] Ids = { "p1", "p2", "p3", "p4" };
    private readonly Random random;
    private readonly Totals totals;
    private readonly VirtualEngineTransport network = new(new VirtualLinkConditions { LatencyTicks = 1 });
    private readonly RecordingDiagnostics diagnostics = new();
    private readonly SessionEngine client;
    private readonly List<HostState> hosts = new();
    private Connection? connection;
    private Shown? shown;
    private string? listedIdentity;
    private ulong tick;
    private ulong generation;
    private int lists;
    private int roleChanges;
    private int last;

    internal ParticipantListFuzz(int seed, Totals totals)
    {
        random = new Random(seed);
        this.totals = totals;
        client = SessionEngine.CreateClient(
            new EngineOptions
            {
                StepRateHz = 60,
                CallbackFailures = EngineCallbackFailureMode.Propagate,
                Diagnostics = diagnostics,
                AfterClientSessionEnds = _ => new SoloSessionStart(new MultiplayerSessionId("solo-after-end"), new SessionSeed(5), new ParticipantId("local"), "Me")
            },
            new MultiplayerSessionId("joining"),
            0);
        client.SubscribeSession(snapshot => Check(snapshot, exact: false));
        hosts.Add(new HostState(0, "host-a", headless: false));
        hosts.Add(new HostState(1, "host-b", headless: false));
        hosts.Add(new HostState(2, "host-c", headless: true));
        hosts.Add(new HostState(3, "host-a", headless: false) { Process = 100 });
    }

    /// <summary>Gets what happened, step by step, for the report of a failing seed.</summary>
    internal List<string> Trace { get; } = new();

    internal void Run(int steps)
    {
        for (var step = 0; step < steps; step++)
        {
            var roll = random.Next(100);
            var current = connection;
            if (current == null || current.Link.State == EngineLinkState.Closed)
            {
                if (roll < 55) Connect(PickHost());
                else if (roll < 75) Pump();
                else if (roll < 83) Restart(random.Next(hosts.Count));
                else if (roll < 95) Expire(random.Next(hosts.Count));
                else RoleChange();
            }
            else if (current.Phase == Phase.Awaiting)
            {
                if (roll < 40) Accept(current);
                else if (roll < 75) Pump();
                else if (roll < 82) Sever(current);
                else if (roll < 89) Connect(PickHost());
                else if (roll < 92) Restart(current.Host.Index);
                else if (roll < 95) Reject(current);
                else if (roll < 99) ToggleStall(current);
                else RoleChange();
            }
            else if (current.Phase == Phase.Ended || (!current.Accepted && roll < 70))
            {
                // A host's last word, or an answer the client has not read yet: mostly let it arrive.
                Pump();
            }
            else
            {
                if (roll < 12) SnapshotPart(current);
                else if (roll < 26) List(current);
                else if (roll < 50) Pump();
                else if (roll < 58) Sever(current);
                else if (roll < 66) Connect(PickHost());
                else if (roll < 74) Connect(current.Host.Index);
                else if (roll < 79) Connect(OtherCopy(current.Host.Index));
                else if (roll < 82) Restart(current.Host.Index);
                else if (roll < 86) Replace(current);
                else if (roll < 88) End(current);
                else if (roll < 92) Expire(random.Next(hosts.Count));
                else if (roll < 98) ToggleStall(current);
                else RoleChange();
            }

            Check(client.Snapshot, exact: true);
        }

        for (var drain = 0; drain < 4; drain++)
        {
            Pump();
            Check(client.Snapshot, exact: true);
        }
    }

    /// <summary>The engine is about to read a JoinAccepted from <paramref name="from"/>: the list stays only as the rule says.</summary>
    private void OnJoinAccepted(Connection from, JoinAcceptedMessage message)
    {
        That(message.ParticipantId == from.ClientId, "the script answered with the id it assigned");
        from.Accepted = true;
        totals.Joins++;
        var identity = from.Host.Identity;
        var sameRecord = message.RecordKept && listedIdentity == identity;
        var sameIds = shown != null && shown.ClientId == message.ParticipantId && shown.HostId == message.HostParticipantId;
        var keep = sameRecord && sameIds;
        if (shown != null)
        {
            if (keep) totals.Kept++;
            else totals.Withheld++;
            if (keep && shown.Host != from.Host.Index) totals.KeptByTheOtherCopy++;
            if (!keep && sameRecord) totals.WithheldForOtherIds++;
            if (!keep && sameIds && !message.RecordKept && listedIdentity == identity) totals.WithheldForANewRecord++;
        }

        Trace.Add("  join with " + from.Label + (message.RecordKept ? " (record kept)" : " (new record)") + " as " + message.ParticipantId + ": the list of " +
                  (shown?.ToString() ?? "nobody") + (keep ? " stays" : " is withheld"));
        if (!keep) shown = null;
        if (!message.RecordKept && listedIdentity == identity) listedIdentity = string.Empty;
    }

    /// <summary>The engine is about to read <paramref name="from"/>'s participant list.</summary>
    private void OnList(Connection from, List<ParticipantEntry> entries)
    {
        totals.Lists++;
        shown = new Shown(from.Host.Index, from.Process, entries, from.ClientId, from.HostId);
        listedIdentity = from.Host.Identity;
        Trace.Add("  list from " + from.Label + ": " + string.Join(",", entries.Select(entry => entry.Id)));
    }

    /// <summary>
    /// Checks a snapshot the client exposes against the list of the process that sent it. Every one must show that list
    /// row for row, or nobody while it is withheld, and may mark as local or host only the rows that process gave this
    /// player and itself. After a step, when the model has caught up with the engine, the marks must also be exactly
    /// there: the local mark while the client is joined, the host mark whenever the list holds that process's player.
    /// </summary>
    private void Check(MultiplayerSessionSnapshot snapshot, bool exact)
    {
        if (client.Role != EngineRole.Client) return;
        totals.Snapshots++;
        var rows = snapshot.Participants;
        if (shown == null)
        {
            That(rows.Count == 0 && !snapshot.LocalParticipantId.HasValue && !snapshot.HostParticipantId.HasValue,
                "with the list withheld the client shows nobody, not " + Describe(snapshot));
            if (connection is { Accepted: true, Discarded: false }) totals.WindowSnapshots++;
            return;
        }

        That(rows.Count == shown.Entries.Count, "the client shows the list of " + shown + ", not " + Describe(snapshot));
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var entry = shown.Entries[index];
            That(row.Id.Value == entry.Id && row.DisplayName == entry.DisplayName && row.IsConnected == entry.IsConnected,
                "row " + index + " is " + shown + "'s, not " + Describe(snapshot));
            That(!row.IsLocal || row.Id.Value == shown.ClientId, "only the id " + shown + " gave this player is local, not in " + Describe(snapshot));
            That(!row.IsHost || (entry.IsHost && row.Id.Value == shown.HostId), "only " + shown + "'s own player is the host, not in " + Describe(snapshot));
        }

        if (!exact) return;
        var joined = connection is { Accepted: true, Discarded: false };
        var local = joined && shown.Entries.Any(entry => entry.Id == shown.ClientId) ? shown.ClientId : null;
        var host = shown.HostId != null && shown.Entries.Any(entry => entry.IsHost && entry.Id == shown.HostId) ? shown.HostId : null;
        That(snapshot.LocalParticipantId?.Value == local && snapshot.HostParticipantId?.Value == host,
            "the marks are " + (local ?? "none") + " and " + (host ?? "none") + " for the list of " + shown + ", not " + Describe(snapshot));
    }

    private static string Describe(MultiplayerSessionSnapshot snapshot) =>
        snapshot.Id + " " + snapshot.State + " [" + ParticipantListTests.Rows(snapshot) + "]";

    private int PickHost()
    {
        if (random.Next(3) == 0) last = random.Next(hosts.Count);
        return last;
    }

    /// <summary>Gets the process running alongside under the same identity as <paramref name="index"/>, or any host.</summary>
    private int OtherCopy(int index) => index == 0 ? 3 : index == 3 ? 0 : random.Next(hosts.Count);

    private void Pump()
    {
        tick++;
        network.Now = tick;
        network.DeliverDue(tick);
        var wasClient = client.Role == EngineRole.Client;
        client.Pump((long)(tick * 1_000_000UL / 60));
        var violation = diagnostics.Reports.FirstOrDefault(report => report.Kind == EngineDiagnosticKind.ProtocolViolation);
        if (violation != null) throw new InvalidOperationException("the script broke the protocol: " + violation.Message);
        if (wasClient && client.Role == EngineRole.Host)
        {
            // The host refused or ended the session, and the engine went back to solo play: a role change.
            Trace.Add("  back to solo play");
            connection = null;
            Forget();
            return;
        }

        if (connection != null && connection.Link.State == EngineLinkState.Closed && client.Role == EngineRole.Client) Discard(connection);
    }

    private void Connect(int index)
    {
        if (client.Role == EngineRole.Host)
        {
            Forget();
            client.SwitchToClient(new MultiplayerSessionId("joining-" + (++roleChanges)));
        }

        if (connection != null) Discard(connection);
        var host = hosts[index];
        var pair = network.Connect("me", host.Identity);
        var link = new SniffLink(pair.ClientEnd, this);
        connection = new Connection(host, host.Process, new RawPeer(pair.HostEnd), link);
        link.Owner = connection;
        last = index;
        Trace.Add("connect to " + connection.Label);
        client.Connect(link, "Me");
    }

    /// <summary>
    /// The engine let <paramref name="lost"/> go. A join whose request left and whose answer never came may hide a record
    /// the host made meanwhile, so the ids of that host's list name nobody now, and no later join keeps the list.
    /// </summary>
    private void Discard(Connection lost)
    {
        if (lost.Discarded) return;
        lost.Discarded = true;
        if (shown != null) totals.LinksLostWithAList++;
        if (lost.Accepted || !lost.RequestSent || listedIdentity != lost.Host.Identity) return;
        listedIdentity = string.Empty;
        totals.Unanswered++;
        Trace.Add("  the join to " + lost.Label + " went unanswered");
    }

    /// <summary>A role change lists nobody and forgets the listed host.</summary>
    private void Forget()
    {
        shown = null;
        listedIdentity = null;
    }

    /// <summary>What the runs reached, added up over every seed.</summary>
    internal sealed class Totals
    {
        internal long Snapshots;
        internal long WindowSnapshots;
        internal long Joins;
        internal long Lists;
        internal long Kept;
        internal long Withheld;
        internal long KeptByTheOtherCopy;
        internal long WithheldForOtherIds;
        internal long WithheldForANewRecord;
        internal long Unanswered;
        internal long LinksLostWithAList;

        public override string ToString() =>
            Snapshots + " snapshots checked, " + WindowSnapshots + " of them joined with the list withheld; " + Joins + " joins accepted and " + Lists +
            " lists read; " + Kept + " joins kept a list, " + KeptByTheOtherCopy + " of them with the other copy under the same identity giving the same ids, and " +
            Withheld + " withheld one, " + WithheldForOtherIds + " of them for the other copy's ids and " + WithheldForANewRecord +
            " for a new record with the same ids; " + Unanswered + " unanswered joins; " + LinksLostWithAList + " links lost with a list shown";
    }

    private enum Phase
    {
        Awaiting,
        Joined,
        Ended
    }

    /// <summary>A list the client shows: the process that sent it, its rows, and the ids it gave this player and itself.</summary>
    private sealed record Shown(int Host, int Process, List<ParticipantEntry> Entries, string ClientId, string? HostId)
    {
        public override string ToString() => "h" + Host + "p" + Process;
    }
}
