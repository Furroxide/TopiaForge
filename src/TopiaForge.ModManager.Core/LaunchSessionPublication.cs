using System;

namespace TopiaForge.ModManager.Core
{
    /// <summary>The identity a session launch's records are reported under: its request, role and intent.</summary>
    public sealed class LaunchSessionIdentity
    {
        public LaunchSessionIdentity(string requestId, string role, string intentId)
        {
            RequestId = LaunchContractValues.Token(requestId, nameof(requestId));
            Role = LaunchSessionValues.Choice(role, nameof(role), LaunchContractValues.SessionRoles);
            IntentId = LaunchContractValues.Token(intentId, nameof(intentId));
        }

        public static LaunchSessionIdentity Of(ProfileLaunchConfigurationV5 request) =>
            new LaunchSessionIdentity((request ?? throw new ArgumentNullException(nameof(request))).RequestId, request.Session.Role,
                request.Session.IntentId);

        public string RequestId { get; }
        public string Role { get; }
        public string IntentId { get; }
        public string Command => Role == "host" ? "host-session" : "join-session";
    }

    /// <summary>
    /// Reports a session launch's runtime records as progress and outcomes V2 until the multiplayer provider reports the
    /// session itself. The runtime runs a V5 request's executable profile through its V4 path, which reports version 1:
    /// <list type="bullet">
    /// <item>progress keeps its phase and sequence, with the session still <c>pending</c>;</item>
    /// <item>a launch that failed or was cancelled before the session started is reported as the session launch's
    /// outcome, under its own command;</item>
    /// <item>a successful executable command is not yet a session launch's success, and a gamemode session's end is not
    /// the live session's: both are withheld, and the provider reports those.</item>
    /// </list>
    /// </summary>
    public static class LaunchSessionPublication
    {
        public static LaunchProgress ToSessionProgress(LaunchProgress progress, LaunchSessionIdentity session)
        {
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (progress.Session != null) return progress;
            return new LaunchProgress(progress.RequestId, progress.Sequence, progress.Phase, progress.SessionId, progress.NativeBusy,
                new LaunchProgressSession(session.Role, session.IntentId, "pending"));
        }

        /// <summary>The V2 outcome for <paramref name="outcome"/>, or null when it is withheld.</summary>
        public static LaunchOutcome? ToSessionOutcome(LaunchOutcome outcome, LaunchSessionIdentity session)
        {
            if (outcome == null) throw new ArgumentNullException(nameof(outcome));
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (outcome.Multiplayer != null) return outcome;
            if (outcome.Kind != "launch" || outcome.Status == "succeeded") return null;
            return new LaunchOutcome("launch", outcome.RequestId, outcome.Sequence, outcome.Phase, outcome.Status, outcome.Blocks,
                outcome.SessionId, session.Command, outcome.Error, new LaunchOutcomeMultiplayer(session.Role, session.IntentId));
        }
    }
}
