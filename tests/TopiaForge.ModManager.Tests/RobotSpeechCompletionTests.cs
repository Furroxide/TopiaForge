using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotSpeechCompletionTests
    {
        internal static void Run()
        {
            FarHostSequential(); NoRecipients(); AllListeners(); Failures(); AcknowledgementValidation(); ResetContinuation();
            Console.WriteLine("Robot speech completion: 6 focused groups passed.");
        }
        private static void Check(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Robot speech completion: " + reason); }

        private static void FarHostSequential()
        {
            using var rig = new RobotSpeechRig();
            rig.Move(rig.Host, 60);
            var completed = 0;
            async Task Dialogue()
            {
                var first = await rig.Robot.SayAsync("First complete line.");
                Check(first.Succeeded && first.Value, "first remote playback completes successfully"); completed++;
                var second = await rig.Robot.SayAsync("Second complete line.");
                Check(second.Succeeded && second.Value, "second remote playback completes successfully"); completed++;
            }
            var dialogue = Dialogue(); rig.World.Frames(5);
            Check(completed == 0 && !dialogue.IsCompleted, "FarHostSequential: await must remain pending before remote completion");
            var body = rig.Guests[0].Body;
            Check(body.SpeechPlays == 1 && rig.Canonical.SpeechPlays == 0, "far host never synthesizes or bypasses earshot");
            rig.Later();
            Check(!dialogue.IsCompleted && body.SpeechPlays == 1 && !body.SpeechTokens[0].IsCancellationRequested,
                "elapsed pace alone cannot certify or truncate playback");
            body.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true)); rig.World.Frames(5);
            Check(completed == 1 && body.SpeechPlays == 2 && !dialogue.IsCompleted,
                "second awaited line starts only after the first real completion report");
            Check(!body.SpeechTokens[0].IsCancellationRequested, "completed first line is never replaced or cancelled");
            rig.Later(); Check(completed == 1 && !body.SpeechTokens[1].IsCancellationRequested, "second stays intact until completion");
            body.SpeechCompletions[1].SetResult(OperationResult<bool>.Success(true)); rig.World.Frames(5);
            dialogue.GetAwaiter().GetResult();
            Check(completed == 2 && rig.World.Host.World.Speech.ActiveCount == 0, "two sequential awaited lines retire without pending work");
            Console.WriteLine("FarHostSequential: passed two controlled native completions, no truncation or refusal.");
        }

        private static void NoRecipients()
        {
            using var rig = new RobotSpeechRig(); rig.Move(rig.Host, 60); rig.Move(rig.Guests[0], 60);
            var completed = 0;
            async Task Dialogue()
            {
                for (var i = 0; i < 6; i++)
                {
                    var result = await rig.Robot.SayAsync("Nobody nearby.");
                    Check(result.Succeeded && !result.Value, "empty audience returns false after pacing, without rate refusal"); completed++;
                }
            }
            var dialogue = Dialogue();
            for (var i = 0; i < 6; i++)
            {
                Check(completed == i && !dialogue.IsCompleted, "NoRecipients: each empty-audience line must wait its pace");
                rig.Later();
            }
            dialogue.GetAwaiter().GetResult();
            Check(completed == 6 && rig.Canonical.SpeechPlays == 0 && rig.Guests[0].Body.SpeechPlays == 0,
                "empty audience does not synthesize for an unauthorized listener");
            Check(rig.Log.All(p => p.Payload[1] != RobotSpeechWire.Line), "empty audience discloses no text");
            Console.WriteLine("NoRecipients: passed six paced sequential awaits without synthesis or rate refusal.");
        }

        private static void AllListeners()
        {
            using var rig = new RobotSpeechRig(2);
            var call = rig.Robot.SayAsync("Everyone finishes."); rig.World.Frames(4);
            rig.Canonical.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true));
            rig.Guests[0].Body.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true));
            rig.Later(); Check(!call.IsCompleted, "near host and fast listener cannot finish for slower listener");
            rig.Guests[1].Body.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true)); rig.World.Frames(5);
            Check(call.IsCompleted && call.Result.Succeeded && call.Result.Value, "all admitted listeners have completed");
            using var shortLine = new RobotSpeechRig(0);
            var shortCall = shortLine.Robot.SayAsync("Short.");
            shortLine.Canonical.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true)); shortLine.World.Frames(3);
            Check(!shortCall.IsCompleted, "even very short local playback observes unchanged minimum pace");
            shortLine.Later(); Check(shortCall.IsCompleted && shortCall.Result.Succeeded, "short playback completes after pace");
        }

        private static void Failures()
        {
            foreach (var loss in new[] { "caller", "world", "provider", "owner", "ownership", "disconnect", "hidden", "failure", "timeout" })
            {
                using var rig = new RobotSpeechRig(); rig.Move(rig.Host, 60);
                using var cancellation = new CancellationTokenSource();
                var call = rig.Robot.SayAsync("Must not report completion.", cancellation.Token); rig.World.Frames(5);
                var body = rig.Guests[0].Body;
                Check(!call.IsCompleted, loss + " begins with pending playback");
                switch (loss)
                {
                    case "caller": cancellation.Cancel(); break;
                    case "world": rig.Host.Scene++; break;
                    case "provider": rig.World.Host.Lifetime.Dispose(); break;
                    case "owner": rig.Owner.Lifetime.BeginStop(); break;
                    case "ownership": rig.World.Host.Runtime.Spawns.Remove(rig.World.Host.Runtime.Spawns.Find(rig.Canonical)!); break;
                    case "disconnect": rig.World.Network.LoseLink(rig.Guests[0].Peer.Control); break;
                    case "hidden": body.Visible = false; break;
                    case "failure": body.SpeechCompletions[0].SetResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Unavailable.")); break;
                    case "timeout": rig.World.Clock.Advance(16_000_000); break;
                }
                rig.Later();
                Check(call.IsCompleted && !call.Result.Succeeded, loss + " settles boundedly without claiming playback success");
                Check(rig.World.Host.World.Speech.ActiveCount == 0, loss + " releases retained operation");
            }
            // Silence from a still-connected peer is not an acknowledgement, including a full return window.
            using var stalled = new RobotSpeechRig(); stalled.Move(stalled.Host, 60);
            var pending = stalled.Robot.SayAsync("No completion report."); stalled.World.Frames(4);
            stalled.Guests[0].Peer.Paused = true; stalled.Later();
            Check(!pending.IsCompleted, "a silent connected listener cannot complete a line");
            stalled.World.Clock.Advance(16_000_000); stalled.World.Frames(3);
            Check(pending.IsCompleted && !pending.Result.Succeeded, "missing completion report expires at the existing deadline");
        }

        private static void ResetContinuation()
        {
            using var rig = new RobotSpeechRig(); rig.Move(rig.Host, 60);
            async Task Dialogue()
            {
                var result = await rig.Robot.SayAsync("World is ending.");
                Check(!result.Succeeded, "world reset fails the old call");
                var retry = await rig.Robot.SayAsync("Reentrant call during reset.");
                Check(retry.ErrorCode == ModErrorCode.Unavailable, "reset continuation cannot readmit old-world work");
            }
            var dialogue = Dialogue(); rig.World.Frames(4); rig.Host.Scene++; rig.World.Frames(3);
            Check(dialogue.IsCompleted, "reset continuation settles without mutating the retiring list");
            dialogue.GetAwaiter().GetResult();
        }

        private static byte[] Finished(byte[] line, byte status = 1, int changed = 0)
        {
            var reader = new WorldWireReader(); reader.Reset(line); reader.TryReadHeader(out _); reader.TryReadByte(out _);
            reader.TryReadUInt64(out var world); reader.TryReadString(32, out var challenge);
            reader.TryReadUInt64(out var nonce); reader.TryReadVarUInt(uint.MaxValue, out var entity);
            var writer = new WorldWireWriter(RobotSpeechWire.MaximumBytes);
            RobotSpeechWire.Header(writer, RobotSpeechWire.Finished, changed == 1 ? world + 1 : world,
                changed == 2 ? new string('f', 32) : challenge, changed == 3 ? nonce + 1 : nonce, changed == 4 ? (uint)entity + 1 : (uint)entity);
            writer.WriteByte(status); return writer.Segment.ToArray();
        }
        private static bool Deliver(RobotSpeechRig rig, byte[] bytes, ParticipantId sender)
        {
            var reader = new WorldWireReader(); reader.Reset(bytes);
            return reader.TryReadHeader(out var type) && rig.World.Host.World.Speech.Handle(type, sender, reader);
        }
        private static void AcknowledgementValidation()
        {
            using var rig = new RobotSpeechRig(2); rig.Move(rig.Host, 60); rig.Move(rig.Guests[1], 60);
            var call = rig.Robot.SayAsync("Bound to this playback."); rig.World.Frames(4);
            var ack = Finished(rig.LastLine); var sender = rig.Guests[0].Peer.Control.Id;
            Check(!Deliver(rig, ack, rig.Guests[1].Peer.Control.Id) && !Deliver(rig, ack, rig.World.Host.Control.Id),
                "only an admitted remote sender can report its own completion");
            for (var i = 1; i <= 4; i++) Check(!Deliver(rig, Finished(rig.LastLine, changed: i), sender), "world/challenge/nonce/entity must match exactly");
            for (var size = 0; size < ack.Length; size++) Check(!Deliver(rig, ack.Take(size).ToArray(), sender), "truncated acknowledgements fail closed");
            Check(!Deliver(rig, Finished(rig.LastLine, 2), sender) && !Deliver(rig, ack.Concat(new byte[] { 0 }).ToArray(), sender),
                "status and exact framing are validated");
            var oldVersion = (byte[])ack.Clone(); oldVersion[2] = 1;
            Check(!Deliver(rig, oldVersion, sender), "schema one cannot certify schema two completion");
            rig.Later(); Check(!call.IsCompleted, "invalid acknowledgements cannot finish playback");
            rig.Guests[0].Body.SpeechCompletions[0].SetResult(OperationResult<bool>.Success(true)); rig.World.Frames(5);
            Check(call.IsCompleted && call.Result.Succeeded && !Deliver(rig, ack, sender), "real completion accepted once, replay rejected");
            var next = rig.Robot.SayAsync("Different line."); rig.World.Frames(4);
            Check(!Deliver(rig, ack, sender) && !next.IsCompleted, "old completion cannot finish a later line");
            var priorStream = Finished(rig.LastLine);
            rig.World.Network.LoseLink(rig.Guests[0].Peer.Control); rig.World.Frames(4);
            rig.World.Network.Restore(rig.Guests[0].Peer.Control); rig.World.Frames(40);
            Check(!Deliver(rig, priorStream, sender), "retired connection completion cannot replay after reconnect");
            rig.Host.Scene++; rig.World.Frames(3);
            Check(!Deliver(rig, priorStream, sender), "old world completion cannot replay after reset");
        }
    }
}
