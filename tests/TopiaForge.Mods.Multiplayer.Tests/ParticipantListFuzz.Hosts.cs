using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The scripted hosts of <see cref="ParticipantListFuzz"/>: what each one sends, what it keeps of the client, and the
/// link through which the model sees what the client sends and reads.
/// </summary>
internal sealed partial class ParticipantListFuzz
{
    private void Accept(Connection current)
    {
        foreach (var message in current.Raw.Receive())
        {
            if (message.Type == MessageType.JoinRequest) current.RequestReceived = true;
        }

        if (!current.RequestReceived) return;
        var host = current.Host;
        var kept = host.Records.TryGetValue(current.Process, out var id);
        if (!kept)
        {
            // Each process numbers its players itself, so a new record may take the id an old one had, as a host that
            // restarted gives its first player p1 again.
            id = Ids[random.Next(3)];
            host.Records[current.Process] = id;
        }

        current.ClientId = id!;
        current.HostId = host.Headless ? null : "local";
        current.Epoch = host.EpochOf(current.Process);
        current.Phase = Phase.Joined;
        var accepted = new JoinAcceptedMessage
        {
            SessionId = host.SessionOf(current.Process),
            Seed = 1,
            Epoch = current.Epoch,
            ParticipantId = current.ClientId,
            HostParticipantId = current.HostId,
            Tick = tick,
            StepRateHz = 60,
            NextSequence = 1,
            RecordKept = kept,
            Generation = ++generation
        };

        // A small world fits the whole answer in one frame; a large one sends the snapshot and the list later.
        var roster = random.Next(3) == 0 ? Roster(current) : null;
        current.Raw.Send((frame, body) =>
        {
            JoinMessages.WriteJoinAccepted(frame, body, accepted);
            if (roster == null) return;
            SessionMessages.WriteEpochMessage(frame, body, MessageType.SnapshotBegin, current.Epoch);
            SessionMessages.WriteEmpty(frame, body, MessageType.SnapshotEnd);
            SessionMessages.WriteParticipants(frame, body, roster);
            SessionMessages.WriteCommit(frame, body, tick, 0);
        });
        Trace.Add("accept from " + current.Label + (kept ? " (record kept)" : " (new record)") + " as " + current.ClientId + (roster == null ? string.Empty : ", with the whole answer"));
    }

    private void SnapshotPart(Connection current)
    {
        if (!current.SnapshotOpen)
        {
            current.SnapshotOpen = true;
            current.Raw.Send((frame, body) => SessionMessages.WriteEpochMessage(frame, body, MessageType.SnapshotBegin, current.Epoch));
            Trace.Add("snapshot begins from " + current.Label);
            return;
        }

        current.SnapshotOpen = false;
        var roster = random.Next(2) == 0 ? Roster(current) : null;
        current.Raw.Send((frame, body) =>
        {
            SessionMessages.WriteEmpty(frame, body, MessageType.SnapshotEnd);
            if (roster != null) SessionMessages.WriteParticipants(frame, body, roster);
            SessionMessages.WriteCommit(frame, body, tick, 0);
        });
        Trace.Add("snapshot ends from " + current.Label + (roster == null ? string.Empty : ", with a list"));
    }

    private void List(Connection current)
    {
        var roster = Roster(current);
        current.Raw.Send((frame, body) =>
        {
            SessionMessages.WriteParticipants(frame, body, roster);
            SessionMessages.WriteCommit(frame, body, tick, 0);
        });
        Trace.Add("list sent from " + current.Label);
    }

    /// <summary>
    /// The players of <paramref name="current"/>'s process: its own player, unless headless, this client, and some of the
    /// others. Every name says which process sent which list, so a row shown from another list never passes.
    /// </summary>
    private List<ParticipantEntry> Roster(Connection current)
    {
        var version = ++lists;
        string Name(string id) => current.Label + "v" + version + "-" + id;
        var entries = new List<ParticipantEntry>();
        if (!current.Host.Headless) entries.Add(new ParticipantEntry("local", Name("local"), true, true, MultiplayerConnectionQuality.Unknown, null, null));
        var others = Ids.Where(id => id != current.ClientId && random.Next(2) == 0).ToList();
        var at = random.Next(others.Count + 1);
        for (var index = 0; index <= others.Count; index++)
        {
            if (index == at) entries.Add(new ParticipantEntry(current.ClientId, Name(current.ClientId), true, false, MultiplayerConnectionQuality.Unknown, null, null));
            if (index < others.Count)
            {
                entries.Add(new ParticipantEntry(others[index], Name(others[index]), random.Next(4) != 0, false, MultiplayerConnectionQuality.Unknown, null, null));
            }
        }

        return entries;
    }

    private void Replace(Connection current)
    {
        var replacement = current.Host.Replace(current.Process);
        current.Epoch = replacement.Epoch;
        current.SnapshotOpen = false;
        current.Raw.Send((frame, body) =>
        {
            SessionMessages.WriteSessionReplaced(frame, body, new SessionReplacedMessage(replacement.Session, 2, replacement.Epoch));
            SessionMessages.WriteCommit(frame, body, tick, 0);
        });
        Trace.Add("replace in place at " + current.Label);
    }

    private void End(Connection current)
    {
        current.Raw.Send((frame, body) => SessionMessages.WriteSessionEnded(frame, body, MultiplayerSessionEndReason.HostEnded, "Bye."));
        current.Phase = Phase.Ended;
        Trace.Add("end from " + current.Label);
    }

    private void Reject(Connection current)
    {
        current.Raw.Send((frame, body) => JoinMessages.WriteJoinRejected(
            frame,
            body,
            new JoinRejectedMessage(MultiplayerSessionEndReason.AdmissionRejected, "No.", Array.Empty<byte>())));
        current.Phase = Phase.Ended;
        Trace.Add("reject from " + current.Label);
    }

    private void Sever(Connection current)
    {
        network.Sever(current.Raw.End);
        Trace.Add("sever " + current.Label);
    }

    private void ToggleStall(Connection current)
    {
        var end = current.Link.Inner;
        var stall = end.State == EngineLinkState.Open;
        foreach (var side in new[] { end, end.Peer! })
        {
            if (side.State != EngineLinkState.Closed) side.State = stall ? EngineLinkState.Stalled : EngineLinkState.Open;
            side.Outbound!.Held = stall;
        }

        Trace.Add((stall ? "stall " : "recover ") + current.Label);
    }

    private void Restart(int index)
    {
        var host = hosts[index];
        if (connection != null && connection.Host == host && connection.Link.State != EngineLinkState.Closed) network.Sever(connection.Raw.End);
        host.Process++;
        Trace.Add("restart host " + index + " as process " + host.Process);
    }

    private void Expire(int index)
    {
        var host = hosts[index];
        if (connection != null && connection.Host == host && connection.Process == host.Process && connection.Link.State != EngineLinkState.Closed) return;
        if (host.Records.Remove(host.Process)) Trace.Add("host " + index + " process " + host.Process + " forgets the record");
    }

    private void RoleChange()
    {
        if (client.Role != EngineRole.Client) return;
        Forget();
        connection = null;
        client.SwitchToSolo(new SoloSessionStart(new MultiplayerSessionId("solo-" + (++roleChanges)), new SessionSeed(3), new ParticipantId("local"), "Me"));
        Trace.Add("back to solo play");
    }

    /// <summary>One scripted host identity: the process it runs, the client's record in each, and each one's session.</summary>
    private sealed class HostState
    {
        private readonly Dictionary<int, (string Session, ulong Epoch)> sessions = new();

        internal HostState(int index, string identity, bool headless)
        {
            Index = index;
            Identity = identity;
            Headless = headless;
        }

        internal int Index { get; }
        internal string Identity { get; }
        internal bool Headless { get; }
        internal int Process { get; set; } = 1;

        /// <summary>Gets the id each process gave the client, while it keeps the client's record.</summary>
        internal Dictionary<int, string> Records { get; } = new();

        internal string SessionOf(int process) => Current(process).Session;

        internal ulong EpochOf(int process) => Current(process).Epoch;

        internal (string Session, ulong Epoch) Replace(int process)
        {
            var now = Current(process);
            var next = ("s" + Index + "x" + process + "r" + (now.Epoch + 1), now.Epoch + 1);
            sessions[process] = next;
            return next;
        }

        private (string Session, ulong Epoch) Current(int process)
        {
            if (!sessions.TryGetValue(process, out var session)) sessions[process] = session = ("s" + Index + "x" + process, 1);
            return session;
        }
    }

    /// <summary>One link to one host process, and how far its join got.</summary>
    private sealed class Connection
    {
        internal Connection(HostState host, int process, RawPeer raw, SniffLink link)
        {
            Host = host;
            Process = process;
            Raw = raw;
            Link = link;
        }

        internal HostState Host { get; }
        internal int Process { get; }
        internal RawPeer Raw { get; }
        internal SniffLink Link { get; }
        internal string Label => "h" + Host.Index + "p" + Process;
        internal Phase Phase { get; set; }
        internal string ClientId { get; set; } = string.Empty;
        internal string? HostId { get; set; }
        internal ulong Epoch { get; set; }
        internal bool RequestSent { get; set; }
        internal bool RequestReceived { get; set; }
        internal bool Accepted { get; set; }
        internal bool Discarded { get; set; }
        internal bool SnapshotOpen { get; set; }
    }

    /// <summary>The client's end of a link, telling the model what the client sends and what it is about to read.</summary>
    private sealed class SniffLink : IEngineLink
    {
        private readonly ParticipantListFuzz fuzz;

        internal SniffLink(VirtualLinkEnd inner, ParticipantListFuzz fuzz)
        {
            Inner = inner;
            this.fuzz = fuzz;
        }

        internal VirtualLinkEnd Inner { get; }

        internal Connection? Owner { get; set; }

        public string PeerIdentity => Inner.PeerIdentity;

        public MultiplayerPlayerKey PlayerKey => Inner.PlayerKey;

        public byte[] ChannelBinding => Inner.ChannelBinding;

        public EngineLinkState State => Inner.State;

        public void Send(EngineChannel channel, ArraySegment<byte> frame)
        {
            if (channel == EngineChannel.Session && Inner.State != EngineLinkState.Closed)
            {
                var reader = new ProtocolReader(frame.ToArray());
                while (!reader.AtEnd)
                {
                    var type = reader.ReadVarUInt();
                    reader.Skip(reader.ReadLength(ProtocolLimits.MaximumFrameBytes));
                    if (type == MessageType.JoinRequest) Owner!.RequestSent = true;
                }
            }

            Inner.Send(channel, frame);
        }

        public bool TryReceive(out EngineChannel channel, out byte[] frame)
        {
            if (!Inner.TryReceive(out channel, out frame)) return false;
            if (channel != EngineChannel.Session) return true;
            var reader = new ProtocolReader(frame);
            while (!reader.AtEnd)
            {
                var type = reader.ReadVarUInt();
                var body = reader.Slice(reader.ReadLength(ProtocolLimits.MaximumFrameBytes));
                if (type == MessageType.JoinAccepted) fuzz.OnJoinAccepted(Owner!, JoinMessages.ReadJoinAccepted(body));
                else if (type == MessageType.ParticipantsChanged) fuzz.OnList(Owner!, SessionMessages.ReadParticipants(body));
            }

            return true;
        }

        public void Close() => Inner.Close();
    }
}
