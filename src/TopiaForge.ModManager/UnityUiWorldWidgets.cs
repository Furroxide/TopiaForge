using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private Func<double> clock = () => 0;
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
            // A mod may create its first countdown before the gameplay loop samples its first frame.
            var origin = UnityEngine.Time.realtimeSinceStartupAsDouble;
            clock = () => Math.Max(0, UnityEngine.Time.realtimeSinceStartupAsDouble - origin);
            labelEntities = entities;
        }
        private sealed partial class UnityUiSurface
        {
            internal void SetClock(Func<double> value) { callbacks.Clock = value; }
        }
        private static void RenderProgress(UiProgress progress, TopiaForgeContainer parent)
        {
            var column = parent.Column(TopiaForgeGap.Xs, TopiaForgeGap.None);
            if (progress.Label.Length > 0) column.Label(progress.Label).PlainText();
            column.ProgressBar().Tone(ToNativeTone(progress.Tone)).SetFraction(progress.Fraction);
        }
        private static void RenderCountdown(UiCountdown countdown, TopiaForgeContainer parent, Func<double> clock)
        {
            var column = parent.Column(TopiaForgeGap.None, TopiaForgeGap.None).Dynamic();
            var label = column.Label(string.Empty).PlainText().Tone(ToNativeTone(countdown.Tone));
            var driver = column.Go.AddComponent<UiCountdownDriver>();
            driver.Initialize(countdown, clock, label);
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
            Update();
        }
        private void Update()
        {
            if (node == null || clock == null || label == null) return;
            var remaining = node.RemainingSeconds(clock());
            if (seconds == remaining) return;
            seconds = remaining; label.SetText(prefix, remaining);
        }
        private void OnDestroy() { node = null; clock = null; label = null; }
    }
}
