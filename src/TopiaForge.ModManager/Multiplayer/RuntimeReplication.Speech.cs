using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class RuntimeReplication : IInternalEntitySpeech
    {
        public Task<OperationResult<bool>> SayAsync(IEntity entity, string kind, string text, CancellationToken cancellationToken)
        {
            UnityMainThreadGuard.AssertCurrent();
            var refused = Admit<bool>() ?? CheckKind<bool>(kind, "speech");
            if (refused != null) return Task.FromResult(refused);
            if (Realm == InternalNetRealm.Client)
                return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative, "Only the owning host context may speak for a robot."));
            bool Owns()
            {
                if (lifetime.IsStopping || entity == null || !entity.IsAlive) return false;
                if (MultiplayerSpawnLedger.Records(Profile))
                {
                    var record = services.Runtime.Spawns.Find(entity);
                    return record != null && record.IsLive && ReferenceEquals(record.Owner, this)
                        && record.OwnerSlot == 0 && record.Description.Kind == kind;
                }
                if (localCommandTargets != null)
                    foreach (var entry in localCommandTargets)
                        if (ReferenceEquals(entry.Entity, entity) && entry.Kind == kind) return true;
                return false;
            }
            if (!Owns()) return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative,
                "This context does not own a current host robot."));
            return services.Authority is NetworkWorld world
                ? world.Speech.SayAsync(this, entity, kind, text, Owns, cancellationToken)
                : Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Robot speech delivery is unavailable; retry after the world is ready."));
        }
    }
}
