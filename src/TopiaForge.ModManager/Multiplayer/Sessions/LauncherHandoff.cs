using System;
using System.Globalization;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// What INSTALL &amp; JOIN IN LAUNCHER, or the failure view's Open launcher, hands the TopiaForge launcher: a
    /// <c>topiaforge://join/</c> link the system opens, and, when the server can be named without an address, the handoff the
    /// launcher that owns this game takes instead (docs/internal/MultiplayerLaunchContract.md, section 6). A server typed by
    /// address has a link only, since a handoff never carries an address; the current session's has a handoff only.
    /// </summary>
    internal sealed class LauncherHandoff
    {
        private LauncherHandoff(string? link, LaunchHandoffTarget? target)
        {
            Link = link;
            Target = target;
        }

        /// <summary>Gets the link the system opens when no launcher owns this game; null for the current session.</summary>
        internal string? Link { get; }

        /// <summary>Gets what the handoff names: an invite, a listing or the current session; null for a typed address.</summary>
        internal LaunchHandoffTarget? Target { get; }

        /// <summary>A session heard on the local network, by its server id.</summary>
        internal static LauncherHandoff? ForLan(string? serverId) =>
            InternalText.IsServerId(serverId) ? new LauncherHandoff(LauncherJoinLink.ForServer(serverId), LaunchHandoffTarget.Listing(serverId!, "lan")) : null;

        /// <summary>
        /// What a session link names: an invite's link hands the invite over, a listed server's its listing, and a direct link,
        /// which holds the address the player typed, only itself. Null for anything that is not a session link.
        /// </summary>
        internal static LauncherHandoff? ForLink(string? link)
        {
            if (!LauncherJoinLink.IsSessionLink(link)) return null;
            var rest = link!.Substring(LauncherJoinLink.Prefix.Length);
            var invite = rest.IndexOf('/') < 0 ? LauncherJoinLink.NormalizeInviteCode(rest) : null;
            if (invite != null) return new LauncherHandoff(link, LaunchHandoffTarget.Invite(invite));
            if (rest.StartsWith("server/", StringComparison.Ordinal)) return new LauncherHandoff(link, LaunchHandoffTarget.Listing(rest.Substring(7), "internet"));
            return new LauncherHandoff(link, null);
        }

        /// <summary>The session this game was launched to join, which the launcher plans once more.</summary>
        internal static LauncherHandoff Current() => new LauncherHandoff(null, LaunchHandoffTarget.Current());

        /// <summary>A live join's target, an invite or a listing, as the launcher named it; null for anything else.</summary>
        internal static LauncherHandoff? ForTarget(LaunchSessionTarget? target)
        {
            if (target == null) return null;
            if (target.Kind == "invite" && target.InviteCode != null)
                return new LauncherHandoff(LauncherJoinLink.ForInvite(target.InviteCode), LaunchHandoffTarget.Invite(target.InviteCode));
            if (target.Kind == "listing" && target.Source != null)
                return new LauncherHandoff(LauncherJoinLink.ForServer(target.ServerId), LaunchHandoffTarget.Listing(target.ServerId, target.Source));
            return null;
        }
    }

    /// <summary>
    /// Hands joins to the TopiaForge launcher. When the launcher owns this game, a join it can name without an address goes as
    /// a <c>session-handoff</c> file, which the launcher takes and joins through its owned restart; the file is written on the
    /// background queue, since a refused write waits for the launcher's read. Otherwise, or for a typed address, the system
    /// opens the join's link, as before. A handoff that cannot be written falls back to the link when there is one.
    /// </summary>
    internal sealed class LauncherHandoffs
    {
        internal const string NotDelivered = "The TopiaForge launcher could not take the join. Open the launcher and join from there.";

        private readonly IMultiplayerSessionEnvironment environment;
        private readonly MultiplayerBackgroundQueue queue;
        private readonly Action<string, ScreenTone> notify;

        internal LauncherHandoffs(IMultiplayerSessionEnvironment environment, MultiplayerBackgroundQueue queue, Action<string, ScreenTone> notify)
        {
            this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            this.notify = notify ?? throw new ArgumentNullException(nameof(notify));
        }

        /// <summary>Gets whether <paramref name="handoff"/> can reach the launcher: by its link, or as a handoff to a launcher that owns this game.</summary>
        internal bool CanHandOff(LauncherHandoff? handoff) =>
            handoff != null && (handoff.Link != null || (handoff.Target != null && environment.Launcher?.RequestId != null));

        /// <summary>Hands <paramref name="handoff"/> to the launcher; <paramref name="done"/> is the toast once it is on its way.</summary>
        internal void HandOff(LauncherHandoff handoff, string done)
        {
            if (handoff == null) throw new ArgumentNullException(nameof(handoff));
            var channels = environment.Launcher;
            var requestId = channels?.RequestId;
            if (channels != null && requestId != null && handoff.Target != null)
            {
                var written = new SessionHandoff(requestId, NewHandoffId(), handoff.Target);
                queue.Enqueue(() => channels.WriteHandoff(written), failure =>
                {
                    if (failure == null)
                    {
                        notify(done, ScreenTone.Success);
                        return;
                    }

                    if (handoff.Link == null)
                    {
                        notify(NotDelivered, ScreenTone.Warning);
                        return;
                    }

                    environment.OpenUrl(handoff.Link);
                    notify(done, ScreenTone.Success);
                });
                return;
            }

            if (handoff.Link == null)
            {
                notify(NotDelivered, ScreenTone.Warning);
                return;
            }

            environment.OpenUrl(handoff.Link);
            notify(done, ScreenTone.Success);
        }

        private static string NewHandoffId() =>
            "handoff-" + Guid.NewGuid().ToString("N").Substring(0, 16).ToLower(CultureInfo.InvariantCulture);
    }
}
