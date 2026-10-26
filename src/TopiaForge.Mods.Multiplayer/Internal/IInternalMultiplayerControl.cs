using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// The multiplayer provider's control surface for TopiaForge's own runtime: hosting, joining, moderation, system
    /// channels, readiness and the frame. Only the reserved multiplayer provider may publish it in the service registry,
    /// and ModManager's composition root is its caller; the in-game interface reaches it through the runtime. Every member
    /// runs on the main thread, which owns the session engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here carries or exposes a network address. A join target is an opaque handle the provider makes from what
    /// the player typed or followed: a session link, an invite code, a LAN listing, or a host and port that only the
    /// provider's network layer resolves. Failures name a reason, never an address.
    /// </para>
    /// <para>
    /// While a runtime is attached it drives the frame: <see cref="Pump"/> first, before world events and mods, and
    /// <see cref="Flush"/> last, after mods and the runtime's own sends. Without one the provider pumps itself from its
    /// own update subscription, as it did before the runtime existed.
    /// </para>
    /// </remarks>
    internal interface IInternalMultiplayerControl
    {
        /// <summary>Gets whether live sessions exist in this process. While they are off, hosting and joining are unavailable.</summary>
        bool IsLiveAvailable { get; }

        /// <summary>Gets this process's role. It allocates nothing, so a per-frame hook may read it.</summary>
        InternalSessionRole Role { get; }

        /// <summary>Gets whether a readiness hold keeps this player short of Ready.</summary>
        bool IsReadinessHeld { get; }

        /// <summary>Gets the voice service, or null while voice is unavailable.</summary>
        IInternalVoiceService? Voice { get; }

        /// <summary>Gets the sessions heard on the local network while browsing; empty otherwise.</summary>
        IReadOnlyList<InternalLanListing> LanSessions { get; }

        /// <summary>
        /// Gets the session as this process's engine holds it now: the local player, the host's player, and every
        /// participant with its name and whether its link is up, a player whose link was lost included for as long as its
        /// grace keeps its record. The same instance comes back while nothing changes, so a per-frame hook may read it and
        /// compare.
        /// </summary>
        MultiplayerSessionSnapshot SessionSnapshot { get; }

        /// <summary>
        /// Attaches the runtime that drives the frame and supplies content. Returns false, changing nothing, when another
        /// runtime is attached.
        /// </summary>
        bool AttachRuntime(IInternalMultiplayerRuntimeHost runtime);

        /// <summary>
        /// Detaches <paramref name="runtime"/> if it is the attached one; the provider then pumps itself again. The runtime
        /// detaches as shutdown begins, before any mod stops, so the provider saves its own records here, while its files
        /// still take writes.
        /// </summary>
        void DetachRuntime(IInternalMultiplayerRuntimeHost runtime);

        /// <summary>Frame step 1: takes what the network received and pumps the session engine.</summary>
        void Pump();

        /// <summary>Frame step 4: sends what the frame queued since <see cref="Pump"/>.</summary>
        void Flush();

        /// <summary>Gets hosting, joining and browsing as one snapshot for the interface. It allocates; read it on change.</summary>
        InternalLiveStatus GetStatus();

        /// <summary>
        /// Opens this session to other players. It starts at a later pump, once the identity is ready; the status reports
        /// how it went. Refused while joining, while already hosting, or with a standalone mod enabled.
        /// </summary>
        OperationResult<bool> StartHosting(InternalHostSettings settings);

        /// <summary>Stops hosting: every player's session ends with the host's reason, and this player plays on alone.</summary>
        void StopHosting();

        /// <summary>
        /// Makes a join target from what the player typed or followed: a <c>topiaforge://join/</c> link, an invite code, or
        /// a host and port. The handle keeps the text inside the provider; false when the text is none of these.
        /// </summary>
        bool TryCreateJoinTarget(string text, out IInternalJoinTarget? target);

        /// <summary>
        /// Starts joining <paramref name="target"/>. This player's session stays as it is until the link to the host is
        /// up, so a join that fails early changes nothing. Refused while hosting or while another join runs.
        /// </summary>
        OperationResult<bool> Join(IInternalJoinTarget target, InternalJoinSettings settings);

        /// <summary>Gives the password a join is waiting for. It is used once and never stored.</summary>
        OperationResult<bool> SupplyPassword(string password);

        /// <summary>Leaves the joined session, or stops a join in progress. Leaving is never refused.</summary>
        void Leave();

        /// <summary>
        /// Looks a server up without joining it: its verified description and whether this game's content fits. Unless
        /// <paramref name="allowDirect"/>, no host beyond the local network is contacted. An invite or a listed server is
        /// looked up through the TopiaForge service alone, which never connects to the host.
        /// </summary>
        IInternalServerQuery Query(IInternalJoinTarget target, bool allowDirect);

        /// <summary>Starts listening for sessions on the local network. Browsing never answers a host beyond it.</summary>
        void StartBrowsing();

        /// <summary>Stops browsing the local network.</summary>
        void StopBrowsing();

        /// <summary>Ends one player's connection with <paramref name="message"/>. Host only.</summary>
        OperationResult<bool> Kick(ParticipantId participant, string message);

        /// <summary>Bans the identity behind <paramref name="participant"/> from this host and ends its connection. Host only.</summary>
        OperationResult<bool> Ban(ParticipantId participant);

        /// <summary>Lifts the ban <paramref name="banId"/> names, from <see cref="GetBans"/>.</summary>
        OperationResult<bool> Unban(string banId);

        /// <summary>Gets this host's bans, newest last.</summary>
        IReadOnlyList<InternalBanEntry> GetBans();

        /// <summary>
        /// Mutes or unmutes <paramref name="participant"/> for everyone. The host drops a muted player's chat and voice at
        /// ingress, so other players never receive it. The mute follows the player's identity until hosting stops. Host only.
        /// </summary>
        OperationResult<bool> SetServerMute(ParticipantId participant, bool muted);

        /// <summary>
        /// Gets whether the host muted <paramref name="participant"/>, by hand or automatically for abusing voice chat; while
        /// it lasts the host drops their chat and voice at ingress. A host answers from its moderation, and a joined player
        /// from the host's voice roster, so a player can see that the host muted them.
        /// </summary>
        bool IsServerMuted(ParticipantId participant);

        /// <summary>Gets whether <paramref name="participant"/> has finished joining: it holds the session's world and is Ready.</summary>
        bool IsParticipantReady(ParticipantId participant);

        /// <summary>
        /// Replaces the session in place, as a new gamemode or world session does: registrations survive, values return to
        /// their defaults, objects despawn, and every joined player resynchronizes. Host only.
        /// </summary>
        OperationResult<bool> ReplaceSession();

        /// <summary>Opens the runtime's channel of <paramref name="kind"/>. One owner at a time; disposing it closes it.</summary>
        OperationResult<IInternalSystemChannel> OpenSystemChannel(InternalSystemChannelKind kind);

        /// <summary>
        /// Holds this player short of Ready while the runtime prepares, for example while it loads the host's world and
        /// baseline. A joining player's session stays Synchronizing, and its host is not told it is ready, until every hold
        /// is disposed. A host is always ready, so its holds change nothing.
        /// </summary>
        /// <remarks>
        /// Holds are bounded, so a host that sends its snapshot and then withholds the rest cannot keep a player waiting
        /// for ever. Once the snapshot has arrived, the join ends as timed out after
        /// <see cref="InternalReadinessLimits.IdleTimeoutSeconds"/> without progress, and after
        /// <see cref="InternalReadinessLimits.MaximumSeconds"/> in all from the first hold, however often the host replaces
        /// the session before the player is Ready. A system message from the host counts as progress, and so does
        /// <see cref="IInternalReadinessHold.ReportProgress"/> while the runtime works on its own.
        /// </remarks>
        IInternalReadinessHold HoldReadiness(string reason);

        /// <summary>
        /// Sets the source the session asks which network object an entity is. One at a time; disposing the returned lease
        /// removes it. Until a source is set, the session knows no entities.
        /// </summary>
        IDisposable SetEntityIdentitySource(IInternalEntityIdentitySource source);

        /// <summary>Sets the source of players' avatars and poses. One at a time; disposing the returned lease removes it.</summary>
        IDisposable SetAvatarSource(IInternalAvatarSource source);

        /// <summary>Gets the provider's counters, for diagnostics. They name participants and channels, never addresses.</summary>
        InternalMultiplayerStatistics GetStatistics();

        /// <summary>
        /// Creates another invite for the session this player hosts through the TopiaForge service, and makes it the one
        /// <see cref="InternalLiveStatus.Internet"/> shows once the service answers. Earlier invites keep working until they
        /// expire or are withdrawn. Host only; refused while the session is not registered with the service.
        /// </summary>
        OperationResult<bool> CreateInvite();

        /// <summary>
        /// Withdraws the invite <paramref name="code"/> names. Nobody can join with it again, and players who joined with it
        /// lose their relay when its credential next renews, within ten minutes, as the service ends their tickets. Host only.
        /// </summary>
        OperationResult<bool> RevokeInvite(string code);

        /// <summary>
        /// Takes the session a launch asked for and plans it: host settings, or a join target pinned by the server and
        /// descriptor the launcher verified. It starts nothing; the runtime opens or joins the plan through the realm. A join
        /// to a planned target, and hosting with a plan's settings, keep the launch's own privacy whatever settings they are
        /// started with. Refused as <see cref="ModErrorCode.Unavailable"/> while live sessions are off, without an attached
        /// runtime, or when the session needs the TopiaForge service and none is configured, and as
        /// <see cref="ModErrorCode.InvalidArgument"/> for a target the network layer cannot read. The provider keeps nothing of
        /// <paramref name="session"/> but what the plan's own handles need.
        /// </summary>
        OperationResult<IInternalLaunchPlan> PlanLaunchSession(InternalLaunchSession session);
    }

    /// <summary>One readiness hold. Disposing it releases the hold; disposing it again does nothing.</summary>
    internal interface IInternalReadinessHold : IDisposable
    {
        /// <summary>
        /// Says the runtime is still making progress, as it loads a large world, so the hold's idle bound starts again. It
        /// never extends a hold past <see cref="InternalReadinessLimits.MaximumSeconds"/>. Main thread only.
        /// </summary>
        void ReportProgress();
    }

    /// <summary>
    /// How long a joining player waits to be Ready, and how long its host waits for it. Until the player has been Ready, it
    /// gives the host <see cref="SynchronizeSeconds"/> for each snapshot and its own holds <see cref="MaximumSeconds"/> from
    /// the first of them, and waits <see cref="AllowanceSeconds"/> in all, from link-up or from the arrival of the
    /// replacement that took it out of the session. The host waits <see cref="HostSeconds"/>, a minute longer, from its
    /// acceptance, or from that replacement and again from the player's report that it arrived, however long it waited
    /// behind earlier traffic; replacements while the player is still joining start neither wait over. So an honest player
    /// always gives up first, and a player that stalls never keeps its slot.
    /// </summary>
    internal static class InternalReadinessLimits
    {
        /// <summary>The longest the host may take to send a snapshot: from link-up, or from the replacement that asked for it.</summary>
        internal const int SynchronizeSeconds = 120;

        /// <summary>The longest a hold may go without progress.</summary>
        internal const int IdleTimeoutSeconds = 60;

        /// <summary>The longest holds may last in all, progress or not, from the first hold until the player is Ready.</summary>
        internal const int MaximumSeconds = 300;

        /// <summary>The longest a joining player waits in all until it has been Ready: a snapshot's wait and the holds'.</summary>
        internal const int AllowanceSeconds = SynchronizeSeconds + MaximumSeconds;

        /// <summary>
        /// How long a host waits for a player to acknowledge its snapshot, from the join's acceptance, or from the player's
        /// report that a replacement reached it: the player's whole allowance, and a minute for the report to reach the host.
        /// </summary>
        internal const int HostSeconds = AllowanceSeconds + 60;
    }

    /// <summary>The role this process plays in its session.</summary>
    internal enum InternalSessionRole
    {
        /// <summary>Single player: the solo session, open to nobody. A join still resolving is solo until its link is up.</summary>
        Solo = 0,

        /// <summary>A listen host: this player's session, open to other players.</summary>
        ListenHost = 1,

        /// <summary>Joined to another player's session, from the moment the link to its host is up.</summary>
        Client = 2
    }

    /// <summary>
    /// Answers which network object an entity is, and the reverse. The runtime's network world implements it; the session's
    /// <c>TryGetNetworkObjectId</c> and <c>TryGetEntity</c> ask it. Main thread only.
    /// </summary>
    internal interface IInternalEntityIdentitySource
    {
        bool TryGetNetworkObjectId(IEntity entity, out NetworkObjectId id);

        bool TryGetEntity(NetworkObjectId id, out IEntity? entity);
    }

    /// <summary>
    /// Answers each player's avatar and latest pose. The runtime's presence layer implements it; the session's
    /// <c>TryGetAvatar</c> and <c>TryGetPose</c> ask it. Main thread only.
    /// </summary>
    internal interface IInternalAvatarSource
    {
        bool TryGetAvatar(ParticipantId participant, out IEntity? avatar);

        bool TryGetPose(ParticipantId participant, out MultiplayerPose pose);
    }
}
