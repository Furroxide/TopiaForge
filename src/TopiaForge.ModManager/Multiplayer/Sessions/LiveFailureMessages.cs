using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>How the failure view presents one failure: its words, where it stopped, and what the player can do next.</summary>
    internal sealed class FailureMessage
    {
        internal FailureMessage(string title, string message, ConnectingStep step, bool retryable, bool launcherHelps = false, bool canTrust = false)
        {
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Step = step;
            Retryable = retryable;
            LauncherHelps = launcherHelps;
            CanTrust = canTrust;
        }

        internal string Title { get; }

        internal string Message { get; }

        /// <summary>Gets the earliest step this failure can stop at; the join fails there or at the step it had reached.</summary>
        internal ConnectingStep Step { get; }

        internal bool Retryable { get; }

        /// <summary>Gets whether the launcher can fix it, by installing the host's exact mods.</summary>
        internal bool LauncherHelps { get; }

        /// <summary>Gets whether the player may trust the server that now answers a typed address.</summary>
        internal bool CanTrust { get; }
    }

    /// <summary>
    /// Every way hosting or a join can fail, in this game's own words. Nothing here names an address: what the host wrote
    /// is shown apart, redacted, through <see cref="HostText.HostSays"/>. The TopiaForge service's failures are worded in
    /// <c>LiveFailureMessages.Internet.cs</c>.
    /// </summary>
    internal static partial class LiveFailureMessages
    {
        internal const string UnreadableContent =
            "This game's mods could not be read: a file of one of them is in use or cannot be opened. Close the program using it, then try again.";

        internal const string ServiceNotConfigured =
            "This game is not set up to reach the TopiaForge service, so invites and listed servers are unavailable. LAN and direct play still work.";

        /// <summary>A typed host's server that could not be checked, because the record of earlier ones could not be read.</summary>
        internal const string UncheckedServer =
            "This game could not read its record of the servers it has met by address, so it could not check this one. Join it only if you trust it, or try again in a moment.";

        /// <summary>
        /// Reads what the runtime or the provider threw as hosting or a join started as a refusal: a bad setting or state
        /// keeps its words, which the screens redact before they show them, and anything else is named by its type alone.
        /// </summary>
        internal static OperationResult<bool> Thrown(Exception exception) =>
            exception is ArgumentException || exception is InvalidOperationException
                ? OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, exception.Message)
                : OperationResult<bool>.Failure(ModErrorCode.External, "The multiplayer provider failed (" + exception.GetType().Name + ").");

        /// <summary>
        /// Gets how a join that ended with <paramref name="failure"/> is presented; refusals read the host's code, and an
        /// unavailable relay reads whether the host takes direct connections, as the status's
        /// <see cref="InternalInternetStatus.HostAllowsDirect"/> says.
        /// </summary>
        internal static FailureMessage ForJoin(InternalLiveFailure failure, InternalAdmissionCode code, bool hostAllowsDirect = false)
        {
            var internet = InternetJoin(failure, hostAllowsDirect);
            if (internet != null) return internet;
            switch (failure)
            {
                case InternalLiveFailure.None:
                    return new FailureMessage("Joined", string.Empty, ConnectingStep.Spawning, false);
                case InternalLiveFailure.Cancelled:
                    return new FailureMessage("Join cancelled", "The join stopped before it connected. Try again.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.Left:
                    return new FailureMessage("Left the session", "You left the session.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.Unreachable:
                    return new FailureMessage("Host not reachable", "The host could not be reached. Check that its game is still running and that its network lets players in, then try again.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.TimedOut:
                    return new FailureMessage("Timed out", "The host did not answer in time. Try again in a moment.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.DescriptorInvalid:
                    return new FailureMessage("Server not verified", "The host's server information could not be verified, so nothing was joined. Try again; if it fails again, ask the host to update TopiaForge.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.HostIdentityChanged:
                    return new FailureMessage("Server changed", "This address now leads to a different server than it did before. Join it only if you trust it.", ConnectingStep.Resolving, false, canTrust: true);
                case InternalLiveFailure.KnownHostsUnreadable:
                    return new FailureMessage("Server not checked", UncheckedServer, ConnectingStep.Resolving, true, canTrust: true);
                case InternalLiveFailure.WrongGameBuild:
                    return new FailureMessage("Different game build", "The host runs a different build of Robotopia. Both players need the same build to play together: update the game, or ask the host to.", ConnectingStep.VerifyingMods, false);
                case InternalLiveFailure.ProtocolMismatch:
                    return new FailureMessage("Different multiplayer version", "The host's TopiaForge multiplayer version cannot play with this game's. Update TopiaForge where it is older.", ConnectingStep.VerifyingMods, false);
                case InternalLiveFailure.ContentMismatch:
                    return new FailureMessage("Mods do not match", "This game's mods do not match the host's. The TopiaForge launcher can install the host's exact mods and join for you.", ConnectingStep.VerifyingMods, false, launcherHelps: true);
                case InternalLiveFailure.DirectNotAllowed:
                    return new FailureMessage(MultiplayerPrivacyText.HideMyIp + " is on", MultiplayerPrivacyText.HideMyIp + " is on, so this game did not contact the host.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.HandshakeFailed:
                    return new FailureMessage("Host not verified", "The host did not prove it is the server it announced, so the connection was closed. Try again; if it fails again, check the server with its host.", ConnectingStep.Authenticating, true);
                case InternalLiveFailure.Rejected:
                    return ForRejection(code);
                case InternalLiveFailure.Kicked:
                    return new FailureMessage("Removed by the host", "The host removed you from the session.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.Banned:
                    return new FailureMessage("Banned by the host", "The host banned you from this session.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.HostEnded:
                    return new FailureMessage("Session ended", "The host ended the session.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.ConnectionLost:
                    return new FailureMessage("Connection lost", "The connection to the host was lost. Try again to rejoin.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.PortInUse:
                    return new FailureMessage("Port in use", "Another program is using the multiplayer port. Close it, then try again.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.NotFound:
                    return new FailureMessage("Session not found", "The session could not be found. It may have ended; check with the host, then try again.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.IdentityUnavailable:
                    return new FailureMessage("Identity unavailable", "This game's multiplayer identity could not be read or created. Check that TopiaForge can write to its data folder, then try again.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.NetworkStopped:
                    return new FailureMessage("Network stopped", "The host's network stopped after repeated errors, so the session ended. Join again once the host is back.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.ContentUnreadable:
                    return new FailureMessage("Mods could not be read", UnreadableContent, ConnectingStep.VerifyingMods, true);
                default:
                    return new FailureMessage("Join failed", "The join could not finish. Try again.", ConnectingStep.Resolving, true);
            }
        }

        /// <summary>Gets how hosting that stopped with <paramref name="failure"/> is presented, or a listing with the service that failed.</summary>
        internal static FailureMessage ForHost(InternalLiveFailure failure)
        {
            var internet = InternetHost(failure);
            if (internet != null) return internet;
            switch (failure)
            {
                case InternalLiveFailure.None:
                    return new FailureMessage("Hosting", string.Empty, ConnectingStep.Resolving, false);
                case InternalLiveFailure.PortInUse:
                    return new FailureMessage("Port in use", "Another program is using the multiplayer port, so this game could not host. Close it, then try again.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.IdentityUnavailable:
                    return new FailureMessage("Identity unavailable", "This game's multiplayer identity could not be read or created, so it could not host. Check that TopiaForge can write to its data folder, then try again.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.ContentUnreadable:
                    return new FailureMessage("Mods could not be read", UnreadableContent, ConnectingStep.Resolving, true);
                case InternalLiveFailure.NetworkStopped:
                    return new FailureMessage("Network stopped", "This game's network stopped after repeated errors, so hosting ended and every player left. Host again to let them back in.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.Rejected:
                    return new FailureMessage("Password not prepared", "The session password could not be prepared, so hosting did not start. Try again.", ConnectingStep.Resolving, true);
                default:
                    return new FailureMessage("Hosting stopped", "Hosting stopped before players could join. Try again.", ConnectingStep.Resolving, true);
            }
        }

        private static FailureMessage ForRejection(InternalAdmissionCode code)
        {
            switch (code)
            {
                case InternalAdmissionCode.Full:
                    return new FailureMessage("Session full", "The session is full. Try again when a player leaves.", ConnectingStep.Authenticating, true);
                case InternalAdmissionCode.PasswordRequired:
                    return new FailureMessage("Password needed", "The session needs a password. Retry to enter it.", ConnectingStep.Authenticating, true);
                case InternalAdmissionCode.PasswordWrong:
                    return new FailureMessage("Wrong password", "The password is wrong. Retry to enter it again.", ConnectingStep.Authenticating, true);
                case InternalAdmissionCode.TooManyAttempts:
                    return new FailureMessage("Too many attempts", "The host is turning joins away after too many wrong passwords. Wait a moment, then try again.", ConnectingStep.Authenticating, true);
                case InternalAdmissionCode.Banned:
                    return new FailureMessage("Banned by the host", "The host has banned you from this session.", ConnectingStep.Authenticating, false);
                case InternalAdmissionCode.Content:
                    return new FailureMessage("Mods do not match", "This game's mods do not match the session's. The TopiaForge launcher can install the host's exact mods and join for you.", ConnectingStep.VerifyingMods, false, launcherHelps: true);
                case InternalAdmissionCode.Protocol:
                    return new FailureMessage("Different multiplayer version", "The host's TopiaForge multiplayer version could not read this game's request. Update TopiaForge where it is older.", ConnectingStep.VerifyingMods, false);
                case InternalAdmissionCode.NotReady:
                    return new FailureMessage("Host not ready", "The host is not ready for players yet. Try again shortly.", ConnectingStep.VerifyingMods, true);
                default:
                    return new FailureMessage("Join refused", "The host refused the join. Try again, or ask the host why.", ConnectingStep.Authenticating, true);
            }
        }
    }
}
