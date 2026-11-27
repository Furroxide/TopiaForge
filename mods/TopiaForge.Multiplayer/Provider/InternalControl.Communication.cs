using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer
{
    internal sealed partial class InternalControl : IInternalCommunicationControl, IInternalCommunicationActivityControl
    {
        public bool IsVoiceActive(ParticipantId sender) => engine.IsVoiceActive(sender);
        public bool HasVoiceActivity(ParticipantId sender, ParticipantId listener) => engine.HasVoiceActivity(sender, listener);
        public void ClearVoiceActivity() => engine.ClearVoiceActivity();

        public bool HasCommunicationRouting => engine.HasCommunicationRouting;

        public OperationResult<ICommunicationRouting> RouteCommunications(IModLifetime lifetime, CommunicationPolicy policy) =>
            engine.RouteCommunications(lifetime, policy);

        public bool CanCommunicate(ParticipantId sender, ParticipantId listener) => engine.CanCommunicate(sender, listener);
    }
}
