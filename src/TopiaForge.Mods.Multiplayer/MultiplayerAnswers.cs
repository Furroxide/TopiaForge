using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Generator SPI behind every generated <c>Submit…</c> command method, which submits one command and hands the host's
    /// answer to a callback. Mods call the generated method; nothing else needs this type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With TopiaForge's session engine, which the game's provider and the test rig both run, the answer runs once, on
    /// the main thread. Where this process holds world authority, solo play included, the host answers its own player
    /// before the call returns; elsewhere it runs in the frame that brings the host's result. A session that ends, is
    /// replaced or loses its connection answers a waiting submission with <see cref="ModErrorCode.Cancelled"/>.
    /// </para>
    /// <para>
    /// The answer never runs once the owning mod stops, once its generated binding is released, or after the caller's
    /// cancellation token is cancelled, so a callback may use whatever its mod or scope owns. Nothing is polled: a mod
    /// with no submission in flight pays nothing per frame.
    /// </para>
    /// <para>
    /// Any other <see cref="IMultiplayerSession"/> is answered where its <c>SubmitAsync</c> task completes, with the same
    /// cancellation rules.
    /// </para>
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class MultiplayerAnswers
    {
        private const string CancelledMessage = "The multiplayer submission was cancelled.";
        private const string FailedMessage = "The multiplayer provider could not complete the submission.";

        /// <summary>Submits one command and hands its confirmation to <paramref name="answered"/>, if one is given.</summary>
        /// <param name="session">The session the generated contract is bound to.</param>
        /// <param name="commandType">The generated command identity.</param>
        /// <param name="request">The request payload.</param>
        /// <param name="answered">Receives the confirmation once, or never when the submission is withdrawn; null submits without waiting for an answer.</param>
        /// <param name="ownerStopping">Cancelled when the generated binding is released; the answer never runs after that.</param>
        /// <param name="cancellationToken">Withdraws the submission, and its answer, when cancelled.</param>
        public static void Submit<TRequest, TResponse>(
            IMultiplayerSession session,
            MultiplayerCommandType<TRequest, TResponse> commandType,
            TRequest request,
            Action<MultiplayerCommandConfirmation<TResponse>>? answered,
            CancellationToken ownerStopping,
            CancellationToken cancellationToken)
            where TRequest : class
            where TResponse : class
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (commandType == null) throw new ArgumentNullException(nameof(commandType));
            if (request == null) throw new ArgumentNullException(nameof(request));

            // A withdrawn submission is not made at all: its owner, or the scope that holds the token, has stopped.
            if (ownerStopping.IsCancellationRequested || cancellationToken.IsCancellationRequested) return;
            if (answered != null && session is IInternalAnswerDispatch dispatch)
            {
                dispatch.Submit(commandType, request, answered, ownerStopping, cancellationToken);
                return;
            }

            var submitted = session.SubmitAsync(commandType, request, cancellationToken);
            if (answered == null) return;
            var answer = new FallbackAnswer<TResponse>(answered, ownerStopping, cancellationToken);
            if (submitted.IsCompleted)
            {
                answer.Deliver(submitted);
                return;
            }

            submitted.ContinueWith(
                FallbackAnswer<TResponse>.Continue,
                answer,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private sealed class FallbackAnswer<TResponse>
            where TResponse : class
        {
            internal static readonly Action<Task<MultiplayerCommandConfirmation<TResponse>>, object?> Continue =
                (submitted, state) => ((FallbackAnswer<TResponse>)state!).Deliver(submitted);

            private readonly Action<MultiplayerCommandConfirmation<TResponse>> answered;
            private readonly CancellationToken ownerStopping;
            private readonly CancellationToken cancellationToken;

            internal FallbackAnswer(
                Action<MultiplayerCommandConfirmation<TResponse>> answered,
                CancellationToken ownerStopping,
                CancellationToken cancellationToken)
            {
                this.answered = answered;
                this.ownerStopping = ownerStopping;
                this.cancellationToken = cancellationToken;
            }

            internal void Deliver(Task<MultiplayerCommandConfirmation<TResponse>> submitted)
            {
                if (ownerStopping.IsCancellationRequested || cancellationToken.IsCancellationRequested) return;
                answered(Confirmation(submitted));
            }

            private static MultiplayerCommandConfirmation<TResponse> Confirmation(Task<MultiplayerCommandConfirmation<TResponse>> submitted)
            {
                if (submitted.Status == TaskStatus.RanToCompletion && submitted.Result != null) return submitted.Result;
                var result = submitted.IsCanceled
                    ? OperationResult<TResponse>.Failure(ModErrorCode.Cancelled, CancelledMessage)
                    : OperationResult<TResponse>.Failure(ModErrorCode.Unknown, FailedMessage);
                return new MultiplayerCommandConfirmation<TResponse>(default, default, false, result);
            }
        }
    }
}
