using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService : IUiClockReceiver
    {
        private Func<double> clock = () => 0;
        private Func<string> clockEpoch = () => "offline";
        public void FollowClock(Func<double> value, Func<string> epoch) { clock = value; clockEpoch = epoch; }
        private UnityWorldLabels? labels;
        private UnityEntityRegistry? labelEntities;
        public double ClockTime { get { UnityMainThreadGuard.AssertCurrent(); return clock(); } }
        public IUiWorldLabels Labels
        {
            get
            {
                UnityMainThreadGuard.AssertCurrent();
                if (labels != null) return labels;
                labels = new UnityWorldLabels(host, lifetime, gameplay, labelEntities!);
                if (!lifetime.IsStopping) lifetime.Track(labels);
                return labels;
            }
        }
        internal void AttachWorldWidgets(UnityEntityRegistry entities)
        {
            labelEntities = entities;
        }
        private sealed partial class UnityUiSurface
        {
            internal void SetClock(Func<double> value, Func<string> epoch)
            { callbacks.Clock = value; callbacks.ClockEpoch = epoch; }
        }
        private static TopiaForgeLabel? RenderProgress(UiProgress progress, TopiaForgeContainer parent)
        {
            var column = parent.Column(TopiaForgeGap.Xs, TopiaForgeGap.None);
            var label = progress.Label.Length == 0 ? null : column.Label(progress.Label).PlainText().Tone(ToNativeTone(progress.Tone));
            column.ProgressBar().Tone(ToNativeTone(progress.Tone)).SetHorizontalLayoutFraction(progress.Fraction);
            return label;
        }
        private static TopiaForgeLabel RenderCountdown(UiCountdown countdown, TopiaForgeContainer parent, Func<double> clock)
        {
            var column = parent.Column(TopiaForgeGap.None, TopiaForgeGap.None).Dynamic();
            var label = column.Label(string.Empty).PlainText().Tone(ToNativeTone(countdown.Tone));
            var driver = column.Go.AddComponent<UiCountdownDriver>();
            driver.Initialize(countdown, clock, label);
            return label;
        }
    }
    internal sealed class UiCountdownDriver : MonoBehaviour
    {
        private UiCountdown? node;
        private Func<double>? clock;
        private TopiaForgeLabel? label;
        private string prefix = string.Empty;
        private int seconds = -1;
        internal void Initialize(UiCountdown node, Func<double> clock, TopiaForgeLabel label)
        {
            this.node = node; this.clock = clock; this.label = label;
            prefix = node.Label.Length == 0 ? string.Empty : node.Label + " ";
            Refresh();
        }
        private void Update() => Refresh();

        internal void Refresh()
        {
            if (node == null || clock == null || label == null) return;
            var remaining = node.RemainingSeconds(clock());
            if (seconds == remaining) return;
            seconds = remaining; label.SetText(prefix, remaining);
        }
        private void OnDestroy() { node = null; clock = null; label = null; }
    }
}
