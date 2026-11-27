using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// A joined player's predictions (WS3.7): a spawn on the player's intent is made here, frozen, and asked of the host
    /// under a provisional id. The host's verdict adopts it, and the instance then stands for the host's entity in place
    /// of a replica, under the same handle; or refuses it, and the instance ends with a toast. Requests keep to an honest
    /// player's pace (<see cref="SpawnAuthorityRules.ClientPace"/>), go again when the stream restarts, and a prediction
    /// whose verdict never comes ends after <see cref="SpawnAuthorityRules.PredictionTimeoutMicros"/>. When the player's mod
    /// disposes an object of its own, the host is asked to destroy it.
    /// </summary>
    internal sealed partial class NetworkWorldClient
    {
        private readonly Dictionary<uint, ClientPrediction> predictions = new Dictionary<uint, ClientPrediction>();
        private readonly Dictionary<uint, ClientPrediction> adoptions = new Dictionary<uint, ClientPrediction>();
        private readonly Dictionary<string, LocalSpawn> localByEntity = new Dictionary<string, LocalSpawn>(StringComparer.Ordinal);
        private readonly List<ClientPrediction> spawnQueue = new List<ClientPrediction>();
        private readonly List<ClientPrediction> owned = new List<ClientPrediction>();
        private readonly List<ClientPrediction> predictionScratch = new List<ClientPrediction>();
        private readonly WorldWireWriter authorityMessage = new WorldWireWriter();
        private TokenBucket? spawnPace;
        private uint nextProvisional;
        private long lastRefusalToast = long.MinValue;

        /// <summary>Gets how many predictions wait for the host's verdict.</summary>
        internal int PendingPredictions { get; private set; }

        /// <summary>Gets how many of this player's adopted objects stand here under their mod's handles.</summary>
        internal int OwnedObjects => owned.Count;

        /// <summary>
        /// Says whether this player may predict a spawn now, before its mod's spawn runs here: not past the host's quota of
        /// live objects, counting those still waiting for a verdict.
        /// </summary>
        internal SpawnRefusal CheckPrediction()
        {
            if (!IsActive) return SpawnRefusal.HostBusy;
            return owned.Count + PendingPredictions >= SpawnAuthorityRules.MaximumPendingSpawns ? SpawnRefusal.QuotaExceeded : SpawnRefusal.None;
        }

        /// <summary>
        /// Takes a spawn this player's mod made here for its player as a prediction: freezes it and asks the host for it.
        /// <paramref name="ownedInstance"/> ends an asset spawn, and <paramref name="handler"/> a provider's.
        /// </summary>
        internal ClientPrediction Predict(
            string modId,
            string kind,
            byte[] description,
            IEntity entity,
            IDisposable? ownedInstance,
            Mods.Internal.IInternalSpawnHandler? handler,
            IntentSource source,
            bool fromInputFrame,
            TransformState transform,
            IUiService? ui)
        {
            var provisional = NextProvisional();
            var prediction = new ClientPrediction(provisional, modId, kind, description, entity, ownedInstance, handler, source, fromInputFrame,
                ToNetScale(transform.Scale), ToNetPose(transform), ui);
            Freeze(prediction);
            predictions[provisional] = prediction;
            localByEntity[prediction.EntityKey] = prediction;
            PendingPredictions++;
            spawnQueue.Add(prediction);
            counters.PredictionsMade++;
            PumpSpawns(clock());
            return prediction;
        }

        private uint NextProvisional()
        {
            do
            {
                nextProvisional = nextProvisional == SpawnAuthorityRules.MaximumProvisionalId ? 1 : nextProvisional + 1;
            }
            while (predictions.ContainsKey(nextProvisional));
            return nextProvisional;
        }

        /// <summary>Keeps a local instance where its mod put it: kinematic, unmarked, moved by nothing but its mod until it binds.</summary>
        private void Freeze(LocalSpawn local)
        {
            local.Object = Adopt(local.Entity);
            if (local.Object == null) return;
            try
            {
                backend.PrepareReplica(local.Object);
            }
            catch (Exception)
            {
                counters.MarkFailures++;
            }
        }

        /// <summary>The host adopted the spawn of <paramref name="provisional"/> as <paramref name="id"/>: its instance stands for it.</summary>
        private void Adopted(uint provisional, NetworkEntityId id)
        {
            if (!predictions.TryGetValue(provisional, out var prediction) ||
                (prediction.State != PredictionState.Queued && prediction.State != PredictionState.Sent) || adoptions.ContainsKey(id.Value))
            {
                counters.VerdictsIgnored++;
                return;
            }

            if (entities.TryGetValue(id.Value, out var mirrored))
            {
                // The entity arrived first, as when a baseline listed it before the verdict: its replica gives way.
                if (mirrored.Source != WorldEntrySource.SdkSpawn || mirrored.Local != null || !prediction.Matches(mirrored.Kind, mirrored.Payload))
                {
                    counters.VerdictsIgnored++;
                    return;
                }

                Settle(prediction, PredictionEvent.Adopted);
                Release(mirrored);
                BindLocal(mirrored, prediction, ClientEntityBinding.Own);
            }
            else
            {
                Settle(prediction, PredictionEvent.Adopted);
                prediction.Bound = id;
                prediction.AdoptedInBaseline = counters.BaselinesReceived;
                adoptions[id.Value] = prediction;
            }

            counters.PredictionsAdopted++;
        }

        /// <summary>The host refused the spawn of <paramref name="provisional"/>: its instance ends, and the player is told.</summary>
        private void Refused(uint provisional, SpawnRefusal reason)
        {
            if (!predictions.TryGetValue(provisional, out var prediction) ||
                (prediction.State != PredictionState.Queued && prediction.State != PredictionState.Sent))
            {
                counters.VerdictsIgnored++;
                return;
            }

            counters.PredictionsRefused++;
            EndPrediction(prediction, PredictionEvent.Refused, reason);
        }

        /// <summary>Moves a prediction's state, keeping the count of those that wait for a verdict.</summary>
        private void Settle(ClientPrediction prediction, PredictionEvent happened)
        {
            var waited = prediction.State == PredictionState.Queued || prediction.State == PredictionState.Sent;
            prediction.Apply(happened, clock());
            var waits = prediction.State == PredictionState.Queued || prediction.State == PredictionState.Sent;
            if (waited && !waits) PendingPredictions--;
        }

        /// <summary>Ends a prediction here: its instance goes, and a refusal or a timeout is shown as a toast.</summary>
        private void EndPrediction(ClientPrediction prediction, PredictionEvent happened, SpawnRefusal shown)
        {
            Settle(prediction, happened);
            Settle(prediction, PredictionEvent.Ended);
            ForgetPrediction(prediction);
            prediction.End();
            if (shown != SpawnRefusal.None) Toast(prediction, shown);
        }

        private void ForgetPrediction(ClientPrediction prediction)
        {
            if (predictions.TryGetValue(prediction.Provisional, out var current) && ReferenceEquals(current, prediction)) predictions.Remove(prediction.Provisional);
            if (localByEntity.TryGetValue(prediction.EntityKey, out var local) && ReferenceEquals(local, prediction)) localByEntity.Remove(prediction.EntityKey);
            spawnQueue.Remove(prediction);
            owned.Remove(prediction);
            if (prediction.Bound.IsValid && adoptions.TryGetValue(prediction.Bound.Value, out var waiting) && ReferenceEquals(waiting, prediction))
            {
                adoptions.Remove(prediction.Bound.Value);
            }
        }

        /// <summary>
        /// Shows a refusal through the context's UI service, which renders through the kit's toasts. Refusals closer together
        /// than <see cref="SpawnAuthorityRules.RefusalToastIntervalMicros"/> are counted, not shown, so a burst of them is one toast.
        /// </summary>
        private void Toast(ClientPrediction prediction, SpawnRefusal reason)
        {
            var now = clock();
            if (prediction.Ui == null || (lastRefusalToast != long.MinValue && now - lastRefusalToast < SpawnAuthorityRules.RefusalToastIntervalMicros)) return;
            lastRefusalToast = now;
            try
            {
                if (prediction.Ui.ShowToast(SpawnAuthorityRules.RefusalText(reason), UiTone.Warning).Succeeded) counters.RefusalToasts++;
            }
            catch (Exception)
            {
                // A context that stopped meanwhile shows nothing; the instance ended regardless.
            }
        }

        /// <summary>Binds this player's own instance to the entity it stands for, in place of a replica.</summary>
        private void BindLocal(ClientWorldEntity mirrored, LocalSpawn local, ClientEntityBinding binding)
        {
            local.Bound = mirrored.Id;
            mirrored.Local = local;
            mirrored.Unbuildable = false;
            var present = local.Object ?? Adopt(local.Entity);
            local.Object = present;
            if (present != null) Attach(mirrored, present, binding);
            else mirrored.Binding = binding;
            ApplyBoundProvider(mirrored, local.Entity);
            if (local is ClientPrediction prediction && !owned.Contains(prediction)) owned.Add(prediction);
        }

        /// <summary>
        /// Build's first step for an SDK spawn: this player's adopted prediction, or its waiting shadow with the entity's key,
        /// stands for the entity in place of a replica. A shadow made for another asset ends, and the replica is built.
        /// </summary>
        private bool TryBindLocal(ClientWorldEntity mirrored)
        {
            if (mirrored.Source != WorldEntrySource.SdkSpawn) return false;
            if (adoptions.TryGetValue(mirrored.Id.Value, out var prediction))
            {
                adoptions.Remove(mirrored.Id.Value);
                if (prediction.IsEnded || !prediction.IsAlive || !prediction.Matches(mirrored.Kind, mirrored.Payload)) return false;
                BindLocal(mirrored, prediction, ClientEntityBinding.Own);
                return true;
            }

            return TryBindShadow(mirrored);
        }

        /// <summary>Whether an entity this player's own instance stands for keeps it when the host describes it again.</summary>
        private static bool KeepsLocalBinding(ClientWorldEntity known, WorldEntry update) =>
            known.Local != null && known.Source == update.Source && known.Local.Matches(update.Kind, update.Payload);

        /// <summary>Ends the instance that stood for an entity that ended here: its mod's handle dies with it.</summary>
        private void EndLocal(LocalSpawn? local)
        {
            switch (local)
            {
                case ClientPrediction prediction:
                    Settle(prediction, PredictionEvent.Ended);
                    ForgetPrediction(prediction);
                    prediction.End();
                    break;
                case ClientShadow shadow:
                    EndShadow(shadow, ShadowEvent.Ended);
                    break;
            }
        }

        /// <summary>Sends what the spawn budget allows of the requests that wait, oldest first; a full window waits for the next frame.</summary>
        private void PumpSpawns(long now)
        {
            if (spawnQueue.Count == 0) return;
            var pace = spawnPace ??= NewPace(NetRequestKind.Spawn, now);
            while (spawnQueue.Count > 0 && pace.MicrosUntilAvailable(now) == 0)
            {
                var prediction = spawnQueue[0];
                if (prediction.State != PredictionState.Queued)
                {
                    spawnQueue.RemoveAt(0);
                    continue;
                }

                AuthorityWire.WriteSpawnRequest(authorityMessage, prediction.Provisional, prediction.ModId, prediction.Kind, prediction.Source,
                    prediction.FromInputFrame, prediction.Scale, prediction.Pose, prediction.Description);
                if (!channel.TrySendToHost(authorityMessage)) return;
                pace.TryTake(now);
                spawnQueue.RemoveAt(0);
                Settle(prediction, PredictionEvent.Sent);
                counters.AuthorityRequestsSent++;
            }
        }

        /// <summary>
        /// The prediction half of a frame: what waits is sent, a verdict that never came ends its prediction, and an object of
        /// this player's that its mod disposed is asked of the host to destroy. Nothing here runs, or allocates, while this
        /// player has no prediction.
        /// </summary>
        private void PumpPredictions(long now)
        {
            PumpSpawns(now);
            if (predictions.Count == 0) return;
            predictionScratch.Clear();
            foreach (var prediction in predictions.Values)
            {
                if (!prediction.IsAlive || TimedOut(prediction, now)) predictionScratch.Add(prediction);
            }

            foreach (var prediction in predictionScratch)
            {
                if (prediction.IsAlive && prediction.State == PredictionState.Adopted)
                {
                    // Adopted, but its entity never arrived: the host ended it, and the despawn passed this player by.
                    AdoptionEnded(prediction.Bound.Value);
                    continue;
                }

                if (prediction.IsAlive)
                {
                    // The host may make it yet, its verdict lost: it is asked to end what it makes of it, so nothing of this
                    // player's stands there that this player no longer holds.
                    counters.PredictionsTimedOut++;
                    if (prediction.State == PredictionState.Sent) QueueDestroy(AuthorityTarget.Provisional(prediction.Provisional));
                    EndPrediction(prediction, PredictionEvent.TimedOut, SpawnRefusal.TimedOut);
                    continue;
                }

                // Its mod disposed it, as it may any object of its own: the host is asked to destroy what it made of it.
                if (prediction.State == PredictionState.Adopted)
                {
                    QueueDestroy(prediction.Bound.IsValid && entities.ContainsKey(prediction.Bound.Value)
                        ? AuthorityTarget.Entity(prediction.Bound)
                        : AuthorityTarget.Provisional(prediction.Provisional));
                    counters.DisposalsForwarded++;
                }
                else if (prediction.State == PredictionState.Sent)
                {
                    QueueDestroy(AuthorityTarget.Provisional(prediction.Provisional));
                    counters.PredictionsEnded++;
                }
                else
                {
                    counters.PredictionsEnded++;
                }

                Settle(prediction, PredictionEvent.Ended);
                ForgetPrediction(prediction);
                prediction.End();
            }

            predictionScratch.Clear();
        }

        /// <summary>Whether a prediction waited too long: for its verdict, or, once adopted, for its entity's entry.</summary>
        private bool TimedOut(ClientPrediction prediction, long now)
        {
            if (prediction.State == PredictionState.Sent) return now - prediction.SentMicros > SpawnAuthorityRules.PredictionTimeoutMicros;
            return prediction.State == PredictionState.Adopted && adoptions.ContainsKey(prediction.Bound.Value) &&
                   now - prediction.AdoptedMicros > SpawnAuthorityRules.PredictionTimeoutMicros;
        }

        /// <summary>The stream restarted: requests in flight are lost, so every one still waiting goes again, oldest first.</summary>
        private void PredictionsRestarted()
        {
            if (predictions.Count == 0) return;
            predictionScratch.Clear();
            foreach (var prediction in predictions.Values)
            {
                if (prediction.State == PredictionState.Sent) predictionScratch.Add(prediction);
            }

            predictionScratch.Sort((left, right) => left.Provisional.CompareTo(right.Provisional));
            foreach (var prediction in predictionScratch)
            {
                prediction.Apply(PredictionEvent.StreamRestarted, clock());
                if (!spawnQueue.Contains(prediction)) spawnQueue.Add(prediction);
            }

            predictionScratch.Clear();
            spawnQueue.Sort((left, right) => left.Provisional.CompareTo(right.Provisional));
        }

        /// <summary>
        /// This player's world went: every prediction ends. One adopted by another world of the host ends too, while one that
        /// still waits is asked again of the world there is now, when <paramref name="keepWaiting"/>.
        /// </summary>
        private void EndPredictions(bool keepWaiting)
        {
            predictionScratch.Clear();
            foreach (var prediction in predictions.Values)
            {
                if (keepWaiting && (prediction.State == PredictionState.Queued || prediction.State == PredictionState.Sent)) continue;
                predictionScratch.Add(prediction);
            }

            foreach (var prediction in predictionScratch)
            {
                counters.PredictionsEnded++;
                Settle(prediction, PredictionEvent.Ended);
                ForgetPrediction(prediction);
                prediction.End();
            }

            predictionScratch.Clear();
            adoptions.Clear();
            owned.Clear();
            if (keepWaiting)
            {
                PredictionsRestarted();
                return;
            }

            spawnQueue.Clear();
            PendingPredictions = 0;
            spawnPace = null;
        }

        /// <summary>
        /// A despawn, or a baseline that no longer lists it, names an entity this player was adopted into before its entry
        /// arrived: the host no longer has it, so the prediction ends.
        /// </summary>
        private void AdoptionEnded(uint id)
        {
            if (!adoptions.TryGetValue(id, out var prediction)) return;
            adoptions.Remove(id);
            counters.PredictionsEnded++;
            Settle(prediction, PredictionEvent.Ended);
            ForgetPrediction(prediction);
            prediction.End();
        }

        /// <summary>
        /// A baseline ended without listing entities this player was adopted into before it began: the host has none of them
        /// now. One adopted since the baseline began is not its to list, and waits for its announcement.
        /// </summary>
        private void EndUnlistedAdoptions()
        {
            if (adoptions.Count == 0) return;
            predictionScratch.Clear();
            foreach (var pair in adoptions)
            {
                if (!entities.ContainsKey(pair.Key) && pair.Value.AdoptedInBaseline < counters.BaselinesReceived) predictionScratch.Add(pair.Value);
            }

            foreach (var prediction in predictionScratch) AdoptionEnded(prediction.Bound.Value);
            predictionScratch.Clear();
        }

        private static TokenBucket NewPace(NetRequestKind kind, long now)
        {
            var limit = SpawnAuthorityRules.ClientPace(kind);
            return new TokenBucket(limit.Burst, limit.PerSecond, Math.Max(0, now));
        }

        private static NetVector3 ToNetScale(Vec3 scale) => new NetVector3(scale.X, scale.Y, scale.Z);

        private static NetPose ToNetPose(TransformState transform) =>
            new NetPose(
                new NetVector3(transform.Position.X, transform.Position.Y, transform.Position.Z),
                new NetQuaternion(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W));
    }
}
