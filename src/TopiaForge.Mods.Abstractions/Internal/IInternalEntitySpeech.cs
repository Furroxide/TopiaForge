using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Internal
{
    // Trusted runtime/provider seam. No native objects or delegates have a wire representation.
    internal interface IInternalEntitySpeech
    {
        Task<OperationResult<bool>> SayAsync(IEntity entity, string kind, string text, CancellationToken cancellationToken);
        bool IsCurrentSpeechOwner(IEntity entity, string kind);
        CancellationToken SpeechStoppingToken { get; }
    }

    internal interface IInternalSpeechAgent
    {
        Task<OperationResult<bool>> SayAsync(string text, CancellationToken cancellationToken);
    }

    internal interface IInternalSpeechProvider
    {
        bool IsSpeechVisible(IEntity entity);
        OperationResult<string> VoiceIdentity(IEntity entity);
        Task<OperationResult<bool>> PlaySpeechAsync(IEntity entity, string voice, string text, CancellationToken cancellationToken);
    }
}
