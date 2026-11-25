using System;
using TopiaForge.Mods;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Observes native selection separately from press, including keyboard and controller submit.</summary>
    internal sealed class TopiaForgeCueButton : Button
    {
        internal const string SelectionName = "Selection bar";
        internal const string PressName = "Press outline";

        private Action<UiButtonCue>? show;
        private UiButtonCue available;
        private bool pressed;

        internal UiButtonCue ActiveCues { get; private set; }
        internal bool IsSelected { get; private set; }

        internal void ObserveCues(Action<UiButtonCue> observer) => show = observer;

        internal void ConfigureCues(UiButtonCue cues)
        {
            available = cues;
            PublishCues();
        }

        public override void OnSelect(BaseEventData eventData)
        {
            IsSelected = true;
            base.OnSelect(eventData);
            PublishCues();
        }

        public override void OnDeselect(BaseEventData eventData)
        {
            IsSelected = false;
            base.OnDeselect(eventData);
            PublishCues();
        }

        protected override void DoStateTransition(SelectionState state, bool instant)
        {
            base.DoStateTransition(state, instant);
            pressed = state == SelectionState.Pressed;
            PublishCues();
        }

        protected override void OnDisable()
        {
            IsSelected = false;
            pressed = false;
            PublishCues();
            base.OnDisable();
        }

        private void PublishCues()
        {
            var active = IsInteractable() ? available &
                ((IsSelected ? UiButtonCue.Selection : UiButtonCue.None) | (pressed ? UiButtonCue.Press : UiButtonCue.None))
                : UiButtonCue.None;
            if (active == ActiveCues) return;
            ActiveCues = active;
            show?.Invoke(active);
        }
    }
}
