using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Inactive request-correlated progress wire; native draining is orthogonal to session phase. Version 1 reports a
    /// gamemode launch. Version 2 reports a session launch (request V5): it carries a <see cref="Session"/> block and may be
    /// <c>connecting</c> or <c>synchronizing</c>, which only a join is.
    /// </summary>
    public sealed class LaunchProgress
    {
        public const int SchemaVersion = 1;
        public const int SessionSchemaVersion = 2;

        public LaunchProgress(string requestId, int sequence, string phase, string? sessionId = null, bool? nativeBusy = null,
            LaunchProgressSession? session = null)
        {
            RequestId = LaunchContractValues.Token(requestId, nameof(requestId));
            Sequence = LaunchContractValues.Revision(sequence, nameof(sequence));
            Phase = LaunchContractValues.Choice(phase, nameof(phase), session == null ? LaunchContractValues.Phases : LaunchContractValues.SessionPhases);
            SessionId = sessionId == null ? null : LaunchContractValues.Token(sessionId, nameof(sessionId));
            NativeBusy = nativeBusy;
            Session = session;
            if (session != null && session.Role != "join" && (Phase == "connecting" || Phase == "synchronizing"))
                throw new ArgumentException("Only a join connects or synchronizes.", nameof(phase));
        }

        public string RequestId { get; }
        public int Sequence { get; }
        public string Phase { get; }
        public string? SessionId { get; }
        public bool? NativeBusy { get; }

        /// <summary>Gets the session a V5 launch hosts or joins; present exactly in version 2.</summary>
        public LaunchProgressSession? Session { get; }
        public int Version => Session == null ? SchemaVersion : SessionSchemaVersion;
    }

    /// <summary>Framework-independent spelling of an SDK operation failure.</summary>
    public sealed class LaunchExecutionError
    {
        public LaunchExecutionError(string code, string message)
        {
            Code = LaunchContractValues.Choice(code, nameof(code), LaunchContractValues.RuntimeErrors);
            Message = LaunchContractValues.Text(message, nameof(message), 1, 4096);
        }

        public string Code { get; }
        public string Message { get; }
    }

    /// <summary>
    /// One operation outcome or one terminal session outcome; these are distinct events. Version 1 reports a gamemode
    /// launch. Version 2 reports a session launch (request V5) with a <see cref="Multiplayer"/> block: a launch outcome
    /// succeeds once hosting is open or the join is in session, and fails with a failure and the host's admission report;
    /// a terminal outcome says why the session ended.
    /// </summary>
    public sealed class LaunchOutcome
    {
        public const int SchemaVersion = 1;
        public const int SessionSchemaVersion = 2;

        public LaunchOutcome(string kind, string requestId, int sequence, string phase, string status,
            IEnumerable<LaunchBlock> blocks, string? sessionId = null, string? command = null, LaunchExecutionError? error = null,
            LaunchOutcomeMultiplayer? multiplayer = null)
        {
            Kind = LaunchContractValues.Choice(kind, nameof(kind), new[] { "launch", "session" });
            RequestId = LaunchContractValues.Token(requestId, nameof(requestId));
            Sequence = LaunchContractValues.Revision(sequence, nameof(sequence));
            Phase = LaunchContractValues.Choice(phase, nameof(phase), multiplayer == null ? LaunchContractValues.Phases : LaunchContractValues.SessionPhases);
            Status = LaunchContractValues.Choice(status, nameof(status), LaunchContractValues.Statuses);
            SessionId = sessionId == null ? null : LaunchContractValues.Token(sessionId, nameof(sessionId));
            Error = error == null ? null : new LaunchExecutionError(error.Code, error.Message);
            Blocks = LaunchBlockCollection.Copy(blocks);
            Multiplayer = multiplayer;
            if (multiplayer != null)
            {
                Command = command == null ? null : LaunchContractValues.Choice(command, nameof(command), LaunchContractValues.SessionCommands);
                CheckSessionOutcome(multiplayer);
                return;
            }

            if (Kind == "launch")
            {
                Command = LaunchContractValues.Choice(command!, nameof(command), LaunchContractValues.Commands);
                if (Status == "succeeded" && (Command == "launch-target"
                    ? Phase != "running" || SessionId == null : Phase != "idle" || SessionId != null))
                    throw new ArgumentException("Successful launch outcome does not match its command and readiness phase.");
            }
            else if (command != null || SessionId == null || Phase != "idle")
            {
                throw new ArgumentException("A terminal session outcome requires an idle session identity and no command.");
            }

            if (Status == "failed" && Error == null && Blocks.Count == 0)
                throw new ArgumentException("Failed outcomes require an operation error or a blocking reason.");
            if (Status == "succeeded" && (Error != null || Blocks.Count != 0))
                throw new ArgumentException("Successful outcomes cannot carry failures.");
        }

        public string Kind { get; }
        public string RequestId { get; }
        public int Sequence { get; }
        public string Phase { get; }
        public string Status { get; }
        public IReadOnlyList<LaunchBlock> Blocks { get; }
        public string? SessionId { get; }
        public string? Command { get; }
        public LaunchExecutionError? Error { get; }

        /// <summary>Gets how a session launch went; present exactly in version 2.</summary>
        public LaunchOutcomeMultiplayer? Multiplayer { get; }
        public int Version => Multiplayer == null ? SchemaVersion : SessionSchemaVersion;

        /// <summary>
        /// Version 2's rules. A launch outcome names the role's command; it succeeds without failures at Running with a
        /// gamemode session or at Idle without one, and fails with a block, an error or a session failure, which alone may
        /// carry the admission report. A terminal outcome is Idle, names no command and says why the session ended.
        /// </summary>
        private void CheckSessionOutcome(LaunchOutcomeMultiplayer session)
        {
            var failed = session.Failure != null;
            var invalid = (session.Role != "join" && (Phase == "connecting" || Phase == "synchronizing"))
                || (Status == "succeeded" && (Blocks.Count != 0 || Error != null || failed))
                || (failed && Status != "failed");
            if (Kind == "launch")
            {
                var commandFor = session.Role == "host" ? "host-session" : "join-session";
                invalid = invalid || Command != commandFor || session.EndReason != null || (session.Admission != null && !failed)
                    || (Status == "failed" && Blocks.Count == 0 && Error == null && !failed)
                    || (Status == "succeeded" && (Phase == "idle" ? SessionId != null : Phase != "running" || SessionId == null));
            }
            else
            {
                invalid = invalid || Command != null || Phase != "idle" || session.EndReason == null || failed
                    || (session.Admission != null && session.EndReason != "admission-rejected")
                    || (Status == "failed" && Blocks.Count == 0 && Error == null);
            }

            if (invalid) throw new ArgumentException("Inconsistent session launch outcome.");
        }
    }
}
