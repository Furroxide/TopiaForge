using System;

namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// The runtime's replication seam for one mod context: where the context runs, replica factories, canonical
    /// registration and spawn requests, command routing, network references, time authority and the microphone. Trusted
    /// first-party providers reach it through <see cref="IInternalReplicationContext"/>. Every member runs on the main
    /// thread, and what a context registers ends with its lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here carries or exposes a network address. Entities and players are named by opaque runtime ids, and a
    /// failure names a reason.
    /// </para>
    /// <para>
    /// Some members are stubs until the WS3 slice that owns their behaviour lands, and each says so. Until then a command
    /// runs here, as single player always has, and no player reference resolves. The realm, the route, the time authority
    /// and the microphone already answer by the execution model's rules, the network world (WS3.6) builds replicas,
    /// announces what is registered and names entities, and authority routing (WS3.7) forwards a joined player's spawns on
    /// its player's intent and binds its scripted ones as shadows of the host's. A context whose profile does not run in
    /// the current realm, such as a gamemode server on a joined player, is refused with
    /// <see cref="ModErrorCode.NotAuthoritative"/>, and a stopping one with <see cref="ModErrorCode.Cancelled"/>.
    /// </para>
    /// </remarks>
    internal interface IRuntimeReplication
    {
        // Worlds supplies drawing and authored identity; runtime owns authority, lifetime and private delivery.
        IViewControls WorldView(IInternalWorldViewRenderer renderer);
        IViewControls PlayerWorldView(IInternalWorldViewRenderer renderer, string player);
        void ActivateWorldView(IInternalWorldViewBinding binding);

        /// <summary>Gets this process's realm now. It allocates nothing, so a per-frame check may read it.</summary>
        InternalNetRealm Realm { get; }

        /// <summary>Gets how this context's mod takes part in sessions, read from its effective manifest.</summary>
        InternalNetworkClass NetworkClass { get; }

        /// <summary>Gets where this context's code runs: process-wide, as a world owner, as a gamemode server or as a participant.</summary>
        InternalExecutionContext ExecutionContext { get; }

        /// <summary>
        /// Says where a world-changing call from this context takes effect in the current realm, for code that runs because
        /// of <paramref name="intent"/>: here and canonically, forwarded to the host, as a shadow of the host's result, here
        /// only, or not at all. It allocates nothing.
        /// </summary>
        InternalRoute Route(InternalIntent intent);

        /// <summary>
        /// Registers how this process builds its replica of an entity of <paramref name="kind"/> that the host announced. A
        /// kind has one factory in the process; disposing the lease removes it.
        /// </summary>
        /// <remarks>
        /// A joined player's network world (WS3.6) builds the host's entities of this kind through the factory, and releases
        /// them through it when they end, when a baseline no longer lists them, or when the player leaves.
        /// </remarks>
        OperationResult<IDisposable> RegisterReplicaFactory(string kind, IInternalReplicaFactory factory);

        /// <summary>
        /// Registers an entity this context spawned, or built as world content, as a canonical network entity with the
        /// description peers build it from. Offline and on a listen host the runtime records it at once, so that opening this
        /// session to other players later can baseline it, and a listen host announces it to its players. Registering a
        /// recorded entity again replaces its description, and the record ends when the entity does, or when this context
        /// ends. A client-local mod's registration is kept local: nothing it does is networked.
        /// </summary>
        /// <remarks>
        /// On a joined player nothing is ever recorded as canonical (WS3.7). World content waits to bind, by its content key,
        /// to the host's entity of that key. A scripted registration is a shadow: the entity stays frozen here and binds in
        /// place to the host's spawn of the same mod and shadow key, and ends through the kind's spawn handler if the host's
        /// never comes or comes for another kind. A registration on the player's intent is a prediction, which the host
        /// makes again through the kind's spawn handler (<see cref="RegisterSpawnHandler"/>); without one on this player it is
        /// refused with <see cref="ModErrorCode.NotAuthoritative"/>.
        /// </remarks>
        OperationResult<bool> RegisterCanonical(IEntity entity, InternalSpawnDescriptor descriptor);

        /// <summary>Spawns through <paramref name="spawn"/> and registers what it returns as canonical; a failed spawn is returned as it is.</summary>
        /// <remarks>
        /// Offline and on a listen host the spawn runs here and is recorded. On a joined player it routes as
        /// <see cref="RegisterCanonical"/> does (WS3.7): a scripted spawn runs here as a shadow of the host's, and one on the
        /// player's intent runs here as a frozen prediction while the host makes it again through the kind's spawn handler;
        /// the host's verdict adopts it in place, under the same handle, or ends it through the handler with a toast. Without
        /// a spawn handler for the kind on this player, a spawn on the player's intent is refused with
        /// <see cref="ModErrorCode.NotAuthoritative"/> before anything runs.
        /// </remarks>
        OperationResult<IEntity> RequestSpawn(InternalSpawnDescriptor descriptor, Func<OperationResult<IEntity>> spawn);

        /// <summary>
        /// Registers how this process makes and ends this mod's entities of <paramref name="kind"/> for authority routing
        /// (WS3.7). A kind has one handler in the process; disposing the lease removes it.
        /// </summary>
        /// <remarks>
        /// On a listen host, a joined player's spawn of the kind on its player's intent is made through
        /// <see cref="IInternalSpawnHandler.Spawn"/>, inside that player's intent (<see cref="CurrentIntent"/> names them),
        /// and what it returns is recorded as that player's: announced to everyone, and ended through
        /// <see cref="IInternalSpawnHandler.Despawn"/> when they destroy it or leave. On a joined player the handler ends a
        /// prediction the host refused and a shadow that never bound. Every peer runs the same mod, so each registers the
        /// same handler. A trusted shared provider may opt in through <see cref="IInternalSharedSpawnHandler"/> to serve
        /// other shared consuming mods; the consumer still owns each spawn's profile, quota and lifetime.
        /// </remarks>
        OperationResult<IDisposable> RegisterSpawnHandler(string kind, IInternalSpawnHandler handler);

        /// <summary>
        /// Registers the handler that runs this mod's commands of <paramref name="kind"/>. A mod has one handler per kind;
        /// disposing the lease removes it.
        /// </summary>
        OperationResult<IDisposable> RegisterCommand(string kind, Func<InternalCommand, OperationResult<bool>> handler);

        /// <summary>Routes one of this mod's commands to its handler, with <paramref name="payload"/> of at most 16 KiB.</summary>
        /// <remarks>
        /// Stub: until authority routing (WS3.7) the handler runs here at once in every realm. From then a joined player
        /// sends the command to the host, whose handler runs it after checking the sender.
        /// </remarks>
        OperationResult<bool> SubmitCommand(string kind, byte[] payload);

        /// <summary>Makes a reference that names <paramref name="entity"/> on every peer.</summary>
        /// <remarks>
        /// The network world names replicated entities by their network root. The Players directory names local players
        /// and presence avatars by the provider's current player-lifetime key, never a process-local id or reusable slot.
        /// </remarks>
        bool TryCreateRef(IEntity entity, out InternalNetRef reference);

        /// <summary>Finds the entity a reference names in this process.</summary>
        /// <remarks>As for <see cref="TryCreateRef"/>: entities resolve while this session replicates them, and players from WS3.11.</remarks>
        bool TryResolveRef(InternalNetRef reference, out IEntity? entity);

        /// <summary>
        /// Gets whether this context may freeze, slow or step the session's time now: in single player every context may;
        /// online only a gamemode server on the host, and never a client-local mod's.
        /// </summary>
        InternalTimeAuthority TimeAuthority { get; }

        /// <summary>
        /// Gets whether the game's own pause freezes the world: in single player only, since online the shared world never
        /// freezes. Online the runtime's pause guard keeps the world at <see cref="SessionTime"/> while a player's pause menu is
        /// open, and Chronos stops yielding the clock to it.
        /// </summary>
        bool NativePauseFreezesWorld { get; }

        /// <summary>
        /// Takes the microphone for <paramref name="use"/>, naming why in <paramref name="reason"/>. The game's own robot
        /// recording comes first, robot dialogue next and voice chat last: a use is refused with
        /// <see cref="ModErrorCode.Conflict"/> while one that comes before it holds the microphone, and a lease is preempted
        /// while one that comes before it takes the microphone later. Disposing the lease gives the microphone back.
        /// </summary>
        OperationResult<IInternalMicrophoneLease> AcquireMicrophone(InternalMicrophoneUse use, string reason);

        /// <summary>
        /// Gets the player intent the calling code acts on now, as this context's mod sees it (WS3.4): what caused it, and
        /// whose it is, this process's own player or a participant a listen host acts for. Scripted code, another mod's code,
        /// code on another thread than the main one, an intent from before the realm last changed, and anything offline see
        /// none. Authority routing (WS3.7) asks it whether a spawn, a transform change or a destroy carries intent, and
        /// whose, and whether that intent is one of the player's direct actions or only an input-active frame
        /// (<see cref="InternalIntentRecord.IsDirectAction"/>, <see cref="InternalIntentRecord.IsInputFrame"/>). It allocates
        /// nothing.
        /// </summary>
        InternalIntentRecord CurrentIntent { get; }

        /// <summary>
        /// Says where a world-changing call from this context takes effect now: <see cref="Route"/> for
        /// <see cref="CurrentIntent"/>'s source. It allocates nothing.
        /// </summary>
        InternalRoute RouteCurrentIntent();

        /// <summary>
        /// Gets the game's clock, which Chronos drives the world's time scale through, so that Chronos and the runtime's
        /// pause guard act on one clock (WS3.13). It is Unity's time in the game, and null where the runtime has no clock of
        /// the game, as in a test host without one.
        /// </summary>
        IInternalGameClock? GameClock { get; }

        /// <summary>
        /// Gets the session's time as this process runs it (WS3.13): offline and on a listen host, what this process's time
        /// authority published last, real time until it publishes; on a joined player, the host's, real time until the
        /// host's first arrives. It allocates nothing.
        /// </summary>
        InternalSessionTime SessionTime { get; }

        /// <summary>
        /// Publishes the session's time as this process's time authority derives it: Chronos does, whenever its derived
        /// scale or mode changes. Offline it is kept, so that a game opened to other players starts from it. On a listen host
        /// every joined player follows it, at most ten updates a second with the newest winning, and a player that joins,
        /// rejoins or moves to a replaced session receives it at once. A joined player follows its host's time and never
        /// publishes: it is refused with <see cref="ModErrorCode.NotAuthoritative"/>, and a stopping context with
        /// <see cref="ModErrorCode.Cancelled"/>. It allocates nothing.
        /// </summary>
        /// <returns>True when the session's time changed, false when it already was <paramref name="time"/>.</returns>
        OperationResult<bool> PublishSessionTime(InternalSessionTime time);

        /// <summary>
        /// Registers how this process makes the bodies remote players' avatars wear (WS3.10): RobotKit offers the game's own
        /// robots. The process has one factory; a second is refused with <see cref="ModErrorCode.Conflict"/>, and disposing
        /// the lease removes it. Avatars made while no factory is registered, or whose body could not be made, wear the
        /// runtime's tinted capsule.
        /// </summary>
        OperationResult<IDisposable> RegisterAvatarBodyFactory(IInternalAvatarBodyFactory factory);
    }

    /// <summary>
    /// Builds this process's replica of an entity another peer announced, and ends it again. The network world (WS3.6)
    /// keeps the replica kinematic and moves it to the host's samples; the factory owns it otherwise. Main thread.
    /// </summary>
    internal interface IInternalReplicaFactory
    {
        /// <summary>Builds the replica <paramref name="descriptor"/> describes, or says why it cannot.</summary>
        OperationResult<IEntity> CreateReplica(InternalSpawnDescriptor descriptor);

        /// <summary>
        /// Ends a replica this factory built, because the host despawned its entity, a baseline no longer lists it, or this
        /// process left the session. It is called once per replica and must not throw.
        /// </summary>
        void ReleaseReplica(IEntity replica);
    }

    /// <summary>
    /// Makes and ends one provider kind's entities for authority routing (WS3.7): a listen host makes a joined player's
    /// requested entity through it, and every peer ends what it made through it. Main thread.
    /// </summary>
    internal interface IInternalSpawnHandler
    {
        /// <summary>
        /// Makes the canonical entity <paramref name="descriptor"/> describes, for the player the calling context's
        /// <see cref="IRuntimeReplication.CurrentIntent"/> names, or says why it cannot. A participant context's own code
        /// forwards no intent, so for its spawns <see cref="IRuntimeReplication.CurrentIntent"/> names no player; the runtime
        /// records the entity as that player's all the same. The runtime records what it returns; the handler must not
        /// register it itself.
        /// </summary>
        OperationResult<IEntity> Spawn(InternalSpawnDescriptor descriptor);

        /// <summary>
        /// Ends an entity this kind's spawn made: a player's object they destroyed or left behind, a joined player's
        /// prediction the host refused, or a shadow that never bound. It is called once per entity and must not throw.
        /// </summary>
        void Despawn(IEntity entity);
    }

    /// <summary>A hold on the microphone. Disposing it gives the microphone back; disposing it again does nothing.</summary>
    internal interface IInternalMicrophoneLease : IDisposable
    {
        /// <summary>Gets the use the microphone was taken for.</summary>
        InternalMicrophoneUse Use { get; }

        /// <summary>Gets whether a use that comes first holds the microphone now; the holder must not capture meanwhile.</summary>
        bool IsPreempted { get; }

        /// <summary>Gets whether the lease has been given back.</summary>
        bool IsReleased { get; }
    }
}
