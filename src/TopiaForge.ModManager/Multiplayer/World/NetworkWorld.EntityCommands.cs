using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class NetworkWorldClient
    {
        internal const int MaximumEntityCommands = 128;
        private readonly List<PendingEntityCommand> entityCommands = new List<PendingEntityCommand>();

        private bool TryUpdateProvider(ClientWorldEntity known, WorldEntry update)
        {
            if (known.Kind != update.Kind || known.Source != update.Source || known.Object == null ||
                factories(update.Kind) is not IInternalReplicaCommands provider) return false;
            try
            {
                return provider.ApplyReplica(known.Object.Entity, new InternalSpawnDescriptor(update.Kind,
                    ToTransform(update.Pose, update.Scale), update.Payload)).Succeeded;
            }
            catch (Exception) { return false; }
        }

        private void ApplyBoundProvider(ClientWorldEntity mirrored, IEntity entity)
        {
            try
            {
                if (factories(mirrored.Kind) is IInternalReplicaCommands provider)
                    provider.ApplyReplica(entity, new InternalSpawnDescriptor(mirrored.Kind,
                        ToTransform(mirrored.EntryPose, mirrored.Scale), mirrored.Payload));
            }
            catch (Exception) { counters.MarkFailures++; }
        }

        private void ResetEntityCommands()
        {
            entityCommands.Clear();
            foreach (var local in localByEntity.Values)
            {
                try
                {
                    if (factories(local.Kind) is IInternalReplicaCommandLifetime provider)
                        provider.ResetPendingCommands(local.Entity);
                }
                catch (Exception) { counters.MarkFailures++; }
            }
        }

        // Wait for adoption before edits can change the payload used to match the predicted body.
        internal OperationResult<bool> SubmitEntityCommand(RuntimeReplication context, IEntity entity, string kind, byte[] payload)
        {
            if (!IsActive) return OperationResult<bool>.Failure(ModErrorCode.Unavailable, RuntimeReplication.HostNotReady);
            if (!TryFindLocal(entity, out var local) || local is not ClientPrediction prediction)
                return context.RouteCurrentIntent() == InternalRoute.Shadow
                    ? OperationResult<bool>.Success(false)
                    : Refuse<bool>("Only the player who made a robot can command it.");
            if (!string.Equals(local.ModId, context.OwnerModId, StringComparison.OrdinalIgnoreCase))
                return Refuse<bool>("This context does not own the entity.");
            if (!prediction.IsAlive) return Refuse<bool>("The robot is no longer available.");
            if (entityCommands.Count >= MaximumEntityCommands)
                return OperationResult<bool>.Failure(ModErrorCode.RateLimited, "Too many robot commands are waiting; try again shortly.");
            entityCommands.Add(new PendingEntityCommand(context, prediction, kind, (byte[])payload.Clone()));
            PumpChanges(clock());
            return OperationResult<bool>.Success(true);
        }

        private void PumpEntityCommands(long now)
        {
            for (var index = 0; index < entityCommands.Count;)
            {
                var command = entityCommands[index];
                var prediction = command.Prediction;
                if (command.Context.IsStopping || !prediction.IsAlive ||
                    prediction.State == PredictionState.Ended || prediction.State == PredictionState.Refused)
                { entityCommands.RemoveAt(index); continue; }
                if (prediction.State != PredictionState.Adopted || !prediction.Bound.IsValid ||
                    !entities.TryGetValue(prediction.Bound.Value, out var mirrored) || !ReferenceEquals(mirrored.Local, prediction))
                { index++; continue; }
                var pace = transformPace ??= NewPace(NetRequestKind.SetTransform, now);
                if (pace.MicrosUntilAvailable(now) != 0) return;
                EntityCommandWire.Write(authorityMessage, AuthorityTarget.Entity(prediction.Bound),
                    command.Context.OwnerModId, command.Kind, command.Payload);
                if (!channel.TrySendToHost(authorityMessage)) return;
                pace.TryTake(now);
                entityCommands.RemoveAt(index);
                counters.AuthorityRequestsSent++;
            }
        }

        private sealed class PendingEntityCommand
        {
            internal PendingEntityCommand(RuntimeReplication context, ClientPrediction prediction, string kind, byte[] payload)
            { Context = context; Prediction = prediction; Kind = kind; Payload = payload; }
            internal RuntimeReplication Context { get; }
            internal ClientPrediction Prediction { get; }
            internal string Kind { get; }
            internal byte[] Payload { get; }
        }
    }

    internal sealed partial class NetworkWorldProxies
    {
        private bool EntityCommand(ParticipantId sender, WorldWireReader reader)
        {
            if (!EntityCommandWire.Read(reader, out var target, out var mod, out var kind, out var payload)) return Malformed();
            if (!byParticipant.TryGetValue(sender, out var proxy)) return true;
            var found = TryResolve(proxy, target, out var spawn, out _, out var relation);
            // Charge invalid requests too. Sharing the transform budget bounds all mutating traffic together.
            var allowed = limiter.Evaluate(proxy.Slot, false, NetRequestKind.SetTransform, relation, 0, clock());
            if (!found || allowed != NetPermission.Allowed || relation != NetTargetRelation.Self ||
                spawn?.Record == null || !IsShared(mod) || !string.Equals(spawn.ModId, mod, StringComparison.OrdinalIgnoreCase))
            { counters.ChangesRefused++; return true; }
            using (intents.EnterForwarded(mod, IntentSource.OwnerCommand, sender.Value, false))
            {
                if (!replication.ExecuteEntityCommand(spawn.Record, kind, payload).Succeeded) counters.ChangesRefused++;
            }
            return true;
        }
    }
}
