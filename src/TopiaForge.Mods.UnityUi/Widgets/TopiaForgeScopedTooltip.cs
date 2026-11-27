using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    internal static class TopiaForgeScopedTooltip
    {
        internal static void Install(TopiaForgeWidget wrapper, string message, bool enabled, UiPaintRole backdrop,
            TopiaForgeLayerBand band = TopiaForgeLayerBand.Window)
        {
            if (!enabled) return;
            var canvas = wrapper.Go.GetComponentInParent<Canvas>(includeInactive: true)?.rootCanvas;
            if (canvas == null) return;

            var layer = TopiaForgeComponents.GetOrAdd<TopiaForgeTooltipLayer>(canvas.gameObject);
            layer.Initialize(canvas);
            var trigger = wrapper.Go.AddComponent<TopiaForgeScopedTooltipTrigger>();
            trigger.Initialize(wrapper, layer, message, backdrop, band);

            // The wrapper catches the empty area around its child. Child hit targets keep their own events.
            var graphic = wrapper.Go.GetComponent<Graphic>();
            if (graphic == null)
            {
                graphic = wrapper.Go.AddComponent<Image>();
                graphic.color = Color.clear;
            }
            graphic.raycastTarget = true;

            var hasFocusTarget = false;
            foreach (var selectable in wrapper.Go.GetComponentsInChildren<Selectable>(includeInactive: true))
            {
                if (!selectable.enabled || !selectable.IsInteractable() || selectable.navigation.mode == Navigation.Mode.None)
                    continue;
                AddFocusEvents(selectable, trigger);
                hasFocusTarget = true;
            }
            if (hasFocusTarget) return;

            var layout = wrapper.EnsureLayoutElement();
            layout.minWidth = Mathf.Max(layout.minWidth, TopiaForgeTokens.ControlHeight);
            layout.minHeight = Mathf.Max(layout.minHeight, TopiaForgeTokens.ControlHeight);
            var anchor = wrapper.Go.AddComponent<Selectable>();
            anchor.targetGraphic = graphic;
            anchor.transition = Selectable.Transition.None;
            AddFocusEvents(anchor, trigger);
            TopiaForgeControlFocus.Install(wrapper, anchor, backdrop);
        }

        private static void AddFocusEvents(Selectable target, TopiaForgeScopedTooltipTrigger trigger) =>
            target.gameObject.AddComponent<TopiaForgeScopedTooltipFocus>().Initialize(target, trigger);

        internal static RectTransform? VisiblePopup(GameObject wrapper) =>
            wrapper.GetComponent<TopiaForgeScopedTooltipTrigger>()?.VisiblePopup;
    }

    // Unity filters events sent to disabled behaviours. Only the separate clock driver sleeps.
    internal sealed class TopiaForgeScopedTooltipTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private TopiaForgeTooltipLayer? layer;

        internal TopiaForgeWidget Widget { get; private set; } = null!;
        internal string Message { get; private set; } = string.Empty;
        internal UiPaintRole Backdrop { get; private set; }
        internal TopiaForgeLayerBand Band { get; private set; }
        internal RectTransform? VisiblePopup => layer != null ? layer.VisiblePopupFor(this) : null;

        internal void Initialize(TopiaForgeWidget widget, TopiaForgeTooltipLayer popupLayer, string message,
            UiPaintRole backdrop, TopiaForgeLayerBand band)
        {
            Widget = widget;
            layer = popupLayer;
            Message = message;
            Backdrop = backdrop;
            Band = band;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (layer != null) layer.BeginHover(this);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (layer != null) layer.EndHover(this);
        }

        internal void Select(Selectable target)
        {
            if (layer != null) layer.BeginFocus(this, target);
        }

        internal void Deselect(Selectable target)
        {
            if (layer != null) layer.EndFocus(this, target);
        }

        private void OnDisable()
        {
            if (layer != null) layer.Release(this);
        }

        private void OnDestroy()
        {
            OnDisable();
            layer = null;
        }
    }

    internal sealed class TopiaForgeScopedTooltipFocus : MonoBehaviour, ISelectHandler, IDeselectHandler
    {
        private Selectable? target;
        private TopiaForgeScopedTooltipTrigger? trigger;

        internal void Initialize(Selectable selectable, TopiaForgeScopedTooltipTrigger owner)
        {
            target = selectable;
            trigger = owner;
        }

        public void OnSelect(BaseEventData eventData)
        {
            if (eventData is not PointerEventData && target != null && trigger != null && target.IsInteractable())
                trigger.Select(target);
        }

        public void OnDeselect(BaseEventData eventData) => Release();

        private void OnDisable() => Release();

        private void Release()
        {
            if (target != null && trigger != null) trigger.Deselect(target);
        }
    }
}
