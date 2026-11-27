using System;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Where a <see cref="SessionHandoff"/> asks the launcher to join. None holds an address: a server typed by address in
    /// game goes to the launcher as a <c>topiaforge://join/…</c> link instead, and <c>current</c> reuses the target of this
    /// process's own launch request, which the launcher keeps.
    /// </summary>
    public sealed class LaunchHandoffTarget
    {
        private LaunchHandoffTarget(string kind, string? inviteCode, string? serverId, string? source)
        {
            Kind = kind;
            InviteCode = inviteCode;
            ServerId = serverId;
            Source = source;
        }

        public static LaunchHandoffTarget Invite(string inviteCode) =>
            new LaunchHandoffTarget("invite", LaunchSessionValues.InviteCode(inviteCode, nameof(inviteCode)), null, null);

        public static LaunchHandoffTarget Listing(string serverId, string source) => new LaunchHandoffTarget("listing", null,
            LaunchSessionValues.Sha256(serverId, nameof(serverId)), LaunchSessionValues.Choice(source, nameof(source), LaunchContractValues.ListingSources));

        /// <summary>The session this process was launched to join, planned once more.</summary>
        public static LaunchHandoffTarget Current() => new LaunchHandoffTarget("current", null, null, null);

        public string Kind { get; }
        public string? InviteCode { get; }
        public string? ServerId { get; }
        public string? Source { get; }
    }

    /// <summary>
    /// The game asks the launcher that owns its process to run the join flow (<c>session-handoff-&lt;request key&gt;.json</c>,
    /// game to launcher). The launcher plans, asks consent, installs and restarts into the server's profile.
    /// </summary>
    public sealed class SessionHandoff
    {
        public const int SchemaVersion = 1;

        public SessionHandoff(string requestId, string handoffId, LaunchHandoffTarget target)
        {
            RequestId = LaunchContractValues.Token(requestId, nameof(requestId));
            HandoffId = LaunchContractValues.Token(handoffId, nameof(handoffId));
            Target = target ?? throw new ArgumentNullException(nameof(target));
        }

        /// <summary>Gets the launch request this process was started with.</summary>
        public string RequestId { get; }
        public string HandoffId { get; }
        public LaunchHandoffTarget Target { get; }
    }

    /// <summary>
    /// The launcher asks this running game to join a server live, its packages already matching the server's
    /// (<c>session-join-&lt;request key&gt;.json</c>, launcher to game). The game refuses a join whose package-set digest is
    /// not its own, and reports the join's progress and outcomes V2 under <see cref="JoinId"/>. The session is a join of an
    /// invite or a listing: a direct target needs a launch request, the only place an address may be staged.
    /// </summary>
    public sealed class SessionJoinRequest
    {
        public const int SchemaVersion = 1;

        public SessionJoinRequest(string requestId, string joinId, string packageSetDigest, LaunchSessionRequest session)
        {
            RequestId = LaunchContractValues.Token(requestId, nameof(requestId));
            JoinId = LaunchContractValues.Token(joinId, nameof(joinId));
            PackageSetDigest = LaunchContractValues.Digest(packageSetDigest);
            Session = session ?? throw new ArgumentNullException(nameof(session));
            if (session.Role != "join" || session.Target?.Kind == "direct" || JoinId == RequestId)
                throw new ArgumentException("A live join joins an invite or a listing under its own id.");
        }

        /// <summary>Gets the running process's launch request.</summary>
        public string RequestId { get; }
        public string JoinId { get; }
        public string PackageSetDigest { get; }
        public LaunchSessionRequest Session { get; }
    }
}
