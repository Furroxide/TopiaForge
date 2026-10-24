using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class NetworkWorldMotion
    {
        private readonly List<MotionReply> replies = new List<MotionReply>();

        bool IWorldMessageHandler.Handle(byte type, ParticipantId sender, WorldWireReader reader)
        {
            var fromHost = type == MotionMessages.Verdict || type == MotionMessages.State;
            if (fromHost != world.Client.IsActive || (!fromHost && !world.Host.IsActive)) return false;
            if (!fromHost && world.Channel.GetGeneration(sender) == 0) return false;
            var bytes = reader.Remaining + 2;
            if (!MotionWire.Read(reader, type, read)) return false;
            if (fromHost) ReceiveState(type, read);
            else if (type == MotionMessages.Grab) ReceiveGrab(sender, read);
            else
            {
                if (!world.Proxies.TryGetProxy(sender, out var proxy)) return false;
                if (!incoming.TryGetValue(proxy.Slot, out var budget))
                {
                    budget = new TokenBucket(MotionRules.BurstBytes, MotionRules.BytesPerSecond, Now);
                    incoming.Add(proxy.Slot, budget);
                }

                if (!budget.TryTake(Now, bytes)) return false;
                var target = read.Target;
                if (target.IsScene)
                {
                    if (!world.Host.Index.TryGetBindable(target.SceneKey, out var scene) || !scene.IsBound) return false;
                    target = new MotionTarget(scene.Bound);
                }
                if (!hosted.TryGetValue(target.Id.Value, out var state) || !TryParticipantPosition(sender, read, out var position)) return false;
                ApplySample(state, proxy.Slot, type, read, position);
            }

            return true;
        }

        private void Describe(HostedMotion state, MotionMessage message, OwnershipResult result)
        {
            message.Target = new MotionTarget(state.Entity.Id);
            message.Epoch = state.Rules.Epoch;
            message.Slot = state.Rules.Owner.Participant;
            message.State = state.Rules.State;
            message.Result = result;
            message.Pose = state.Pose;
            message.AngularVelocity = state.AngularVelocity;
            message.TimeMicros = Now;
        }

        private void Reply(ParticipantId participant, MotionMessage asked, HostedMotion? state, OwnershipResult result)
        {
            var count = 0;
            foreach (var queued in replies) if (queued.Participant.Equals(participant)) count++;
            if (count >= 16 || replies.Count >= 256) return;
            var message = new MotionMessage
            {
                Target = asked.Target,
                Request = asked.Request,
                Pose = new NetPose(NetVector3.Zero, new NetQuaternion(0, 0, 0, 1)),
                Result = result,
                TimeMicros = Now
            };
            if (state != null) Describe(state, message, result);
            replies.Add(new MotionReply(participant, world.Channel.GetGeneration(participant), message));
            FlushReplies();
        }

        private void FlushReplies()
        {
            for (var index = 0; index < replies.Count;)
            {
                var reply = replies[index];
                if (world.Channel.GetGeneration(reply.Participant) != reply.Generation)
                {
                    replies.RemoveAt(index);
                    continue;
                }
                MotionWire.Write(writer, MotionMessages.Verdict, reply.Message);
                if (!world.Channel.TrySendTo(reply.Participant, writer, writer.Length)) { index++; continue; }
                BytesSent += writer.Length;
                replies.RemoveAt(index);
            }
        }

        private void SendState(HostedMotion state, long now)
        {
            if (state.Dirty && now >= state.NextBroadcast)
            {
                Describe(state, write, OwnershipResult.NoChange);
                write.Request = 0;
                MotionWire.Write(writer, MotionMessages.State, write);
                if (!outgoing.TryTake(now, writer.Length)) return;
                state.Skipped.Clear();
                world.Channel.Broadcast(writer, state.Skipped);
                BytesSent += writer.Length;
                state.Dirty = false;
                state.NextBroadcast = MotionRules.NextSample(state.NextBroadcast, now);
            }
            else if (state.Skipped.Count > 0)
            {
                Describe(state, write, OwnershipResult.NoChange);
                write.Request = 0;
                MotionWire.Write(writer, MotionMessages.State, write);
                for (var index = state.Skipped.Count - 1; index >= 0; index--)
                {
                    var participant = state.Skipped[index];
                    if (world.Channel.GetGeneration(participant) == 0) { state.Skipped.RemoveAt(index); continue; }
                    if (!world.Channel.CanSendTo(participant, writer.Length) || !outgoing.TryTake(now, writer.Length)) continue;
                    if (world.Channel.TrySendTo(participant, writer, writer.Length)) state.Skipped.RemoveAt(index);
                }
            }
        }

        private readonly struct MotionReply
        {
            internal MotionReply(ParticipantId participant, ulong generation, MotionMessage message) { Participant = participant; Generation = generation; Message = message; }
            internal ParticipantId Participant { get; }
            internal ulong Generation { get; }
            internal MotionMessage Message { get; }
        }
    }
}
