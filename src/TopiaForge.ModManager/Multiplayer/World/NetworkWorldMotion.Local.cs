using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class NetworkWorldMotion
    {
        private void PumpLocal()
        {
            var now = Now;
            for (var index = local.Count - 1; index >= 0; index--)
            {
                var lease = local[index];
                if (lease.Ended || !lease.Body.IsAlive)
                {
                    EndLocal(lease);
                    local.RemoveAt(index);
                    continue;
                }

                if (!lease.Granted)
                {
                    if (now - lease.CreatedMicros > MotionRules.Policy.AnswerTimeoutMicros)
                    {
                        Refused(lease, OwnershipResult.NoChange);
                        local.RemoveAt(index);
                        continue;
                    }
                }

                if (lease.Target.IsScene && world.Host.TryBindMotion(lease.Target, out var entity))
                    lease.Target = new MotionTarget(entity.Id);
                if (!TryLocalPosition(out var position) ||
                    !lease.Body.TryReadMotion(out var pose, out var angular)) continue;
                if (!MotionRules.IsValid(pose.Position) || !MotionRules.IsValidVelocity(pose.Velocity) || !MotionRules.IsValidVelocity(angular))
                {
                    Refused(lease, OwnershipResult.Rejected);
                    continue;
                }

                var type = lease.NeedsHandback || lease.Context?.IsStopping == true || (lease.Releasing && now >= lease.ReleaseDeadline)
                    ? MotionMessages.Handback
                    : lease.Releasing && !lease.ReleaseSent ? MotionMessages.Release : MotionMessages.Sample;
                if (type == MotionMessages.Sample && now < lease.NextSampleMicros) continue;
                write.Target = lease.Target;
                write.Request = lease.Request;
                write.Epoch = lease.Epoch;
                write.PlayerPosition = position;
                write.Pose = pose;
                write.AngularVelocity = angular;
                if (world.Host.IsActive)
                {
                    if (!hosted.TryGetValue(lease.Target.Id.Value, out var state))
                    {
                        Refused(lease, OwnershipResult.NoChange);
                        continue;
                    }

                    if (state.Rules.Owner.Participant != MotionRules.HostSlot || state.Rules.Epoch != lease.Epoch)
                    {
                        Superseded(lease, state.Change);
                        continue;
                    }

                    ApplySample(state, MotionRules.HostSlot, type, write, position);
                }
                else
                {
                    MotionWire.Write(writer, type, write);
                    if (!SendToHost()) continue;
                }

                lease.NextSampleMicros = MotionRules.NextSample(lease.NextSampleMicros, now);
                if (type == MotionMessages.Release) lease.ReleaseSent = true;
                if (type != MotionMessages.Handback) continue;
                EndLocal(lease);
                local.RemoveAt(index);
            }
        }

        private void ReceiveState(byte type, MotionMessage message)
        {
            // A message older than the epoch this player knows changes nothing, except that an answer always settles its
            // request: a grab the host refused before looking at the body names no epoch at all.
            var stale = false;
            if (message.Target.Id.IsValid)
            {
                stale = epochs.TryGetValue(message.Target.Id.Value, out var epoch) && unchecked((int)(message.Epoch - epoch)) < 0;
                if (!stale && (epochs.Count < NetworkWorldClient.MaximumEntities || epochs.ContainsKey(message.Target.Id.Value)))
                    epochs[message.Target.Id.Value] = message.Epoch;
            }

            foreach (var lease in local)
            {
                if (type == MotionMessages.Verdict && lease.Request == message.Request)
                {
                    if (!IsGranted(message.Result)) Refused(lease, message.Result);
                    else if (stale) Refused(lease, OwnershipResult.NoChange);
                    else
                    {
                        lease.Target = message.Target;
                        lease.Epoch = message.Epoch;
                        lease.Granted = true;
                        world.Client.SetSimulated(lease.Entity, true);
                    }
                }
                else if (!stale && lease.Target.Id.IsValid && lease.Target.Id.Equals(message.Target.Id) &&
                    unchecked((int)(message.Epoch - lease.Epoch)) > 0) Superseded(lease, message.Result);
            }

            if (!stale) world.Client.ApplyMotion(message);
        }

        /// <summary>
        /// Ends a lease a later change of owner overtook, telling the player why. A body the player held and already threw
        /// or dropped ends quietly, however it went back, unless the host refused its motion: another player catching it
        /// is no failure. A grab still waiting for its answer was never the player's, so it is told.
        /// </summary>
        private void Superseded(NetEntityMotion lease, OwnershipResult change)
        {
            if (lease.Granted && lease.Releasing && change != OwnershipResult.Rejected) Refused(lease, change, tell: false);
            else Refused(lease, change == OwnershipResult.Granted ? OwnershipResult.Denied : change);
        }

        private void Refused(NetEntityMotion lease, OwnershipResult result, bool tell = true)
        {
            if (lease.Ended) return;
            EndLocal(lease);
            if (lease.Target.IsScene && lease.Body.IsAlive) lease.Body.WriteMotion(lease.InitialPose, lease.InitialAngular);
            if (tell) Tell(lease.Context?.Ui, MotionRules.Refusal(result));
        }
    }
}
