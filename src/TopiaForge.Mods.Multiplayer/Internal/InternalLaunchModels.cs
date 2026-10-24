using System;

namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// The session a launch asks this process to host or join: launch request V5's <c>session</c>, or the session of a live
    /// join the launcher staged for this running game (docs/internal/MultiplayerLaunchContract.md). The runtime has
    /// validated every value, and decided <see cref="AllowDirect"/> from the player's consent for this session alone. It
    /// hands the session to the provider once, through <see cref="IInternalMultiplayerControl.PlanLaunchSession"/>, and
    /// keeps none of it afterwards.
    /// </summary>
    /// <remarks>
    /// This is the one SPI type that carries an address, and only inward: a direct target's host and port, which only the
    /// provider's network layer resolves, and the game port a host listens on. No SPI member returns it or holds it, and
    /// neither it nor its target prints either value.
    /// </remarks>
    internal sealed class InternalLaunchSession
    {
        internal const int MaximumPort = 65535;

        internal InternalLaunchSession(string intentId, string displayName, bool allowDirect, InternalLaunchHost? host = null,
            InternalLaunchTarget? target = null)
        {
            if (string.IsNullOrEmpty(intentId) || intentId.Length > 128) throw new ArgumentException("An intent id is 1 to 128 characters.", nameof(intentId));
            if (!InternalText.IsDisplayText(displayName, InternalJoinSettings.MaximumDisplayNameLength))
            {
                throw new ArgumentException("A display name is 1 to 24 printable characters.", nameof(displayName));
            }

            if ((host == null) == (target == null)) throw new ArgumentException("A launch session either hosts or joins.", nameof(host));
            IntentId = intentId;
            DisplayName = displayName;
            AllowDirect = allowDirect;
            Host = host;
            Target = target;
        }

        /// <summary>Gets the launcher's intent: one host or join attempt, which every record of this session names.</summary>
        internal string IntentId { get; }

        /// <summary>Gets the name this player hosts or joins as.</summary>
        internal string DisplayName { get; }

        /// <summary>
        /// Gets whether this session may use direct paths beyond the local network, which show other players this player's
        /// address. It is true only when the player consented for this session; otherwise the session is relay-only.
        /// </summary>
        internal bool AllowDirect { get; }

        /// <summary>Gets what a host opens; null for a join.</summary>
        internal InternalLaunchHost? Host { get; }

        /// <summary>Gets the server a join goes to; null for a host.</summary>
        internal InternalLaunchTarget? Target { get; }

        /// <summary>
        /// Gets or sets the game port a host listens on: zero for any free port. A port other than zero is used exactly, and
        /// hosting fails when another program holds it. A join ignores it.
        /// </summary>
        internal int Port
        {
            get => port;
            set => port = value >= 0 && value <= MaximumPort ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        /// <summary>Gets or sets whether a host may ask the router to forward its port; it matters only when direct play is allowed.</summary>
        internal bool RouterForwarding { get; set; }

        /// <summary>Gets or sets whether the launcher runs in developer mode, which alone lets a host serve its packages.</summary>
        internal bool DeveloperMode { get; set; }

        /// <summary>Never names the target or the port.</summary>
        public override string ToString() => Host != null ? "launch session (host)" : "launch session (join " + Target + ")";

        private int port;
    }

    /// <summary>What a launched host opens. Its password is typed in game: <see cref="PasswordRequired"/> only says to ask.</summary>
    internal sealed class InternalLaunchHost
    {
        internal InternalLaunchHost(string name, int maxPlayers, InternalSessionVisibility visibility, bool voice, bool passwordRequired,
            bool servePackages = false)
        {
            if (!InternalText.IsDisplayText(name, InternalHostSettings.MaximumNameLength)) throw new ArgumentException("A server name is 1 to 48 printable characters.", nameof(name));
            if (maxPlayers < InternalHostSettings.MinimumPlayers || maxPlayers > InternalHostSettings.MaximumPlayers) throw new ArgumentOutOfRangeException(nameof(maxPlayers));
            if (!Enum.IsDefined(typeof(InternalSessionVisibility), visibility)) throw new ArgumentOutOfRangeException(nameof(visibility));
            Name = name;
            MaxPlayers = maxPlayers;
            Visibility = visibility;
            Voice = voice;
            PasswordRequired = passwordRequired;
            ServePackages = servePackages;
        }

        internal string Name { get; }

        internal int MaxPlayers { get; }

        internal InternalSessionVisibility Visibility { get; }

        internal bool Voice { get; }

        internal bool PasswordRequired { get; }

        /// <summary>Gets whether the host serves its package archives to joining players, which only developer mode allows.</summary>
        internal bool ServePackages { get; }
    }

    /// <summary>
    /// The server a launched join goes to, pinned as the launcher verified it: by its server id and by the SHA-256 of the exact
    /// descriptor the launcher planned the join from. Only a direct target holds an address.
    /// </summary>
    internal sealed class InternalLaunchTarget
    {
        private InternalLaunchTarget(InternalJoinTargetKind kind, string serverId, string descriptorSha256, string? inviteCode, string? host, int port)
        {
            if (!InternalText.IsServerId(serverId)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            if (!InternalText.IsSha256(descriptorSha256)) throw new ArgumentException("A descriptor hash is 64 lowercase hexadecimal characters.", nameof(descriptorSha256));
            Kind = kind;
            ServerId = serverId;
            DescriptorSha256 = descriptorSha256;
            InviteCode = inviteCode;
            Host = host;
            Port = port;
        }

        /// <summary>An invite, which the TopiaForge service resolves; <paramref name="code"/> is its canonical code.</summary>
        internal static InternalLaunchTarget Invite(string code, string serverId, string descriptorSha256)
        {
            if (string.IsNullOrEmpty(code) || code.Length != 10) throw new ArgumentException("An invite code is ten characters.", nameof(code));
            return new InternalLaunchTarget(InternalJoinTargetKind.Invite, serverId, descriptorSha256, code, null, 0);
        }

        /// <summary>A server heard on the local network (<paramref name="lan"/>) or listed by the TopiaForge service.</summary>
        internal static InternalLaunchTarget Listing(string serverId, string descriptorSha256, bool lan) =>
            new InternalLaunchTarget(lan ? InternalJoinTargetKind.Lan : InternalJoinTargetKind.ListedServer, serverId, descriptorSha256, null, null, 0);

        /// <summary>A host and port the launcher reached and verified: an address, which only the provider's network layer resolves.</summary>
        internal static InternalLaunchTarget Direct(string host, int port, string serverId, string descriptorSha256)
        {
            if (string.IsNullOrEmpty(host) || host.Length > 253) throw new ArgumentException("A direct target names its host.", nameof(host));
            if (port < 1 || port > InternalLaunchSession.MaximumPort) throw new ArgumentOutOfRangeException(nameof(port));
            return new InternalLaunchTarget(InternalJoinTargetKind.Direct, serverId, descriptorSha256, null, host, port);
        }

        internal InternalJoinTargetKind Kind { get; }

        internal string ServerId { get; }

        /// <summary>Gets the SHA-256 of the signed descriptor the launcher planned from; a host serving any other fails as admission changed.</summary>
        internal string DescriptorSha256 { get; }

        internal string? InviteCode { get; }

        /// <summary>Gets a direct target's host. It is an address.</summary>
        internal string? Host { get; }

        /// <summary>Gets a direct target's port; zero for any other kind.</summary>
        internal int Port { get; }

        /// <summary>Never names the host, the port or an invite's code.</summary>
        public override string ToString() => Kind.ToString();
    }

    /// <summary>
    /// What the provider made of a launch session: host settings, or an opaque join target pinned by the server and
    /// descriptor the launcher verified, with the launch's own name and privacy. Starting it is the runtime's: it opens the
    /// settings or joins the target through the realm, as the in-game flows do. Nothing here holds an address.
    /// </summary>
    internal interface IInternalLaunchPlan
    {
        /// <summary>Gets the launcher's intent this plan carries out.</summary>
        string IntentId { get; }

        /// <summary>Gets the name this player hosts or joins as, the launch's own.</summary>
        string DisplayName { get; }

        /// <summary>
        /// Gets whether the session may use direct paths beyond the local network: the launch's choice, and nothing else's.
        /// The provider holds this plan's hosting and its join to it, whatever settings they are started with.
        /// </summary>
        bool AllowDirect { get; }

        /// <summary>
        /// Gets the settings that open a launched host's session; null for a join. Only this instance carries the launch's game
        /// port: the provider recognizes it when hosting starts. Set its password from what the player types, never earlier.
        /// </summary>
        InternalHostSettings? Host { get; }

        /// <summary>Gets whether the host asks the player for a password before it opens.</summary>
        bool PasswordRequired { get; }

        /// <summary>
        /// Gets where a launched join goes; null for a host. A join to it refuses a host whose signed descriptor hashes other
        /// than the one the launcher planned from, as <see cref="InternalLiveFailure.AdmissionChanged"/>, before anything
        /// connects.
        /// </summary>
        IInternalJoinTarget? Target { get; }
    }
}
