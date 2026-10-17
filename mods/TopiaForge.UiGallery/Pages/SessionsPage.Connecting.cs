using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Sessions;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>The connecting overlay walking a made-up join: each phase in turn, a password, a failure, or success.</summary>
    internal sealed class FakeConnecting : IConnectingScreen
    {
        private const float StepSeconds = 0.9f;

        private static readonly string[] Labels =
        {
            "RESOLVING", "CONNECTING", "AUTHENTICATING", "VERIFYING MODS", "LOADING WORLD", "SYNCHRONIZING", "SPAWNING"
        };

        private static readonly string[] NoLines = Array.Empty<string>();
        private static readonly string[] ModLines = { "Zombies 0.2.0 is missing.", "Sandbox: the host runs 0.2.0; this game has 0.3.0." };

        private readonly GallerySessionScreens owner;
        private readonly string[] details = new string[ConnectingSteps.Count];
        private int version;
        private int active = -1;
        private int failed = -1;
        private bool complete;
        private int skipped;
        private int failAt = -1;
        private int sync;
        private float timer;
        private bool askPassword;

        internal FakeConnecting(GallerySessionScreens owner)
        {
            this.owner = owner;
            Clear();
        }

        public bool IsVisible { get; private set; }

        public string Title { get; private set; } = string.Empty;

        public IReadOnlyList<string> StepLabels => Labels;

        public ConnectingSteps Steps => new ConnectingSteps(version, active, failed, complete, skipped);

        public string StatusText => HasFailed ? FailureTitle : complete ? "Joined " + Title + "." : NeedsPassword ? "This session needs a password." : "Joining " + Title + "...";

        public bool NeedsPassword { get; private set; }

        public string PasswordError { get; private set; } = string.Empty;

        public bool HasFailed => failed >= 0;

        public string FailureTitle => HasFailed ? "Mods do not match" : string.Empty;

        public string FailureMessage => HasFailed ? "This game's mods do not match the host's. The TopiaForge launcher can install the host's exact mods and join for you." : string.Empty;

        public IReadOnlyList<string> FailureLines => HasFailed ? ModLines : NoLines;

        public string HostSays => HasFailed ? "The host says: see you on [address] tonight" : string.Empty;

        public bool CanRetry => HasFailed;

        public bool CanOpenLauncher => HasFailed;

        public bool CanCopyDetails => HasFailed;

        public bool CanTrust => false;

        public bool CanCancel => IsVisible && !HasFailed && !complete;

        /// <summary>Starts a made-up join, failing at <paramref name="failAt"/>, or succeeding when it is -1.</summary>
        internal void Start(string title, int failAt, bool password = false)
        {
            Title = title;
            this.failAt = failAt;
            askPassword = password;
            Clear();
            IsVisible = true;
            active = 0;
            details[0] = "Finding the host";
            Bump();
        }

        public string DetailOf(int step) => step >= 0 && step < details.Length ? details[step] : string.Empty;

        public void SubmitPassword(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                PasswordError = "A password is 1 to 64 characters with no control characters.";
                return;
            }

            PasswordError = string.Empty;
            NeedsPassword = false;
            askPassword = false;
        }

        public void Retry() => Start(Title, -1);

        public void OpenLauncher()
        {
            owner.Notify("Handed the join to the TopiaForge launcher.", ScreenTone.Success);
            Dismiss();
        }

        public void CopyDetails() => owner.Notify("Copied the failure details.", ScreenTone.Success);

        public void Trust()
        {
        }

        public void Cancel()
        {
            IsVisible = false;
            owner.Notify("Join cancelled.");
        }

        public void Dismiss() => IsVisible = false;

        internal void Tick(float deltaTime)
        {
            if (!IsVisible || HasFailed || NeedsPassword) return;
            timer += deltaTime;
            if (complete)
            {
                if (timer >= StepSeconds) IsVisible = false;
                return;
            }

            if (timer < StepSeconds / (active == (int)ConnectingStep.Synchronizing ? 3f : 1f)) return;
            timer = 0f;
            if (active == (int)ConnectingStep.Connecting && askPassword)
            {
                NeedsPassword = true;
                details[1] = "Password needed";
                return;
            }

            if (active == (int)ConnectingStep.Synchronizing && sync < 7)
            {
                sync++;
                details[active] = sync + "/7";
                if (sync < 7) return;
            }

            if (failAt >= 0 && active + 1 >= failAt)
            {
                for (var step = active + 1; step < failAt; step++) skipped |= 1 << step;
                failed = failAt;
                active = -1;
                Bump();
                return;
            }

            Advance();
        }

        private void Advance()
        {
            var next = active + 1;
            if (next == (int)ConnectingStep.LoadingWorld) next++;
            if (next >= ConnectingSteps.Count)
            {
                skipped |= 1 << (int)ConnectingStep.LoadingWorld;
                complete = true;
                active = -1;
                owner.Notify("Joined " + Title + ".", ScreenTone.Success);
                Bump();
                return;
            }

            if (next == (int)ConnectingStep.Synchronizing) skipped |= 1 << (int)ConnectingStep.LoadingWorld;
            active = next;
            details[next] = next == 1 ? "LAN" : next == 2 ? "Waiting for the host" : next == 5 ? "0/7" : string.Empty;
            Bump();
        }

        private void Clear()
        {
            for (var index = 0; index < details.Length; index++) details[index] = string.Empty;
            active = -1;
            failed = -1;
            complete = false;
            skipped = 0;
            sync = 0;
            timer = 0f;
            NeedsPassword = false;
            PasswordError = string.Empty;
        }

        private void Bump() => version++;
    }
}
