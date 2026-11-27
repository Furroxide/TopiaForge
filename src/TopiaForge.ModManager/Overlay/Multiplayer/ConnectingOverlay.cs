using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;
#if TOPIAFORGE_LOADER_TRUST_DIALOGS
using ConnectingPanel = TopiaForge.Mods.UnityUi.TopiaForgeOverlay;
#else
using ConnectingPanel = TopiaForge.Mods.UnityUi.TopiaForgeModalInstance;
#endif

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The connecting overlay: a protected Paper surface with the join's phase stepper (Resolving, Connecting, Authenticating, Verifying
    /// mods, Loading world, Synchronizing, Spawning), a password field when the session asks, and a failure view with Retry,
    /// Open launcher and Copy details. Cancel and Back stop a first join; during a re-follow only Cancel leaves, while Back
    /// hides that attempt's card. Once nothing runs that Cancel
    /// would stop, as while a finished join lingers or a follow waits at the menu, CLOSE takes its place. Its layout is
    /// rebuilt only when it switches between those; otherwise it binds through dirty-checked setters, the failure's own
    /// words included, so a failure that replaces another in the same layout never keeps the earlier one's.
    /// </summary>
    internal sealed class ConnectingOverlay
    {
        /// <summary>The body height on roomy screens. The kit caps it to keep the title and actions visible.</summary>
        internal const float PreferredScrollHeight = 456f;

        private readonly UiHost host;
        private readonly IConnectingScreen screen;
        private readonly StepperOperations operations = new StepperOperations();
        private ConnectingPanel? overlay;
        private TopiaForgePhaseStepper? stepper;
        private TopiaForgeLabel? title;
        private TopiaForgeLabel? status;
        private TopiaForgeContainer? body;
        private TopiaForgeContainer? actions;
        private TopiaForgeContainer? passwordRow;
        private TopiaForgeInputField? password;
        private TopiaForgeLabel? passwordError;
        private TopiaForgeLabel? failureTitle;
        private TopiaForgeLabel? failureMessage;
        private TopiaForgeLabel? hostSays;
        private TopiaForgeLabel? lines;
        private TopiaForgeButton? retry;
        private TopiaForgeButton? launcher;
        private TopiaForgeButton? copy;
        private TopiaForgeButton? trust;
        private TopiaForgeButton? defaultAction;
        private IReadOnlyList<string>? shownLines;
        private int appliedVersion = -1;
        private int shownShape = -1;

        internal ConnectingOverlay(UiHost host, IConnectingScreen screen)
        {
            this.host = host;
            this.screen = screen;
        }

        internal void Tick()
        {
#if TOPIAFORGE_LOADER_TRUST_DIALOGS
            if (overlay is { IsRetired: true })
            {
                Close();
                screen.Dismiss();
            }
#endif
            if (screen.IsVisible && overlay == null) Open();
            else if (!screen.IsVisible && overlay != null) Close();
            if (overlay == null) return;
#if TOPIAFORGE_LOADER_TRUST_DIALOGS
            overlay.RefreshViewport();
#endif
            if (Shape() != shownShape)
            {
                BuildBody();
#if TOPIAFORGE_LOADER_TRUST_DIALOGS
                overlay.CompositionChanged();
#endif
                FocusDefaultAction();
            }
            Bind();
        }

        internal void Close()
        {
            var open = overlay;
            overlay = null;
            if (open == null) return;
#if TOPIAFORGE_LOADER_TRUST_DIALOGS
            if (!open.IsRetired) password?.SetText(string.Empty);
            open.Retire(static () => { });
#else
            password?.SetText(string.Empty);
            open.Close();
#endif
        }

        private void Open()
        {
            // Persistent: joining from a running game returns to the main menu first, and the overlay follows the join
            // through that scene load, and later through loading the host's world.
#if TOPIAFORGE_LOADER_TRUST_DIALOGS
            var opened = host.TrustOverlay(screen.Title, 680f);
            title = opened.TitleLabel.PlainText().Ellipsis().AlignTopLeft();
#else
            // The Gallery links this view for its synthetic Sessions preview.
            var opened = host.Modal.Custom(string.Empty, TopiaForgeScheme.Paper, 680f, persistent: true);
            title = opened.Content.Label(screen.Title, TopiaForgeTextStyle.Title).PlainText().Ellipsis();
#endif
            overlay = opened;
            var scroll = opened.Content.Scroll(TopiaForgeGap.Sm);
            scroll.FixedHeight(PreferredScrollHeight);
            stepper = scroll.Content.VerticalPhaseStepper(ToArray(screen.StepLabels));
            operations.Stepper = stepper;
            appliedVersion = -1;
            status = scroll.Content.Label(string.Empty, TopiaForgeTextStyle.Body).PlainText();
            body = scroll.Content.Column(TopiaForgeGap.Sm);
            actions = opened.Content.Row(TopiaForgeGap.Sm);
            BuildBody();
#if TOPIAFORGE_LOADER_TRUST_DIALOGS
            opened.LimitTrustedScrollBody();
#endif

            // Back dismisses through the presenter: cancel the first join, hide a re-follow or a settled view.
            opened.Closed += () =>
            {
                if (!ReferenceEquals(overlay, opened)) return;
                Close();
                screen.Dismiss();
            };
            opened.Show();
            FocusDefaultAction();
        }

        /// <summary>Gets the overlay's layout: running, asking for a password, failed, or settled with nothing Cancel would stop.</summary>
        private int Shape() => screen.HasFailed ? 2 : screen.NeedsPassword ? 1 : screen.CanCancel ? 0 : 3;

        private void BuildBody()
        {
            shownShape = Shape();
            host.Clear(body!);
            host.Clear(actions!);
            passwordRow = null;
            password = null;
            passwordError = null;
            failureTitle = failureMessage = hostSays = lines = null;
            retry = launcher = copy = trust = null;
            shownLines = null;
            if (shownShape != 2)
            {
                if (shownShape == 1)
                {
                    passwordRow = body!.Column(TopiaForgeGap.Xs);
                    var entry = passwordRow.Row(TopiaForgeGap.Sm);
                    entry.FixedHeight(TopiaForgeTokens.ControlHeight);
                    password = entry.Input("Session password", string.Empty, _ => { }).Password();
                    password.SetCharacterLimit(64);
                    password.OnSubmit(_ => SubmitPassword());
                    entry.Button("SUBMIT", SubmitPassword);
                    passwordError = passwordRow.Label(string.Empty, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Danger).PlainText();
                }

                actions!.Spacer().Flex(1f, 0f);

                // No CANCEL that cancels nothing: a finished join, or a follow that waits at the menu, closes instead.
                if (shownShape == 3) defaultAction = actions.Button("CLOSE", screen.Dismiss, TopiaForgeButtonStyle.Outline);
                else defaultAction = actions.Button("CANCEL", screen.Cancel, TopiaForgeButtonStyle.Outline);
                return;
            }

            failureTitle = body!.Label(screen.FailureTitle, TopiaForgeTextStyle.Heading).Tone(TopiaForgeTone.Danger).PlainText();
            failureMessage = body.Label(screen.FailureMessage, TopiaForgeTextStyle.Body).PlainText();
            lines = body.Label(string.Empty, TopiaForgeTextStyle.Caption).PlainText();
            hostSays = body.Label(screen.HostSays, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).PlainText();
            var buttons = actions!;
            retry = buttons.Button("RETRY", screen.Retry);
            launcher = buttons.Button("OPEN LAUNCHER", screen.OpenLauncher, TopiaForgeButtonStyle.Outline);
            copy = buttons.Button("COPY DETAILS", screen.CopyDetails, TopiaForgeButtonStyle.Ghost);
            trust = buttons.Button("TRUST AND JOIN", screen.Trust, TopiaForgeButtonStyle.Outline);
            buttons.Spacer().Flex(1f, 0f);
            defaultAction = buttons.Button("CLOSE", screen.Dismiss, TopiaForgeButtonStyle.Ghost);
        }

        private void FocusDefaultAction()
        {
#if TOPIAFORGE_LOADER_TRUST_DIALOGS
            if (overlay?.HasNavigationFocus == true) defaultAction?.Focus();
#else
            defaultAction?.Focus();
#endif
        }

        private void Bind()
        {
            title!.SetText(screen.Title);
            status!.SetText(screen.StatusText);
            var steps = screen.Steps;
            if (steps.Version != appliedVersion)
            {
                appliedVersion = steps.Version;
                ConnectingStepReplay.Apply(steps, operations);
            }

            for (var step = 0; step < ConnectingSteps.Count; step++) stepper!.SetDetail(step, screen.DetailOf(step));
            passwordError?.SetText(screen.PasswordError);

            // A RETRY that fails at once replaces one failure with another before the layout changes: the heading and the
            // words are bound every frame, so they are always the current failure's.
            failureTitle?.SetText(screen.FailureTitle);
            failureMessage?.SetText(screen.FailureMessage);
            hostSays?.SetText(screen.HostSays);

            if (lines != null && !ReferenceEquals(screen.FailureLines, shownLines))
            {
                shownLines = screen.FailureLines;
                lines.SetText(string.Join("\n", shownLines));
            }

            retry?.SetEnabled(screen.CanRetry);
            launcher?.SetEnabled(screen.CanOpenLauncher);
            copy?.SetEnabled(screen.CanCopyDetails);
            trust?.SetVisible(screen.CanTrust);
            trust?.SetEnabled(screen.CanTrust);
        }

        /// <summary>Hands the typed password to the join once and clears the field: it is never kept.</summary>
        private void SubmitPassword()
        {
            var field = password;
            if (field == null) return;
            var typed = field.Text;
            field.SetText(string.Empty);
            screen.SubmitPassword(typed);
        }

        private static string[] ToArray(IReadOnlyList<string> labels)
        {
            var array = new string[labels.Count];
            for (var index = 0; index < array.Length; index++) array[index] = labels[index];
            return array;
        }

        /// <summary>The kit stepper behind the replay's operations.</summary>
        private sealed class StepperOperations : IStepperOperations
        {
            internal TopiaForgePhaseStepper? Stepper { get; set; }

            public bool Reset() => Stepper != null && Stepper.Reset();

            public bool Skip(int index) => Stepper != null && Stepper.Skip(index);

            public bool MoveTo(int index) => Stepper != null && Stepper.MoveTo(index);

            public bool Fail() => Stepper != null && Stepper.Fail();

            public bool Complete() => Stepper != null && Stepper.Complete();
        }
    }
}
