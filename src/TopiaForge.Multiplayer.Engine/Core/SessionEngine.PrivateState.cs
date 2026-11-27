using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal sealed partial class SessionEngine
    {
        internal const int MaximumPrivateFields = 64;
        internal const int MaximumPrivateValueBytes = 16 * 1024;
        private readonly Dictionary<WireKey, PerPlayerRecord> privateStates = new Dictionary<WireKey, PerPlayerRecord>();
        private readonly Dictionary<WireKey, StagedState> privateReceived = new Dictionary<WireKey, StagedState>();
        // The receive inventory may be ahead of the last Commit. Only privateReceived is adoptable.
        private readonly Dictionary<WireKey, StagedState> privateStaged = new Dictionary<WireKey, StagedState>();
        private bool privateChangesPending;
        private bool privateBaseline;

        internal OperationResult<IPerPlayerState<T>> RegisterPrivateState<T>(string owner, ReplicatedStateDefinition<T> definition) where T : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            RequireOwnerThread();
            var refusal = RegistrationRefusal<IPerPlayerState<T>>("Private state");
            if (refusal != null) return refusal;
            var key = new WireKey(owner, definition.Id);
            if (privateStates.ContainsKey(key) || states.ContainsKey(key))
                return OperationResult<IPerPlayerState<T>>.Failure(ModErrorCode.Conflict, "The state id is already registered.");
            if (privateStates.Count >= MaximumPrivateFields)
                return OperationResult<IPerPlayerState<T>>.Failure(ModErrorCode.RateLimited, "At most 64 private fields may be registered.");
            if (definition.Codec.MaximumEncodedBytes > MaximumPrivateValueBytes || definition.Delivery != MultiplayerDelivery.Coalesced)
                return OperationResult<IPerPlayerState<T>>.Failure(ModErrorCode.InvalidArgument, "Private state requires coalesced delivery and a codec bound of at most 16 KiB.");
            var encoded = EngineCodec.Encode(definition.Codec, definition.InitialValue);
            if (!encoded.TryGetValue(out var initial)) return OperationResult<IPerPlayerState<T>>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            if (!EngineCodec.Decode(definition.Codec, initial).Succeeded)
                return OperationResult<IPerPlayerState<T>>.Failure(ModErrorCode.InvalidArgument, "The private default does not decode.");
            var record = new PerPlayerRecord<T>(this, key, definition, initial);
            if (Role == EngineRole.Client && localParticipantId.HasValue && privateReceived.TryGetValue(key, out var received))
            {
                var slot = record.Slot(localParticipantId.Value);
                if (!slot.CanDecode(received.Bytes))
                    return OperationResult<IPerPlayerState<T>>.Failure(ModErrorCode.InvalidArgument, "The private value does not match its codec.");
                slot.ResetTo(received.Bytes, received.Version);
                slot.NotifiedBytes = slot.CurrentBytes;
                slot.NotifiedVersion = slot.CurrentVersion;
            }

            privateStates.Add(key, record);
            return OperationResult<IPerPlayerState<T>>.Success(record);
        }

        internal bool HasPrivatePlayer(ParticipantId player) => player.IsValid &&
            (Role == EngineRole.Host ? FindParticipant(player) != null : localParticipantId.HasValue && localParticipantId.Value.Equals(player));

        internal void RemovePrivateState(PerPlayerRecord record)
        {
            if (privateStates.TryGetValue(record.Key, out var current) && ReferenceEquals(current, record)) privateStates.Remove(record.Key);
        }

        internal void PrivateValueChanged(StateRecord slot)
        {
            if (Nullable.Equals(slot.PrivatePlayer, localParticipantId)) MarkStateChanged(slot);
        }

        private void ResetPrivateState()
        {
            privateReceived.Clear();
            privateStaged.Clear();
            privateChangesPending = false;
            privateBaseline = false;
            foreach (var record in privateStates.Values)
            {
                record.Reset();
                record.Values.Clear();
            }
        }

        // Called by reconciliation after the closing Commit, before any state observers or completions.
        private bool ReconcilePrivateState()
        {
            if (!privateChangesPending || !localParticipantId.HasValue) return true;
            // A binding may have appeared since receive. Validate it before publishing any private value.
            foreach (var record in privateStates.Values)
            {
                if (privateStaged.TryGetValue(record.Key, out var staged) && !record.Slot(localParticipantId.Value).CanDecode(staged.Bytes))
                {
                    FailContract("private state '" + record.Key + "'");
                    return false;
                }
            }

            privateChangesPending = false;
            privateReceived.Clear();
            foreach (var staged in privateStaged) privateReceived.Add(staged.Key, staged.Value);
            foreach (var record in privateStates.Values)
            {
                if (!privateReceived.TryGetValue(record.Key, out var received)) { record.Reset(); continue; }
                var slot = record.Slot(localParticipantId.Value);
                slot.ResetTo(received.Bytes, received.Version);
                PrivateValueChanged(slot);
            }
            return true;
        }

        private void ForgetPrivatePlayer(ParticipantId player)
        {
            foreach (var record in privateStates.Values) record.Forget(player);
        }
    }
}
