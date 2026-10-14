namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// The session a launch request V5 asks this process to host or join, as the runtime hands it to the multiplayer
    /// provider. It is internal: mods never see it. The runtime has already validated every value. A direct target's host
    /// and port are an address, so nothing that holds a ticket may log or expose them.
    /// </summary>
    internal sealed class LaunchSessionTicket
    {
        internal LaunchSessionTicket(string role, string intentId, string displayName, bool allowDirect, int port, bool upnp, bool lan,
            string? serviceOrigin, LaunchSessionTicketTarget? target, LaunchSessionTicketHost? host, int devInstanceIndex = -1,
            int devInstanceCount = 0)
        {
            Role = role;
            IntentId = intentId;
            DisplayName = displayName;
            AllowDirect = allowDirect;
            Port = port;
            Upnp = upnp;
            Lan = lan;
            ServiceOrigin = serviceOrigin;
            Target = target;
            Host = host;
            DevInstanceIndex = devInstanceIndex;
            DevInstanceCount = devInstanceCount;
        }

        /// <summary>Gets <c>host</c> or <c>join</c>.</summary>
        internal string Role { get; }
        internal string IntentId { get; }
        internal string DisplayName { get; }

        /// <summary>Gets whether the player allowed direct play for this session; otherwise it is relay-only.</summary>
        internal bool AllowDirect { get; }

        /// <summary>Gets the local game port, or zero for any free port.</summary>
        internal int Port { get; }
        internal bool Upnp { get; }
        internal bool Lan { get; }
        internal string? ServiceOrigin { get; }
        internal LaunchSessionTicketTarget? Target { get; }
        internal LaunchSessionTicketHost? Host { get; }

        /// <summary>Gets the developer loop's peer slot, or -1 outside it.</summary>
        internal int DevInstanceIndex { get; }
        internal int DevInstanceCount { get; }

        public override string ToString() => "launch session (" + Role + ")";
    }

    /// <summary>
    /// The server a join goes to, pinned by its server id, the exact descriptor it was planned from and its package digest.
    /// The pinned server id spares the game a second trust-on-first-use check. Only a direct target has a host.
    /// </summary>
    internal sealed class LaunchSessionTicketTarget
    {
        internal LaunchSessionTicketTarget(string kind, string serverId, string descriptorSha256, string packageDigest, string? inviteCode,
            string? source, string? host, int port)
        {
            Kind = kind;
            ServerId = serverId;
            DescriptorSha256 = descriptorSha256;
            PackageDigest = packageDigest;
            InviteCode = inviteCode;
            Source = source;
            Host = host;
            Port = port;
        }

        /// <summary>Gets <c>invite</c>, <c>listing</c> or <c>direct</c>.</summary>
        internal string Kind { get; }
        internal string ServerId { get; }
        internal string DescriptorSha256 { get; }
        internal string PackageDigest { get; }
        internal string? InviteCode { get; }

        /// <summary>Gets a listing's source: <c>internet</c> or <c>lan</c>.</summary>
        internal string? Source { get; }

        /// <summary>Gets a direct target's host. It is an address.</summary>
        internal string? Host { get; }
        internal int Port { get; }

        public override string ToString() => Kind + " target";
    }

    /// <summary>What a host opens. A protected session's password is typed in game; it is never staged.</summary>
    internal sealed class LaunchSessionTicketHost
    {
        internal LaunchSessionTicketHost(string name, int maxPlayers, string visibility, bool voice, bool passwordRequired, bool servePackages)
        {
            Name = name;
            MaxPlayers = maxPlayers;
            Visibility = visibility;
            Voice = voice;
            PasswordRequired = passwordRequired;
            ServePackages = servePackages;
        }

        internal string Name { get; }
        internal int MaxPlayers { get; }

        /// <summary>Gets <c>public</c>, <c>invite</c> or <c>lan</c>.</summary>
        internal string Visibility { get; }
        internal bool Voice { get; }
        internal bool PasswordRequired { get; }
        internal bool ServePackages { get; }
    }

    /// <summary>
    /// Hands one ticket from the runtime to the multiplayer provider: the runtime offers it at startup, before mods load,
    /// and the provider takes it once. Taking it does not start anything; the in-game flows decide when to host or join.
    /// </summary>
    internal static class LaunchSessionTickets
    {
        private static readonly object Gate = new object();
        private static LaunchSessionTicket? pending;

        internal static void Offer(LaunchSessionTicket ticket)
        {
            lock (Gate) pending = ticket;
        }

        internal static LaunchSessionTicket? Take()
        {
            lock (Gate)
            {
                var ticket = pending;
                pending = null;
                return ticket;
            }
        }
    }
}
