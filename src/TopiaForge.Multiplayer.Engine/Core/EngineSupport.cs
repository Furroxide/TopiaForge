using System;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>A wire id scoped by its owner mod: registrations and messages are keyed by both.</summary>
    internal readonly struct WireKey : IEquatable<WireKey>
    {
        internal WireKey(string owner, string id)
        {
            Owner = owner;
            Id = id;
        }

        internal string Owner { get; }
        internal string Id { get; }

        public bool Equals(WireKey other) =>
            string.Equals(Owner, other.Owner, StringComparison.Ordinal) &&
            string.Equals(Id, other.Id, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is WireKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (StringComparer.Ordinal.GetHashCode(Owner ?? string.Empty) * 397) ^
                    StringComparer.Ordinal.GetHashCode(Id ?? string.Empty);
            }
        }

        public override string ToString() => Owner + "/" + Id;
    }

    /// <summary>A disposable that runs its action once, from any thread.</summary>
    internal sealed class EngineLease : IDisposable
    {
        private Action? dispose;

        internal EngineLease(Action dispose)
        {
            this.dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
        }

        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }

    /// <summary>
    /// Allows at most <c>capacity</c> events in any sliding window, measured in engine microseconds. The test rig and
    /// production share it, so a rate limit behaves the same on virtual and real time.
    /// </summary>
    internal sealed class SlidingWindowRateLimiter
    {
        private readonly long[] stamps;
        private readonly long windowMicros;
        private int head;
        private int count;

        internal SlidingWindowRateLimiter(int capacity, long windowMicros)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (windowMicros < 1) throw new ArgumentOutOfRangeException(nameof(windowMicros));
            stamps = new long[capacity];
            this.windowMicros = windowMicros;
        }

        internal bool TryAcquire(long nowMicros)
        {
            while (count > 0 && stamps[head] + windowMicros <= nowMicros)
            {
                head = (head + 1) % stamps.Length;
                count--;
            }

            if (count >= stamps.Length) return false;
            stamps[(head + count) % stamps.Length] = nowMicros;
            count++;
            return true;
        }
    }

    /// <summary>Codec calls with bound checks and exception capture. Stored bytes are never handed to a codec directly.</summary>
    internal static class EngineCodec
    {
        internal static OperationResult<byte[]> Encode<T>(IMultiplayerCodec<T> codec, T value) where T : class
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            try
            {
                var encoded = codec.Encode(value);
                if (encoded == null)
                {
                    return OperationResult<byte[]>.Failure(ModErrorCode.Unknown, "The multiplayer codec returned no result.");
                }

                if (!encoded.TryGetValue(out var bytes)) return OperationResult<byte[]>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
                if (bytes == null)
                {
                    return OperationResult<byte[]>.Failure(ModErrorCode.Unknown, "The multiplayer codec returned null bytes.");
                }

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

        internal static OperationResult<T> Decode<T>(IMultiplayerCodec<T> codec, byte[] bytes) where T : class
        {
            try
            {
                if (bytes.Length > codec.MaximumEncodedBytes)
                {
                    return OperationResult<T>.Failure(
                        ModErrorCode.InvalidArgument,
                        "The encoded multiplayer payload exceeded its declared bound.");
                }

                var decoded = codec.Decode((byte[])bytes.Clone());
                if (decoded == null)
                {
                    return OperationResult<T>.Failure(ModErrorCode.Unknown, "The multiplayer codec returned no decode result.");
                }

                if (decoded.Succeeded && decoded.Value == null)
                {
                    return OperationResult<T>.Failure(ModErrorCode.Unknown, "The multiplayer codec decoded a null value.");
                }

                return decoded;
            }
            catch (Exception exception)
            {
                return FromException<T>(exception, "The multiplayer codec failed while decoding a payload.");
            }
        }

        /// <summary>Decodes bytes the engine already validated; a failure here is an engine or codec defect.</summary>
        internal static T DecodeTrusted<T>(IMultiplayerCodec<T> codec, byte[] bytes, string what) where T : class
        {
            var decoded = Decode(codec, bytes);
            if (!decoded.TryGetValue(out var value))
            {
                throw new InvalidOperationException("Unable to decode " + what + ": " + decoded.ErrorMessage);
            }

            return value;
        }

        internal static OperationResult<T> FromException<T>(Exception exception, string message) where T : notnull =>
            OperationResult<T>.Failure(
                exception is OperationCanceledException ? ModErrorCode.Cancelled : ModErrorCode.Unknown,
                message + " " + exception.GetType().Name + ".");

        internal static bool BytesEqual(byte[] first, byte[] second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first.Length != second.Length) return false;
            for (var index = 0; index < first.Length; index++)
            {
                if (first[index] != second[index]) return false;
            }

            return true;
        }
    }
}
