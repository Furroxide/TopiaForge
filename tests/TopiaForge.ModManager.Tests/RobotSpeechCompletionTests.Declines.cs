using System;
using System.Linq;
using System.Reflection;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotSpeechCompletionTests
    {
        private static bool DeliverToGuest(RobotSpeechRig rig, byte[] bytes, ParticipantId? sender = null)
        {
            var reader = new WorldWireReader(); reader.Reset(bytes);
            return reader.TryReadHeader(out var type)
                && rig.Guests[0].Peer.World.Speech.Handle(type, sender ?? rig.World.Host.Control.Id, reader);
        }

        private static byte[][] Reports(RobotSpeechRig.SpeechPeer peer) => peer.Channel.Log!
            .Where(p => p.Payload[1] == RobotSpeechWire.Finished).Select(p => p.Payload).ToArray();

        private static byte[] RewriteLine(byte[] line, ulong? nonce = null, ulong? stamp = null, int changed = 0)
        {
            var reader = new WorldWireReader(); reader.Reset(line); reader.TryReadHeader(out _); reader.TryReadByte(out _);
            reader.TryReadUInt64(out var world); reader.TryReadString(32, out var challenge);
            reader.TryReadUInt64(out var oldNonce); reader.TryReadVarUInt(uint.MaxValue, out var entity);
            reader.TryReadUInt64(out var oldStamp); reader.TryReadString(128, out var mod); reader.TryReadString(128, out var kind);
            reader.TryReadString(64, out var voice); reader.TryReadString(2048, out var text);
            var writer = new WorldWireWriter(RobotSpeechWire.MaximumBytes);
            RobotSpeechWire.Header(writer, RobotSpeechWire.Line, changed == 1 ? world + 1 : world,
                changed == 2 ? new string('f', 32) : challenge, nonce ?? oldNonce, (uint)entity);
            writer.WriteUInt64(stamp ?? oldStamp); writer.WriteString(mod); writer.WriteString(kind);
            writer.WriteString(voice); writer.WriteString(text); return writer.Segment.ToArray();
        }

        private static void DeclinedAdmissions()
        {
            foreach (var refusal in new[] { "hidden", "earshot", "missing", "budget", "expired" })
            {
                using var rig = new RobotSpeechRig(2); rig.Move(rig.Host, 60);
                var guest = rig.Guests[0]; guest.Peer.Paused = true;
                var body = guest.Body;
                var call = rig.Robot.SayAsync("Other listeners can finish.");
                var line = rig.Log.Last(p => p.Payload[1] == RobotSpeechWire.Line && p.To.Equals(guest.Peer.Control.Id)).Payload;
                switch (refusal)
                {
                    case "hidden": body.Visible = false; break;
                    // The host's validated feet remain nearby; only the receiver's replica differs.
                    case "earshot": body.Body.Position = new NetVector3(21, 0, 0); break;
                    case "missing": body.Body.Destroy(); break;
                    case "budget":
                        // Seed only budget exhaustion. The real parser, reply and host completion paths run below.
                        var incoming = (TokenBucket)typeof(NetworkRobotSpeech).GetField("incoming", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(guest.Peer.World.Speech)!;
                        Check(incoming.TryTake(rig.World.Clock.Now, 8), "receive budget starts full");
                        break;
                    case "expired":
                        line = RewriteLine(line, stamp: (ulong)(rig.World.Clock.Now - 2_000_001));
                        break;
                }
                Check(DeliverToGuest(rig, line), refusal + " closes an authenticated line");
                Check(body.SpeechPlays == 0 && guest.Peer.World.Speech.ActiveCount == 0,
                    refusal + " retains neither playback nor secret text");
                var reports = Reports(guest);
                Check(reports.Length == 1 && reports[0].SequenceEqual(Finished(line, RobotSpeechWire.Declined)),
                    refusal + " returns exactly the reason-free terminal report");
                Check(!DeliverToGuest(rig, line) && Reports(guest).Length == 1, refusal + " replay has no reply");
                // The parser ran directly while this peer's frame loop was paused. Its report is queued,
                // not transmitted: flush the real transport before testing a subsequent disconnection.
                guest.Peer.Control.Flush();
                rig.World.Frames(5);
                Check(!call.IsCompleted && rig.Guests[1].Body.SpeechPlays == 1,
                    refusal + " cannot finish for an accepting listener");
                // A declined entry is gone: even a subsequent disconnect cannot poison this operation.
                rig.World.Network.LoseLink(guest.Peer.Control); rig.World.Frames(3);
                Check(!call.IsCompleted, refusal + " departure leaves the accepting listener running");
                rig.Guests[1].Body.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true));
                rig.Later();
                Check(call.IsCompleted && call.Result.Succeeded && call.Result.Value,
                    refusal + " completes after the remaining playback and pace, without waiting fifteen seconds");
                Check(rig.World.Host.World.Speech.ActiveCount == 0, refusal + " retires the host operation");
            }
            using var empty = new RobotSpeechRig(); empty.Move(empty.Host, 60); empty.Guests[0].Body.Visible = false;
            for (var i = 0; i < 3; i++)
            {
                var call = empty.Robot.SayAsync("No accepting listeners."); empty.World.Frames(5);
                Check(!call.IsCompleted, "all-declined still observes the one-second pace");
                empty.Later();
                Check(call.IsCompleted && call.Result.Succeeded && !call.Result.Value,
                    "all-declined sequential calls succeed false, without fictitious playback or leaked state");
            }
            using var local = new RobotSpeechRig(); local.Guests[0].Body.Visible = false;
            var localCall = local.Robot.SayAsync("Local playback still matters."); local.Later();
            Check(!localCall.IsCompleted, "remote decline cannot bypass host-local playback");
            local.Canonical.SpeechCompletions[0].SetResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Unavailable."));
            local.World.Frames(3);
            Check(localCall.IsCompleted && !localCall.Result.Succeeded, "remote decline cannot erase local failure");
        }

        private static void DeclineValidation()
        {
            using var rig = new RobotSpeechRig(2); rig.Move(rig.Host, 60); rig.Move(rig.Guests[1], 60);
            var guest = rig.Guests[0]; guest.Peer.Paused = true; guest.Body.Visible = false;
            var call = rig.Robot.SayAsync("A private refusal."); var line = rig.LastLine;
            for (var size = 0; size < line.Length; size++)
                Check(!DeliverToGuest(rig, line.Take(size).ToArray()), "truncated lines cannot solicit declines");
            Check(!DeliverToGuest(rig, line, rig.Guests[1].Peer.Control.Id), "non-host cannot solicit a decline");
            Check(!DeliverToGuest(rig, RewriteLine(line, changed: 1)) && !DeliverToGuest(rig, RewriteLine(line, changed: 2)),
                "wrong world/challenge cannot solicit a decline");
            Check(!DeliverToGuest(rig, RewriteLine(line, nonce: 0))
                && !DeliverToGuest(rig, RewriteLine(line, stamp: (ulong)rig.World.Clock.Now + 1)),
                "zero nonce and future timestamp are malformed, not admission refusals");
            var oldLine = (byte[])line.Clone(); oldLine[2] = 2;
            var invalidText = (byte[])line.Clone(); invalidText[invalidText.Length - 1] = 0xff;
            var oversized = new byte[RobotSpeechWire.MaximumBytes + 1]; Array.Copy(line, oversized, line.Length);
            Check(!DeliverToGuest(rig, oldLine) && !DeliverToGuest(rig, line.Concat(new byte[] { 0 }).ToArray())
                && !DeliverToGuest(rig, invalidText) && !DeliverToGuest(rig, oversized),
                "old schema, surplus bytes, malformed text and oversized lines cannot solicit a decline");
            Check(Reports(guest).Length == 0, "malformed and untrusted lines have no response oracle");
            var report = Finished(line, RobotSpeechWire.Declined); var sender = guest.Peer.Control.Id;
            Check(!Deliver(rig, report, rig.Guests[1].Peer.Control.Id) && !Deliver(rig, report, rig.World.Host.Control.Id),
                "decline is bound to the admitted authenticated remote sender");
            for (var i = 1; i <= 4; i++)
                Check(!Deliver(rig, Finished(line, RobotSpeechWire.Declined, i), sender), "decline routing must match exactly");
            for (var size = 0; size < report.Length; size++)
                Check(!Deliver(rig, report.Take(size).ToArray(), sender), "truncated decline fails closed");
            foreach (var status in new byte[] { 3, 127, 255 })
                Check(!Deliver(rig, Finished(line, status), sender), "unknown status fails closed");
            Check(!Deliver(rig, report.Concat(new byte[] { 0 }).ToArray(), sender), "decline consumes its entire frame");
            var oldReport = (byte[])report.Clone(); oldReport[2] = 2;
            Check(!Deliver(rig, oldReport, sender), "old schema cannot submit a new terminal status");
            Check(DeliverToGuest(rig, line), "valid refusal survives malformed attempts without nonce poisoning");
            Check(Reports(guest).Length == 1 && Reports(guest)[0].SequenceEqual(report), "only a closed reason-free decline is sent");
            Check(!DeliverToGuest(rig, line) && Reports(guest).Length == 1, "duplicate decline produces no amplification");
            Check(Deliver(rig, report, sender) && !Deliver(rig, report, sender), "decline closes its recipient once");
            Check(!Deliver(rig, Finished(line, RobotSpeechWire.Completed), sender)
                && !Deliver(rig, Finished(line, RobotSpeechWire.Failed), sender), "closed decline cannot change status");
            rig.Later(); Check(call.IsCompleted && call.Result.Succeeded && !call.Result.Value, "valid decline is not failure or playback");
            var next = rig.Robot.SayAsync("Next operation."); var nextReport = Finished(rig.LastLine, RobotSpeechWire.Declined);
            Check(!Deliver(rig, report, sender) && !next.IsCompleted, "old decline cannot settle a later operation");
            Check(Deliver(rig, nextReport, sender), "later operation has its own recipient");
            rig.Later(); Check(next.IsCompleted && next.Result.Succeeded && !next.Result.Value, "second operation retires independently");
            rig.World.Network.LoseLink(guest.Peer.Control); rig.World.Frames(3);
            rig.World.Network.Restore(guest.Peer.Control); guest.Peer.Paused = false; rig.World.Frames(40);
            Check(!Deliver(rig, nextReport, sender), "decline cannot replay across reconnect");
            rig.Host.Scene++; rig.World.Frames(3);
            Check(!Deliver(rig, nextReport, sender), "decline cannot replay across world reset");

            using var flood = new RobotSpeechRig(); flood.Move(flood.Host, 60);
            var target = flood.Guests[0]; target.Peer.Paused = true; target.Body.Visible = false;
            flood.Robot.SayAsync("Bound replies."); var seed = flood.LastLine;
            for (ulong nonce = 1; nonce <= 40; nonce++)
                Check(DeliverToGuest(flood, RewriteLine(seed, nonce)), "authenticated refusal does not queue playback");
            Check(Reports(target).Length == 8 && target.Peer.World.Speech.ActiveCount == 0,
                "fresh-nonce abuse is bounded to eight small declines with no retained operation");
            flood.World.Clock.Advance(250_000);
            Check(DeliverToGuest(flood, RewriteLine(seed, 41)) && Reports(target).Length == 9,
                "decline budget replenishes at four replies per second");
        }

        private static void SchemaCompatibility()
        {
            Check(RobotSpeechWire.Version == 3 && RobotSpeechWire.Ready == 72 && RobotSpeechWire.Line == 73
                && RobotSpeechWire.Cancel == 74 && RobotSpeechWire.Withdraw == 75 && RobotSpeechWire.Finished == 76
                && RobotSpeechWire.Failed == 0 && RobotSpeechWire.Completed == 1 && RobotSpeechWire.Declined == 2,
                "speech schema three preserves message ids and defines a closed three-status contract");
            foreach (var schema in new byte[] { 1, 2, 3, 4 })
            {
                using var rig = new RobotSpeechRig(); rig.Move(rig.Host, 60);
                var guest = rig.Guests[0]; guest.Peer.Paused = true;
                var ready = (byte[])guest.Channel.Log!.Last(p => p.Payload[1] == RobotSpeechWire.Ready).Payload.Clone();
                ready[2] = schema; rig.World.Host.World.Speech.Reset(); rig.Log.Clear();
                Check(Deliver(rig, ready, guest.Peer.Control.Id) == (schema == 3), "only matching schema establishes readiness");
                var call = rig.Robot.SayAsync("Only compatible listeners.");
                Check(rig.Log.Count(p => p.Payload[1] == RobotSpeechWire.Line) == (schema == 3 ? 1 : 0),
                    "incompatible schemas never receive text or acquire a pending recipient");
                rig.Later();
                Check(schema == 3 ? !call.IsCompleted : call.IsCompleted && call.Result.Succeeded && !call.Result.Value,
                    "incompatible schemas cannot stall completion");
            }
        }

        private static void ActiveCapDecline()
        {
            using var rig = new RobotSpeechRig(); rig.Move(rig.Host, 60);
            var guest = rig.Guests[0]; guest.Peer.Paused = true;
            rig.Robot.SayAsync("Cap receiver work."); var seed = rig.LastLine;
            // Hold frame maintenance to exercise the parser's active cap even with cancelled prior copies.
            for (ulong nonce = 1; nonce <= 32; nonce++)
            {
                rig.World.Clock.Advance(250_000);
                Check(DeliverToGuest(rig, RewriteLine(seed, nonce, (ulong)rig.World.Clock.Now)), "receive admits up to its active cap");
            }
            Check(guest.Peer.World.Speech.ActiveCount == 32 && guest.Body.SpeechPlays == 32, "receiver reached its independent cap");
            rig.World.Clock.Advance(250_000);
            var overflow = RewriteLine(seed, 33, (ulong)rig.World.Clock.Now);
            Check(DeliverToGuest(rig, overflow) && guest.Peer.World.Speech.ActiveCount == 32 && guest.Body.SpeechPlays == 32,
                "full active cap declines without extra playback");
            Check(Reports(guest).Single().SequenceEqual(Finished(overflow, RobotSpeechWire.Declined)),
                "full active cap uses the same reason-free terminal status");
        }
    }
}
