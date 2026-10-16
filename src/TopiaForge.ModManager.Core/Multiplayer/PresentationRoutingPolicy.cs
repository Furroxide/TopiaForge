using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>A UI or audio call a mod makes.</summary>
    internal enum PresentationKind
    {
        /// <summary>A HUD surface.</summary>
        HudSurface = 0,

        /// <summary>An interactive window.</summary>
        Window = 1,

        /// <summary>An immersive full-screen tool.</summary>
        Tool = 2,

        Modal = 3,
        Toast = 4,
        AudioCue = 5
    }

    internal enum PresentationRoute
    {
        /// <summary>Rendered on this process only.</summary>
        Local = 0,

        /// <summary>Rendered here and mirrored read-only to every player, coalesced to at most 10 Hz.</summary>
        MirrorToEveryone = 1,

        /// <summary>Played here and broadcast to every player, rate-limited and deduplicated.</summary>
        BroadcastToEveryone = 2,

        /// <summary>Shown to the host's own player only.</summary>
        HostOnly = 3,

        /// <summary>
        /// Sent to one remote player. Windows and modals stay interactive: the client sends back a control id, and
        /// the callback runs on the host after checking it came from the targeted player.
        /// </summary>
        TargetPlayer = 4,

        /// <summary>A client may not drive another player's UI.</summary>
        Refused = 5
    }

    /// <summary>
    /// Where UI and audio go. Gamemode-server code on the host is routed by kind: HUD surfaces are mirrored to
    /// everyone, toasts and audio cues broadcast, windows, tools and modals stay with the host. <c>IPlayer.Ui</c> calls
    /// target one player. Every other context renders locally, and so does every call from a client-local mod, which
    /// is never networked: other players may not have it.
    /// </summary>
    internal static class PresentationRoutingPolicy
    {
        /// <summary>The minimum gap between two mirrored updates of one HUD surface: 10 Hz.</summary>
        public const long HudMirrorIntervalMicros = 100_000;

        public static bool IsDefined(PresentationKind kind) =>
            kind >= PresentationKind.HudSurface && kind <= PresentationKind.AudioCue;

        /// <summary>Routes an untargeted call made through a mod context's own UI and audio services.</summary>
        public static PresentationRoute Route(NetRealm realm, ModNetProfile profile, PresentationKind kind)
        {
            NetRealms.Require(realm);
            if (!IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (realm != NetRealm.ListenHost || profile.Context != ModExecutionContext.GamemodeServer) return PresentationRoute.Local;
            if (!IsNetworked(realm, profile)) return PresentationRoute.Local;
            switch (kind)
            {
                case PresentationKind.HudSurface:
                    return PresentationRoute.MirrorToEveryone;
                case PresentationKind.Toast:
                case PresentationKind.AudioCue:
                    return PresentationRoute.BroadcastToEveryone;
                default:
                    return PresentationRoute.HostOnly;
            }
        }

        /// <summary>
        /// Routes an <c>IPlayer.Ui</c> call. The local player's UI renders locally in every realm; only the host may
        /// target another player, and never from a client-local mod.
        /// </summary>
        public static PresentationRoute RouteTargeted(
            NetRealm realm,
            ModNetProfile profile,
            bool targetIsLocalPlayer,
            PresentationKind kind)
        {
            NetRealms.Require(realm);
            if (!IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (targetIsLocalPlayer) return PresentationRoute.Local;
            return realm == NetRealm.ListenHost && IsNetworked(realm, profile)
                ? PresentationRoute.TargetPlayer
                : PresentationRoute.Refused;
        }

        /// <summary>Whether a remote player can interact with this kind once targeted.</summary>
        public static bool IsInteractive(PresentationKind kind)
        {
            if (!IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            return kind == PresentationKind.Window || kind == PresentationKind.Tool || kind == PresentationKind.Modal;
        }

        /// <summary>Whether the profile's presentation may reach other players: it runs here and is not client-local.</summary>
        private static bool IsNetworked(NetRealm realm, ModNetProfile profile) =>
            profile.NetworkClass != ModNetworkClass.ClientLocal && profile.RunsOn(realm);
    }
}
