using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class NetworkWorldMotion
    {
        private HostedMotion? FindHost(MotionTarget target)
        {
            if (!world.Host.TryBindMotion(target, out var entity) || !(entity.Object is INetworkMotionBody body)) return null;
            if (hosted.TryGetValue(entity.Id.Value, out var known)) return known;
            if (hosted.Count >= MotionRules.MaximumTrackedBodies || !body.TryReadMotion(out _, out _)) return null;
            var state = new HostedMotion(entity, body);
            hosted.Add(entity.Id.Value, state);
            return state;
        }

        private bool GrabHere(MotionTarget target, uint epoch, NetVector3 position, out uint grantedEpoch)
        {
            grantedEpoch = epoch;
            var state = FindHost(target);
            if (state == null || !state.Body.TryReadMotion(out var pose, out var angular)) return false;
            var wasFree = state.Rules.State == OwnershipState.Server;
            var result = state.Rules.Request(MotionRules.HostSlot, epoch, NetQuantization.QuantizePosition(pose.Position),
                NetQuantization.QuantizePosition(position), Now);
            if (result.Result != OwnershipResult.Granted && result.Result != OwnershipResult.AlreadyOwned) return false;
            if (wasFree) state.ResumeKinematic = state.Body.IsKinematic;
            state.Pose = pose;
            state.AngularVelocity = angular;
            grantedEpoch = result.Epoch;
            state.Dirty = true;
            return true;
        }

        private void CancelHostGrab(MotionTarget target)
        {
            var state = FindHost(target);
            if (state == null) return;
            state.Rules.Reclaim();
            state.Restore();
            state.Dirty = true;
        }

        private void ReceiveGrab(ParticipantId sender, MotionMessage message)
        {
            var proxy = world.Proxies.MotionProxy(sender);
            if (proxy == null) return;
            // Spend before resolving or binding an object, including malformed or unknown targets.
            var permitted = world.Proxies.Limiter.Evaluate(proxy.Slot, false, NetRequestKind.MotionGrab,
                NetTargetRelation.Server, proxy.Counted, Now) == NetPermission.Allowed;
            var state = permitted ? FindHost(message.Target) : null;
            var result = OwnershipResult.NoChange;
            if (state != null && state.Body.TryReadMotion(out var pose, out var angular))
            {
                var relation = SpawnAuthorityRules.RelationOf(state.Entity.OwnerSlot, proxy.Slot);
                if (TryParticipantPosition(sender, message, out var position) && PermissionPolicy.Default.Permits(NetRequestKind.MotionGrab, relation) &&
                    CountOwned(proxy.Slot) < MotionRules.MaximumLocalBodies)
                {
                    var wasFree = state.Rules.State == OwnershipState.Server;
                    var outcome = state.Rules.Request(proxy.Slot, message.Epoch, NetQuantization.QuantizePosition(pose.Position),
                        NetQuantization.QuantizePosition(position), Now);
                    result = outcome.Result;
                    if (result == OwnershipResult.Granted || result == OwnershipResult.AlreadyOwned)
                    {
                        if (wasFree) state.ResumeKinematic = state.Body.IsKinematic;
                        state.Participant = sender;
                        state.Generation = world.Channel.GetGeneration(sender);
                        state.Pose = pose;
                        state.AngularVelocity = angular;
                        state.Body.IsKinematic = true;
                        state.Dirty = true;
                        GrabsGranted++;
                    }
                }
            }

            if (result != OwnershipResult.Granted && result != OwnershipResult.AlreadyOwned) GrabsRefused++;
            Reply(sender, message, state, result);
        }

        private int CountOwned(int slot)
        {
            var count = 0;
            foreach (var state in hosted.Values)
                if (state.Rules.Owner.Participant == slot && state.Rules.State != OwnershipState.Server) count++;
            return count;
        }

        private void ApplySample(HostedMotion state, int slot, byte type, MotionMessage message, NetVector3 position)
        {
            var rules = state.Rules;
            var holder = NetQuantization.QuantizePosition(position);
            var bodyPosition = NetQuantization.QuantizePosition(message.Pose.Position);
            OwnershipOutcome outcome;
            if (type == MotionMessages.Handback)
            {
                var thrown = rules.State == OwnershipState.ReleaseWindow;
                outcome = rules.Handback(slot, message.Epoch, new MotionHandback(bodyPosition,
                    message.Pose.Velocity, message.AngularVelocity), holder, Now, out var accepted);
                if (outcome.Result == OwnershipResult.HandedBack)
                {
                    state.Pose = new NetPose(NetQuantization.DequantizePosition(accepted.Position), message.Pose.Rotation, accepted.LinearVelocity);
                    state.AngularVelocity = accepted.AngularVelocity;
                    if (thrown) state.ResumeKinematic = false;
                }
            }
            else
            {
                outcome = rules.Stream(slot, message.Epoch, bodyPosition, holder, Now);
                if (outcome.Result == OwnershipResult.Accepted)
                {
                    state.Pose = new NetPose(message.Pose.Position, message.Pose.Rotation,
                        MotionRules.ClampVelocity(message.Pose.Velocity, MotionRules.Policy.MaximumSpeedMetersPerSecond));
                    state.AngularVelocity = MotionRules.ClampVelocity(message.AngularVelocity, MotionRules.Policy.MaximumAngularSpeedRadiansPerSecond);
                    SamplesAccepted++;
                    if (type == MotionMessages.Release)
                    {
                        outcome = rules.BeginRelease(slot, message.Epoch, holder, Now);
                        if (outcome.Result == OwnershipResult.Releasing) state.ResumeKinematic = false;
                    }
                }
            }

            if (outcome.Result == OwnershipResult.Rejected) SamplesRefused++;
            if (outcome.EpochChanged)
            {
                state.Restore();
                state.Dirty = true;
            }
            else if (outcome.Result == OwnershipResult.Accepted || outcome.Result == OwnershipResult.Releasing)
            {
                if (slot != MotionRules.HostSlot) state.Body.WritePose(state.Pose);
                state.Dirty = true;
            }
        }

        private void PumpHost()
        {
            var now = Now;
            ended.Clear();
            foreach (var pair in hosted)
            {
                var state = pair.Value;
                if (!state.Entity.IsAlive || !state.Body.IsAlive)
                {
                    state.Rules.Reclaim();
                    ended.Add(pair.Key);
                    continue;
                }

                var slot = state.Rules.Owner.Participant;
                var result = slot != 0 && slot != MotionRules.HostSlot &&
                    world.Channel.GetGeneration(state.Participant) != state.Generation
                    ? state.Rules.LinkClosed(slot, now) : state.Rules.Poll(now);
                if (result.EpochChanged)
                {
                    state.Restore();
                    state.Dirty = true;
                }

                SendState(state, now);
            }

            foreach (var id in ended) hosted.Remove(id);
            ended.Clear();
            FlushReplies();
        }

        internal void Disconnect(int slot)
        {
            incoming.Remove(slot);
            foreach (var state in hosted.Values)
            {
                if (!state.Rules.Disconnect(slot).EpochChanged) continue;
                state.Restore();
                state.Dirty = true;
            }
        }
    }
}
