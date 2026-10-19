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
    /// Some members are stubs until the WS3 slice that owns their behaviour lands, and each says so. Until then a spawn
    /// request and a command run here, as single player always has, and no player reference resolves. The realm, the
    /// route, the time authority and the microphone already answer by the execution model's rules, and the network world
    /// (WS3.6) builds replicas, announces what is registered and names entities. A context whose profile does not
    /// run in the current realm, such as a gamemode server on a joined player, is refused with
    /// <see cref="ModErrorCode.NotAuthoritative"/>, and a stopping one with <see cref="ModErrorCode.Cancelled"/>.
    /// </para>
    /// </remarks>
    internal interface IRuntimeReplication
    {
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
        /// ends. A client-local mod's registration is kept local: nothing it does is networked. On a joined player nothing is
        /// recorded: world content waits to bind, by its content key, to the host's entity of that key.
        /// </summary>
        /// <remarks>Stub: from WS3.7 a joined player's other registrations bind as shadows of the host's.</remarks>
        OperationResult<bool> RegisterCanonical(IEntity entity, InternalSpawnDescriptor descriptor);

        /// <summary>Spawns through <paramref name="spawn"/> and registers what it returns as canonical; a failed spawn is returned as it is.</summary>
        /// <remarks>
        /// Stub: until authority routing (WS3.7) the spawn runs here in every realm, as in single player, and a joined
        /// player's is recorded as nothing. From then a joined player forwards it to the host, predicts it locally and is
        /// told whether the host adopted it.
        /// </remarks>
        OperationResult<IEntity> RequestSpawn(InternalSpawnDescriptor descriptor, Func<OperationResult<IEntity>> spawn);

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
        /// The network world (WS3.6) names the entities this session replicates, a part of one by its network root. Stub for
        /// players: false until the players directory (WS3.11) names them.
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

        /// <summary>Gets whether the game's own pause freezes the world: in single player only, since online the shared world never freezes.</summary>
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
