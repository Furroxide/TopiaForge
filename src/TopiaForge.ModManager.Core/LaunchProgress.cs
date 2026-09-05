using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Inactive request-correlated progress wire; native draining is orthogonal to session phase.</summary>
    public sealed class LaunchProgress
    {
        public const int SchemaVersion = 1;

        public LaunchProgress(string requestId, int sequence, string phase, string? sessionId = null, bool? nativeBusy = null)
        {
            RequestId = LaunchContractValues.Token(requestId, nameof(requestId));
            Sequence = LaunchContractValues.Revision(sequence, nameof(sequence));
            Phase = LaunchContractValues.Choice(phase, nameof(phase), LaunchContractValues.Phases);
            SessionId = sessionId == null ? null : LaunchContractValues.Token(sessionId, nameof(sessionId));
            NativeBusy = nativeBusy;
        }

        public string RequestId { get; }
        public int Sequence { get; }
        public string Phase { get; }
        public string? SessionId { get; }
        public bool? NativeBusy { get; }
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

    /// <summary>One operation outcome or one terminal session outcome; these are distinct events.</summary>
    public sealed class LaunchOutcome
    {
        public const int SchemaVersion = 1;

        public LaunchOutcome(string kind, string requestId, int sequence, string phase, string status,
            IEnumerable<LaunchBlock> blocks, string? sessionId = null, string? command = null, LaunchExecutionError? error = null)
        {
            Kind = LaunchContractValues.Choice(kind, nameof(kind), new[] { "launch", "session" });
            RequestId = LaunchContractValues.Token(requestId, nameof(requestId));
            Sequence = LaunchContractValues.Revision(sequence, nameof(sequence));
            Phase = LaunchContractValues.Choice(phase, nameof(phase), LaunchContractValues.Phases);
            Status = LaunchContractValues.Choice(status, nameof(status), LaunchContractValues.Statuses);
            SessionId = sessionId == null ? null : LaunchContractValues.Token(sessionId, nameof(sessionId));
            Error = error == null ? null : new LaunchExecutionError(error.Code, error.Message);
            Blocks = LaunchBlockCollection.Copy(blocks);
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
    }
}
