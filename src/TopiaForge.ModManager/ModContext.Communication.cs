using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext : IInternalCommunicationContext
    {
        OperationResult<ICommunicationRouting> IInternalCommunicationContext.RouteCommunications(CommunicationPolicy policy)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (Lifetime.IsStopping)
                return OperationResult<ICommunicationRouting>.Failure(ModErrorCode.Cancelled, "The context is stopping.");
            if (NetProfile.Context != ModExecutionContext.GamemodeServer || replication.Services.Runtime.Realm == NetRealm.Client)
                return OperationResult<ICommunicationRouting>.Failure(ModErrorCode.NotAuthoritative, "Only a gamemode server may route communications.");
            return replication.Services.Runtime.Control is IInternalCommunicationControl control
                ? control.RouteCommunications(Lifetime, policy)
                : OperationResult<ICommunicationRouting>.Failure(ModErrorCode.Unavailable, "The multiplayer provider is unavailable.");
        }
    }
}
