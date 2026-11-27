using System;
using System.Globalization;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// The developer switch for live sessions, until the in-game interface and the launcher's session launches arrive.
    /// <c>TOPIAFORGE_LIVE_MULTIPLAYER=1</c> turns live sessions on. Then <c>TOPIAFORGE_MULTIPLAYER_HOST=&lt;name&gt;</c>
    /// opens the session on the LAN, on <c>TOPIAFORGE_MULTIPLAYER_PORT</c> or 27250; or
    /// <c>TOPIAFORGE_MULTIPLAYER_JOIN=&lt;host&gt;:&lt;port&gt;</c> joins one, as <c>TOPIAFORGE_MULTIPLAYER_NAME</c>. Progress is
    /// logged by phase and reason, never by address.
    /// </summary>
    internal sealed class DeveloperSessions
    {
        internal const string LiveVariable = "TOPIAFORGE_LIVE_MULTIPLAYER";

        private readonly LiveSessions live;
        private readonly IModLogger logger;
        private readonly string? hostName;
        private readonly JoinTarget? joinTarget;
        private readonly string displayName;
        private readonly int port;
        private readonly Func<bool>? contentPending;
        private bool started;
        private HostPhase lastHostPhase;
        private JoinPhase? lastJoinPhase;

        /// <summary>Drives live sessions as the developer variables ask; <see cref="Create"/> reads them, and tests pass them.</summary>
        /// <param name="contentPending">Says while the runtime still identifies this game's packages; the switch waits for it.</param>
        internal DeveloperSessions(
            LiveSessions live,
            IModLogger logger,
            string? hostName,
            JoinTarget? joinTarget,
            string displayName,
            int port,
            Func<bool>? contentPending = null)
        {
            this.live = live;
            this.logger = logger;
            this.hostName = hostName;
            this.joinTarget = joinTarget;
            this.displayName = displayName;
            this.port = port;
            this.contentPending = contentPending;
        }

        internal static bool IsLiveEnabled() => Environment.GetEnvironmentVariable(LiveVariable) == "1";

        /// <summary>Reads the developer variables; returns null when they ask for nothing.</summary>
        internal static DeveloperSessions? Create(LiveSessions live, IModLogger logger, Func<bool>? contentPending = null)
        {
            var host = Environment.GetEnvironmentVariable("TOPIAFORGE_MULTIPLAYER_HOST");
            var join = Environment.GetEnvironmentVariable("TOPIAFORGE_MULTIPLAYER_JOIN");
            var name = Environment.GetEnvironmentVariable("TOPIAFORGE_MULTIPLAYER_NAME");
            var portText = Environment.GetEnvironmentVariable("TOPIAFORGE_MULTIPLAYER_PORT");
            var port = HostSettings.DefaultPort;
            if (!string.IsNullOrEmpty(portText) && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port > 65535))
            {
                logger.Warn("Multiplayer: TOPIAFORGE_MULTIPLAYER_PORT is not a port; using 27250.");
                port = HostSettings.DefaultPort;
            }

            JoinTarget? target = null;
            if (!string.IsNullOrEmpty(join))
            {
                var colon = join!.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(join.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var joinPort) ||
                    !SessionLink.TryNormalizeHost(join.Substring(0, colon), out _) || joinPort < 1 || joinPort > 65535)
                {
                    logger.Warn("Multiplayer: TOPIAFORGE_MULTIPLAYER_JOIN must be host:port.");
                }
                else
                {
                    target = JoinTarget.Direct(join.Substring(0, colon), joinPort);
                }
            }

            if (string.IsNullOrEmpty(host) && target == null) return null;
            var displayName = WireText.IsDisplayText(name, 24) ? name! : "Player";
            var hostName = WireText.IsDisplayText(host, ServerDescriptor.MaximumNameLength) ? host : null;
            return new DeveloperSessions(live, logger, hostName, target, displayName, port, contentPending);
        }

        /// <summary>Main thread, every frame, after the provider pumped.</summary>
        internal void Pump()
        {
            if (!started)
            {
                if (!live.IsReady)
                {
                    _ = live.PrepareAsync();
                    return;
                }

                // The runtime identifies this game's packages off the main thread; hosting or joining before it finishes
                // would treat them as unreadable.
                if (contentPending != null && contentPending()) return;
                started = true;
                if (hostName != null)
                {
                    live.StartHosting(new HostSettings(hostName) { Port = port });
                    logger.Info("Multiplayer: opening this session on the LAN (developer switch).");
                }
                else if (joinTarget != null)
                {
                    live.StartJoin(joinTarget, new JoinSettings(displayName));
                    logger.Info("Multiplayer: joining a session (developer switch).");
                }
            }

            if (live.HostPhase != lastHostPhase)
            {
                lastHostPhase = live.HostPhase;
                var detail = live.HostPhase == HostPhase.Failed ? " (" + live.HostFailure + ")" : live.HostPhase == HostPhase.Hosting ? " on port " + live.Host!.Port.ToString(CultureInfo.InvariantCulture) : string.Empty;
                logger.Info("Multiplayer: hosting is " + live.HostPhase + detail + ".");
            }

            var join = live.Join;
            if (join != null && join.Phase != lastJoinPhase)
            {
                lastJoinPhase = join.Phase;

                // The failure's name only: a kick's or a refusal's words are the host's own and may hold anything.
                var detail = join.Phase == JoinPhase.Ended ? " (" + join.Failure + ")" : string.Empty;
                logger.Info("Multiplayer: the join is " + join.Phase + detail + ".");
            }
        }
    }
}
