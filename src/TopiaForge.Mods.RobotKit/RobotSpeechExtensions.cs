using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods
{
    /// <summary>Host-owned robot speech with automatic private earshot delivery and local native synthesis.</summary>
    [RequiresTopiaForge("0.2.0")]
    public static class RobotSpeechExtensions
    {
        /// <summary>
        /// Speaks up to 512 plain-text characters using the robot's bound native voice. Only the owning host context
        /// may speak. Listeners within 20 metres in the current world receive the line; late joiners do not.
        /// A new line replaces this robot's previous line. Cancellation, disposal and world changes stop playback.
        /// Unavailable native voice bindings return Unavailable before any listener receives the line.
        /// The call waits for every accepting listener's native playback completion, including remote reports, and at
        /// least the one-second speech pace; Success(true) means at least one listener played the line to the end.
        /// A remote refusal before playback, when accepted by the host, excludes only that listener without failing
        /// the call. With no accepting listeners, Success(false)
        /// waits the same pace without synthesis or replay. Sequential awaits therefore do not replace or rate-limit
        /// their predecessor.
        /// A listener lost during playback does not fail the line while another heard it to the end; Unavailable
        /// means nobody did, so check who could have heard it before repeating a line. At the host's 15-second
        /// deadline the remaining copies stop, and the line returns TimedOut unless a listener had already heard it to
        /// the end. Caller cancellation, replacement, or retirement
        /// of the owning scope, provider or world returns Cancelled.
        /// Completion reports native task completion, not proof of audible sound. Concurrent calls still share budgets.
        /// </summary>
        [RequiresTopiaForge("0.2.0")]
        public static Task<OperationResult<bool>> SayAsync(this IRobotAgent agent, string text,
            CancellationToken cancellationToken = default)
        {
            if (agent == null) throw new ArgumentNullException(nameof(agent));
            return agent is IInternalSpeechAgent speech ? speech.SayAsync(text, cancellationToken)
                : Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable,
                    "Robot speech is unavailable; resolve a current RobotKit agent and retry."));
        }
    }
}
