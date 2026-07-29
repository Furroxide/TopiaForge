using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Selects recipients for a transient canonical presentation event.</summary>
    public enum MultiplayerAudience
    {
        /// <summary>Every interactive participant receives the event.</summary>
        Everyone = 0,

        /// <summary>Only the originating participant receives the event.</summary>
        Sender = 1,

        /// <summary>Every interactive participant except the originating participant receives the event.</summary>
        Others = 2
    }

    /// <summary>Generator/provider SPI identifying one stable transient presentation-event wire type.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class PresentationEventType<TEvent> where TEvent : class
    {
        /// <summary>Creates a presentation-event type reference with its generated bounded codec.</summary>
        public PresentationEventType(string id, IMultiplayerCodec<TEvent> codec)
        {
            Id = MultiplayerIdentityValidation.Require(id, nameof(id));
            Codec = codec ?? throw new ArgumentNullException(nameof(codec));
            if (codec.MaximumEncodedBytes < 0 || codec.MaximumEncodedBytes > 1024 * 1024)
                throw new ArgumentException("Presentation-event codec bounds must be between zero and 1 MiB.", nameof(codec));
        }

        /// <summary>Gets the stable namespaced wire event id.</summary>
        public string Id { get; }

        /// <summary>Gets the generated bounded event codec.</summary>
        public IMultiplayerCodec<TEvent> Codec { get; }
    }

    /// <summary>
    /// Registers the local decoder and optional presentation handler for an event. Headless peers register the same
    /// event type with a null handler so transport compatibility never depends on presentation availability.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class PresentationEventDefinition<TEvent> where TEvent : class
    {
        /// <summary>Creates a local presentation-event definition.</summary>
        public PresentationEventDefinition(PresentationEventType<TEvent> type, Action<TEvent>? handler = null)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            Handler = handler;
        }

        /// <summary>Gets the generated event type and bounded codec.</summary>
        public PresentationEventType<TEvent> Type { get; }

        /// <summary>Gets the local presentation handler, or null when this process has no presentation side.</summary>
        public Action<TEvent>? Handler { get; }
    }

    /// <summary>Provider SPI for a lifetime-owned local presentation-event registration.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IPresentationEventRegistration : IDisposable
    {
        /// <summary>Gets the stable namespaced event id.</summary>
        string Id { get; }

        /// <summary>Gets whether the local event codec and handler remain registered.</summary>
        bool IsActive { get; }
    }

    /// <summary>Provides authenticated sender, tick, cancellation, and buffered-event context to a command.</summary>
    public class MultiplayerCommandContext
    {
        private readonly Func<string, byte[], MultiplayerAudience, OperationResult<bool>> emit;

        /// <summary>Creates command context. Runtime providers, generators, and tests normally construct this value.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public MultiplayerCommandContext(
            ParticipantId senderId,
            NetworkTick tick,
            CancellationToken cancellationToken,
            Func<string, byte[], MultiplayerAudience, OperationResult<bool>> emit)
        {
            if (!senderId.IsValid)
            {
                throw new ArgumentException(
                    "A command context requires a valid authenticated sender identity.",
                    nameof(senderId));
            }

            SenderId = senderId;
            Tick = tick;
            CancellationToken = cancellationToken;
            this.emit = emit ?? throw new ArgumentNullException(nameof(emit));
        }

        /// <summary>Gets the transport-authenticated sender.</summary>
        public ParticipantId SenderId { get; }

        /// <summary>Gets the canonical or predicted tick.</summary>
        public NetworkTick Tick { get; }

        /// <summary>Gets cancellation tied to the session and command deadline.</summary>
        public CancellationToken CancellationToken { get; }

        /// <summary>Buffers a transient event until canonical command acceptance.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public OperationResult<bool> Emit<TEvent>(
            PresentationEventType<TEvent> eventType,
            TEvent value,
            MultiplayerAudience audience = MultiplayerAudience.Everyone) where TEvent : class
        {
            if (eventType == null) throw new ArgumentNullException(nameof(eventType));
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (!Enum.IsDefined(typeof(MultiplayerAudience), audience)) throw new ArgumentOutOfRangeException(nameof(audience));
            var encoded = eventType.Codec.Encode(value);
            if (!encoded.TryGetValue(out var bytes))
                return OperationResult<bool>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            if (bytes == null || bytes.Length > eventType.Codec.MaximumEncodedBytes)
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The presentation-event codec exceeded its declared maximum size.");
            return emit(eventType.Id, (byte[])bytes.Clone(), audience);
        }
    }

    /// <summary>Adds an explicit canonical target and authenticated ownership result for replicated-object input.</summary>
    public sealed class ReplicatedObjectCommandContext : MultiplayerCommandContext
    {
        /// <summary>Creates replicated-object command context. Providers and test rigs normally construct this.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public ReplicatedObjectCommandContext(
            ParticipantId senderId,
            NetworkObjectId targetObjectId,
            NetworkTick tick,
            bool senderOwnsTarget,
            CancellationToken cancellationToken,
            Func<string, byte[], MultiplayerAudience, OperationResult<bool>> emit)
            : base(senderId, tick, cancellationToken, emit)
        {
            if (!targetObjectId.IsValid)
            {
                throw new ArgumentException(
                    "A replicated-object command context requires a valid target identity.",
                    nameof(targetObjectId));
            }

            TargetObjectId = targetObjectId;
            SenderOwnsTarget = senderOwnsTarget;
        }

        /// <summary>Gets the canonical server-assigned target object identity.</summary>
        public NetworkObjectId TargetObjectId { get; }

        /// <summary>Gets whether the authenticated sender currently owns this exact target.</summary>
        public bool SenderOwnsTarget { get; }
    }

    /// <summary>Generator/provider SPI identifying one stable multiplayer command wire type.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class MultiplayerCommandType<TRequest, TResponse>
        where TRequest : class
        where TResponse : class
    {
        /// <summary>Creates a typed command reference.</summary>
        public MultiplayerCommandType(string id)
        {
            Id = MultiplayerIdentityValidation.Require(id, nameof(id));
        }

        /// <summary>Gets the stable namespaced command id.</summary>
        public string Id { get; }
    }

    /// <summary>Generator/provider SPI defining one typed command handler, bounds, and prediction behavior.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class MultiplayerCommandDefinition<TRequest, TResponse>
        where TRequest : class
        where TResponse : class
    {
        /// <summary>Creates a command definition.</summary>
        public MultiplayerCommandDefinition(
            MultiplayerCommandType<TRequest, TResponse> type,
            IMultiplayerCodec<TRequest> requestCodec,
            IMultiplayerCodec<TResponse> responseCodec,
            Func<MultiplayerCommandContext, TRequest, OperationResult<TResponse>> handler,
            PredictionMode prediction = PredictionMode.None,
            int maximumPerSecond = 30,
            int maximumPayloadBytes = 16 * 1024)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            RequestCodec = requestCodec ?? throw new ArgumentNullException(nameof(requestCodec));
            ResponseCodec = responseCodec ?? throw new ArgumentNullException(nameof(responseCodec));
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            if (!Enum.IsDefined(typeof(PredictionMode), prediction)) throw new ArgumentOutOfRangeException(nameof(prediction));
            if (maximumPerSecond < 1 || maximumPerSecond > 1000) throw new ArgumentOutOfRangeException(nameof(maximumPerSecond));
            if (maximumPayloadBytes < 64 || maximumPayloadBytes > 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
            }

            if (requestCodec.MaximumEncodedBytes > maximumPayloadBytes ||
                responseCodec.MaximumEncodedBytes > maximumPayloadBytes)
            {
                throw new ArgumentException(
                    "Generated request and response codec bounds must not exceed the command payload limit.",
                    nameof(maximumPayloadBytes));
            }

            if (requestCodec.MaximumEncodedBytes < 0 || responseCodec.MaximumEncodedBytes < 0)
            {
                throw new ArgumentException("Codec bounds cannot be negative.", nameof(requestCodec));
            }

            Prediction = prediction;
            MaximumPerSecond = maximumPerSecond;
            MaximumPayloadBytes = maximumPayloadBytes;
        }

        /// <summary>Gets the stable mod-local command id.</summary>
        public string Id => Type.Id;

        /// <summary>Gets the generated typed command reference.</summary>
        public MultiplayerCommandType<TRequest, TResponse> Type { get; }

        /// <summary>Gets the generated request codec.</summary>
        public IMultiplayerCodec<TRequest> RequestCodec { get; }

        /// <summary>Gets the generated response codec.</summary>
        public IMultiplayerCodec<TResponse> ResponseCodec { get; }

        /// <summary>Gets the canonical transactional handler.</summary>
        public Func<MultiplayerCommandContext, TRequest, OperationResult<TResponse>> Handler { get; }

        /// <summary>Gets prediction behavior.</summary>
        public PredictionMode Prediction { get; }

        /// <summary>Gets the per-sender command rate limit.</summary>
        public int MaximumPerSecond { get; }

        /// <summary>Gets the maximum encoded request or response payload size.</summary>
        public int MaximumPayloadBytes { get; }
    }

    /// <summary>Reports eventual canonical acceptance or rejection of a submitted command.</summary>
    public sealed class MultiplayerCommandConfirmation<T> where T : class
    {
        /// <summary>Creates a command confirmation.</summary>
        public MultiplayerCommandConfirmation(NetworkTick submittedAt, NetworkTick confirmedAt, bool wasPredicted, OperationResult<T> result)
        {
            SubmittedAt = submittedAt;
            ConfirmedAt = confirmedAt;
            WasPredicted = wasPredicted;
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        /// <summary>Gets the local submission tick.</summary>
        public NetworkTick SubmittedAt { get; }

        /// <summary>Gets the canonical resolution tick.</summary>
        public NetworkTick ConfirmedAt { get; }

        /// <summary>Gets whether the owning client predicted this command.</summary>
        public bool WasPredicted { get; }

        /// <summary>
        /// Gets the canonical result. A successful DTO payload is codec-detached from provider storage and from
        /// confirmation payloads delivered to other callers.
        /// </summary>
        public OperationResult<T> Result { get; }
    }

    /// <summary>Provider SPI for a lifetime-owned command registration.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IMultiplayerCommandRegistration : IDisposable
    {
        /// <summary>Gets the stable mod-local command id.</summary>
        string Id { get; }

        /// <summary>Gets whether the handler remains registered.</summary>
        bool IsActive { get; }
    }

    /// <summary>
    /// Provides an owner-scoped transport-neutral facade for sequential sessions. Registrations live until disposed
    /// and providers reapply them when <see cref="MultiplayerSessionSnapshot.Id"/> changes.
    /// </summary>
    public interface IMultiplayerSession
    {
        /// <summary>Gets the latest immutable session snapshot.</summary>
        MultiplayerSessionSnapshot Snapshot { get; }

        /// <summary>
        /// Gets a token cancelled when the current session id is replaced or ends. The facade and its registrations
        /// remain valid across replacement; callers re-read this property for the next session.
        /// </summary>
        CancellationToken CurrentSessionToken { get; }

        /// <summary>Subscribes to readiness, participant, side, or tick changes.</summary>
        IDisposable SubscribeChanged(Action<MultiplayerSessionSnapshot> handler);

        /// <summary>Registers one snapshot-backed state value for the current owner and session.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        OperationResult<IReplicatedState<T>> RegisterState<T>(ReplicatedStateDefinition<T> definition) where T : class;

        /// <summary>Registers one typed bounded command handler.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        OperationResult<IMultiplayerCommandRegistration> RegisterCommand<TRequest, TResponse>(
            MultiplayerCommandDefinition<TRequest, TResponse> definition)
            where TRequest : class
            where TResponse : class;

        /// <summary>Registers local codecs and the canonical handler for one generated replicated-object type.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        OperationResult<IReplicatedObjectTypeRegistration> RegisterObjectType<TState, TInput>(
            ReplicatedObjectTypeDefinition<TState, TInput> definition)
            where TState : class
            where TInput : class;

        /// <summary>Submits one command and waits for canonical confirmation.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        Task<MultiplayerCommandConfirmation<TResponse>> SubmitAsync<TRequest, TResponse>(
            MultiplayerCommandType<TRequest, TResponse> commandType,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class;

        /// <summary>Creates a canonical server-owned or owner-predicted instance of a registered object type.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        OperationResult<IReplicatedObject<TState, TInput>> SpawnObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            TState initialState,
            ParticipantId? ownerId = null)
            where TState : class
            where TInput : class;

        /// <summary>Canonically despawns one object; non-server callers fail with NotAuthoritative.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        OperationResult<bool> DespawnObject(NetworkObjectId id);

        /// <summary>Gets all currently discoverable instances of one generated object type.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        IReadOnlyList<IReplicatedObject<TState, TInput>> GetObjects<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type)
            where TState : class
            where TInput : class;

        /// <summary>Tries to get one currently discoverable instance with the requested generated type.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        bool TryGetObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            NetworkObjectId id,
            out IReplicatedObject<TState, TInput>? replicatedObject)
            where TState : class
            where TInput : class;

        /// <summary>Subscribes to canonical spawn, state/ownership change, and despawn notifications for one type.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        IDisposable SubscribeObjects<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            Action<ReplicatedObjectChange<TState, TInput>> handler)
            where TState : class
            where TInput : class;

        /// <summary>Tries to map a process-local entity to a server-assigned network object identity.</summary>
        bool TryGetNetworkObjectId(IEntity entity, out NetworkObjectId id);

        /// <summary>Registers one bounded presentation-event codec and optional local handler.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        OperationResult<IPresentationEventRegistration> RegisterPresentation<TEvent>(
            PresentationEventDefinition<TEvent> definition) where TEvent : class;

        /// <summary>Publishes a canonical transient presentation event; non-authority callers fail.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        OperationResult<bool> PublishPresentation<TEvent>(
            PresentationEventType<TEvent> eventType,
            TEvent value,
            MultiplayerAudience audience = MultiplayerAudience.Everyone) where TEvent : class;
    }

    /// <summary>Convenience resolution for the dependency-scoped multiplayer provider.</summary>
    public static class MultiplayerContextExtensions
    {
        /// <summary>Resolves the required multiplayer provider declared by the mod manifest.</summary>
        public static IMultiplayerSession RequireMultiplayer(this IModContext context) =>
            context.RequireExtension<IMultiplayerSession>();

        /// <summary>Tries to resolve an optional multiplayer provider.</summary>
        public static bool TryGetMultiplayer(this IModContext context, out IMultiplayerSession? session) =>
            context.TryGetExtension(out session);
    }
}
