using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>A session launch's result as the launcher reads it: the outcome's status, its multiplayer block, and an error.</summary>
    internal sealed class SessionLaunchResult
    {
        internal SessionLaunchResult(string status, LaunchOutcomeMultiplayer multiplayer, LaunchExecutionError? error = null)
        {
            Status = status ?? throw new ArgumentNullException(nameof(status));
            Multiplayer = multiplayer ?? throw new ArgumentNullException(nameof(multiplayer));
            Error = error;
        }

        /// <summary>Gets <c>succeeded</c>, <c>failed</c> or <c>cancelled</c>.</summary>
        internal string Status { get; }

        internal LaunchOutcomeMultiplayer Multiplayer { get; }

        /// <summary>Gets what failed in this game itself, for a cause no join or host failure names; null otherwise.</summary>
        internal LaunchExecutionError? Error { get; }
    }

    /// <summary>
    /// Maps the provider's status onto progress V2's <c>session</c> block and outcome V2's <c>multiplayer</c> block
    /// (docs/internal/MultiplayerLaunchContract.md, sections 4 and 5), without side effects, so the records can be checked
    /// against the shared fixtures byte for byte. A join's failures are the launcher's join failures of the same name; this
    /// game folds its own causes into them, and says the rest as an error. Every text is this game's own words: what a host
    /// wrote reaches a record only in its admission report, redacted. Nothing here names an address.
    /// </summary>
    internal static class SessionLaunchRecords
    {
        internal const string AdmissionChangedDetail = "The server changed after you planned the join.";
        internal const string PackagesChangedDetail = "The server's packages changed after you planned the join.";
        internal const string PasswordWrongDetail = "The password was wrong.";

        /// <summary>
        /// The session block for this status. <paramref name="started"/> says whether the in-game flows began the session yet,
        /// and <paramref name="succeeded"/> whether it opened: until then a provider with nothing to report yet is still
        /// starting the session, and after it, the session has stopped.
        /// </summary>
        internal static LaunchProgressSession Progress(LaunchSessionIdentity identity, bool started, InternalLiveStatus status, bool succeeded = false)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (status == null) throw new ArgumentNullException(nameof(status));
            return identity.Role == "host" ? HostProgress(identity, started, status, succeeded) : JoinProgress(identity, started, status, succeeded);
        }

        /// <summary>The phase a join's state reports in place of the runtime's: <c>connecting</c>, <c>synchronizing</c>, or null.</summary>
        internal static string? PhaseOf(LaunchProgressSession block)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            if (block.Role != "join") return null;
            switch (block.State)
            {
                case "resolving":
                case "checking":
                case "awaiting-password":
                case "preparing-proof":
                case "connecting":
                case "reconnecting":
                    return "connecting";
                case "synchronizing":
                    return "synchronizing";
                default:
                    return null;
            }
        }

        /// <summary>
        /// The launch's result in this status, or null while it is still on its way: a host succeeds once hosting is open, a join
        /// once it is in session; a join the player cancels or leaves is cancelled. <paramref name="synchronized"/> says whether
        /// the join had reached its world before it ended; <paramref name="hostPort"/> is the port a host was to use, or zero.
        /// </summary>
        internal static SessionLaunchResult? Result(LaunchSessionIdentity identity, InternalLiveStatus status, bool synchronized = false, int hostPort = 0)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (status == null) throw new ArgumentNullException(nameof(status));
            if (identity.Role == "host")
            {
                if (status.HostPhase == InternalHostPhase.Hosting)
                    return new SessionLaunchResult("succeeded", Block(identity, ServerId(status.HostServerId)));
                return status.HostPhase == InternalHostPhase.Failed ? HostFailure(identity, status.HostFailure, hostPort) : null;
            }

            var server = ServerId(status.Server?.ServerId);
            if (status.JoinPhase == InternalJoinPhase.InSession) return new SessionLaunchResult("succeeded", Block(identity, server));
            if (status.JoinPhase != InternalJoinPhase.Ended) return null;
            return status.JoinFailure == InternalLiveFailure.Cancelled || status.JoinFailure == InternalLiveFailure.Left || status.JoinFailure == InternalLiveFailure.None
                ? new SessionLaunchResult("cancelled", Block(identity, null))
                : JoinFailure(identity, status, server, synchronized);
        }

        /// <summary>A launch that failed in a way the launcher's join flow names, with this game's sentence for it.</summary>
        internal static SessionLaunchResult Failure(LaunchSessionIdentity identity, string failure, string detail) => Failed(identity, null, failure, detail);

        /// <summary>
        /// A launch the in-game flows could not start, or the player turned down: failed with this game's error, or cancelled.
        /// The words can come from the provider or the realm, so they are redacted as a log line is.
        /// </summary>
        internal static SessionLaunchResult Refused(LaunchSessionIdentity identity, ModErrorCode code, string message) =>
            code == ModErrorCode.Cancelled
                ? new SessionLaunchResult("cancelled", Block(identity, null))
                : new SessionLaunchResult("failed", Block(identity, null),
                    new LaunchExecutionError(ErrorName(code), Text(MultiplayerDiagnosticRedactor.Redact(message ?? string.Empty), "The session did not start.")));

        /// <summary>
        /// The terminal record's block for a session that ended after the launch succeeded: why it ended, from the host's or
        /// the join's last status. <paramref name="replaced"/> says a newer join replaced it.
        /// </summary>
        internal static LaunchOutcomeMultiplayer Ended(LaunchSessionIdentity identity, InternalLiveStatus status, bool replaced = false)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (status == null) throw new ArgumentNullException(nameof(status));
            if (identity.Role == "host")
            {
                var reason = status.HostPhase == InternalHostPhase.Failed ? "transport-failed" : "host-ended";
                return new LaunchOutcomeMultiplayer(identity.Role, identity.IntentId, ServerId(status.HostServerId), endReason: reason);
            }

            var server = ServerId(status.Server?.ServerId);
            if (replaced) return new LaunchOutcomeMultiplayer(identity.Role, identity.IntentId, server, endReason: "replaced");
            switch (status.JoinFailure)
            {
                case InternalLiveFailure.Rejected:
                    return new LaunchOutcomeMultiplayer(identity.Role, identity.IntentId, server, admission: Admission(status), endReason: "admission-rejected");
                default:
                    return new LaunchOutcomeMultiplayer(identity.Role, identity.IntentId, server, endReason: EndReason(status.JoinFailure));
            }
        }

        /// <summary>Names a runtime error code as the outcome's error does: <c>invalidArgument</c>, <c>unavailable</c> and so on.</summary>
        internal static string ErrorName(ModErrorCode code)
        {
            var name = code == ModErrorCode.None ? ModErrorCode.Unknown.ToString() : code.ToString();
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private static LaunchProgressSession HostProgress(LaunchSessionIdentity identity, bool started, InternalLiveStatus status, bool succeeded)
        {
            if (!started) return new LaunchProgressSession(identity.Role, identity.IntentId, "pending");
            switch (status.HostPhase)
            {
                case InternalHostPhase.Hosting:
                    var maximum = status.MaxPlayers;
                    var counted = maximum >= LaunchSessionValues.MinimumPlayers && maximum <= LaunchSessionValues.MaximumPlayers;
                    var invite = status.Internet.Invite?.Code;
                    return new LaunchProgressSession(identity.Role, identity.IntentId, "hosting", ServerId(status.HostServerId),
                        counted ? Math.Min(Math.Max(status.ConnectedPlayers, 1), maximum) : (int?)null, counted ? maximum : (int?)null,
                        invite != null && IsInviteCode(invite) ? invite : null);
                case InternalHostPhase.Preparing:
                    return new LaunchProgressSession(identity.Role, identity.IntentId, "starting");
                case InternalHostPhase.Idle when !succeeded:
                    // Asked to host, before the provider reports it.
                    return new LaunchProgressSession(identity.Role, identity.IntentId, "starting");
                default:
                    return new LaunchProgressSession(identity.Role, identity.IntentId, "stopped");
            }
        }

        private static LaunchProgressSession JoinProgress(LaunchSessionIdentity identity, bool started, InternalLiveStatus status, bool succeeded)
        {
            var state = !started ? "pending" : succeeded && status.JoinPhase == InternalJoinPhase.None ? "ended" : JoinState(status.JoinPhase);
            var server = ServerId(status.Server?.ServerId);
            int? players = null;
            int? maximum = null;
            var announced = status.Server?.MaxPlayers ?? 0;
            if ((state == "in-session" || state == "reconnecting") && announced >= LaunchSessionValues.MinimumPlayers && announced <= LaunchSessionValues.MaximumPlayers)
            {
                maximum = announced;
                players = Math.Min(Math.Max(status.ConnectedPlayers, 1), announced);
            }

            return new LaunchProgressSession(identity.Role, identity.IntentId, state, server, players, maximum);
        }

        private static string JoinState(InternalJoinPhase phase)
        {
            switch (phase)
            {
                case InternalJoinPhase.Resolving:
                    return "resolving";
                case InternalJoinPhase.Checking:
                    return "checking";
                case InternalJoinPhase.AwaitingPassword:
                    return "awaiting-password";
                case InternalJoinPhase.PreparingProof:
                    return "preparing-proof";
                case InternalJoinPhase.Connecting:
                    return "connecting";
                case InternalJoinPhase.Synchronizing:
                    return "synchronizing";
                case InternalJoinPhase.InSession:
                    return "in-session";
                case InternalJoinPhase.Reconnecting:
                    return "reconnecting";
                case InternalJoinPhase.Ended:
                    return "ended";
                default:
                    // A join that has not reached the provider yet, as one waiting for this game's mods, is still on its way.
                    return "resolving";
            }
        }

        private static SessionLaunchResult HostFailure(LaunchSessionIdentity identity, InternalLiveFailure failure, int hostPort)
        {
            switch (failure)
            {
                case InternalLiveFailure.PortInUse:
                    return Failed(identity, null, "port-in-use", hostPort > 0
                        ? "Another program is using port " + hostPort.ToString(CultureInfo.InvariantCulture) + "."
                        : "Another program is using the game's port.");
                case InternalLiveFailure.IdentityUnavailable:
                    return Failed(identity, null, "identity-unavailable", "This game's multiplayer identity could not be read or created.");
                case InternalLiveFailure.NetworkStopped:
                    return Failed(identity, null, "network-stopped", "This game's network stopped after repeated errors.");
                case InternalLiveFailure.ServiceUnavailable:
                case InternalLiveFailure.ServiceDisabled:
                case InternalLiveFailure.ServiceRateLimited:
                case InternalLiveFailure.ServiceRefused:
                case InternalLiveFailure.ServiceNotConfigured:
                case InternalLiveFailure.RelayUnavailable:
                    return Failed(identity, null, "service-unavailable", "The TopiaForge service could not take this session.");
                case InternalLiveFailure.ContentUnreadable:
                    return Errored(identity, ModErrorCode.Io, "This game's mods could not be read.");
                case InternalLiveFailure.Rejected:
                    return Errored(identity, ModErrorCode.External, "The session password could not be prepared.");
                default:
                    return Errored(identity, ModErrorCode.External, "Hosting stopped before players could join.");
            }
        }

        private static SessionLaunchResult JoinFailure(LaunchSessionIdentity identity, InternalLiveStatus status, string? server, bool synchronized)
        {
            var code = status.RejectionCode;
            switch (status.JoinFailure)
            {
                case InternalLiveFailure.Unreachable:
                case InternalLiveFailure.NotFound:
                case InternalLiveFailure.HostOffline:
                case InternalLiveFailure.HostNotAnswering:
                case InternalLiveFailure.ServiceUnavailable:
                case InternalLiveFailure.ServiceRateLimited:
                    return Failed(identity, server, "timeout", "The host did not answer in time.");
                case InternalLiveFailure.TimedOut:
                    return synchronized
                        ? Failed(identity, server, "snapshot-failed", "The host's world did not arrive in time.")
                        : Failed(identity, server, "timeout", "The host did not answer in time.");
                case InternalLiveFailure.UdpBlocked:
                case InternalLiveFailure.NoRoute:
                case InternalLiveFailure.SignalingFailed:
                    return Failed(identity, server, "nat-traversal-failed", "No network path to the host worked.");
                case InternalLiveFailure.RelayUnavailable:
                    return Failed(identity, server, "relay-unavailable", "The relay was unavailable, and this session keeps to it.");
                case InternalLiveFailure.ProtocolMismatch:
                case InternalLiveFailure.ServiceRefused:
                    return Failed(identity, server, "protocol-mismatch", "The host's multiplayer version cannot play with this game's.");
                case InternalLiveFailure.DirectNotAllowed:
                    return Failed(identity, server, "direct-not-allowed", "Reaching the host would show it this player's address, which this session does not allow.");
                case InternalLiveFailure.Kicked:
                    return Failed(identity, server, "kicked", "The host removed this player.");
                case InternalLiveFailure.Banned:
                    return Failed(identity, server, "kicked", "The host has banned this player.");
                case InternalLiveFailure.ConnectionLost:
                case InternalLiveFailure.HostEnded:
                case InternalLiveFailure.NetworkStopped:
                    return Failed(identity, server, "disconnected", "The connection to the host was lost.");
                case InternalLiveFailure.AdmissionChanged:
                    return Failed(identity, server, "admission-changed", AdmissionChangedDetail);
                case InternalLiveFailure.ContentMismatch:
                    return Failed(identity, server, "admission-changed", PackagesChangedDetail);
                case InternalLiveFailure.DescriptorInvalid:
                    return Failed(identity, server, "admission-changed", "The server's description could not be verified.");
                case InternalLiveFailure.HostIdentityChanged:
                    return Failed(identity, server, "admission-changed", "The server you planned to join no longer answers there.");
                case InternalLiveFailure.WrongGameBuild:
                    return Failed(identity, server, "admission-changed", "The server now runs another Robotopia build.");
                case InternalLiveFailure.HandshakeFailed:
                    return Failed(identity, server, "admission-changed", "The host did not prove it is the server you planned to join.");
                case InternalLiveFailure.InviteNotFound:
                case InternalLiveFailure.InviteWithdrawn:
                    return Failed(identity, server, "admission-changed", "The invite no longer works.");
                case InternalLiveFailure.ServerNotListed:
                    return Failed(identity, server, "admission-changed", "The server is no longer listed.");
                case InternalLiveFailure.Rejected:
                    return Rejected(identity, status, server, code);
                case InternalLiveFailure.IdentityUnavailable:
                    return Errored(identity, ModErrorCode.Io, "This game's multiplayer identity could not be read or created.");
                case InternalLiveFailure.ContentUnreadable:
                    return Errored(identity, ModErrorCode.Io, "This game's mods could not be read.");
                case InternalLiveFailure.ServiceNotConfigured:
                    return Errored(identity, ModErrorCode.Unavailable, "This game is not set up to reach the TopiaForge service.");
                case InternalLiveFailure.ServiceDisabled:
                    return Errored(identity, ModErrorCode.Unavailable, "The TopiaForge service has turned Internet joining off for now.");
                case InternalLiveFailure.ServerBlocked:
                    return Errored(identity, ModErrorCode.Unavailable, "The TopiaForge service holds this server back for now.");
                default:
                    return Errored(identity, ModErrorCode.External, "The join could not finish.");
            }
        }

        /// <summary>A host's refusal, with its admission report, which only a join's failure carries.</summary>
        private static SessionLaunchResult Rejected(LaunchSessionIdentity identity, InternalLiveStatus status, string? server, InternalAdmissionCode code)
        {
            string failure;
            string detail;
            switch (code)
            {
                case InternalAdmissionCode.Full:
                    failure = "server-full";
                    detail = "The session is full.";
                    break;
                case InternalAdmissionCode.PasswordRequired:
                    failure = "password-rejected";
                    detail = "The session needs a password.";
                    break;
                case InternalAdmissionCode.PasswordWrong:
                    failure = "password-rejected";
                    detail = PasswordWrongDetail;
                    break;
                case InternalAdmissionCode.TooManyAttempts:
                    failure = "password-rejected";
                    detail = "The host turned the join away after too many wrong passwords.";
                    break;
                case InternalAdmissionCode.Content:
                    failure = "admission-changed";
                    detail = PackagesChangedDetail;
                    break;
                case InternalAdmissionCode.Protocol:
                    failure = "protocol-mismatch";
                    detail = "The host's multiplayer version cannot play with this game's.";
                    break;
                case InternalAdmissionCode.NotReady:
                    failure = "timeout";
                    detail = "The host was not ready for players yet.";
                    break;
                case InternalAdmissionCode.Banned:
                    failure = "kicked";
                    detail = "The host has banned this player.";
                    break;
                default:
                    failure = "kicked";
                    detail = "The host turned the join away.";
                    break;
            }

            return new SessionLaunchResult("failed", new LaunchOutcomeMultiplayer(identity.Role, identity.IntentId, server, failure, detail, Admission(status)));
        }

        /// <summary>
        /// The host's admission report: its code and its problem lines. The lines are the host's words, so each is redacted, and
        /// a line that is not display text once redacted is dropped; at most 64 are kept.
        /// </summary>
        private static LaunchAdmissionReport Admission(InternalLiveStatus status)
        {
            var problems = new List<string>();
            if (status.ContentProblemsFromHost)
            {
                foreach (var problem in status.ContentProblems)
                {
                    if (problems.Count >= LaunchSessionValues.MaximumProblems) break;
                    var line = Bounded(MultiplayerDiagnosticRedactor.Redact(problem ?? string.Empty), LaunchSessionValues.MaximumProblemLength);
                    if (line != null) problems.Add(line);
                }
            }

            return new LaunchAdmissionReport(AdmissionCode(status.RejectionCode), problems);
        }

        private static string AdmissionCode(InternalAdmissionCode code)
        {
            switch (code)
            {
                case InternalAdmissionCode.Full:
                    return "full";
                case InternalAdmissionCode.Protocol:
                    return "protocol";
                case InternalAdmissionCode.PasswordRequired:
                    return "password-required";
                case InternalAdmissionCode.PasswordWrong:
                    return "password-wrong";
                case InternalAdmissionCode.TooManyAttempts:
                    return "too-many-attempts";
                case InternalAdmissionCode.Banned:
                    return "banned";
                case InternalAdmissionCode.Content:
                    return "content";
                default:
                    return "other";
            }
        }

        private static string EndReason(InternalLiveFailure failure)
        {
            switch (failure)
            {
                case InternalLiveFailure.None:
                case InternalLiveFailure.Left:
                case InternalLiveFailure.Cancelled:
                    return "left";
                case InternalLiveFailure.HostEnded:
                    return "host-ended";
                case InternalLiveFailure.Kicked:
                    return "kicked";
                case InternalLiveFailure.Banned:
                    return "banned";
                case InternalLiveFailure.TimedOut:
                    return "timed-out";
                case InternalLiveFailure.ProtocolMismatch:
                    return "protocol-mismatch";
                default:
                    return "transport-failed";
            }
        }

        private static SessionLaunchResult Failed(LaunchSessionIdentity identity, string? server, string failure, string detail) =>
            new SessionLaunchResult("failed", new LaunchOutcomeMultiplayer(identity.Role, identity.IntentId, server, failure, detail));

        private static SessionLaunchResult Errored(LaunchSessionIdentity identity, ModErrorCode code, string message) =>
            new SessionLaunchResult("failed", Block(identity, null), new LaunchExecutionError(ErrorName(code), message));

        private static LaunchOutcomeMultiplayer Block(LaunchSessionIdentity identity, string? server) =>
            new LaunchOutcomeMultiplayer(identity.Role, identity.IntentId, server);

        /// <summary>A server id the records may name; anything else is left out rather than written.</summary>
        private static string? ServerId(string? value) => InternalText.IsServerId(value) ? value : null;

        private static bool IsInviteCode(string value)
        {
            try
            {
                return LaunchSessionValues.InviteCode(value, nameof(value)) == value;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>Text an error may carry: bounded, or <paramref name="fallback"/> when nothing usable is left.</summary>
        private static string Text(string? value, string fallback) => Bounded(value ?? string.Empty, 1024) ?? fallback;

        private static string? Bounded(string value, int maximum)
        {
            var text = value.Trim();
            if (text.Length > maximum) text = text.Substring(0, maximum).TrimEnd();
            try
            {
                return LaunchSessionValues.DisplayText(text, nameof(value), 1, maximum);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
