using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class RuntimeReplication : IInternalEntityCommands
    {
        // Non-recorded profiles still own local bodies, but must never enter the shared ledger or client routing.
        private List<(IEntity Entity, string Kind)>? localCommandTargets;

        private void TrackLocalCommandTarget(IEntity entity, string kind)
        {
            if (!TrackRelease()) return;
            var targets = localCommandTargets ??= new List<(IEntity, string)>();
            targets.RemoveAll(entry => !entry.Entity.IsAlive);
            targets.Add((entity, kind));
        }

        public OperationResult<bool> SubmitEntityCommand(IEntity target, string kind, byte[] payload)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var refused = Admit<bool>() ?? CheckKind<bool>(kind, "entity command");
            if (refused != null) return refused;
            if (payload.Length > InternalReplicationLimits.MaximumPayloadBytes)
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "An entity command is at most 16 KiB.");
            if (services.Runtime.Realm == NetRealm.Client)
            {
                if (services.Authority is NetworkWorld world)
                    return world.Client.SubmitEntityCommand(this, target, kind, payload);
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, HostNotReady);
            }
            if (!MultiplayerSpawnLedger.Records(Profile)) return ExecuteLocalEntityCommand(target, kind, payload);
            var record = services.Runtime.Spawns.Find(target);
            if (record == null || !string.Equals(record.Description.OwnerModId, OwnerModId, StringComparison.OrdinalIgnoreCase))
                return OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative, "This context does not own the entity.");
            return services.ExecuteEntityCommand(record, kind, payload);
        }

        private OperationResult<bool> ExecuteLocalEntityCommand(IEntity target, string kind, byte[] payload)
        {
            localCommandTargets?.RemoveAll(entry => !entry.Entity.IsAlive);
            var owned = false;
            if (localCommandTargets != null)
                foreach (var entry in localCommandTargets)
                    if (ReferenceEquals(entry.Entity, target) && entry.Kind == kind) { owned = true; break; }
            if (!owned)
                return OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative, "This context does not own the entity.");
            if (!services.TryGetReplicaFactory(kind, out var factory) || !(factory is IInternalReplicaCommands commands))
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The entity command is unavailable.");
            try
            {
                return commands.Execute(target, payload, reference =>
                    services.TryResolveRef(reference, out var resolved) ? resolved : null);
            }
            catch (Exception)
            {
                return OperationResult<bool>.Failure(ModErrorCode.External, "The entity command could not be applied.");
            }
            finally { localCommandTargets?.RemoveAll(entry => !entry.Entity.IsAlive); }
        }
    }

    internal sealed partial class MultiplayerReplication
    {
        internal OperationResult<bool> ExecuteEntityCommand(MultiplayerSpawnRecord record, string kind, byte[] payload)
        {
            if (!record.IsLive || record.Description.Kind != kind ||
                !TryGetReplicaFactory(kind, out var factory) || !(factory is IInternalReplicaCommands commands))
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The entity command is unavailable.");
            try
            {
                var result = commands.Execute(record.Entity, payload, reference =>
                    TryResolveRef(reference, out var resolved) ? resolved : null);
                if (result.Succeeded && record.IsLive)
                {
                    var descriptor = commands.Describe(record.Entity);
                    var previous = record.Description;
                    if (previous.Descriptor is InternalSpawnDescriptor before && before.Payload.Span.SequenceEqual(descriptor.Payload.Span)) return result;
                    if (descriptor.Kind != kind) throw new InvalidOperationException("A command cannot change entity kind.");
                    Runtime.Spawns.Record(record.Entity, new MultiplayerSpawnDescription(previous.OwnerModId,
                        previous.Profile, previous.Source, kind, previous.ContentKey, descriptor.Transform, descriptor),
                        record.Owner, record.ShadowKey, record.OwnerSlot);
                }
                return result;
            }
            catch (Exception)
            {
                return OperationResult<bool>.Failure(ModErrorCode.External, "The entity command could not be applied.");
            }
        }
    }
}
