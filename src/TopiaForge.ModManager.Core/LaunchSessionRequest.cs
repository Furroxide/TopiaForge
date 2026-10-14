using System;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// The player's explicit consent to direct play for one session, which the <c>direct</c> privacy requires. It names the
    /// session it was given for and the address disclosure version the player had acknowledged.
    /// </summary>
    public sealed class LaunchDirectConsent
    {
        public LaunchDirectConsent(string intentId, int disclosure)
        {
            IntentId = LaunchContractValues.Token(intentId, nameof(intentId));
            Disclosure = LaunchSessionValues.Ranged(disclosure, nameof(disclosure), 1, int.MaxValue);
        }

        public string IntentId { get; }
        public int Disclosure { get; }
    }

    /// <summary>How the session's transport may reach peers, and its local port (zero for any free port).</summary>
    public sealed class LaunchSessionTransport
    {
        public LaunchSessionTransport(string privacy, LaunchDirectConsent? directConsent, int port, bool upnp, bool lan)
        {
            Privacy = LaunchSessionValues.Choice(privacy, nameof(privacy), LaunchContractValues.Privacies);
            DirectConsent = directConsent;
            Port = LaunchSessionValues.Ranged(port, nameof(port), 0, 65535);
            Upnp = upnp;
            Lan = lan;
        }

        public string Privacy { get; }
        public LaunchDirectConsent? DirectConsent { get; }
        public int Port { get; }
        public bool Upnp { get; }
        public bool Lan { get; }
        public bool AllowsDirect => Privacy == "direct";
    }

    /// <summary>What a host opens. The password of a protected session is typed in game; it is never staged.</summary>
    public sealed class LaunchHostOptions
    {
        public LaunchHostOptions(string name, int maxPlayers, string visibility, bool voice, bool passwordRequired, bool servePackages)
        {
            Name = LaunchSessionValues.DisplayText(name, nameof(name), 1, LaunchSessionValues.MaximumServerName);
            MaxPlayers = LaunchSessionValues.Ranged(maxPlayers, nameof(maxPlayers), LaunchSessionValues.MinimumPlayers, LaunchSessionValues.MaximumPlayers);
            Visibility = LaunchSessionValues.Choice(visibility, nameof(visibility), LaunchContractValues.Visibilities);
            Voice = voice;
            PasswordRequired = passwordRequired;
            ServePackages = servePackages;
        }

        public string Name { get; }
        public int MaxPlayers { get; }
        public string Visibility { get; }
        public bool Voice { get; }
        public bool PasswordRequired { get; }

        /// <summary>Gets whether the host serves its package archives to joining players (developer mode only).</summary>
        public bool ServePackages { get; }
    }

    /// <summary>One peer of the developer loop's <c>--peers N</c>: its slot among <see cref="Count"/> peers.</summary>
    public sealed class LaunchDevInstance
    {
        public LaunchDevInstance(int index, int count)
        {
            Count = LaunchSessionValues.Ranged(count, nameof(count), 2, 8);
            Index = LaunchSessionValues.Ranged(index, nameof(index), 0, Count - 1);
        }

        public int Index { get; }
        public int Count { get; }
    }

    /// <summary>
    /// The server a join goes to, pinned by its server id, the SHA-256 of the exact descriptor it was planned from, and that
    /// descriptor's package digest. Only a direct target holds an address, and only in the session's own launch request;
    /// <see cref="ToString"/> never shows it.
    /// </summary>
    public sealed class LaunchSessionTarget
    {
        private LaunchSessionTarget(string kind, string serverId, string descriptorSha256, string packageDigest, string? inviteCode,
            string? source, string? host, int port)
        {
            Kind = kind;
            ServerId = LaunchSessionValues.Sha256(serverId, nameof(serverId));
            DescriptorSha256 = LaunchSessionValues.Sha256(descriptorSha256, nameof(descriptorSha256));
            PackageDigest = LaunchSessionValues.Sha256(packageDigest, nameof(packageDigest));
            InviteCode = inviteCode;
            Source = source;
            Host = host;
            Port = port;
        }

        public static LaunchSessionTarget Invite(string inviteCode, string serverId, string descriptorSha256, string packageDigest) =>
            new LaunchSessionTarget("invite", serverId, descriptorSha256, packageDigest,
                LaunchSessionValues.InviteCode(inviteCode, nameof(inviteCode)), null, null, 0);

        public static LaunchSessionTarget Listing(string source, string serverId, string descriptorSha256, string packageDigest) =>
            new LaunchSessionTarget("listing", serverId, descriptorSha256, packageDigest, null,
                LaunchSessionValues.Choice(source, nameof(source), LaunchContractValues.ListingSources), null, 0);

        public static LaunchSessionTarget Direct(string host, int port, string serverId, string descriptorSha256, string packageDigest) =>
            new LaunchSessionTarget("direct", serverId, descriptorSha256, packageDigest, null, null,
                LaunchSessionValues.DirectHost(host, nameof(host)), LaunchSessionValues.Ranged(port, nameof(port), 1, 65535));

        /// <summary>Gets <c>invite</c>, <c>listing</c> or <c>direct</c>.</summary>
        public string Kind { get; }
        public string ServerId { get; }
        public string DescriptorSha256 { get; }
        public string PackageDigest { get; }
        public string? InviteCode { get; }

        /// <summary>Gets a listing's source: <c>internet</c> or <c>lan</c>.</summary>
        public string? Source { get; }

        /// <summary>Gets a direct target's host. It is an address.</summary>
        public string? Host { get; }
        public int Port { get; }

        /// <summary>Gets whether the control plane must be reached to find the server.</summary>
        public bool NeedsService => Kind == "invite" || (Kind == "listing" && Source == "internet");

        /// <summary>Gets whether a direct target stays on the local network, which relay-only privacy allows.</summary>
        public bool OnLocalNetwork => Kind == "direct" && LaunchSessionHosts.IsLocalNetwork(Host!);

        public override string ToString() => Kind + " target";
    }

    /// <summary>
    /// The <c>session</c> object of launch request V5: who hosts or joins, under what privacy, and where. A host carries
    /// <see cref="Host"/>, a join <see cref="Target"/>. Direct privacy carries the player's consent for this session; under
    /// relay-only a direct target may only name a host on the local network, and any other is refused for lacking that
    /// consent. Invites, Internet listings, and public or invite-only hosting need a service origin; LAN listings and LAN or
    /// public hosting need LAN discovery on. Passwords are typed in game and never staged.
    /// </summary>
    public sealed class LaunchSessionRequest
    {
        public LaunchSessionRequest(string role, string intentId, string displayName, LaunchSessionTransport transport,
            string? serviceOrigin = null, LaunchSessionTarget? target = null, LaunchHostOptions? host = null,
            LaunchDevInstance? devInstance = null)
        {
            Role = LaunchSessionValues.Choice(role, nameof(role), LaunchContractValues.SessionRoles);
            IntentId = LaunchContractValues.Token(intentId, nameof(intentId));
            DisplayName = LaunchSessionValues.DisplayText(displayName, nameof(displayName), LaunchSessionValues.MinimumDisplayName,
                LaunchSessionValues.MaximumDisplayName);
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            ServiceOrigin = serviceOrigin == null ? null : LaunchSessionValues.ServiceOrigin(serviceOrigin, nameof(serviceOrigin));
            Target = target;
            Host = host;
            DevInstance = devInstance;
            var consent = transport.DirectConsent;
            if ((Role == "host") != (host != null) || (Role == "join") != (target != null) || (Role == "join" && transport.Upnp)
                || transport.AllowsDirect != (consent != null) || (consent != null && consent.IntentId != IntentId))
                throw new ArgumentException("Inconsistent launch session.");
            if (target != null && target.Kind == "direct" && !transport.AllowsDirect && !target.OnLocalNetwork)
                throw new ArgumentException("A direct target beyond the local network needs consent to direct play.");
            if (target != null && ((target.Kind == "listing" && target.Source == "lan" && !transport.Lan) || (target.NeedsService && ServiceOrigin == null)))
                throw new ArgumentException("Inconsistent launch session.");
            var visibility = host?.Visibility;
            if (((visibility == "public" || visibility == "lan") && !transport.Lan) || ((visibility == "public" || visibility == "invite") && ServiceOrigin == null))
                throw new ArgumentException("Inconsistent launch session.");
        }

        /// <summary>Gets <c>host</c> or <c>join</c>.</summary>
        public string Role { get; }

        /// <summary>Gets the launcher's intent: one host or join attempt, kept across the restarts and re-plans it takes.</summary>
        public string IntentId { get; }
        public string DisplayName { get; }
        public LaunchSessionTransport Transport { get; }
        public string? ServiceOrigin { get; }
        public LaunchSessionTarget? Target { get; }
        public LaunchHostOptions? Host { get; }
        public LaunchDevInstance? DevInstance { get; }
        public string Command => Role == "host" ? "host-session" : "join-session";

        public override string ToString() => "launch session (" + Role + (Target == null ? string.Empty : ", " + Target) + ")";
    }
}
