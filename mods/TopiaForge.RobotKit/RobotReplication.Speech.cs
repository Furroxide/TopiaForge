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
        void BindSpeechPlayback(object association, Func<bool> isCurrent, CancellationToken ownerStopping = default);
    }

    internal sealed partial class RobotReplication : IInternalSpeechProvider
    {
        private void BindSpeech(IRobotAgent agent, IRuntimeReplication replication)
        {
            if (agent is IRobotNativeSpeech native)
            {
                BindPlayback(states[agent.Id], replication);
                native.BindSpeech((text, token) => replication is IInternalEntitySpeech speech
                    ? speech.SayAsync(agent, RobotReplicationWire.Kind, text, token)
                    : Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Robot speech routing is unavailable.")));
            }
        }

        private void BindPlayback(State state, IRuntimeReplication? consumer)
        {
            var association = new object();
            state.SpeechAssociation = association;
            state.SpeechConsumer = consumer;
            var owner = consumer as IInternalEntitySpeech;
            var ownerStopping = owner?.SpeechStoppingToken ?? default;
            if (state.Agent is IRobotNativeSpeech native)
                native.BindSpeechPlayback(association, () => states.TryGetValue(state.Agent.Id, out var current)
                    && ReferenceEquals(current, state) && state.Agent.IsAlive
                    && ReferenceEquals(state.SpeechAssociation, association) && ReferenceEquals(state.SpeechConsumer, consumer)
                    && (consumer == null ? state.IsReplica : owner != null && !ownerStopping.IsCancellationRequested
                        && (consumer.Realm == InternalNetRealm.Client || owner.IsCurrentSpeechOwner(state.Agent, RobotReplicationWire.Kind))),
                    ownerStopping);
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
