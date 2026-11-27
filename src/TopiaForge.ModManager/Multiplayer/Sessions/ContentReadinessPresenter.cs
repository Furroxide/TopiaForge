using System;
using System.Globalization;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The progress line HOST and BROWSE show while the runtime identifies this game's mods off the main thread, and its
    /// failure with a retry. It reads the runtime's readiness each frame; its text is rebuilt only when the shown percent
    /// changes, so a settled state allocates nothing.
    /// </summary>
    internal sealed class ContentReadinessPresenter : IContentReadinessScreen
    {
        internal const string PendingPrefix = "Identifying this game's mods for multiplayer";
        internal const string PendingText = PendingPrefix + "...";

        private readonly IMultiplayerSessionEnvironment environment;
        private int shownPercent = -1;
        private string text = string.Empty;

        internal ContentReadinessPresenter(IMultiplayerSessionEnvironment environment)
        {
            this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
        }

        public ContentReadinessState State { get; private set; } = ContentReadinessState.Pending;

        public string Text => text;

        public float Fraction { get; private set; }

        public bool CanRetry => State == ContentReadinessState.Unreadable;

        internal bool IsReady => State == ContentReadinessState.Ready;

        public void Retry()
        {
            if (!CanRetry) return;
            environment.RetryContentIdentification();
            Tick();
        }

        /// <summary>Reads the runtime's readiness. Reading it starts identification when nothing has, so call it only while shown.</summary>
        internal void Tick()
        {
            InternalContentReadiness readiness;
            try
            {
                readiness = environment.ContentReadiness;
            }
            catch (Exception)
            {
                readiness = new InternalContentReadiness(InternalContentState.Unreadable, 0, 0, 0, 0);
            }

            switch (readiness.State)
            {
                case InternalContentState.Ready:
                    Set(ContentReadinessState.Ready, 1f, -1, string.Empty);
                    break;
                case InternalContentState.Unreadable:
                    Set(ContentReadinessState.Unreadable, 0f, -2, LiveFailureMessages.UnreadableContent);
                    break;
                default:
                    var fraction = readiness.BytesTotal > 0 ? (float)((double)readiness.BytesDone / readiness.BytesTotal)
                        : readiness.PackagesTotal > 0 ? (float)readiness.PackagesDone / readiness.PackagesTotal
                        : 0f;
                    var percent = (int)Math.Floor(Math.Min(Math.Max(fraction, 0f), 1f) * 100f);
                    var shown = percent == shownPercent ? text
                        : readiness.PackagesTotal == 0 ? PendingText
                        : PendingPrefix + ": " + percent.ToString(CultureInfo.InvariantCulture) + "%";
                    Set(ContentReadinessState.Pending, fraction, readiness.PackagesTotal == 0 ? -3 : percent, shown);
                    break;
            }
        }

        private void Set(ContentReadinessState state, float fraction, int percent, string shown)
        {
            State = state;
            Fraction = fraction;
            shownPercent = percent;
            text = shown;
        }
    }
}
