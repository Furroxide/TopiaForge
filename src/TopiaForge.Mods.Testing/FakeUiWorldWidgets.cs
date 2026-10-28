using System;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService
    {
        private Func<double> widgetClock = () => 0;
        private FakeUiWorldLabels? labels;
        private FakeModEvents? widgetEvents;
        /// <summary>Gets the inspectable local world-label pool.</summary>
        [RequiresTopiaForge("0.2.0")]
        public FakeUiWorldLabels Labels
        {
            get
            {
                if (labels != null) return labels;
                labels = new FakeUiWorldLabels(lifetime, () => scenes?.IsGameplay ?? true, () => scenes?.ActiveInstanceId ?? 0);
                if (!lifetime.IsStopping)
                {
                    lifetime.Track(labels);
                    if (widgetEvents != null)
                    {
                        widgetEvents.SubscribeLateUpdate(_ => labels.Prune());
                        widgetEvents.SubscribeSceneLifecycle(scene =>
                        { if (scene.Phase == SceneLifecyclePhase.Unloaded) labels.Unload(scene.SceneInstanceId); });
                    }
                }
                return labels;
            }
        }
        IUiWorldLabels IUiService.Labels => Labels;
        /// <inheritdoc/>
        [RequiresTopiaForge("0.2.0")]
        public double ClockTime => widgetClock();
        internal void FollowWidgets(IGameTime time, FakeModEvents events)
        {
            var start = time.Frame.ElapsedTime;
            widgetClock = () => Math.Max(0, time.Frame.ElapsedTime - start);
            widgetEvents = events;
        }
    }
    public sealed partial class FakeUiSurface
    {
        private Func<double> widgetClock = () => 0;
        internal void SetClock(Func<double> value) { widgetClock = value; }
        /// <summary>Reads the current countdown without an author update callback or a replacement composition.</summary>
        [RequiresTopiaForge("0.2.0")]
        public bool TryGetCountdownSeconds(string id, out int seconds)
        {
            seconds = 0;
            if (!TryFindNode(id, out var node) || !(node is UiCountdown countdown)) return false;
            seconds = countdown.RemainingSeconds(widgetClock()); return true;
        }
    }
}
