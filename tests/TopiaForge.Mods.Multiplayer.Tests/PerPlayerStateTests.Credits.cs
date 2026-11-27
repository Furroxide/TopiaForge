using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class PerPlayerStateTests
{
    private static void RemovalPrecedesReplacementUnderBackpressure()
    {
        using var h = new EngineHarness();
        var fields = new List<IPerPlayerState<Blob>>();
        var packetSize = (InternalSystemLimits.HostWindowBytes - Envelope(0, "alpha").Length - 50) / 64;
        for (var i = 0; i < 64; i++)
        {
            var id = ("field-" + i).PadRight(128, 'x');
            // Include the actual large byte-array length prefix when calculating the packet overhead.
            var overhead = Envelope(1, "alpha", id, new byte[16 * 1024 - 1]).Length - (16 * 1024 - 1);
            var definition = new ReplicatedStateDefinition<Blob>(id, new Blob { Bytes = new byte[packetSize - overhead] }, new BlobCodec());
            fields.Add(h.HostSession.RegisterPerPlayerState(definition).Value!);
        }
        var raw = h.AddRawPeer("alpha"); raw.Join("Alpha"); h.Advance(4);
        var messages = raw.Receive();
        var generation = JoinMessages.ReadJoinAccepted(messages.Single(x => x.Type == MessageType.JoinAccepted).Reader()).Generation;
        var sent = messages.Where(x => x.Type == MessageType.SystemMessage).Select(x => SystemMessages.Read(x.Reader())).ToArray();
        var size = sent.Sum(x => x.Payload.Length);
        var remaining = InternalSystemLimits.HostWindowBytes - size;
        That(sent.Length == 65 && remaining < Envelope(2, "alpha", "field-0".PadRight(128, 'x')).Length &&
             remaining >= Envelope(1, "alpha", "new", new byte[] { 1 }).Length, "the window fits the new field but cannot fit the old field's removal");
        fields[0].Dispose();
        using var replacement = h.HostSession.RegisterPerPlayerState(new ReplicatedStateDefinition<Blob>("new", new Blob { Bytes = new byte[] { 1 } }, new BlobCodec())).Value!;
        h.Advance(2);
        That(!raw.Receive().Any(x => x.Type == MessageType.SystemMessage), "a new field cannot bypass a blocked removal and overflow the receiver");
        raw.Send((f, w) => SystemMessages.WriteCredit(f, w, 0, generation, (byte)InternalSystemChannelKind.PrivateState, (ulong)sent.Length, (ulong)size));
        h.Advance(3);
        var resumed = raw.Receive().Where(x => x.Type == MessageType.SystemMessage).Select(x => SystemMessages.Read(x.Reader())).ToArray();
        That(resumed.Length == 2 && resumed[0].Payload[1] == 2 && resumed[1].Payload[1] == 1, "credit releases removal before replacement");
        foreach (var field in fields) field.Dispose();
    }
}
