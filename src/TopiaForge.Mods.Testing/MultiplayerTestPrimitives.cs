using System;
using System.Collections.Generic;
using System.Threading;

namespace TopiaForge.Mods.Testing
{
    internal sealed class TestLease : IDisposable
    {
        private Action? dispose;

        internal TestLease(Action dispose)
        {
            this.dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
        }

        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }

    internal sealed class BufferedTestPresentation
    {
        internal BufferedTestPresentation(
            string id,
            byte[] bytes,
            MultiplayerAudience audience,
            ulong sequence = 0)
        {
            Id = id;
            Bytes = bytes == null ? throw new ArgumentNullException(nameof(bytes)) : (byte[])bytes.Clone();
            Audience = audience;
            Sequence = sequence;
        }

        internal string Id { get; }
        internal byte[] Bytes { get; }
        internal MultiplayerAudience Audience { get; }
        internal ulong Sequence { get; }

        internal BufferedTestPresentation Copy() =>
            new BufferedTestPresentation(Id, Bytes, Audience, Sequence);

        internal BufferedTestPresentation WithSequence(ulong sequence) =>
            new BufferedTestPresentation(Id, Bytes, Audience, sequence);
    }

    internal interface ITestPresentation : IPresentationEventRegistration
    {
        void Deliver(byte[] bytes);
    }

    internal sealed class TestPresentation<TEvent> : ITestPresentation where TEvent : class
    {
        private readonly MultiplayerTestSession session;
        private readonly PresentationEventDefinition<TEvent> definition;

        internal TestPresentation(
            MultiplayerTestSession session,
            PresentationEventDefinition<TEvent> definition)
        {
            this.session = session;
            this.definition = definition;
            IsActive = true;
        }

        internal PresentationEventType<TEvent> Type => definition.Type;
        public string Id => definition.Type.Id;
        public bool IsActive { get; private set; }

        public void Deliver(byte[] bytes)
        {
            if (!IsActive || definition.Handler == null) return;
            var decoded = MultiplayerTestCodec.Decode(definition.Type.Codec, bytes);
            if (!decoded.TryGetValue(out var value))
                throw new InvalidOperationException("Unable to decode presentation event '" + Id + "': " + decoded.ErrorMessage);
            definition.Handler(value);
        }

        public void Dispose()
        {
            if (!IsActive) return;
            IsActive = false;
            session.RemovePresentation(Id, this);
        }
    }

    internal sealed class TestStateSnapshot
    {
        internal TestStateSnapshot(string id, byte[] bytes, ulong version)
        {
            Id = id;
            Bytes = bytes;
            Version = version;
        }

        internal string Id { get; }
        internal byte[] Bytes { get; }
        internal ulong Version { get; }

        internal TestStateSnapshot Copy() =>
            new TestStateSnapshot(Id, (byte[])Bytes.Clone(), Version);
    }

    internal enum TestStateSnapshotScope
    {
        Delta = 0,
        Complete = 1
    }

    internal interface IPendingTestPrediction
    {
        ulong PredictionOrder { get; }
        bool WasPredicted { get; }
        bool IsCompleted { get; }
        void Replay();
    }

    internal sealed class TestObjectSnapshot
    {
        internal TestObjectSnapshot(
            string typeId,
            NetworkObjectId id,
            ParticipantId? ownerId,
            byte[] bytes,
            ulong version,
            bool isDespawn = false)
        {
            if (string.IsNullOrWhiteSpace(typeId)) throw new ArgumentException("A replicated-object type id is required.", nameof(typeId));
            TypeId = typeId;
            Id = id;
            OwnerId = ownerId;
            Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
            Version = version;
            IsDespawn = isDespawn;
        }

        internal string TypeId { get; }
        internal NetworkObjectId Id { get; }
        internal ParticipantId? OwnerId { get; }
        internal byte[] Bytes { get; }
        internal ulong Version { get; }
        internal bool IsDespawn { get; }

        internal TestObjectSnapshot Copy() =>
            new TestObjectSnapshot(TypeId, Id, OwnerId, (byte[])Bytes.Clone(), Version, IsDespawn);
    }

    internal static class MultiplayerTestCodec
    {
        internal static OperationResult<byte[]> Encode<T>(IMultiplayerCodec<T> codec, T value) where T : class
        {
            if (codec == null) throw new ArgumentNullException(nameof(codec));
            if (value == null) throw new ArgumentNullException(nameof(value));
            try
            {
                var encoded = codec.Encode(value);
                if (encoded == null)
                    return OperationResult<byte[]>.Failure(ModErrorCode.Unknown, "The multiplayer codec returned no result.");
                if (!encoded.TryGetValue(out var bytes))
                {
                    return OperationResult<byte[]>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
                }

                if (bytes == null)
                    return OperationResult<byte[]>.Failure(ModErrorCode.Unknown, "The multiplayer codec returned null bytes.");
                if (bytes.Length > codec.MaximumEncodedBytes)
                {
                    return OperationResult<byte[]>.Failure(
                        ModErrorCode.InvalidArgument,
                        "The generated codec exceeded its declared maximum size.");
                }

                return OperationResult<byte[]>.Success((byte[])bytes.Clone());
            }
            catch (Exception exception)
            {
                return FromException<byte[]>(exception, "The multiplayer codec failed while encoding a payload.");
            }
        }

        internal static OperationResult<T> RoundTrip<T>(IMultiplayerCodec<T> codec, T value) where T : class
        {
            var encoded = Encode(codec, value);
            if (!encoded.TryGetValue(out var bytes))
            {
                return OperationResult<T>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            }

            return Decode(codec, bytes);
        }

        internal static OperationResult<T> Decode<T>(IMultiplayerCodec<T> codec, byte[] bytes) where T : class
        {
            if (codec == null) throw new ArgumentNullException(nameof(codec));
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            try
            {
                if (bytes.Length > codec.MaximumEncodedBytes)
                {
                    return OperationResult<T>.Failure(
                        ModErrorCode.InvalidArgument,
                        "The encoded multiplayer payload exceeded its declared bound.");
                }

                var decoded = codec.Decode((byte[])bytes.Clone());
                return decoded ?? OperationResult<T>.Failure(
                    ModErrorCode.Unknown,
                    "The multiplayer codec returned no decode result.");
            }
            catch (Exception exception)
            {
                return FromException<T>(exception, "The multiplayer codec failed while decoding a payload.");
            }
        }

        internal static OperationResult<T> FromException<T>(Exception exception, string message) where T : notnull =>
            OperationResult<T>.Failure(
                exception is OperationCanceledException ? ModErrorCode.Cancelled : ModErrorCode.Unknown,
                message + " " + exception.GetType().Name + ".");
    }

    internal interface ITestState : IDisposable
    {
        string Id { get; }
        ulong Version { get; }
        TestStateSnapshot CaptureCurrent();
        void CommitCurrent();
        void PublishCurrent();
        void RestoreCurrent(TestStateSnapshot snapshot, bool notify = true);
        void ResetToConfirmed(bool notify = true);
        void ResetForNewSession();
        void ApplyCanonical(TestStateSnapshot snapshot);
    }

    internal sealed class TestReplicatedState<T> : IReplicatedState<T>, ITestState where T : class
    {
        private readonly MultiplayerTestSession session;
        private readonly IMultiplayerCodec<T> codec;
        private readonly List<Action<T>> handlers = new List<Action<T>>();
        private readonly T initial;
        private T current;
        private T confirmed;
        private ulong version;
        private ulong confirmedVersion;
        private bool disposed;

        internal TestReplicatedState(
            MultiplayerTestSession session,
            ReplicatedStateDefinition<T> definition,
            T initialValue)
        {
            this.session = session;
            codec = definition.Codec;
            Id = definition.Id;
            initial = Clone(initialValue);
            current = Clone(initial);
            confirmed = Clone(initial);
        }

        public string Id { get; }
        public T Value => Clone(current);
        public ulong Version => version;

        public OperationResult<T> Update(Func<T, OperationResult<T>> updater)
        {
            if (updater == null) throw new ArgumentNullException(nameof(updater));
            if (disposed)
            {
                return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The replicated test state is disposed.");
            }

            if (!session.CanMutateState)
            {
                return OperationResult<T>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "A remote client may mutate replicated state only inside an owner-predicted handler.");
            }

            var currentCopy = MultiplayerTestCodec.RoundTrip(codec, current);
            if (!currentCopy.TryGetValue(out var detachedCurrent)) return currentCopy;
            var proposed = updater(detachedCurrent);
            if (!proposed.TryGetValue(out var next)) return proposed;
            var encoded = MultiplayerTestCodec.Encode(codec, next);
            if (!encoded.TryGetValue(out var bytes))
            {
                return OperationResult<T>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            }

            var copied = MultiplayerTestCodec.Decode(codec, bytes);
            if (!copied.TryGetValue(out var value)) return copied;
            var response = MultiplayerTestCodec.Decode(codec, bytes);
            if (!response.TryGetValue(out var detachedResponse)) return response;
            current = value;
            version++;
            if (!session.DefersCanonicalStateNotifications) Notify(value);
            session.OnStateMutated(this);
            return OperationResult<T>.Success(detachedResponse);
        }

        public IDisposable SubscribeChanged(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (disposed) throw new ObjectDisposedException(nameof(TestReplicatedState<T>));
            handlers.Add(handler);
            return new TestLease(() => handlers.Remove(handler));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            handlers.Clear();
            session.RemoveState(Id, this);
        }

        public TestStateSnapshot CaptureCurrent()
        {
            var encoded = MultiplayerTestCodec.Encode(codec, current);
            if (!encoded.TryGetValue(out var bytes))
            {
                throw new InvalidOperationException("Unable to snapshot replicated test state '" + Id + "': " + encoded.ErrorMessage);
            }

            return new TestStateSnapshot(Id, bytes, version);
        }

        public void CommitCurrent()
        {
            confirmed = Clone(current);
            confirmedVersion = version;
        }

        public void PublishCurrent() => Notify(current);

        public void ResetToConfirmed(bool notify = true)
        {
            current = Clone(confirmed);
            version = confirmedVersion;
            if (notify) Notify(current);
        }

        public void ResetForNewSession()
        {
            current = Clone(initial);
            confirmed = Clone(initial);
            version = 0;
            confirmedVersion = 0;
            Notify(current);
        }

        public void RestoreCurrent(TestStateSnapshot snapshot, bool notify = true)
        {
            var decoded = MultiplayerTestCodec.Decode(codec, snapshot.Bytes);
            if (!decoded.TryGetValue(out var value))
            {
                throw new InvalidOperationException("Unable to restore test state '" + Id + "': " + decoded.ErrorMessage);
            }

            current = value;
            version = snapshot.Version;
            if (notify) Notify(current);
        }

        public void ApplyCanonical(TestStateSnapshot snapshot)
        {
            if (snapshot.Version < confirmedVersion) return;
            if (snapshot.Version == confirmedVersion && version == confirmedVersion)
            {
                var existing = MultiplayerTestCodec.Encode(codec, confirmed);
                if (existing.TryGetValue(out var existingBytes) && BytesEqual(existingBytes, snapshot.Bytes)) return;
            }
            var decoded = MultiplayerTestCodec.Decode(codec, snapshot.Bytes);
            if (!decoded.TryGetValue(out var value))
            {
                throw new InvalidOperationException("Unable to decode canonical state '" + Id + "': " + decoded.ErrorMessage);
            }

            confirmed = Clone(value);
            confirmedVersion = snapshot.Version;
            current = Clone(value);
            version = snapshot.Version;
            Notify(current);
        }

        private T Clone(T value)
        {
            var copied = MultiplayerTestCodec.RoundTrip(codec, value);
            if (!copied.TryGetValue(out var clone))
            {
                throw new InvalidOperationException("Unable to clone replicated test state '" + Id + "': " + copied.ErrorMessage);
            }

            return clone;
        }

        private void Notify(T value)
        {
            foreach (var handler in handlers.ToArray()) handler(Clone(value));
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (first.Length != second.Length) return false;
            for (var index = 0; index < first.Length; index++)
            {
                if (first[index] != second[index]) return false;
            }

            return true;
        }
    }
}
