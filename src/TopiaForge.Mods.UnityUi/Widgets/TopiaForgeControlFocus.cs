using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Draws a separate focus ring for a declarative control's native selectable.</summary>
    internal sealed class TopiaForgeControlFocus : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private const float Gap = 2f;
        private readonly TopiaForgeRoundedImage image;

        private TopiaForgeControlFocus(TopiaForgeWidget widget, Selectable control)
            : base(widget.Host, widget.Scheme, CreateRing(control.transform))
        {
            image = Go.AddComponent<TopiaForgeRoundedImage>();
            image.raycastTarget = false;
            image.type = Image.Type.Sliced;
            Go.AddComponent<LayoutElement>().ignoreLayout = true;
            var driver = control.gameObject.AddComponent<TopiaForgeControlFocusEvents>();
            driver.Initialize(control, Go);
            ApplyTheme(Theme);
        }

        internal static void Install(TopiaForgeWidget widget)
        {
            foreach (var control in widget.Go.GetComponentsInChildren<Selectable>(includeInactive: true))
            {
                if (control.GetComponent<TopiaForgeControlFocusEvents>() == null)
                    _ = new TopiaForgeControlFocus(widget, control);
            }
        }

        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            var stroke = theme.HighContrast ? 3f : 2f;
            var outset = Gap + stroke;
            TopiaForgeAnchors.Stretch(Rect, -outset, -outset, -outset, -outset);
            image.sprite = TopiaForgeSprites.Ring(TopiaForgeRadius.Control, stroke);
            image.SetShape(theme.Descriptor?.Shape.Control + outset, stroke);
            image.color = theme.FocusRing;
        }

        private static GameObject CreateRing(Transform parent)
        {
            var go = new GameObject("Focus ring", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.SetActive(false);
            return go;
        }
    }

    internal sealed class TopiaForgeControlFocusEvents : MonoBehaviour, ISelectHandler, IDeselectHandler
    {
        private Selectable? control;
        private GameObject? ring;

        internal void Initialize(Selectable selectable, GameObject focusRing)
        {
            control = selectable;
            ring = focusRing;
        }

        public void OnSelect(BaseEventData eventData)
        {
            if (ring != null && control != null && control.IsInteractable()) ring.SetActive(true);
        }

        public void OnDeselect(BaseEventData eventData) => Hide();

        private void OnDisable() => Hide();

        private void Hide()
        {
            if (ring != null) ring.SetActive(false);
        }
    }
}
