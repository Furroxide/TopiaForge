namespace TopiaForge.Mods.Internal
{
    /// <summary>Optional runtime/provider seam; the provider keeps policy and checks inside host routing.</summary>
    internal interface IInternalCommunicationControl
    {
        bool HasCommunicationRouting { get; }
        OperationResult<ICommunicationRouting> RouteCommunications(IModLifetime lifetime, CommunicationPolicy policy);
        bool CanCommunicate(ParticipantId sender, ParticipantId listener);
    }

    internal interface IInternalCommunicationContext
    {
        OperationResult<ICommunicationRouting> RouteCommunications(CommunicationPolicy policy);
    }

    /// <summary>Fresh host-validated presence only, never the public interpolated pose or a caller's claimed position.</summary>
    internal interface IInternalCommunicationPositionSource
    {
        bool TryGetCommunicationPosition(ParticipantId participant, out Vec3 position);
    }

    /// <summary>Presentation hint on a received stream; authorization already happened at the host relay.</summary>
    internal interface IInternalRoutedVoiceStream
    {
        bool NonSpatial { get; }
    }
}
