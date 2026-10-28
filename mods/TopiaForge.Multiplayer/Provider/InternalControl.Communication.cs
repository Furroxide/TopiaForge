using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer
{
    internal sealed partial class InternalControl : IInternalCommunicationControl
    {
        public bool HasCommunicationRouting => engine.HasCommunicationRouting;

        public OperationResult<ICommunicationRouting> RouteCommunications(IModLifetime lifetime, CommunicationPolicy policy) =>
            engine.RouteCommunications(lifetime, policy);

        public bool CanCommunicate(ParticipantId sender, ParticipantId listener) => engine.CanCommunicate(sender, listener);
    }
}
