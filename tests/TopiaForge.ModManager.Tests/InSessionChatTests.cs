using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;
using static TopiaForge.ModManager.Tests.SessionsRig;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class InSessionChatTests
    {
        internal static void Run()
        {
            MessageBoundsAndWire();
            RateLimitsUseMonotonicTime();
            ArrivalJitterDropsNothingTheSenderAllowed();
            RefusedSendsSpendNoAllowance();
            HostRefusalsSpendNoAllowance();
            NormalizedChatTextStaysPrivate();
            HostAssignsIdentityAndLimitsEverySender();
            MutesAndDeparturesAreCheckedAtRelay();
            HostileRelaysAreBoundedAndRedacted();
            LocalCommandsAndModeration();
            DuplicateNamesPointToTheChatId();
            ModCommandFailuresKeepTheirWords();
            FocusEndsWithSessionAndDisposal();
            ConnectionGraceAndNotices();
            PauseActionsFollowTheirLifetime();
            IdleFramesAllocateNothing();
            ChatViewContractTests.Run();
            Console.WriteLine("InSessionChatTests passed: 16 logic groups and the production view contract.");
        }

        private static void MessageBoundsAndWire()
        {
            foreach (var text in new[] { "hello", new string('a', 256), "<b>plain</b>", "hello \ud83d\ude00" })
            {
                That(ChatWire.IsValid(text), "bounded Unicode text is accepted");
                That(ChatWire.TryRead(ChatWire.Write(text), false, out var sender, out var actual) && sender.Length == 0 && actual == text, "request round trip");
                That(ChatWire.TryRead(ChatWire.Write(text, "p1"), true, out sender, out actual) && sender == "p1" && actual == text, "relay round trip");
            }
            foreach (var text in new[] { "", "  ", new string('a', 257), "\ud800", "\udc00" }) That(!ChatWire.IsValid(text), "invalid text is refused");
            That(!ChatWire.TryRead(ChatWire.Write("hello", "p0"), false, out _, out _), "a player cannot claim an identity");
            That(!ChatWire.TryRead(ChatWire.Write("hello"), true, out _, out _), "a host relay needs an identity");
            foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 1, 0, 1, 0, 255 }, new byte[ChatWire.MaximumBytes + 1], ChatWire.Write("ok").Concat(new byte[1]).ToArray() })
                That(!ChatWire.TryRead(bytes, false, out _, out _), "bad lengths and invalid UTF-8 fail closed");
            var oversized = new byte[261];
            oversized[0] = 1; oversized[2] = 1; oversized[3] = 1;
            Array.Fill(oversized, (byte)'x', 4, 257);
            That(!ChatWire.TryRead(oversized, false, out _, out _), "a hostile sender cannot bypass the UTF-16 bound");
        }

        private static void RateLimitsUseMonotonicTime()
        {
            var rate = new ChatRateLimit();
            That(rate.Take(0) && rate.Take(0) && rate.Take(0) && !rate.Take(0), "burst is exactly three");
            That(!rate.Take(-1) && !rate.Take(1_999_999) && rate.Take(2_000_000) && !rate.Take(2_000_000), "one token every two seconds, never on clock reversal");
            foreach (var slack in new[] { ChatRateLimit.HostSlack, ChatRateLimit.RelaySlack })
            {
                var arrival = new ChatRateLimit();
                for (var index = 0; index < 3; index++) That(arrival.Take(0, slack), "arrival burst fits");
                var last = slack == ChatRateLimit.HostSlack ? 1_000_000L : 0;
                That(arrival.Take(last, slack), "the slack permits an early fourth arrival");
                var refill = (long)((3 + slack) * 2_000_000);
                That(!arrival.IsExpired(last + refill - 1, slack) && arrival.IsExpired(last + refill, slack), "rate-table eviction waits for the whole arrival allowance to refill");
            }
            using var rig = new ChatRig(false);
            That(rig.Chat.Send("one").Length == 0 && rig.Chat.Send("two").Length == 0 && rig.Chat.Send("three").Length == 0, "sender burst succeeds");
            That(rig.Chat.Send("four").Contains("Wait two seconds", StringComparison.Ordinal) && rig.Channel.Sent.Count == 3, "sender limit prevents a send");
            rig.Now = 2_000_000;
            That(rig.Chat.Send("next").Length == 0, "sender refills");
            using var host = new ChatRig(true);
            for (var index = 0; index < 3; index++) host.Receive("p1", ChatWire.Write("before reconnect"));
            host.Snapshot = HostSnapshot(MultiplayerSessionState.Ready, "chat", Player("p9", "Sam", "one"));
            host.Receive("p9", ChatWire.Write("after reconnect"));
            That(host.Channel.Sent.Count == 3, "a new participant id cannot reset the authenticated player's host allowance");
            host.Now = 2_000_000; host.Receive("p9", ChatWire.Write("refilled after reconnect"));
            That(host.Channel.Sent.Count == 4, "the rejoined identity still refills normally");
        }

        private static void ArrivalJitterDropsNothingTheSenderAllowed()
        {
            // An honest sender at its full rate: three at once, then a fourth two seconds later, as its own limit allows.
            using var guest = new ChatRig(false);
            for (var index = 0; index < 3; index++) guest.Chat.Send("burst " + index);
            guest.Now = 2_000_000;
            That(guest.Chat.Send("on time").Length == 0 && guest.Channel.Sent.Count == 4, "the sender's own limit allows a fourth message after two seconds");

            // A retransmission holds the first three back 300 ms on the link; the fourth arrives 50 ms after it left.
            using var host = new ChatRig(true);
            host.Now = 300_000;
            for (var index = 0; index < 3; index++) host.Receive("p1", guest.Channel.Sent[index]);
            host.Now = 2_050_000;
            host.Receive("p1", guest.Channel.Sent[3]);
            That(host.Channel.Sent.Count == 4, "the host relays every message the sender's limit allowed, though the link bunched them");

            // The relays leave the host 1.75 s apart and reach a client bunched again: 350 ms late, then 100 ms late.
            using var client = new ChatRig(false);
            client.Now = 650_000;
            for (var index = 0; index < 3; index++) client.Receive("p0", host.Channel.Sent[index]);
            client.Now = 2_150_000;
            client.Receive("p0", host.Channel.Sent[3]);
            var shown = 0;
            while (client.Chat.TryTakeLine(out _)) shown++;
            That(shown == 4, "a client shows every relay its host accepted, though its own link bunched them too");

            // The slack never widens a burst or the rate: a modified client's flood still relays three at once, and one every
            // two seconds after that.
            host.Now = 60_000_000;
            var before = host.Channel.Sent.Count;
            for (var index = 0; index < 10; index++) host.Receive("p2", ChatWire.Write("flood " + index));
            That(host.Channel.Sent.Count - before == 3, "the host still takes at most three at once");
            for (var step = 1; step <= 200; step++) { host.Now = 60_000_000 + step * 100_000L; host.Receive("p2", ChatWire.Write("steady")); }
            That(host.Channel.Sent.Count - before == 13, "over twenty seconds the host takes the burst and ten more, one every two seconds");
        }

        private static void RefusedSendsSpendNoAllowance()
        {
            // A player whose link dropped presses Return a few times; nothing leaves, so nothing is spent.
            using var rig = new ChatRig(false);
            rig.Snapshot = new MultiplayerSessionSnapshot(new MultiplayerSessionId("chat"), MultiplayerSessionState.Ready, MultiplayerProcessKind.Interactive,
                MultiplayerExecutionSide.Client, new ParticipantId("p1"),
                new[] { Player("p0", "Ada", "host-key", host: true), Player("p1", "Sam", "my-key", local: true, connected: false) },
                new NetworkTick(1), new SessionSeed(7), new ParticipantId("p0"), MultiplayerSessionEndReason.None);
            rig.Chat.Tick(rig.Control, rig.Snapshot);
            for (var index = 0; index < 3; index++)
                That(rig.Chat.Send("are you there?").Contains("reconnecting", StringComparison.Ordinal), "a reconnecting player is told to wait for the link");
            rig.Snapshot = ClientSnapshot(MultiplayerSessionState.Ready, "chat", "p1", Player("p2", "Lee", "two"));
            rig.Chat.Tick(rig.Control, rig.Snapshot);
            for (var index = 0; index < 3; index++)
                That(rig.Chat.Send("\u202e").StartsWith("Write a message", StringComparison.Ordinal), "text that is only a direction override sends nothing");
            rig.Channel.Refuse = true;
            for (var index = 0; index < 3; index++)
                That(rig.Chat.Send("window full").StartsWith("The message could not be sent", StringComparison.Ordinal), "a send the link refuses is reported");
            rig.Channel.Refuse = false;
            for (var index = 0; index < 3; index++)
                That(rig.Chat.Send("back " + index).Length == 0, "after nine refusals the player still has the whole burst of three");
            That(rig.Channel.Sent.Count == 3 && rig.Chat.Send("fourth").Contains("Wait two seconds", StringComparison.Ordinal), "and the limit still holds after it");
        }

        private static void HostAssignsIdentityAndLimitsEverySender()
        {
            using var rig = new ChatRig(true);
            rig.Snapshot = HostSnapshot(MultiplayerSessionState.Ready, "chat", Player("p1", "Sam", "one"), Player("p2", "Sam", "two"));
            rig.Receive("p1", ChatWire.Write("pretend", "p0"));
            rig.Receive("unknown", ChatWire.Write("unknown"));
            That(rig.Channel.Sent.Count == 0, "unknown and impersonating senders relay nothing");
            for (var index = 0; index < 10; index++) rig.Receive("p1", ChatWire.Write("line " + index));
            That(rig.Channel.Sent.Count == 3, "host rate limit independently admits exactly three messages");
            That(ChatWire.TryRead(rig.Channel.Sent[0], true, out var sender, out _) && sender == "p1", "host attributes the authenticated sender");
            rig.Receive("p2", ChatWire.Write("other"));
            That(rig.Channel.Sent.Count == 4, "one sender cannot spend another's allowance");
            var authors = new HashSet<string>();
            while (rig.Chat.TryTakeLine(out var line)) authors.Add(line.Author);
            That(authors.SetEquals(new[] { "PLAYER p1 / Sam", "PLAYER p2 / Sam" }), "duplicate names keep distinct provider-assigned attributions");
            rig.Now = 2_000_000;
            rig.Receive("p1", ChatWire.Write("refilled"));
            That(rig.Channel.Sent.Count == 5, "host refills by its own clock");
            rig.Now = 4_000_000;
            rig.Receive("p1", ChatWire.Write("/ban Sam"));
            That(ChatWire.TryRead(rig.Channel.Sent.Last(), true, out _, out var commandText) && commandText == "/ban Sam", "an admitted slash message stays text");
            That(!rig.Control.Calls.Any(call => call.StartsWith("Ban:", StringComparison.Ordinal)), "network text never runs commands");
        }

        private static void MutesAndDeparturesAreCheckedAtRelay()
        {
            using var rig = new ChatRig(true);
            rig.Control.Muted.Add("p1");
            rig.Receive("p1", ChatWire.Write("muted"));
            That(rig.Channel.Sent.Count == 0, "even a previously queued request respects a new mute");
            rig.Control.Muted.Remove("p1");
            rig.Receive("p1", ChatWire.Write("heard"));
            That(rig.Channel.Sent.Count == 1, "unmuted sender can speak");
            rig.Snapshot = HostSnapshot(MultiplayerSessionState.Ready, "chat", Player("p1", "Sam", "one", connected: false));
            rig.Receive("p1", ChatWire.Write("after leave"));
            That(rig.Channel.Sent.Count == 1, "a disconnected or banned sender cannot relay stale queued text");
        }

        private static void HostileRelaysAreBoundedAndRedacted()
        {
            using var rig = new ChatRig(false);
            rig.Receive("p2", ChatWire.Write("forged host", "p0"));
            rig.Receive("p0", ChatWire.Write("unknown", "missing"));
            That(!rig.Chat.TryTakeLine(out _), "only a known host's attributed relays show");
            var message = "<b>hello</b> 192.0.2.8 [2001:db8::7] host.test:27250";
            for (var index = 0; index < 20; index++) rig.Receive("p0", ChatWire.Write(message, "p0"));
            var count = 0;
            while (rig.Chat.TryTakeLine(out var line))
            {
                count++;
                That(!line.Text.Contains("192.0.2.8", StringComparison.Ordinal) && !line.Text.Contains("2001:db8", StringComparison.Ordinal) && !line.Text.Contains("host.test", StringComparison.Ordinal), "relayed addresses never reach the screen");
                That(line.Text.Length <= 256, "relays stay bounded");
            }
            That(count == 4, "host flood is limited on reception too: the burst of three and one message of slack for the second link");
        }

        private sealed class ChatRig : IDisposable
        {
            internal readonly RecordingSessionControl Control = new RecordingSessionControl();
            internal readonly ChatChannel Channel = new ChatChannel();
            internal readonly SessionChat Chat;
            internal long Now;
            internal MultiplayerSessionSnapshot Snapshot;
            internal ChatRig(bool host)
            {
                Control.Role = host ? InternalSessionRole.ListenHost : InternalSessionRole.Client;
                Control.ChatChannel = Channel;
                Snapshot = host ? HostSnapshot(MultiplayerSessionState.Ready, "chat", Player("p1", "Sam", "one"), Player("p2", "Lee", "two"))
                    : ClientSnapshot(MultiplayerSessionState.Ready, "chat", "p1", Player("p2", "Lee", "two"));
                Chat = new SessionChat { Clock = () => Now };
                Chat.Tick(Control, Snapshot);
            }
            internal void Receive(string sender, byte[] bytes)
            { Channel.Inbox.Enqueue(new InternalSystemMessage(new ParticipantId(sender), bytes)); Chat.Tick(Control, Snapshot); }
            public void Dispose() => Chat.Dispose();
        }

        internal sealed class ChatChannel : IInternalSystemChannel
        {
            internal readonly Queue<InternalSystemMessage> Inbox = new Queue<InternalSystemMessage>();
            internal readonly List<byte[]> Sent = new List<byte[]>();
            internal bool Refuse;
            public InternalSystemChannelKind Kind => InternalSystemChannelKind.Chat;
            public bool IsOpen { get; private set; } = true;
            public ulong Generation => 1;
            public ulong GetGeneration(ParticipantId peer) => 1;
            public bool CanSend(InternalSystemTarget target, int bytes) => IsOpen && !Refuse;
            public OperationResult<int> Send(ArraySegment<byte> payload, InternalSystemTarget target, ICollection<ParticipantId>? skipped = null)
            {
                if (Refuse) return OperationResult<int>.Failure(ModErrorCode.RateLimited, "The host has not taken the earlier system messages yet.");
                Sent.Add(payload.ToArray());
                return OperationResult<int>.Success(1);
            }
            public bool TryReceive(out InternalSystemMessage message)
            { if (Inbox.Count > 0) { message = Inbox.Dequeue(); return true; } message = default; return false; }
            public OperationResult<int> SendDatagram(ArraySegment<byte> payload, InternalSystemTarget target) => throw new NotSupportedException();
            public bool TryReceiveDatagram(out InternalSystemDatagram datagram)
            { datagram = default; return false; }
            public InternalSystemChannelStatistics GetStatistics() => new InternalSystemChannelStatistics(Kind, Sent.Count, 0, 0, Inbox.Count, 0);
            public void Dispose() { IsOpen = false; Inbox.Clear(); }
        }
    }
}
