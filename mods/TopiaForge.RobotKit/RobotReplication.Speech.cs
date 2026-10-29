using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    // Implemented only by native bodies, never exposed as a mod service.
    internal interface IRobotNativeSpeech
    {
        bool SpeechVisible { get; }
        OperationResult<string> VoiceIdentity();
        Task<OperationResult<bool>> PlaySpeechAsync(string voice, string text, CancellationToken token);
        void BindSpeech(Func<string, CancellationToken, Task<OperationResult<bool>>> route);
    }

    internal sealed partial class RobotReplication : IInternalSpeechProvider
    {
        private void BindSpeech(IRobotAgent agent, IRuntimeReplication replication)
        {
            if (agent is IRobotNativeSpeech native)
                native.BindSpeech((text, token) => replication is IInternalEntitySpeech speech
                    ? speech.SayAsync(agent, RobotReplicationWire.Kind, text, token)
                    : Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Robot speech routing is unavailable.")));
        }

        private IRobotNativeSpeech? Speech(IEntity entity) => states.TryGetValue(entity.Id, out var state)
            && ReferenceEquals(state.Agent, entity) && state.Agent.IsAlive ? state.Agent as IRobotNativeSpeech : null;

        public bool IsSpeechVisible(IEntity entity) => Speech(entity)?.SpeechVisible == true;
        public OperationResult<string> VoiceIdentity(IEntity entity) => Speech(entity)?.VoiceIdentity()
            ?? OperationResult<string>.Failure(ModErrorCode.Unavailable, "Robot voice bindings are unavailable; retry after the provider recovers.");
        public Task<OperationResult<bool>> PlaySpeechAsync(IEntity entity, string voice, string text, CancellationToken cancellationToken) =>
            Speech(entity)?.PlaySpeechAsync(voice, text, cancellationToken)
            ?? Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Robot speech provider is unavailable."));
    }
}
