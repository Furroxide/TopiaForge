using System;
using System.Runtime.CompilerServices;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService : IUiClockReceiver
    {
        private Func<double> widgetClock = () => 0;
        internal Func<string> WidgetEpoch = () => "fake";
        private static readonly ConditionalWeakTable<IGameTime, ClockIdentity> clockIdentities = new ConditionalWeakTable<IGameTime, ClockIdentity>();
        private sealed class ClockIdentity { internal readonly string Epoch = Guid.NewGuid().ToString("N"); }
        /// <summary>Creates a UI scope on a shared unscaled test clock. Delayed scopes retain the same origin.</summary>
        [RequiresTopiaForge("0.2.0")]
        public FakeUiService(FakeModLifetime lifetime, IGameTime clock) : this(lifetime) { FollowTime(clock); }
        private void FollowTime(IGameTime clock)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            var identity = clockIdentities.GetValue(clock, _ => new ClockIdentity());
            widgetClock = () => clock.Frame.ElapsedTime;
            WidgetEpoch = () => identity.Epoch;
        }
        void IUiClockReceiver.FollowClock(Func<double> clock, Func<string> epoch) { widgetClock = clock; WidgetEpoch = epoch; }
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
            FollowTime(time);
            widgetEvents = events;
        }
    }
    public sealed partial class FakeUiSurface
    {
        private Func<double> widgetClock = () => 0;
        private Func<string> widgetEpoch = () => "fake";
        internal bool WireBounded;
        internal void SetClock(Func<double> value, Func<string> epoch) { widgetClock = value; widgetEpoch = epoch; }
        /// <summary>Reads the current countdown without an author update callback or a replacement composition.</summary>
        [RequiresTopiaForge("0.2.0")]
        public bool TryGetCountdownSeconds(string id, out int seconds)
        {
            seconds = 0;
            if (!TryFindNode(id, out var node) || !(node is UiCountdown countdown)) return false;
            seconds = countdown.ClockEpoch == widgetEpoch() ? countdown.RemainingSeconds(widgetClock()) : 0; return true;
        }
    }
}
