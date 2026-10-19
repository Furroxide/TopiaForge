using System;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>One join the player asked for: where it goes, how the overlay names it, and the choices made for it.</summary>
    internal sealed class JoinRequest
    {
        internal JoinRequest(IInternalJoinTarget target, string title, bool allowDirect, string? password = null, string? trustedServerId = null,
            LauncherHandoff? launcher = null, ILaunchedSession? launch = null)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Title = title ?? string.Empty;
            AllowDirect = allowDirect;
            Password = password;
            TrustedServerId = trustedServerId;
            Launcher = launcher;
            Launch = launch;
        }

        internal IInternalJoinTarget Target { get; }

        /// <summary>Gets the server's name as the overlay shows it, already redacted.</summary>
        internal string Title { get; }

        /// <summary>Gets the player's choice for this session: a direct path, allowed through the approved prompt.</summary>
        internal bool AllowDirect { get; }

        internal string? Password { get; }

        internal string? TrustedServerId { get; }

        /// <summary>Gets what the launcher can take this join over with; null when there is nothing.</summary>
        internal LauncherHandoff? Launcher { get; }

        /// <summary>
        /// Gets the launch this join carries out, when the launcher asked for it: its own name and privacy stand, the launcher
        /// decides any change to them, and it hears whether the join started or why not. Null for a join started in game.
        /// </summary>
        internal ILaunchedSession? Launch { get; }

        /// <summary>The join a launch planned: its pinned target and its own privacy, with the launcher as the way to plan it again.</summary>
        internal static JoinRequest ForLaunch(ILaunchedSession launch, string title)
        {
            if (launch == null) throw new ArgumentNullException(nameof(launch));
            var plan = launch.Plan;
            return new JoinRequest(plan.Target ?? throw new ArgumentException("A launched join has a target.", nameof(launch)), title, plan.AllowDirect,
                launcher: launch.Handoff, launch: launch);
        }

        internal JoinRequest Trusting(string serverId) => new JoinRequest(Target, Title, AllowDirect, Password, serverId, Launcher, Launch);

        /// <summary>The same join with a direct connection allowed, for this session only, as the approved prompt lets it.</summary>
        internal JoinRequest Direct() => new JoinRequest(Target, Title, allowDirect: true, Password, TrustedServerId, Launcher, Launch);

        /// <summary>The same join without a password, so the session asks for one on the connecting overlay.</summary>
        internal JoinRequest WithoutPassword() => new JoinRequest(Target, Title, AllowDirect, null, TrustedServerId, Launcher, Launch);
    }
}
