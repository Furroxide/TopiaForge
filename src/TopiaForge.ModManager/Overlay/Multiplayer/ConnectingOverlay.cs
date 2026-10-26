using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The connecting overlay: a Paper modal with the join's phase stepper (Resolving, Connecting, Authenticating, Verifying
    /// mods, Loading world, Synchronizing, Spawning), a password field when the session asks, and a failure view with Retry,
    /// Open launcher and Copy details. Cancel, the close button and ESC all stop a running join; once nothing runs that Cancel
    /// would stop, as while a finished join lingers or a follow waits at the menu, CLOSE takes its place. Its layout is
    /// rebuilt only when it switches between those; otherwise it binds through dirty-checked setters, the failure's own
    /// words included, so a failure that replaces another in the same layout never keeps the earlier one's.
    /// </summary>
    internal sealed class ConnectingOverlay
    {
        private readonly UiHost host;
        private readonly IConnectingScreen screen;
        private readonly StepperOperations operations = new StepperOperations();
        private TopiaForgeModalInstance? modal;
        private TopiaForgePhaseStepper? stepper;
        private TopiaForgeLabel? title;
        private TopiaForgeLabel? status;
        private TopiaForgeContainer? body;
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
            if (screen.IsVisible && modal == null) Open();
            else if (!screen.IsVisible && modal != null) Close();
            if (modal == null) return;
            if (Shape() != shownShape) BuildBody();
            Bind();
        }

        internal void Close()
        {
            var open = modal;
            modal = null;
            open?.Close();
        }

        private void Open()
        {
            // Persistent: joining from a running game returns to the main menu first, and the overlay follows the join
            // through that scene load, and later through loading the host's world.
            var opened = host.Modal.Custom(string.Empty, TopiaForgeScheme.Paper, 680f, persistent: true);
            modal = opened;
            title = opened.Content.Label(screen.Title, TopiaForgeTextStyle.Title).PlainText().Ellipsis();
            stepper = opened.Content.PhaseStepper(ToArray(screen.StepLabels));
            operations.Stepper = stepper;
            appliedVersion = -1;
            status = opened.Content.Label(string.Empty, TopiaForgeTextStyle.Body).PlainText();
            body = opened.Content.Column(TopiaForgeGap.Sm);
            BuildBody();

            // ESC or any other close stops a running join, or dismisses the failure view.
            opened.Closed += () =>
            {
                if (ReferenceEquals(modal, opened)) modal = null;
                screen.Dismiss();
            };
            opened.Show();
        }

        /// <summary>Gets the overlay's layout: running, asking for a password, failed, or settled with nothing Cancel would stop.</summary>
        private int Shape() => screen.HasFailed ? 2 : screen.NeedsPassword ? 1 : screen.CanCancel ? 0 : 3;

        private void BuildBody()
        {
            shownShape = Shape();
            host.Clear(body!);
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

                var actions = body!.Row(TopiaForgeGap.Sm);
                actions.Spacer().Flex(1f, 0f);

                // No CANCEL that cancels nothing: a finished join, or a follow that waits at the menu, closes instead.
                if (shownShape == 3) actions.Button("CLOSE", screen.Dismiss, TopiaForgeButtonStyle.Outline).Focus();
                else actions.Button("CANCEL", screen.Cancel, TopiaForgeButtonStyle.Outline).Focus();
                return;
            }

            failureTitle = body!.Label(screen.FailureTitle, TopiaForgeTextStyle.Heading).Tone(TopiaForgeTone.Danger).PlainText();
            failureMessage = body.Label(screen.FailureMessage, TopiaForgeTextStyle.Body).PlainText();
            lines = body.Label(string.Empty, TopiaForgeTextStyle.Caption).PlainText();
            hostSays = body.Label(screen.HostSays, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).PlainText();
            var buttons = body.Row(TopiaForgeGap.Sm);
            retry = buttons.Button("RETRY", screen.Retry);
            launcher = buttons.Button("OPEN LAUNCHER", screen.OpenLauncher, TopiaForgeButtonStyle.Outline);
            copy = buttons.Button("COPY DETAILS", screen.CopyDetails, TopiaForgeButtonStyle.Ghost);
            trust = buttons.Button("TRUST AND JOIN", screen.Trust, TopiaForgeButtonStyle.Outline);
            buttons.Spacer().Flex(1f, 0f);
            buttons.Button("CLOSE", screen.Dismiss, TopiaForgeButtonStyle.Ghost).Focus();
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
