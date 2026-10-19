using System;
using System.Diagnostics;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// The multiplayer provider: one session engine hosting this process's session, and an owner-scoped facade for
    /// every consuming mod. Until live transport arrives the session is solo: this process holds both sides, its
    /// player is the host, and canonical work runs inline. Calls made on the provider itself use its own owner
    /// partition, which no consuming mod can reach.
    /// </summary>
    internal sealed partial class MultiplayerProvider : IMultiplayerSession, IOwnerBoundExtensionFactory, IDisposable
    {
        internal const string LocalParticipantId = "local";
        internal const string LocalDisplayName = "Local player";
        private const ulong SoloSeed = 0x544f504941464f52UL;

        private readonly string providerModId;
        private readonly Func<long> clockMicros;
        private readonly SessionEngine engine;
        private readonly EngineSessionFacade reserved;
        private bool disposed;

        internal MultiplayerProvider(string providerModId, IEngineDiagnostics? diagnostics = null, Func<long>? clockMicros = null, LiveSessionsSetup? live = null)
        {
            if (!ProtocolLimits.IsValidIdentity(providerModId)) throw new ArgumentException("A provider mod id is required.", nameof(providerModId));
            this.providerModId = providerModId;
            this.clockMicros = clockMicros ?? MonotonicMicros;
            var options = new EngineOptions { Diagnostics = diagnostics, Voice = CreateVoiceOptions(live) };
            engine = SessionEngine.CreateHost(
                options,
                MultiplayerProcessKind.Interactive,
                SoloSessionId(providerModId),
                new SessionSeed(SoloSeed),
                new ParticipantId(LocalParticipantId),
                LocalDisplayName,
                this.clockMicros());
            voice = StartVoice(diagnostics, live?.VoiceWorkerThread ?? true);
            reserved = engine.CreateFacade(providerModId);
            if (live != null)
            {
                Live = new LiveSessions(engine, live.Files, live.Content, live.Factory ?? new SocketNetworkFactory(this.clockMicros), this.clockMicros,
                    () => DateTimeOffset.UtcNow.ToUnixTimeSeconds(), providerModId, live.Resolver, live.TransportOptions, live.NatOptions);
                if (live.JoinTimings != null) Live.JoinTimings = live.JoinTimings;
                Live.Services = live.Services;
            }
        }

        /// <summary>Gets live hosting and joining, or null while live sessions are switched off.</summary>
        internal LiveSessions? Live { get; }

        internal SessionEngine Engine => engine;

        /// <summary>
        /// Takes the session the launcher asked this process to host or join (launch request V5), once: null when there is
        /// none, or when it cannot be planned yet, which <paramref name="unsupported"/> explains. This only plans the
        /// session; starting it is the in-game flows' decision.
        /// </summary>
        internal LaunchSessionPlan? TakeLaunchSession(out LaunchSessionUnsupported unsupported) =>
            LaunchSessionPlan.FromTicket(LaunchSessionTickets.Take(), out unsupported);

        public MultiplayerSessionSnapshot Snapshot => reserved.Snapshot;

        public System.Threading.CancellationToken CurrentSessionToken => reserved.CurrentSessionToken;

        public IPlayerDirectory Players => reserved.Players;

        /// <summary>Advances the engine to the current time. The provider calls it once per game frame.</summary>
        internal void Pump()
        {
            if (disposed) return;
            Live?.Pump();
            var now = clockMicros();
            engine.Pump(now);
            voice?.Pump(now);
        }

        /// <summary>Sends what the frame queued after <see cref="Pump"/>, at the frame's end.</summary>
        internal void FlushOutbound()
        {
            if (disposed) return;
            engine.FlushOutbound();
        }

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(Type contractType, string ownerModId, IModLifetime lifetime) =>
            CreateOwnerFacade(contractType, ownerModId, lifetime);

        internal object CreateOwnerFacade(Type contractType, string ownerModId, IModLifetime lifetime)
        {
            if (contractType == null) throw new ArgumentNullException(nameof(contractType));
            if (lifetime == null) throw new ArgumentNullException(nameof(lifetime));
            if (contractType != typeof(IMultiplayerSession))
            {
                throw new ArgumentException(
                    "The multiplayer provider only supports owner facades for IMultiplayerSession.",
                    nameof(contractType));
            }

            if (!ProtocolLimits.IsValidIdentity(ownerModId) || string.Equals(ownerModId, providerModId, StringComparison.Ordinal))
            {
                throw new ArgumentException("A consuming mod id is required.", nameof(ownerModId));
            }

            return engine.CreateFacade(ownerModId, lifetime);
        }

        public IDisposable SubscribeChanged(Action<MultiplayerSessionSnapshot> handler) => reserved.SubscribeChanged(handler);

        public OperationResult<IReplicatedState<T>> RegisterState<T>(ReplicatedStateDefinition<T> definition) where T : class =>
            reserved.RegisterState(definition);

        public OperationResult<IMultiplayerCommandRegistration> RegisterCommand<TRequest, TResponse>(
            MultiplayerCommandDefinition<TRequest, TResponse> definition)
            where TRequest : class
            where TResponse : class =>
            reserved.RegisterCommand(definition);

        public OperationResult<IReplicatedObjectTypeRegistration> RegisterObjectType<TState, TInput>(
            ReplicatedObjectTypeDefinition<TState, TInput> definition)
            where TState : class
            where TInput : class =>
            reserved.RegisterObjectType(definition);

        public System.Threading.Tasks.Task<MultiplayerCommandConfirmation<TResponse>> SubmitAsync<TRequest, TResponse>(
            MultiplayerCommandType<TRequest, TResponse> commandType,
            TRequest request,
            System.Threading.CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class =>
            reserved.SubmitAsync(commandType, request, cancellationToken);

        public OperationResult<IReplicatedObject<TState, TInput>> SpawnObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            TState initialState,
            ParticipantId? ownerId = null)
            where TState : class
            where TInput : class =>
            reserved.SpawnObject(type, initialState, ownerId);

        public OperationResult<bool> DespawnObject(NetworkObjectId id) => reserved.DespawnObject(id);

        public System.Collections.Generic.IReadOnlyList<IReplicatedObject<TState, TInput>> GetObjects<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type)
            where TState : class
            where TInput : class =>
            reserved.GetObjects(type);

        public bool TryGetObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            NetworkObjectId id,
            out IReplicatedObject<TState, TInput>? replicatedObject)
            where TState : class
            where TInput : class =>
            reserved.TryGetObject(type, id, out replicatedObject);

        public IDisposable SubscribeObjects<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            Action<ReplicatedObjectChange<TState, TInput>> handler)
            where TState : class
            where TInput : class =>
            reserved.SubscribeObjects(type, handler);

        public bool TryGetNetworkObjectId(IEntity entity, out NetworkObjectId id) => reserved.TryGetNetworkObjectId(entity, out id);

        public OperationResult<IPresentationEventRegistration> RegisterPresentation<TEvent>(PresentationEventDefinition<TEvent> definition)
            where TEvent : class =>
            reserved.RegisterPresentation(definition);

        public OperationResult<bool> PublishPresentation<TEvent>(
            PresentationEventType<TEvent> eventType,
            TEvent value,
            MultiplayerAudience audience = MultiplayerAudience.Everyone)
            where TEvent : class =>
            reserved.PublishPresentation(eventType, value, audience);

        public OperationResult<bool> PublishPresentation<TEvent>(
            PresentationEventType<TEvent> eventType,
            TEvent value,
            PresentationTarget target)
            where TEvent : class =>
            reserved.PublishPresentation(eventType, value, target);

        public OperationResult<IDisposable> RegisterContract(MultiplayerContractDescriptor descriptor) => reserved.RegisterContract(descriptor);

        public IDisposable SubscribeTick(Action<MultiplayerTickContext> handler) => reserved.SubscribeTick(handler);

        public bool TryGetEntity(NetworkObjectId id, out IEntity? entity) => reserved.TryGetEntity(id, out entity);

        public bool TryGetAvatar(ParticipantId participantId, out IEntity? avatar) => reserved.TryGetAvatar(participantId, out avatar);

        public bool TryGetPose(ParticipantId participantId, out MultiplayerPose pose) => reserved.TryGetPose(participantId, out pose);

        /// <summary>Ends the session. Every facade's calls report that the session has ended.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            voice?.Dispose();
            Live?.Dispose();
            engine.Shutdown(MultiplayerSessionEndReason.HostEnded);
        }

        /// <summary>
        /// Names a new solo session uniquely, so a joined host's session and every later solo session differ from it even
        /// when two processes run the same provider.
        /// </summary>
        internal static MultiplayerSessionId SoloSessionId(string providerModId)
        {
            var random = new byte[8];
            Transport.Crypto.CryptoRandom.Fill(random);
            return new MultiplayerSessionId("solo:" + providerModId + ":" + Network.WireText.ToHex(random));
        }

        /// <summary>Reads a monotonic clock in microseconds without overflowing on long uptimes.</summary>
        internal static long MonotonicMicros()
        {
            var timestamp = Stopwatch.GetTimestamp();
            var frequency = Stopwatch.Frequency;
            return ((timestamp / frequency) * 1_000_000) + ((timestamp % frequency) * 1_000_000 / frequency);
        }
    }
}

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>What live sessions need from their host process: data files, installed content, and optionally the network.</summary>
    internal sealed class LiveSessionsSetup
    {
        internal LiveSessionsSetup(INetworkDataFiles files, ILiveSessionContent content)
        {
            Files = files ?? throw new System.ArgumentNullException(nameof(files));
            Content = content ?? throw new System.ArgumentNullException(nameof(content));
        }

        internal INetworkDataFiles Files { get; }
        internal ILiveSessionContent Content { get; }

        /// <summary>Gets or sets the network factory; null uses real sockets.</summary>
        internal INetworkFactory? Factory { get; set; }

        /// <summary>Gets or sets the target resolver; null resolves over real networks.</summary>
        internal IJoinTargetResolver? Resolver { get; set; }

        /// <summary>
        /// Gets or sets what every transport endpoint the live flows build starts from. Hosting, joining and browsing each
        /// ask for a fresh instance and set only their own privacy and capacity on it. Null uses the production defaults.
        /// Seeded randomness given here is in place before the endpoint draws anything, its cookie secrets included.
        /// </summary>
        internal System.Func<Transport.TransportOptions>? TransportOptions { get; set; }

        /// <summary>Gets or sets how long joins wait for their hosts; null uses the production waits.</summary>
        internal LiveJoinTimings? JoinTimings { get; set; }

        /// <summary>
        /// Gets or sets how the TopiaForge service is reached, for tests that put a fake one behind it. Null leaves it to the
        /// configured origin, from the session launch or the manager's configuration, once the runtime attaches.
        /// </summary>
        internal ControlPlane.InternetServices? Services { get; set; }

        /// <summary>
        /// Gets or sets what every NAT agent the live flows build starts from; each flow sets only its own privacy on it.
        /// Null uses the production defaults.
        /// </summary>
        internal System.Func<NatTraversal.NatAgentOptions>? NatOptions { get; set; }

        /// <summary>
        /// Gets or sets whether the provider runs voice chat beside these live sessions; null follows the live switch, as the
        /// game does, which turns both on together.
        /// </summary>
        internal bool? Voice { get; set; }

        /// <summary>
        /// Gets or sets whether the voice worker runs on a thread of its own. False leaves its steps to the caller, as tests on
        /// a virtual clock do.
        /// </summary>
        internal bool VoiceWorkerThread { get; set; } = true;
    }
}
