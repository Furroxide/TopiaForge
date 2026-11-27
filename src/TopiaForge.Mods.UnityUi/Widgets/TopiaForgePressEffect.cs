using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    // The stationary raycast area stays on the button root as these paint layers move.
    internal sealed class TopiaForgePressEffect : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
        IPointerDownHandler, IPointerUpHandler
    {
        private UiHost? host;
        private Button? button;
        private RectTransform? body;
        private Image? shadow;
        private Vector2 restOffset;
        private Color shadowInk;
        private bool hovered;
        private bool pressed;

        internal void Initialize(UiHost owner, Button selectable, RectTransform bodyRect, Image shadowImage)
        {
            host = owner;
            button = selectable;
            body = bodyRect;
            shadow = shadowImage;
        }

        internal void SetShadow(Vector2 offset, Color color)
        {
            restOffset = offset;
            shadowInk = color;
            if (button != null && !button.IsInteractable()) pressed = false;
            Paint();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            hovered = true;
            Paint();
        }

        public void OnPointerExit(PointerEventData eventData) => ResetPointer();

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || button == null || !button.IsInteractable()) return;
            pressed = true;
            Paint();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            pressed = false;
            Paint();
        }

        private void OnDisable() => ResetPointer();

        private void ResetPointer()
        {
            hovered = false;
            pressed = false;
            Paint();
        }

        private void Paint()
        {
            if (host == null || body == null || shadow == null || button == null) return;
            var hasShadow = shadowInk.a > 0f && button.IsInteractable();
            var strength = hasShadow && !host.EffectiveReducedMotion ? Mathf.Clamp01(host.EffectiveMotion) : 0f;
            var lift = hovered && !pressed ? strength : 0f;
            body.anchoredPosition = pressed ? restOffset * strength : new Vector2(lift, 2f * lift);
            var growth = new Vector2(lift, lift);
            shadow.rectTransform.offsetMin = restOffset - growth;
            shadow.rectTransform.offsetMax = restOffset + growth;
            shadow.enabled = hasShadow && !(pressed && strength > 0f);
            shadow.color = shadowInk;
        }
    }
}
