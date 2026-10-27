using TMPro;
using UnityEngine.EventSystems;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// The input behind a kit text field: TextMesh Pro's own, except that it starts taking the keyboard only once every key
    /// held as it was asked to start is released (TopiaForgeTypingStart). TextMesh Pro reads typing from the game window's
    /// own key messages, which the kit's Input System filter never sees, so a key held as navigation reached the field
    /// typed into it, auto-repeat and all. Now that key keeps doing what it did: a held navigation key moves on past the
    /// field, and the field starts with the next fresh press. Main thread only.
    /// </summary>
    internal sealed class TopiaForgeTextInput : TMP_InputField
    {
        private readonly TopiaForgeTypingStart start = new TopiaForgeTypingStart();

        /// <summary>Requests focus even when already selected, without bypassing the held-key wait.</summary>
        internal void FocusWhenReleased()
        {
            Select();
            if (start.Ask(TopiaForgeInputOwnership.AnyKeyHeld))
            {
                ActivateInputField();
            }
        }

        /// <summary>Gets whether selecting the field starts it now: never while a start waits for held keys.</summary>
        public override bool shouldActivateOnSelect
        {
            get => base.shouldActivateOnSelect && !start.Waiting;
            set => base.shouldActivateOnSelect = value;
        }

        /// <summary>Selects the field, which starts at once only with no key held.</summary>
        public override void OnSelect(BaseEventData eventData)
        {
            start.Ask(TopiaForgeInputOwnership.AnyKeyHeld);
            base.OnSelect(eventData);
        }

        /// <summary>A left click starts the field, at once only with no key held.</summary>
        public override void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || start.Ask(TopiaForgeInputOwnership.AnyKeyHeld))
            {
                base.OnPointerClick(eventData);
            }
        }

        /// <summary>A submit starts the field while it does not have the keyboard; that start waits for held keys too.</summary>
        public override void OnSubmit(BaseEventData eventData)
        {
            if (!isFocused)
            {
                start.Submitted(TopiaForgeInputOwnership.AnyKeyHeld);
            }

            base.OnSubmit(eventData);
        }

        /// <summary>Runs a start that waited once the keys are released, before the field's own late update.</summary>
        protected override void LateUpdate()
        {
            var events = EventSystem.current;
            var selected = events != null && ReferenceEquals(events.currentSelectedGameObject, gameObject);
            var step = start.Tick(TopiaForgeInputOwnership.AnyKeyHeld, selected, isFocused);
            if (step == TopiaForgeTypingStep.Wait)
            {
                return;
            }

            if (step == TopiaForgeTypingStep.Start)
            {
                ActivateInputField();
            }

            base.LateUpdate();
        }

        /// <summary>Forgets a start that waits, as the field is hidden or destroyed.</summary>
        protected override void OnDisable()
        {
            start.Reset();
            base.OnDisable();
        }
    }
}
