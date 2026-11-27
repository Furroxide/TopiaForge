using System;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal sealed partial class SessionEngine
    {
        internal bool InTransaction => scope.IsOpen;

        internal TransactionScope Scope => scope;

        /// <summary>
        /// Runs a state mutation: inside an open transaction it joins it; at top level on a host it commits a
        /// transaction of its own; on a client outside a prediction it is not authoritative.
        /// </summary>
        internal OperationResult<T> MutateState<T>(StateRecord record, Func<OperationResult<T>> body) where T : notnull
        {
            RequireOwnerThread();
            if (!record.IsActive) return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The replicated state is disposed.");
            if (ended) return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The multiplayer session has ended.");
            if (scope.IsOpen)
            {
                scope.TouchState(record);
                return body();
            }

            if (Role != EngineRole.Host)
            {
                return OperationResult<T>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "A remote client may mutate replicated state only inside an owner-predicted handler.");
            }

            return RunTopLevel(() =>
            {
                scope.TouchState(record);
                return body();
            });
        }

        internal void WriteState(StateRecord record, byte[] bytes)
        {
            if (!scope.IsOpen) throw new InvalidOperationException("Replicated state can change only inside a transaction.");
            scope.TouchState(record);
            record.CurrentBytes = bytes;
            record.CurrentVersion++;
        }

        /// <summary>Runs a canonical object change such as <c>Apply</c>; only host code may change an object directly.</summary>
        internal OperationResult<T> MutateObject<T>(ObjectRecord record, Func<OperationResult<T>> body) where T : notnull
        {
            RequireOwnerThread();
            if (record.Life != ObjectLife.Live && record.Life != ObjectLife.PendingSpawn)
            {
                return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The replicated object is not spawned.");
            }

            if (Role != EngineRole.Host)
            {
                return OperationResult<T>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "Only canonical server code may change a replicated object directly.");
            }

            if (scope.IsOpen)
            {
                scope.TouchObject(record);
                return body();
            }

            return RunTopLevel(() =>
            {
                scope.TouchObject(record);
                return body();
            });
        }

        internal OperationResult<bool> TransferOwnership(ObjectRecord record, ParticipantId? ownerId)
        {
            RequireOwnerThread();
            if (record.Life != ObjectLife.Live && record.Life != ObjectLife.PendingSpawn)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The replicated object is not spawned.");
            }

            if (Role != EngineRole.Host)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "Only the canonical server may transfer object ownership.");
            }

            if (ownerId.HasValue && !IsConnectedParticipant(ownerId.Value))
            {
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The new owner is not an admitted participant.");
            }

            Func<OperationResult<bool>> transfer = () =>
            {
                scope.TouchObject(record);
                record.CurrentOwner = ownerId;
                record.CurrentVersion++;
                return OperationResult<bool>.Success(true);
            };
            return scope.IsOpen ? transfer() : RunTopLevel(transfer);
        }

        /// <summary>Runs a host operation as its own canonical transaction, then settles.</summary>
        private OperationResult<T> RunTopLevel<T>(Func<OperationResult<T>> body) where T : notnull
        {
            OpenTransaction(TransactionKind.Canonical, localParticipantId ?? default);
            var committed = false;
            OperationResult<T> result;
            try
            {
                result = body();
                if (result.Succeeded)
                {
                    CommitCanonical();
                    committed = true;
                }
            }
            finally
            {
                if (!committed) AbortCanonical();
                CloseTransaction();
            }

            EndOperation();
            return result;
        }

        /// <summary>Runs a command, an object input or a tick handler as one canonical transaction.</summary>
        private WireOutcome RunCanonical(ParticipantId sender, Func<WireOutcome> body)
        {
            OpenTransaction(TransactionKind.Canonical, sender);
            var committed = false;
            WireOutcome outcome;
            try
            {
                outcome = body();
                if (outcome.Succeeded)
                {
                    CommitCanonical();
                    committed = true;
                }
            }
            finally
            {
                if (!committed) AbortCanonical();
                CloseTransaction();
            }

            return outcome;
        }

        /// <summary>Runs a client's owner prediction; on failure every touched value is restored.</summary>
        private void RunPrediction(PendingSubmission pending)
        {
            OpenTransaction(TransactionKind.Prediction, localParticipantId ?? default);
            var committed = false;
            try
            {
                var outcome = pending.Predict(scope);
                if (outcome.Succeeded)
                {
                    foreach (var record in scope.TouchedStates)
                    {
                        if (scope.StateChanged(record)) MarkStateChanged(record);
                    }

                    committed = true;
                }
            }
            finally
            {
                if (!committed) scope.RestoreTouched();
                CloseTransaction();
            }
        }

        private void OpenTransaction(TransactionKind kind, ParticipantId sender) => scope.Open(kind, sender);

        private void CloseTransaction() => scope.Close();

        private void CommitCanonical()
        {
            foreach (var record in scope.TouchedStates)
            {
                if (!scope.StateChanged(record)) continue;
                record.ConfirmedBytes = record.CurrentBytes;
                record.ConfirmedVersion = record.CurrentVersion;
                MarkStateChanged(record);
                OnStateCommitted(record);
            }

            foreach (var record in scope.TouchedObjects)
            {
                if (record.Life != ObjectLife.Live || !scope.ObjectChanged(record)) continue;
                record.Confirm();
                QueueObjectChange(record, ReplicatedObjectChangeKind.Changed);
                OnObjectChanged(record);
            }

            foreach (var record in scope.Spawned)
            {
                if (record.Life != ObjectLife.PendingSpawn) continue;
                record.Life = ObjectLife.Live;
                record.Confirm();
                QueueObjectChange(record, ReplicatedObjectChangeKind.Spawned);
                OnObjectSpawned(record);
            }

            foreach (var record in scope.Despawned)
            {
                if (record.Life != ObjectLife.PendingDespawn) continue;
                record.Life = ObjectLife.Despawned;
                record.Confirm();
                RemoveObject(record);
                QueueObjectChange(record, ReplicatedObjectChangeKind.Despawned);
                OnObjectDespawned(record);
            }

            foreach (var item in scope.Events) PublishEvent(item);
        }

        private void AbortCanonical()
        {
            scope.RestoreTouched();
            foreach (var record in scope.Spawned)
            {
                if (record.Life == ObjectLife.Live) continue;
                record.Life = ObjectLife.Despawned;
                RemoveObject(record);
            }
        }
    }
}
