using System;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>Receives a command's confirmation at the settle point that completes it, on the owner thread.</summary>
    internal interface ICommandAnswer<T>
        where T : class
    {
        /// <summary>Hands over the confirmation. The engine calls it at most once.</summary>
        void Deliver(MultiplayerCommandConfirmation<T> confirmation);
    }

    /// <summary>
    /// One mod's answer callback for one submission. It runs only while the owner's facade is open, the generated binding
    /// that submitted is live, and the caller has not withdrawn the submission; a failing callback is reported, never
    /// thrown into the engine.
    /// </summary>
    internal sealed class CommandAnswer<T> : ICommandAnswer<T>
        where T : class
    {
        private readonly EngineSessionFacade owner;
        private readonly Action<MultiplayerCommandConfirmation<T>> answered;
        private readonly CancellationToken ownerStopping;
        private readonly CancellationToken cancellationToken;
        private readonly string what;
        private bool delivered;

        internal CommandAnswer(
            EngineSessionFacade owner,
            Action<MultiplayerCommandConfirmation<T>> answered,
            CancellationToken ownerStopping,
            CancellationToken cancellationToken,
            string commandId)
        {
            this.owner = owner;
            this.answered = answered;
            this.ownerStopping = ownerStopping;
            this.cancellationToken = cancellationToken;

            // The command's own id, without the contract's: a dotted contract id reads as a host name to the redactor
            // every diagnostic passes through, so it would never reach the log.
            what = "command answer for '" + commandId.Substring(commandId.LastIndexOf('/') + 1) + "'";
        }

        /// <summary>Gets whether anyone still wants the answer: the owner runs, its binding is live, and the caller waits.</summary>
        internal bool IsWanted =>
            !owner.IsClosing && !ownerStopping.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

        public void Deliver(MultiplayerCommandConfirmation<T> confirmation)
        {
            if (delivered) return;
            delivered = true;
            if (!IsWanted) return;
            owner.Engine.InvokeCallback(answered, confirmation, what);
        }
    }
}
