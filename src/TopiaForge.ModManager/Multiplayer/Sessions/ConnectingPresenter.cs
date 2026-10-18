using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>One join the player asked for: where it goes, how the overlay names it, and the choices made for it.</summary>
    internal sealed class JoinRequest
    {
        internal JoinRequest(IInternalJoinTarget target, string title, bool allowDirect, string? password = null, string? trustedServerId = null, string? launcherLink = null)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Title = title ?? string.Empty;
            AllowDirect = allowDirect;
            Password = password;
            TrustedServerId = trustedServerId;
            LauncherLink = launcherLink;
        }

        internal IInternalJoinTarget Target { get; }

        /// <summary>Gets the server's name as the overlay shows it, already redacted.</summary>
        internal string Title { get; }

        /// <summary>Gets the player's choice for this session: a direct path, allowed through the approved prompt.</summary>
        internal bool AllowDirect { get; }

        internal string? Password { get; }

        internal string? TrustedServerId { get; }

        /// <summary>Gets the link the launcher can take this join over with; null when there is none.</summary>
        internal string? LauncherLink { get; }

        internal JoinRequest Trusting(string serverId) => new JoinRequest(Target, Title, AllowDirect, Password, serverId, LauncherLink);

        /// <summary>The same join with a direct connection allowed, for this session only, as the approved prompt lets it.</summary>
        internal JoinRequest Direct() => new JoinRequest(Target, Title, allowDirect: true, Password, TrustedServerId, LauncherLink);

        /// <summary>The same join without a password, so the session asks for one on the connecting overlay.</summary>
        internal JoinRequest WithoutPassword() => new JoinRequest(Target, Title, AllowDirect, null, TrustedServerId, LauncherLink);
    }

    /// <summary>
    /// Runs a join the screens ask for and presents it on the connecting overlay. It asks the runtime's realm rules first,
    /// and joins only through the runtime: a game that is running is left only after "Leave current game?" is confirmed,
    /// and a game that is starting or stopping, the main menu loading after leaving a game included, is waited for on the
    /// overlay until the rules let the join through. Then it follows the control's status onto the stepper
    /// (<see cref="ConnectingStepMap"/>), asks for a password when the session needs one, and on failure shows the failure
    /// view with Retry, Open launcher and Copy details. Cancel works throughout, and leaving is never refused.
    /// </summary>
    internal sealed partial class ConnectingPresenter : IConnectingScreen
    {
        internal const float SuccessLingerSeconds = 0.75f;
        internal const string LeaveTitle = "Leave current game?";
        internal const string LeaveBody = "Joining this session ends the game you are playing. Anything you have not saved is lost.";
        internal const string LeaveConfirm = "LEAVE AND JOIN";
        internal const string PasswordRule = "A password is 1 to 64 characters with no control characters.";
        internal const string WaitingText = "Waiting for the current game to finish starting or stopping...";
        internal const string DetailsNotCopied = "The failure details could not be copied. Try again.";

        /// <summary>The longest delay the overlay waits out before it joins again, a day, as the runtime keeps the service's own.</summary>
        internal const int MaximumRetryWaitSeconds = 86_400;

        private static readonly string[] Labels =
        {
            "RESOLVING", "CONNECTING", "AUTHENTICATING", "VERIFYING MODS", "LOADING WORLD", "SYNCHRONIZING", "SPAWNING"
        };

        private readonly MultiplayerScreensContext context;
        private readonly string[] details = new string[ConnectingSteps.Count];
        private readonly Func<bool> leaveStillAsked;
        private JoinRequest? request;
        private Stage stage;
        private int readsAtStart;
        private bool menuReturned;
        private bool cancelRequested;
        private bool held;
        private float linger;

        // A double, so a wait of hours still counts down at a high frame rate: near a day a float cannot take off a frame's
        // millisecond or two, and the count would stand still.
        private double retryWait;
        private bool retryWhenWaited;
        private int shownWait = -1;
        private int reached;
        private int active = -1;
        private int failed = -1;
        private bool complete;
        private int skipped;
        private int version;
        private ConnectingSteps steps;
        private FailureMessage? failure;
        private InternalLiveFailure failureCode;
        private InternalLiveStatus? failureStatus;
        private string? changedServerId;
        private IReadOnlyList<string> failureLines = Array.Empty<string>();
        private string hostSays = string.Empty;
        private string title = string.Empty;
        private string statusText = string.Empty;
        private string passwordError = string.Empty;

        internal ConnectingPresenter(MultiplayerScreensContext context)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            for (var index = 0; index < details.Length; index++) details[index] = string.Empty;
            leaveStillAsked = () => !IsBusy &&
                SessionRealmGate.ForJoin(context.Environment.EvaluateRealmChange(NetRealm.Client), context.Environment.Realm) == RealmDecision.ConfirmLeaveGame;
        }

        private enum Stage
        {
            Hidden,

            /// <summary>Returning to the main menu after "Leave current game?", before the join.</summary>
            Leaving,

            /// <summary>Waiting for a game that is starting or stopping to settle, before the join.</summary>
            Waiting,
            Joining,
            Succeeded,
            Failed
        }

        /// <summary>Gets whether a join is being prepared or running, so no other may start.</summary>
        internal bool IsBusy => stage == Stage.Leaving || stage == Stage.Waiting || stage == Stage.Joining;

        public bool IsVisible => stage != Stage.Hidden;

        public string Title => title;

        public IReadOnlyList<string> StepLabels => Labels;

        public ConnectingSteps Steps => steps;

        public string StatusText => statusText;

        public bool NeedsPassword => stage == Stage.Joining && context.Status.Reads > readsAtStart && context.Status.Status.JoinPhase == InternalJoinPhase.AwaitingPassword;

        public string PasswordError => passwordError;

        public bool HasFailed => stage == Stage.Failed;

        public string FailureTitle => failure?.Title ?? string.Empty;

        public string FailureMessage => failure?.Message ?? string.Empty;

        public IReadOnlyList<string> FailureLines => failureLines;

        public string HostSays => hostSays;

        public bool CanRetry => HasFailed && request != null && failure != null && failure.Retryable && retryWait <= 0d;

        public bool CanOpenLauncher => HasFailed && request?.LauncherLink != null && failure != null && (failure.LauncherHelps || failure.Retryable);

        public bool CanCopyDetails => HasFailed && failureStatus != null;

        public bool CanTrust => HasFailed && failure != null && failure.CanTrust && changedServerId != null && request != null;

        public bool CanCancel => IsBusy;

        public string DetailOf(int step) => step >= 0 && step < details.Length ? details[step] : string.Empty;

        /// <summary>Starts a join through the runtime's realm rules; a running game is left only once the player confirms.</summary>
        internal void Begin(JoinRequest join)
        {
            if (join == null) throw new ArgumentNullException(nameof(join));
            if (IsBusy)
            {
                context.Notify("A join is already running.", ScreenTone.Warning);
                return;
            }

            if (!context.CanStartSession)
            {
                context.Notify(
                    !context.Status.IsLiveAvailable ? "Live multiplayer is not available."
                    : !context.Settings.IsLoaded ? MultiplayerSessionsPresenter.LoadingSettings
                    : ContentReadinessPresenter.PendingText,
                    ScreenTone.Warning);
                return;
            }

            Decide(join, fromWait: false);
        }

        /// <summary>Main thread, every frame: follows the join, or what comes before it: the return to the menu, or a wait.</summary>
        internal void Tick(float deltaTime)
        {
            switch (stage)
            {
                case Stage.Leaving:
                    // The menu may still load after the game ended; the runtime's rules say busy until it has.
                    if (menuReturned) Settle();
                    break;
                case Stage.Waiting:
                    Settle();
                    break;
                case Stage.Joining:
                    Follow();
                    break;
                case Stage.Succeeded:
                    linger += deltaTime > 0f ? deltaTime : 0f;
                    if (linger >= SuccessLingerSeconds) Hide();
                    break;
                case Stage.Failed:
                    if (retryWait > 0d) CountDown(deltaTime);
                    break;
            }
        }

        /// <summary>
        /// Shows the world synchronizing n of m, for the network world's baseline once it reports it (WS3.6); until then the
        /// step reads "Receiving the world".
        /// </summary>
        internal void ReportSynchronization(int done, int total)
        {
            if (stage != Stage.Joining || total <= 0) return;
            var clamped = Math.Min(Math.Max(done, 0), total);
            details[(int)ConnectingStep.Synchronizing] = clamped.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);
        }

        public void SubmitPassword(string value)
        {
            if (!NeedsPassword) return;
            if (string.IsNullOrEmpty(value) || value.Length > 64 || HasControl(value))
            {
                passwordError = PasswordRule;
                return;
            }

            OperationResult<bool> result;
            try
            {
                result = context.Environment.Control?.SupplyPassword(value) ?? OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Multiplayer is not available.");
            }
            catch (Exception exception)
            {
                result = OperationResult<bool>.Failure(ModErrorCode.Unavailable, exception.GetType().Name);
            }

            passwordError = result.Succeeded ? string.Empty : HostText.ForDisplay(result.ErrorMessage, HostText.LineChars);
            context.Status.RequestRefresh();
        }

        public void Retry()
        {
            if (!CanRetry) return;
            var again = request!;

            // A password the host turned away is asked for again on the overlay, never sent again: the same password cannot
            // succeed, and every try counts toward the host's limit on wrong guesses.
            if (failureCode == InternalLiveFailure.Rejected && IsPasswordRefusal(failureStatus!.RejectionCode)) again = again.WithoutPassword();
            Hide();
            Begin(again);
        }

        public void Trust()
        {
            if (!CanTrust) return;
            var trusted = request!.Trusting(changedServerId!);
            Hide();
            Begin(trusted);
        }

        public void OpenLauncher()
        {
            if (!CanOpenLauncher) return;
            context.Environment.OpenUrl(request!.LauncherLink!);
            context.Notify("Handed the join to the TopiaForge launcher.", ScreenTone.Success);
            Hide();
        }

        public void CopyDetails()
        {
            if (!CanCopyDetails) return;
            var step = (ConnectingStep)Math.Max(failed, 0);
            try
            {
                context.Environment.CopyToClipboard(FailureDetails.Build(
                    failureCode, step, Labels[(int)step], failureStatus!, context.Environment.GameBuild, context.Environment.RuntimeVersion));
            }
            catch (Exception)
            {
                // A clipboard that fails is said in this game's words; nothing it throws reaches the kit's log of failed clicks.
                context.Notify(DetailsNotCopied, ScreenTone.Warning);
                return;
            }

            context.Notify("Copied the failure details.", ScreenTone.Success);
        }

        public void Cancel()
        {
            if (stage == Stage.Joining)
            {
                // Leaving is never refused: the runtime stops the join on its way, whatever the provider says.
                cancelRequested = true;
                context.Environment.Leave();
                context.Status.RequestRefresh();
            }
            else if (stage != Stage.Leaving && stage != Stage.Waiting)
            {
                return;
            }

            Hide();
            context.Notify("Join cancelled.");
        }

        public void Dismiss()
        {
            if (IsBusy) Cancel();
            else Hide();
        }

        private static bool IsPasswordRefusal(InternalAdmissionCode code) =>
            code == InternalAdmissionCode.PasswordRequired || code == InternalAdmissionCode.PasswordWrong || code == InternalAdmissionCode.TooManyAttempts;

        private static bool HasControl(string value)
        {
            foreach (var character in value)
            {
                if (char.IsControl(character)) return true;
            }

            return false;
        }
    }
}
