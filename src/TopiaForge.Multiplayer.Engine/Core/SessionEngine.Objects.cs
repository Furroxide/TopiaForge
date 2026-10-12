using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal sealed partial class SessionEngine
    {
        private readonly Dictionary<string, ObjectRecord> objects = new Dictionary<string, ObjectRecord>(StringComparer.Ordinal);
        private readonly List<ObjectRecord> objectList = new List<ObjectRecord>();
        private ulong nextObjectNumber;

        internal OperationResult<IReplicatedObject<TState, TInput>> SpawnObject<TState, TInput>(
            string owner,
            ReplicatedObjectType<TState, TInput> type,
            TState initialState,
            ParticipantId? ownerId)
            where TState : class
            where TInput : class
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (initialState == null) throw new ArgumentNullException(nameof(initialState));
            RequireOwnerThread();
            if (ended)
            {
                return OperationResult<IReplicatedObject<TState, TInput>>.Failure(ModErrorCode.InvalidState, "The multiplayer session has ended.");
            }

            if (Role != EngineRole.Host)
            {
                return OperationResult<IReplicatedObject<TState, TInput>>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "Only the canonical logical server may spawn replicated objects.");
            }

            if (ownerId.HasValue && !IsConnectedParticipant(ownerId.Value))
            {
                return OperationResult<IReplicatedObject<TState, TInput>>.Failure(
                    ModErrorCode.NotFound,
                    "The requested owner is not an admitted participant.");
            }

            if (!objectTypes.TryGetValue(new WireKey(owner, type.Id), out var found) ||
                found is not ObjectTypeBinding<TState, TInput> binding || !binding.IsActive)
            {
                return OperationResult<IReplicatedObject<TState, TInput>>.Failure(
                    ModErrorCode.NotFound,
                    "Replicated-object type '" + type.Id + "' is not registered with the requested generated types.");
            }

            var encoded = EngineCodec.Encode(binding.Definition.StateCodec, initialState);
            if (!encoded.TryGetValue(out var bytes))
            {
                return OperationResult<IReplicatedObject<TState, TInput>>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            }

            var check = EngineCodec.Decode(binding.Definition.StateCodec, bytes);
            if (!check.Succeeded) return OperationResult<IReplicatedObject<TState, TInput>>.Failure(check.ErrorCode, check.ErrorMessage);
            var record = (ObjectRecord<TState, TInput>)binding.CreateRecord(AllocateObjectId(), ownerId, bytes, 0);
            record.Life = ObjectLife.PendingSpawn;
            AddObject(record);
            if (scope.IsOpen)
            {
                scope.Spawned.Add(record);
                return OperationResult<IReplicatedObject<TState, TInput>>.Success(record);
            }

            var spawned = RunTopLevel(() =>
            {
                scope.Spawned.Add(record);
                return OperationResult<bool>.Success(true);
            });
            return spawned.Succeeded
                ? OperationResult<IReplicatedObject<TState, TInput>>.Success(record)
                : OperationResult<IReplicatedObject<TState, TInput>>.Failure(spawned.ErrorCode, spawned.ErrorMessage);
        }

        internal OperationResult<bool> DespawnObject(string owner, NetworkObjectId id)
        {
            RequireOwnerThread();
            if (Role != EngineRole.Host)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "Only the canonical logical server may despawn replicated objects.");
            }

            if (!id.IsValid || !objects.TryGetValue(id.Value, out var record) ||
                !string.Equals(record.Type.Key.Owner, owner, StringComparison.Ordinal) ||
                (record.Life != ObjectLife.Live && record.Life != ObjectLife.PendingSpawn))
            {
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The replicated object was not found.");
            }

            if (scope.IsOpen && record.Life == ObjectLife.PendingSpawn)
            {
                record.Life = ObjectLife.Despawned;
                RemoveObject(record);
                return OperationResult<bool>.Success(true);
            }

            Func<OperationResult<bool>> despawn = () =>
            {
                scope.TouchObject(record);
                record.Life = ObjectLife.PendingDespawn;
                scope.Despawned.Add(record);
                return OperationResult<bool>.Success(true);
            };
            return scope.IsOpen ? despawn() : RunTopLevel(despawn);
        }

        internal IReadOnlyList<IReplicatedObject<TState, TInput>> GetObjects<TState, TInput>(
            string owner,
            ReplicatedObjectType<TState, TInput> type)
            where TState : class
            where TInput : class
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            RequireOwnerThread();
            var key = new WireKey(owner, type.Id);
            var matches = new List<ObjectRecord<TState, TInput>>();
            foreach (var record in objectList)
            {
                if (record.Type.Key.Equals(key) && record is ObjectRecord<TState, TInput> typed && typed.IsSpawned) matches.Add(typed);
            }

            matches.Sort((left, right) => string.CompareOrdinal(left.Id.Value, right.Id.Value));
            var result = new IReplicatedObject<TState, TInput>[matches.Count];
            for (var index = 0; index < matches.Count; index++) result[index] = matches[index];
            return result;
        }

        internal bool TryGetObject<TState, TInput>(
            string owner,
            ReplicatedObjectType<TState, TInput> type,
            NetworkObjectId id,
            out IReplicatedObject<TState, TInput>? replicatedObject)
            where TState : class
            where TInput : class
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            RequireOwnerThread();
            if (id.IsValid && objects.TryGetValue(id.Value, out var record) &&
                record.Type.Key.Equals(new WireKey(owner, type.Id)) &&
                record is ObjectRecord<TState, TInput> typed && typed.IsSpawned)
            {
                replicatedObject = typed;
                return true;
            }

            replicatedObject = null;
            return false;
        }

        /// <summary>Finds a spawned object by id across every owner, if it has the requested generic types.</summary>
        internal IReplicatedObject<TState, TInput>? FindObject<TState, TInput>(NetworkObjectId id)
            where TState : class
            where TInput : class
        {
            RequireOwnerThread();
            return id.IsValid && objects.TryGetValue(id.Value, out var record) &&
                record is ObjectRecord<TState, TInput> typed && typed.IsSpawned
                ? typed
                : null;
        }

        internal OperationResult<bool> PublishPresentation<TEvent>(
            string owner,
            PresentationEventType<TEvent> eventType,
            TEvent value,
            PresentationTarget target)
            where TEvent : class
        {
            if (eventType == null) throw new ArgumentNullException(nameof(eventType));
            if (value == null) throw new ArgumentNullException(nameof(value));
            RequireOwnerThread();
            if (ended) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The multiplayer session has ended.");
            var predicting = scope.IsOpen && scope.Kind == TransactionKind.Prediction;
            if (Role != EngineRole.Host && !predicting)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "Only the canonical logical server may publish a presentation event.");
            }

            var key = new WireKey(owner, eventType.Id);
            if (!presentations.TryGetValue(key, out var binding) || binding is not PresentationBinding<TEvent> || !binding.IsActive)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.NotFound,
                    "Presentation event '" + eventType.Id + "' is not registered with the requested generated type.");
            }

            var encoded = EngineCodec.Encode(eventType.Codec, value);
            if (!encoded.TryGetValue(out var bytes)) return OperationResult<bool>.Failure(encoded.ErrorCode, encoded.ErrorMessage);

            // A prediction drops the event, as Emit does: the canonical run publishes it.
            if (predicting) return OperationResult<bool>.Success(true);

            // Inside a command, Sender and Others are relative to the participant who sent it.
            var item = new BufferedEvent(key, bytes, target, scope.IsOpen ? scope.Sender : localParticipantId ?? default);
            if (scope.IsOpen)
            {
                scope.Events.Add(item);
            }
            else
            {
                PublishEvent(item);
                EndOperation();
            }

            return OperationResult<bool>.Success(true);
        }

        /// <summary>
        /// Retires an owner whose facade closed, at the next settle point. A host despawns the owner's objects
        /// canonically, so every peer sees them leave; a client drops its copies and cancels the owner's pending
        /// submissions.
        /// </summary>
        internal void RetireOwner(string owner) => Defer(() => RetireOwnerNow(owner));

        private void RetireOwnerNow(string owner)
        {
            if (ended) return;
            var retired = new List<ObjectRecord>();
            foreach (var record in objectList)
            {
                if (record.Life == ObjectLife.Live && string.Equals(record.Type.Key.Owner, owner, StringComparison.Ordinal)) retired.Add(record);
            }

            if (Role == EngineRole.Host)
            {
                if (retired.Count == 0) return;
                RunTopLevel(() =>
                {
                    foreach (var record in retired)
                    {
                        scope.TouchObject(record);
                        record.Life = ObjectLife.PendingDespawn;
                        scope.Despawned.Add(record);
                    }

                    return OperationResult<bool>.Success(true);
                });
                return;
            }

            foreach (var record in retired)
            {
                record.Life = ObjectLife.Despawned;
                RemoveObject(record);
            }

            CancelOwnerSubmissions(owner, "The mod that submitted this stopped.");
        }

        private NetworkObjectId AllocateObjectId() =>
            new NetworkObjectId(options.ObjectIdPrefix + (++nextObjectNumber).ToString(CultureInfo.InvariantCulture));

        private void AddObject(ObjectRecord record)
        {
            objects[record.Id.Value] = record;
            objectList.Add(record);
        }

        private void RemoveObject(ObjectRecord record)
        {
            if (objects.TryGetValue(record.Id.Value, out var current) && ReferenceEquals(current, record))
            {
                objects.Remove(record.Id.Value);
                objectList.Remove(record);
            }
        }

        /// <summary>Retires every object for a new session or an exact snapshot, notifying surviving subscriptions.</summary>
        private void DespawnAllLocally()
        {
            foreach (var record in objectList.ToArray())
            {
                record.Life = ObjectLife.Despawned;
                RemoveObject(record);
                CancelPendingInputs(record, "The replicated object left the session.");
                QueueObjectChange(record, ReplicatedObjectChangeKind.Despawned);
            }
        }
    }
}
