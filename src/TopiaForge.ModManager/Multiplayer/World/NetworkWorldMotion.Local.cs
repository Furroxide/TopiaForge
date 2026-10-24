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
                    if (!hosted.TryGetValue(lease.Target.Id.Value, out var state) ||
                        state.Rules.Owner.Participant != MotionRules.HostSlot || state.Rules.Epoch != lease.Epoch)
                    {
                        Refused(lease, OwnershipResult.Denied);
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
            if (message.Target.Id.IsValid)
            {
                if (epochs.TryGetValue(message.Target.Id.Value, out var epoch) && unchecked((int)(message.Epoch - epoch)) < 0) return;
                if (epochs.Count < NetworkWorldClient.MaximumEntities || epochs.ContainsKey(message.Target.Id.Value))
                    epochs[message.Target.Id.Value] = message.Epoch;
            }

            foreach (var lease in local)
            {
                if (type == MotionMessages.Verdict && lease.Request == message.Request)
                {
                    if (message.Result == OwnershipResult.Granted || message.Result == OwnershipResult.AlreadyOwned)
                    {
                        lease.Target = message.Target;
                        lease.Epoch = message.Epoch;
                        lease.Granted = true;
                        world.Client.SetSimulated(lease.Entity, true);
                    }
                    else Refused(lease, message.Result);
                }
                else if (lease.Target.Id.IsValid && lease.Target.Id.Equals(message.Target.Id) &&
                    unchecked((int)(message.Epoch - lease.Epoch)) > 0) Refused(lease, message.Result);
            }

            world.Client.ApplyMotion(message);
        }

        private void Refused(NetEntityMotion lease, OwnershipResult result)
        {
            if (lease.Ended) return;
            EndLocal(lease);
            if (lease.Target.IsScene && lease.Body.IsAlive) lease.Body.WriteMotion(lease.InitialPose, lease.InitialAngular);
            if (lease.Context?.Ui == null || Now < nextToast) return;
            nextToast = Now + SpawnAuthorityRules.RefusalToastIntervalMicros;
            lease.Context.Ui.ShowToast(MotionRules.Refusal(result), UiTone.Warning);
        }
    }
}
