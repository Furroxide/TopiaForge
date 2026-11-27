using System;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    // Local presentation only. This never completes a command or releases native ownership.
    internal sealed partial class MenuLoadPresentation
    {
        internal const string OpeningText = "Opening the main menu…";
        internal const string WaitingText = "The game has not finished changing scenes. Another load cannot start until the pending work ends. Wait, or close and reopen the game.";
        internal const string CancellingText = "The menu request was cancelled, but the game is still changing scenes. Wait for it to finish before starting another load.";
        internal const string CompletedText = "The main-menu request completed.";
        internal const string FailedText = "The main-menu request failed. Try again, or close and reopen the game.";
        internal const string CancelledText = "The main-menu request was cancelled.";
        internal const string BusyText = "The main-menu request could not start. Wait for the current scene operation, then try again.";
        private readonly IHostDispatcher dispatcher;
        private readonly Func<bool> ownerCurrent;
        private Attempt? current;
        private string text = string.Empty;
        private bool pending;
        internal MenuLoadPresentation(IHostDispatcher dispatcher, Func<bool> ownerCurrent)
        { this.dispatcher = dispatcher; this.ownerCurrent = ownerCurrent; }
        internal bool OwnerCurrent => ownerCurrent();
        internal int Version { get; private set; }
        internal string Text => OwnerCurrent ? text : string.Empty;
        internal bool Pending => OwnerCurrent && pending;
        internal Attempt? CurrentAttempt => OwnerCurrent ? current : null;
        internal bool IsCurrent(Attempt? attempt) => OwnerCurrent && ReferenceEquals(current, attempt);

        internal Attempt? Begin(string requestId)
        {
            if (!OwnerCurrent) return null;
            current = new Attempt(requestId);
            Set(OpeningText, true);
            return current;
        }

        internal void Complete(Attempt? attempt, OperationResult<bool> result)
        {
            if (attempt == null || !IsCurrent(attempt)) return;
            Set(result.Succeeded ? CompletedText : TerminalText(result.ErrorCode), false);
        }

        internal static string TerminalText(ModErrorCode code) => code == ModErrorCode.Cancelled ? CancelledText
            : code == ModErrorCode.Conflict ? BusyText : FailedText;

        private void Set(string value, bool isPending)
        {
            if (text == value && pending == isPending) return;
            text = value; pending = isPending;
            Version = unchecked(Version + 1);
        }

        private async Task ObserveAsync(Attempt attempt, IInternalNativeSceneOperation operation)
        {
            try
            {
                var caller = await operation.Completion.ConfigureAwait(false);
                if (caller.Succeeded) return;
                await dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrent(attempt) || operation.NativeDrained.IsCompleted) return;
                    Set(caller.ErrorCode == ModErrorCode.Cancelled ? CancellingText : WaitingText, true);
                }).ConfigureAwait(false);
            }
            catch { } // Optional feedback never changes the native command or cleanup path.
        }

        internal sealed class Attempt
        {
            internal Attempt(string requestId) { RequestId = requestId; }
            internal string RequestId { get; }
        }
    }
}
