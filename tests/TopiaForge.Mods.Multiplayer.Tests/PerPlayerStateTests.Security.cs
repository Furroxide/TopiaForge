using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class PerPlayerStateTests
{
    private static void PrivateChannelEnforcesTargetsAndAuthority()
    {
        using var h = new EngineHarness(MultiplayerProcessKind.Interactive);
        var a = h.AddClient("alpha"); var b = h.AddClient("beta"); h.Flush();
        using var host = h.Host.OpenSystemChannel(InternalSystemChannelKind.PerPlayer).Value!;
        using var alpha = a.Engine.OpenSystemChannel(InternalSystemChannelKind.PerPlayer).Value!;
        using var beta = b.Engine.OpenSystemChannel(InternalSystemChannelKind.PerPlayer).Value!;
        var bytes = new ArraySegment<byte>(new byte[] { 7, 9 });
        That(!host.Send(bytes, InternalSystemTarget.Everyone).Succeeded && !host.Send(bytes, InternalSystemTarget.AllExcept(Beta)).Succeeded,
            "the private system channel refuses broadcast targets");
        That(host.Send(bytes, InternalSystemTarget.Only(Alpha)).Value == 1, "the host can target one admitted player");
        That(host.Send(bytes, InternalSystemTarget.Only(new ParticipantId("unknown"))).ErrorCode == ModErrorCode.NotFound, "private channel targets must have an authenticated live stream");
        h.Flush();
        That(alpha.TryReceive(out var message) && message.Sender.Value == "host" && !beta.TryReceive(out _), "only alpha receives the authenticated host payload");
        That(alpha.Send(bytes, InternalSystemTarget.Host).ErrorCode == ModErrorCode.NotAuthoritative, "client send is refused");
        That(!host.SendDatagram(bytes, InternalSystemTarget.Only(Alpha)).Succeeded, "private system data has no unreliable path");
        That(!h.Host.OpenSystemChannel(InternalSystemChannelKind.PrivateState).Succeeded, "the contract channel cannot be claimed by a runtime consumer");
        foreach (var kind in new[] { InternalSystemChannelKind.PrivateState, InternalSystemChannelKind.PerPlayer })
        {
            var peer = h.AddRawPeer("hostile" + (byte)kind); peer.Join("Hostile"); h.Flush();
            var generation = SystemStreamTests.JoinedGeneration(peer);
            peer.Send((f, w) => SystemMessages.Write(f, w, 0, generation, (byte)kind, bytes.Array!));
            h.Flush();
            That(!h.Host.IsConnectedParticipant(new ParticipantId("hostile" + (byte)kind)), "client private payload is a protocol violation");
            var datagramPeer = h.AddRawPeer("datagram" + (byte)kind); datagramPeer.Join("Datagram"); h.Flush();
            var datagramGeneration = SystemStreamTests.JoinedGeneration(datagramPeer);
            var buffer = new byte[128];
            var length = SystemDatagrams.Write(buffer, 0, datagramGeneration, (byte)kind, bytes);
            datagramPeer.End.Send(EngineChannel.Realtime, new ArraySegment<byte>(buffer, 0, length)); h.Flush();
            That(!h.Host.IsConnectedParticipant(new ParticipantId("datagram" + (byte)kind)), "private client datagrams cannot bypass host-only delivery");
        }
        That(a.Engine.Snapshot.State == MultiplayerSessionState.Ready && b.Engine.Snapshot.State == MultiplayerSessionState.Ready, "hostile senders cannot end honest peers");
        var previous = alpha.Generation;
        h.Network.Sever(a.Link); h.Advance(); h.Join(a.Engine, "alpha"); h.Flush();
        That(alpha.Generation != previous && !alpha.TryReceive(out _), "runtime consumers see generation changes and no stale queued payloads");
    }

    private static void BoundsAndCredits()
    {
        using var h = new EngineHarness();
        var fields = new List<IPerPlayerState<Blob>>();
        for (var i = 0; i < SessionEngine.MaximumPrivateFields; i++)
        {
            var result = h.HostSession.RegisterPerPlayerState(new ReplicatedStateDefinition<Blob>("blob-" + i, new Blob(), new BlobCodec()));
            That(result.Succeeded, "bounded field registration succeeds"); fields.Add(result.Value!);
        }
        That(h.HostSession.RegisterPerPlayerState(new ReplicatedStateDefinition<Blob>("overflow", new Blob(), new BlobCodec())).ErrorCode == ModErrorCode.RateLimited,
            "the 65th field is rejected");
        var raw = h.AddRawPeer("alpha"); raw.Join("Alpha"); h.Advance(4);
        var messages = raw.Receive();
        var accepted = messages.Single(x => x.Type == MessageType.JoinAccepted);
        var generation = JoinMessages.ReadJoinAccepted(accepted.Reader()).Generation;
        var privateMessages = messages.Where(x => x.Type == MessageType.SystemMessage).Select(x => SystemMessages.Read(x.Reader()))
            .Where(x => x.Kind == (byte)InternalSystemChannelKind.PrivateState).ToArray();
        var bytes = privateMessages.Sum(x => x.Payload.Length);
        That(bytes <= InternalSystemLimits.HostWindowBytes && privateMessages.Length < 65, "baseline stops at the byte credit bound");
        That(privateMessages.All(x => x.Payload.Length <= 20 * 1024), "each complete private field fits the baseline chunk bound");
        h.Advance(3);
        That(!raw.Receive().Any(x => x.Type == MessageType.SystemMessage), "a stalled recipient cannot grow an unbounded outbox");
        fields[^1].Set(Alpha, new Blob { Bytes = Enumerable.Repeat((byte)23, 16 * 1024).ToArray() });
        for (var i = 0; i < fields.Count - 1; i++) fields[i].Set(Alpha, new Blob());
        raw.Send((f, w) => SystemMessages.WriteCredit(f, w, 0, generation, (byte)InternalSystemChannelKind.PrivateState,
            (ulong)privateMessages.Length, (ulong)bytes));
        h.Advance(3);
        var remainder = raw.Receive().Where(x => x.Type == MessageType.SystemMessage).Select(x => SystemMessages.Read(x.Reader())).ToArray();
        That(remainder.Length > 0 && remainder[0].Payload[^1] == 23, "credits finish the baseline before churning fields spend its window");
        fields[0].Dispose();
        That(h.HostSession.RegisterPerPlayerState(new ReplicatedStateDefinition<Blob>("too-large", new Blob(), new BlobCodec(16 * 1024 + 1))).ErrorCode == ModErrorCode.InvalidArgument,
            "a codec above the private payload bound is rejected");
        foreach (var field in fields) field.Dispose();
    }

    private static void HostilePrivateEnvelopes()
    {
        foreach (var kind in new[] { "recipient", "truncated", "before-baseline", "oversized", "fields", "trailing", "duplicate-baseline", "codec", "revision", "operation" })
        {
            var raw = new RawHostClient(); raw.Join();
            if (kind == "codec") Register(raw.Session, "field");
            var reset = Envelope(0, "client");
            raw.Host.Send((f, w) => SystemMessages.Write(f, w, 0, 0, (byte)InternalSystemChannelKind.PrivateState, reset));
            if (kind != "before-baseline") raw.Pump();
            else
            {
                // Use a fresh client with no private baseline.
                raw = new RawHostClient(); raw.Join();
            }
            var target = kind == "recipient" ? "someone-else" : "client";
            var payload = kind == "truncated" ? new byte[] { 1 } : kind == "duplicate-baseline" ? reset :
                Envelope(1, target, "field", new byte[kind == "oversized" ? 16 * 1024 + 1 : 1]);
            if (kind == "trailing") payload = payload.Concat(new byte[] { 9 }).ToArray();
            if (kind == "revision") payload[0] = 2;
            if (kind == "operation") payload[1] = 255;
            var count = kind == "fields" ? 65 : 1;
            for (var i = 0; i < count; i++)
            {
                var sent = kind == "fields" ? Envelope(1, target, "field-" + i, new byte[] { 1 }) : payload;
                raw.Host.Send((f, w) => SystemMessages.Write(f, w, 0, 0, (byte)InternalSystemChannelKind.PrivateState, sent));
            }
            raw.Pump();
            That(raw.Engine.IsEnded && raw.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) == 1, "hostile private envelope is rejected: " + kind);
        }
        var stale = new RawHostClient(); stale.Join();
        stale.Host.Send((f, w) => SystemMessages.Write(f, w, 0, 99, (byte)InternalSystemChannelKind.PrivateState, new byte[] { 255 }));
        stale.Pump();
        That(!stale.Engine.IsEnded, "stale generations are dropped before private decoding");
    }

    private static void GraceExpiryForgetsValues()
    {
        using var h = new EngineHarness(configure: options => options.ParticipantGraceMicros = 1);
        using var host = Register(h.HostSession);
        var a = h.AddClient("alpha"); using var local = Register(a.Session); h.Flush();
        host.Set(Alpha, new PrivateCard { Text = "expired secret" }); h.Flush();
        h.Network.Sever(a.Link); h.Advance(4);
        That(host.Get(Alpha).ErrorCode == ModErrorCode.NotFound, "the expired participant cannot be read");
        h.Join(a.Engine, "alpha"); h.Flush();
        That(host.Get(Alpha).Value!.Text == "" && local.Get(Alpha).Value!.Text == "", "reused identity after grace does not inherit a private value");
    }

    private static byte[] Envelope(byte operation, string player, string id = "field", byte[]? value = null)
    {
        var w = new ProtocolWriter(); w.WriteByte(1); w.WriteByte(operation); w.WriteString(player);
        if (operation != 0) { w.WriteString(EngineHarness.Owner); w.WriteString(id); }
        if (operation == 1) { w.WriteVarUInt(0); w.WriteBytes(value ?? Array.Empty<byte>()); }
        return w.ToArray();
    }

    private sealed class Blob { internal byte[] Bytes { get; set; } = new byte[16 * 1024]; }
    private sealed class BlobCodec(int maximum = 16 * 1024) : IMultiplayerCodec<Blob>
    {
        public int MaximumEncodedBytes => maximum;
        public OperationResult<byte[]> Encode(Blob value) => OperationResult<byte[]>.Success((byte[])value.Bytes.Clone());
        public OperationResult<Blob> Decode(byte[] bytes) => OperationResult<Blob>.Success(new Blob { Bytes = (byte[])bytes.Clone() });
    }
}
