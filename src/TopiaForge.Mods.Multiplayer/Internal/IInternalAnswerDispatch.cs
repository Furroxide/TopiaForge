using System;
using System.Threading;

namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// A session that hands a command's answer to a callback itself: on its own thread, at the settle point that
    /// completes the command, and only while the submitting owner still runs. TopiaForge's session engine implements it
    /// for every owner facade, so generated <c>Submit…</c> methods never poll, hop threads or run late.
    /// </summary>
    internal interface IInternalAnswerDispatch
    {
        /// <summary>
        /// Submits one command and calls <paramref name="answered"/> once with its confirmation, unless the owner stops,
        /// <paramref name="ownerStopping"/> is cancelled or <paramref name="cancellationToken"/> is cancelled first.
        /// </summary>
        void Submit<TRequest, TResponse>(
            MultiplayerCommandType<TRequest, TResponse> commandType,
            TRequest request,
            Action<MultiplayerCommandConfirmation<TResponse>> answered,
            CancellationToken ownerStopping,
            CancellationToken cancellationToken)
            where TRequest : class
            where TResponse : class;
    }
}
