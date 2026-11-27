using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.TestSupport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private static void StingerRetiresExactlyThePublishedScope()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        var independent = PackageSpec.Implicit("io.example.stinger-scope", "0.2.0", 'b');
        using var rig = new ControlRig(173, Sandbox, independent);
        long? frozenClock = null;
        using var host = new UiDeliveryEndpoint(rig.Host, () => frozenClock ?? rig.Network.Now, author: true);
        var peer = rig.AddPeer("Scope recipient", Sandbox, independent);
        using var guest = new UiDeliveryEndpoint(peer, () => frozenClock ?? rig.Network.Now, author: false);
        var other = rig.AddPeer("Second scope recipient", Sandbox, independent);
        using var unaffected = new UiDeliveryEndpoint(other, () => rig.Network.Now, author: false);
        rig.StartHosting(new InternalHostSettings("Scoped stingers", 6));
        rig.JoinAndWait(peer);
        rig.JoinAndWait(other);
        var recipient = OverlayTarget(rig, host, peer).Id;
        var otherRecipient = OverlayTarget(rig, host, other).Id;
        // Freeze presentation time so the real provider can deliver frames without refilling either bucket.
        frozenClock = rig.Network.Now;
        var transport = new RetirementChannel(host.Players.Channel!);
        host.Players.Channel = transport;

        using var first = new ScopeAuthor(rig, host, Sandbox.Id);
        using var sibling = new ScopeAuthor(rig, host, Sandbox.Id);
        using var otherPackage = new ScopeAuthor(rig, host, independent.Id);
        using var unsent = new ScopeAuthor(rig, host, Sandbox.Id);
        for (var index = 0; index < 4; index++)
            That(first.Target(recipient).ShowStinger(AStinger("First " + index)).Succeeded, "One scope fills its own four entries.");
        That(first.Target(recipient).ShowStinger(AStinger("Overflow")).ErrorCode == ModErrorCode.RateLimited,
            "A fourth waiting request is refused for that scope.");

        That(sibling.Broadcast(AStinger("First 0")).Succeeded, "An identical announcement in a sibling scope broadcasts independently.");
        for (var index = 1; index < 4; index++)
            That(sibling.Target(recipient).ShowStinger(AStinger("Sibling " + index)).Succeeded,
                "A sibling scope combines its broadcast and targeted deliveries into its own four entries.");
        That(otherPackage.Target(recipient).ShowStinger(AStinger("Unrelated owner")).Succeeded, "A second package has an independent queue.");

        var filler = new List<ScopeAuthor>();
        try
        {
            for (var eventIndex = 9; eventIndex < 32; eventIndex++)
            {
                if ((eventIndex - 9) % 4 == 0) filler.Add(new ScopeAuthor(rig, host, Sandbox.Id));
                That(filler[^1].Target(recipient).ShowStinger(AStinger("Filler " + eventIndex)).Succeeded,
                    "Independent scopes still fill only the shared 32-event burst.");
            }
            That(unsent.Target(recipient).ShowStinger(AStinger("Not delivered")).ErrorCode == ModErrorCode.RateLimited,
                "A failed first send takes no retirement receipt.");
            unsent.Dispose();
            first.Dispose();
            That(rig.Run(() => guest.Ui.RetiredStingerScopes.Count != 0),
                "An ordered retirement reaches its recipient with both ordinary event buckets full.");
            var firstFrames = guest.Ui.RemoteStingers.Take(4).ToArray();
            var token = firstFrames[0].ScopeToken;
            var siblingToken = guest.Ui.RemoteStingers[4].ScopeToken;
            var unrelatedToken = guest.Ui.RemoteStingers[8].ScopeToken;
            That(guest.Ui.RemoteStingers.Count == 32 && token != siblingToken &&
                firstFrames.All(item => item.Owner == Sandbox.Id && item.ScopeToken == token) &&
                guest.Ui.RemoteStingers.Skip(4).Take(4).All(item => item.Owner == Sandbox.Id && item.ScopeToken == siblingToken) &&
                guest.Ui.RetiredStingerScopes.SequenceEqual(new[] { (Sandbox.Id, token) }),
                "Both routes keep one token per scope and all counted events arrive before the single retirement.");
            That(!guest.Ui.HasStingerScope(Sandbox.Id, token) && guest.Ui.HasStingerScope(Sandbox.Id, siblingToken) &&
                guest.Ui.HasStingerScope(independent.Id, unrelatedToken),
                "Retiring one scope discards its active and waiting entries and preserves same-package siblings and unrelated owners.");
            That(transport.Sent.SequenceEqual(new[] { (recipient, Sandbox.Id, token) }) &&
                unaffected.Ui.HasStingerScope(Sandbox.Id, siblingToken) && unaffected.Ui.RetiredStingerScopes.Count == 0,
                "Only a recipient with a counted event gets one retirement. The unsent scope emits none.");

            var digest = SignedStingerDigest(rig, Sandbox.Id);
            var unauthorized = new byte[32];
            RejectRetirement(rig, guest, transport, recipient, recipient.Value, Sandbox.Id, unauthorized, siblingToken);
            RejectRetirement(rig, guest, transport, recipient, otherRecipient.Value, Sandbox.Id, digest, siblingToken);
            RejectRetirement(rig, guest, transport, recipient, recipient.Value, independent.Id, SignedStingerDigest(rig, independent.Id), siblingToken);
            That(guest.Ui.HasStingerScope(Sandbox.Id, siblingToken) && guest.Ui.HasStingerScope(independent.Id, unrelatedToken),
                "A mismatched digest, audience or owner cannot retire the sibling scope.");

            var late = rig.AddPeer("Late scope recipient", Sandbox, independent);
            using var lateGuest = new UiDeliveryEndpoint(late, () => rig.Network.Now, author: false);
            rig.JoinAndWait(late);
            _ = OverlayTarget(rig, host, late);
            That(lateGuest.Ui.RemoteStingers.Count == 0 && lateGuest.Ui.RetiredStingerScopes.Count == 0 &&
                guest.Ui.RetiredStingerScopes.Count == 1 && transport.Sent.Count == 1,
                "Late join does not replay stingers or closures, and pumping later does not duplicate retirement.");
        }
        finally
        {
            foreach (var scope in filler) scope.Dispose();
        }
    }

    private static void StingerRetirementRetriesOnlyItsOwnStream()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(174, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var first = rig.AddPeer("Slow scope recipient", Sandbox);
        using var waiting = new UiDeliveryEndpoint(first, () => rig.Network.Now, author: false);
        var second = rig.AddPeer("Ready scope recipient", Sandbox);
        using var ready = new UiDeliveryEndpoint(second, () => rig.Network.Now, author: false);
        rig.StartHosting(new InternalHostSettings("Scoped stinger windows", 5));
        rig.JoinAndWait(first);
        rig.JoinAndWait(second);
        var blockedId = OverlayTarget(rig, host, first).Id;
        var readyId = OverlayTarget(rig, host, second).Id;
        var transport = new RetirementChannel(host.Players.Channel!);
        host.Players.Channel = transport;

        using var delayed = new ScopeAuthor(rig, host, Sandbox.Id);
        That(delayed.Broadcast(AStinger("Both streams")).Succeeded &&
            rig.Run(() => waiting.Ui.RemoteStingers.Count == 1 && ready.Ui.RemoteStingers.Count == 1), "Both starting streams have the announcement.");
        var token = waiting.Ui.RemoteStingers[0].ScopeToken;
        transport.Blocked.Add(blockedId);
        delayed.Dispose();
        That(rig.Run(() => ready.Ui.RetiredStingerScopes.Count == 1) &&
            waiting.Ui.HasStingerScope(Sandbox.Id, token) && transport.Sent.SequenceEqual(new[] { (readyId, Sandbox.Id, token) }),
            "One full reliable window keeps only its own closure pending and does not delay healthy recipients.");
        transport.Blocked.Remove(blockedId);
        That(rig.Run(() => waiting.Ui.RetiredStingerScopes.Count == 1) &&
            transport.Sent.SequenceEqual(new[] { (readyId, Sandbox.Id, token), (blockedId, Sandbox.Id, token) }),
            "Reopening the same reliable window sends the pending closure exactly once.");

        using var ending = new ScopeAuthor(rig, host, Sandbox.Id);
        That(ending.Target(blockedId).ShowStinger(AStinger("Old stream")).Succeeded &&
            rig.Run(() => waiting.Ui.RemoteStingers.Count == 2), "A second token can be sent to that recipient.");
        transport.Blocked.Add(blockedId);
        ending.Dispose();
        var resets = waiting.Ui.StingerResets;
        var stream = transport.GetGeneration(blockedId);
        That(rig.Host.Control.Kick(blockedId, "Replace test stream.").Succeeded &&
            rig.Run(() => transport.GetGeneration(blockedId) == 0 && waiting.Ui.StingerResets > resets),
            "Losing the authenticated stream clears its view including the blocked closure.");
        transport.Blocked.Remove(blockedId);
        rig.JoinAndWait(first);
        var rejoined = OverlayTarget(rig, host, first).Id;
        That(transport.GetGeneration(rejoined) != stream && transport.Sent.Count == 2 && transport.SendProbesAfterStreamEnd == 0 &&
            waiting.Ui.RetiredStingerScopes.Count == 1 && waiting.Ui.RemoteStingers.Count == 2,
            "A closure pending on a dead stream is not replayed onto its replacement.");
    }

    private static byte[] SignedStingerDigest(ControlRig rig, string owner)
    {
        var digest = new byte[32];
        var signed = rig.Host.Control.UiPresentationPackageDigest(owner);
        That(signed != null && UiPresentationTheme.TryHexDigest(signed, digest), "The selected package has signed bytes.");
        return digest;
    }

    private static void RejectRetirement(ControlRig rig, UiDeliveryEndpoint guest, RetirementChannel transport,
        ParticipantId routed, string audience, string owner, byte[] digest, ulong scopeToken)
    {
        var rejected = guest.Players.Rejected;
        var writer = new WorldWireWriter(2304);
        writer.Begin(UiStingerDelivery.RetireKind); writer.WriteString(""); writer.WriteString(audience);
        UiWireCodec.WriteStingerRetirement(writer, owner, digest, scopeToken);
        That(transport.Inner.Send(writer.Segment, InternalSystemTarget.Only(routed)).Succeeded &&
            rig.Run(() => guest.Players.Rejected > rejected), "An invalid retirement is refused on an authenticated stream.");
    }

    private sealed class ScopeAuthor : IDisposable
    {
        private readonly UiDeliveryEndpoint host;
        private readonly FakeModLifetime lifetime = new();
        private readonly RuntimeReplication origin;
        private readonly ThemeProbe ui;
        private readonly PlayerDirectory directory;

        internal ScopeAuthor(ControlRig rig, UiDeliveryEndpoint host, string owner)
        {
            this.host = host;
            ui = new ThemeProbe(lifetime, author: true);
            origin = host.Scope(lifetime, ModExecutionContext.GamemodeServer, owner);
            var context = host.Players.Bind(owner, true, new CapturedModLogger(), lifetime,
                new FakeLocalPlayerService(lifetime), ui, new ModInputActivity());
            directory = new PlayerDirectory(() => rig.Host.Control.SessionSnapshot, () => 0, context);
            context.Directory = directory;
        }

        internal IPlayerUi Target(ParticipantId id)
        {
            That(directory.TryGet(id, out var player), "The author scope can address its recipient.");
            return player!.Ui;
        }

        internal OperationResult<bool> Broadcast(UiStingerRequest request) => host.Players.ShowContextStinger(origin, ui, request);

        public void Dispose() { directory.Dispose(); lifetime.Dispose(); }
    }

    private sealed class RetirementChannel(IInternalSystemChannel inner) : IInternalSystemChannel
    {
        internal IInternalSystemChannel Inner => inner;
        internal readonly HashSet<ParticipantId> Blocked = new();
        internal readonly List<(ParticipantId Recipient, string Owner, ulong Token)> Sent = new();
        internal int SendProbesAfterStreamEnd;
        public InternalSystemChannelKind Kind => inner.Kind;
        public bool IsOpen => inner.IsOpen;
        public ulong Generation => inner.Generation;
        public ulong GetGeneration(ParticipantId peer) => inner.GetGeneration(peer);
        public bool CanSend(InternalSystemTarget target, int bytes)
        {
            if (target.Kind == InternalSystemTargetKind.Only && inner.GetGeneration(target.Participant) == 0)
                SendProbesAfterStreamEnd++;
            return !Blocked.Contains(target.Participant) && inner.CanSend(target, bytes);
        }

        public OperationResult<int> Send(ArraySegment<byte> payload, InternalSystemTarget target, ICollection<ParticipantId>? skipped = null)
        {
            var result = inner.Send(payload, target, skipped);
            if (!result.TryGetValue(out var count) || count == 0 || payload.Count < 2 ||
                payload.Array![payload.Offset + 1] != UiStingerDelivery.RetireKind) return result;

            var reader = new WorldWireReader();
            reader.Reset(payload.AsSpan().ToArray());
            _ = reader.TryReadHeader(out _);
            _ = UiNodeWireCodec.Text(reader);
            var audience = UiNodeWireCodec.Text(reader);
            Span<byte> digest = stackalloc byte[32];
            UiWireCodec.ReadStingerRetirement(reader, digest, out var owner, out var token);
            That(target.Kind == InternalSystemTargetKind.Only && count == 1 && audience == target.Participant.Value,
                "Retirement uses exactly the previously counted recipient and stream.");
            Sent.Add((target.Participant, owner, token));
            return result;
        }

        public bool TryReceive(out InternalSystemMessage message) => inner.TryReceive(out message);
        public OperationResult<int> SendDatagram(ArraySegment<byte> payload, InternalSystemTarget target) => inner.SendDatagram(payload, target);
        public bool TryReceiveDatagram(out InternalSystemDatagram message) => inner.TryReceiveDatagram(out message);
        public InternalSystemChannelStatistics GetStatistics() => inner.GetStatistics();
        public void Dispose() => inner.Dispose();
    }
}
