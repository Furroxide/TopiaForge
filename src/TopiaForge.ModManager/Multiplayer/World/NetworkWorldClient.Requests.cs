using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// A joined player's transform changes and destroys (WS3.7). A change to an object of the player's own becomes a request
    /// the host validates, whatever code makes it: the object is the player's, and the host's own run of the mod never held
    /// it. On the player's intent, a change to anything the host or another player owns is refused here, as the host's
    /// default rules would. Scripted code changes nothing the host owns here: the host's own run of the same script does,
    /// and the result arrives as the host's snapshots and despawns. Requests keep to an honest player's pace; transform
    /// changes to one object coalesce, the newest winning.
    /// </summary>
    internal sealed partial class NetworkWorldClient
    {
        internal const string NotYoursToMove = "Only the host, or the player who made it, can move that object.";
        internal const string NotYoursToDestroy = "Only the host, or the player who made it, can destroy that object.";

        private readonly Dictionary<AuthorityTarget, (NetVector3 Scale, NetPose Pose)> transforms = new Dictionary<AuthorityTarget, (NetVector3 Scale, NetPose Pose)>();
        private readonly List<AuthorityTarget> transformOrder = new List<AuthorityTarget>();
        private readonly List<AuthorityTarget> destroyQueue = new List<AuthorityTarget>();
        private TokenBucket? transformPace;
        private TokenBucket? destroyPace;

        /// <summary>
        /// Routes a transform change from code acting on the player's intent, or scripted when <paramref name="scripted"/>;
        /// null when the entity is none of the network world's, so the call goes to the game as it would offline.
        /// </summary>
        internal OperationResult<TransformState>? RouteTransform(IEntity entity, TransformState transform, IEntityService inner, bool scripted)
        {
            if (!IsActive || entity == null) return null;
            if (TryFindLocal(entity, out var local))
            {
                if (local is ClientPrediction prediction)
                {
                    if (prediction.State == PredictionState.Queued || prediction.State == PredictionState.Sent)
                    {
                        // Not adopted yet: it moves here at once, and its request, or a change after it, carries the move.
                        var applied = inner.SetTransform(entity, transform);
                        if (!applied.Succeeded) return applied;
                        prediction.Pose = ToNetPose(transform);
                        if (prediction.State == PredictionState.Sent) QueueTransform(AuthorityTarget.Provisional(prediction.Provisional), transform);
                        return applied;
                    }

                    QueueTransform(prediction.Bound.IsValid ? AuthorityTarget.Entity(prediction.Bound) : AuthorityTarget.Provisional(prediction.Provisional), transform);
                    return OperationResult<TransformState>.Success(transform);
                }

                // A shadow is the host's script's object: that script moves it there; here it moves only while it waits.
                if (!scripted) return Refuse<TransformState>(NotYoursToMove);
                return local.Bound.IsValid ? OperationResult<TransformState>.Success(transform) : inner.SetTransform(entity, transform);
            }

            if (!TryGetMirrored(entity, out _)) return null;
            return scripted ? OperationResult<TransformState>.Success(transform) : Refuse<TransformState>(NotYoursToMove);
        }

        /// <summary>
        /// Routes a destroy from code acting on the player's intent, or scripted when <paramref name="scripted"/>; null when the
        /// entity is none of the network world's.
        /// </summary>
        internal OperationResult<bool>? RouteDestroy(IEntity entity, bool scripted)
        {
            if (!IsActive || entity == null) return null;
            if (TryFindLocal(entity, out var local))
            {
                if (local is ClientPrediction prediction)
                {
                    if (prediction.State == PredictionState.Queued || prediction.State == PredictionState.Sent)
                    {
                        // Not adopted yet: it goes here at once, and the host forgets or destroys what it made of it.
                        if (prediction.State == PredictionState.Sent) QueueDestroy(AuthorityTarget.Provisional(prediction.Provisional));
                        counters.PredictionsEnded++;
                        Settle(prediction, PredictionEvent.Ended);
                        ForgetPrediction(prediction);
                        prediction.End();
                        return OperationResult<bool>.Success(true);
                    }

                    QueueDestroy(prediction.Bound.IsValid && entities.ContainsKey(prediction.Bound.Value)
                        ? AuthorityTarget.Entity(prediction.Bound)
                        : AuthorityTarget.Provisional(prediction.Provisional));
                    return OperationResult<bool>.Success(true);
                }

                if (!scripted) return Refuse<bool>(NotYoursToDestroy);
                if (!local.Bound.IsValid) EndShadow((ClientShadow)local, ShadowEvent.Ended);
                return OperationResult<bool>.Success(true);
            }

            if (!TryGetMirrored(entity, out _)) return null;
            return scripted ? OperationResult<bool>.Success(true) : Refuse<bool>(NotYoursToDestroy);
        }

        private OperationResult<T> Refuse<T>(string message) where T : notnull
        {
            counters.RequestsRefusedHere++;
            return OperationResult<T>.Failure(ModErrorCode.NotAuthoritative, message);
        }

        /// <summary>Gets whether <paramref name="entity"/> is one of this player's predictions or shadows that has not ended.</summary>
        internal bool IsLocal(IEntity entity) => entity != null && TryFindLocal(entity, out _);

        private bool TryFindLocal(IEntity entity, out LocalSpawn local)
        {
            local = null!;
            if (localByEntity.Count == 0) return false;
            string key;
            try
            {
                key = entity.Id;
            }
            catch (Exception)
            {
                return false;
            }

            return !string.IsNullOrEmpty(key) && localByEntity.TryGetValue(key, out local!) && !local.IsEnded;
        }

        /// <summary>Whether <paramref name="entity"/> is, or is part of, an entity this player mirrors from its host.</summary>
        private bool TryGetMirrored(IEntity entity, out ClientWorldEntity mirrored)
        {
            mirrored = null!;
            NetworkEntityId id;
            try
            {
                if (!backend.TryGetNetworkId(entity, out id)) return false;
            }
            catch (Exception)
            {
                return false;
            }

            return entities.TryGetValue(id.Value, out mirrored!);
        }

        private void QueueTransform(AuthorityTarget target, TransformState transform)
        {
            if (!transforms.ContainsKey(target)) transformOrder.Add(target);
            transforms[target] = (ToNetScale(transform.Scale), ToNetPose(transform));
            PumpChanges(clock());
        }

        private void QueueDestroy(AuthorityTarget target)
        {
            // A destroy outdates every move of its target that has not left yet.
            if (transforms.Remove(target)) transformOrder.Remove(target);
            if (!destroyQueue.Contains(target)) destroyQueue.Add(target);
            PumpChanges(clock());
        }

        /// <summary>Sends what the budgets allow of the destroys and transform changes that wait, oldest first.</summary>
        private void PumpChanges(long now)
        {
            if (destroyQueue.Count > 0)
            {
                var pace = destroyPace ??= NewPace(NetRequestKind.Destroy, now);
                while (destroyQueue.Count > 0 && pace.MicrosUntilAvailable(now) == 0)
                {
                    AuthorityWire.WriteDestroyRequest(authorityMessage, destroyQueue[0]);
                    if (!channel.TrySendToHost(authorityMessage)) break;
                    pace.TryTake(now);
                    destroyQueue.RemoveAt(0);
                    counters.AuthorityRequestsSent++;
                }
            }

            if (transformOrder.Count > 0)
            {
                var pace = transformPace ??= NewPace(NetRequestKind.SetTransform, now);
                while (transformOrder.Count > 0 && pace.MicrosUntilAvailable(now) == 0)
                {
                    var target = transformOrder[0];
                    var change = transforms[target];
                    AuthorityWire.WriteTransformRequest(authorityMessage, target, change.Scale, change.Pose);
                    if (!channel.TrySendToHost(authorityMessage)) break;
                    pace.TryTake(now);
                    transformOrder.RemoveAt(0);
                    transforms.Remove(target);
                    counters.AuthorityRequestsSent++;
                }
            }
        }

        /// <summary>This player's world went: changes that wait are dropped with it, and the budgets start again.</summary>
        private void ClearChanges()
        {
            transforms.Clear();
            transformOrder.Clear();
            destroyQueue.Clear();
            transformPace = null;
            destroyPace = null;
        }
    }
}
