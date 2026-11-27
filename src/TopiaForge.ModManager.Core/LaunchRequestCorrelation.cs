namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// The minimal validated identity needed to reject a malformed command without claiming success. A V5 request also
    /// names its session's intent, so its rejection can be reported as the session launch it was.
    /// </summary>
    public sealed class LaunchRequestCorrelation
    {
        internal LaunchRequestCorrelation(string requestId, string command, string? intentId = null)
        {
            RequestId = LaunchContractValues.Token(requestId, nameof(requestId));
            Command = LaunchContractValues.Choice(command, nameof(command), intentId == null ? LaunchContractValues.Commands : LaunchContractValues.SessionCommands);
            IntentId = intentId == null ? null : LaunchContractValues.Token(intentId, nameof(intentId));
        }
        public string RequestId { get; }
        public string Command { get; }

        /// <summary>Gets the session intent of a V5 request; null for V4.</summary>
        public string? IntentId { get; }

        /// <summary>Gets the command the runtime can execute: a rejected session launch returns to the main menu.</summary>
        public string ExecutableCommand => IntentId == null ? Command : "main-menu";

        /// <summary>Gets the session identity a rejected V5 request is reported under; null for V4.</summary>
        public LaunchSessionIdentity? Session => IntentId == null ? null
            : new LaunchSessionIdentity(RequestId, Command == "host-session" ? "host" : "join", IntentId);
    }
    public static partial class LaunchTransportJson
    {
        internal static LaunchRequestCorrelation ReadCorrelation(string json)
        {
            var value = new Fields(Bounded(json, MaxDocumentBytes), allowed: null);
            if (value.Integer("schemaVersion") != ProfileLaunchConfigurationV5.SchemaVersion)
            {
                value.Version(4);
                return new LaunchRequestCorrelation(value.String("requestId"), value.String("command"));
            }

            var session = new Fields(value.Required("session"), allowed: null);
            return new LaunchRequestCorrelation(value.String("requestId"), value.String("command"), session.String("intentId"));
        }
    }
}
