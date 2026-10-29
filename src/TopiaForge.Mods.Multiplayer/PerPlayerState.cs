using System;
using System.ComponentModel;

namespace TopiaForge.Mods
{
    /// <summary>A private value per admitted player. Only the host writes; clients can read only themselves.</summary>
    [RequiresTopiaForge("0.2.0")]
    public interface IPerPlayerState<T> : IDisposable where T : class
    {
        /// <summary>Reads a detached value for a player, or refuses an unknown or non-local client target.</summary>
        OperationResult<T> Get(ParticipantId player);

        /// <summary>Replaces one player's value on the host, within the current command transaction when present.</summary>
        OperationResult<T> Set(ParticipantId player, T value);

        /// <summary>Observes changes to this process's local player's value, including resets. Each callback gets a copy.</summary>
        IDisposable SubscribeChanged(Action<T> handler);
    }

    /// <summary>Additive provider SPI for private state; older providers fail binding without broadcasting a fallback.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [RequiresTopiaForge("0.2.0")]
    public interface IPerPlayerStateSession
    {
        /// <summary>Registers a private field. Coalesced delivery only, at most 16 KiB per value and 64 fields per engine.</summary>
        OperationResult<IPerPlayerState<T>> RegisterPerPlayerState<T>(ReplicatedStateDefinition<T> definition) where T : class;
    }

    /// <summary>
    /// A generated private field marked with <see cref="ReplicatedStateAttribute"/>. Declare a non-secret default;
    /// the host assigns each player's value. Reconnects restore that player's value during the admission grace period.
    /// </summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class PerPlayerState<T> : IPerPlayerState<T> where T : class
    {
        private T? initial;
        private byte[]? frozen;
        private IMultiplayerCodec<T>? initialCodec;
        private IPerPlayerState<T>? connected;
        private bool disposed;

        /// <summary>Declares the public default used for a new player or session.</summary>
        public PerPlayerState(T initialValue) => initial = initialValue ?? throw new ArgumentNullException(nameof(initialValue));

        /// <inheritdoc/>
        public OperationResult<T> Get(ParticipantId player) => connected?.Get(player) ?? Unbound();

        /// <inheritdoc/>
        public OperationResult<T> Set(ParticipantId player, T value) => connected?.Set(player, value) ?? Unbound();

        /// <inheritdoc/>
        public IDisposable SubscribeChanged(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (connected == null) throw new InvalidOperationException("BindMultiplayer must succeed before observing private state.");
            return connected.SubscribeChanged(handler);
        }

        /// <summary>Connects this field to an additive private-state provider. Used by generated bindings.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public OperationResult<IDisposable> Bind(IMultiplayerSession session, string id, IMultiplayerCodec<T> codec) =>
            Bind(session, id, codec, MultiplayerDelivery.Coalesced, 0);

        /// <summary>Connects a coalesced private field with an optional downstream update-rate limit.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public OperationResult<IDisposable> Bind(IMultiplayerSession session, string id, IMultiplayerCodec<T> codec,
            MultiplayerDelivery delivery, int maximumUpdatesPerSecond)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (codec == null) throw new ArgumentNullException(nameof(codec));
            if (disposed) return Failure(ModErrorCode.InvalidState, "The private state handle is disposed.");
            if (connected != null) return Failure(ModErrorCode.Conflict, "The private state handle is already bound.");
            if (session is not IPerPlayerStateSession provider)
                return Failure(ModErrorCode.Unavailable, "This multiplayer provider does not support private state.");
            if (codec.MaximumEncodedBytes < 0 || codec.MaximumEncodedBytes > 16 * 1024 || delivery != MultiplayerDelivery.Coalesced)
                return Failure(ModErrorCode.InvalidArgument, "Private state requires coalesced delivery and a codec bound of at most 16 KiB.");
            try
            {
                if (frozen == null)
                {
                    var encoded = codec.Encode(initial!);
                    if (encoded == null || !encoded.TryGetValue(out var bytes) || bytes == null || bytes.Length > codec.MaximumEncodedBytes)
                        return Failure(ModErrorCode.InvalidArgument, "The private state default could not be encoded within its bound.");
                    frozen = (byte[])bytes.Clone();
                    initialCodec = codec;
                    initial = null;
                }

                var decoded = initialCodec!.Decode((byte[])frozen.Clone());
                if (decoded == null || !decoded.TryGetValue(out var value))
                    return Failure(ModErrorCode.InvalidArgument, "The private state default could not be decoded.");
                var result = provider.RegisterPerPlayerState(new ReplicatedStateDefinition<T>(id, value, codec, delivery, maximumUpdatesPerSecond));
                if (!result.TryGetValue(out var state)) return Failure(result.ErrorCode, result.ErrorMessage);
                connected = state;
                return OperationResult<IDisposable>.Success(new Binding(this, state));
            }
            catch (Exception)
            {
                return Failure(ModErrorCode.InvalidArgument, "The private state codec or definition is invalid.");
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            disposed = true;
            var state = connected;
            connected = null;
            initial = null;
            frozen = null;
            initialCodec = null;
            state?.Dispose();
        }

        private static OperationResult<T> Unbound() => OperationResult<T>.Failure(ModErrorCode.InvalidState, "The private state is not bound.");
        private static OperationResult<IDisposable> Failure(ModErrorCode code, string message) => OperationResult<IDisposable>.Failure(code, message);

        private sealed class Binding : IDisposable
        {
            private PerPlayerState<T>? owner;
            private readonly IPerPlayerState<T> state;
            internal Binding(PerPlayerState<T> owner, IPerPlayerState<T> state) { this.owner = owner; this.state = state; }
            public void Dispose()
            {
                var captured = owner;
                owner = null;
                if (captured == null || !ReferenceEquals(captured.connected, state)) return;
                captured.connected = null;
                state.Dispose();
            }
        }
    }
}
