using TMPro;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class TopiaForgeDropdown
    {
        internal static RectTransform? VisiblePopup(TMP_Dropdown dropdown) =>
            (dropdown as TopiaForgeTmpDropdown)?.VisiblePopup;

        private sealed class TopiaForgeTmpDropdown : TMP_Dropdown, ITopiaForgeDismissable
        {
            private GameObject? list;
            private GameObject? blocker;
            private RectTransform? viewport;
            private Rect lastViewport;
            private Vector2 preferredPosition;
            private Vector2 preferredSize;
            private TopiaForgeLayerBand band;
            private float itemRadius;
            private bool highContrast;

            internal RectTransform? VisiblePopup =>
                list != null && blocker != null && list.activeInHierarchy && blocker.activeInHierarchy
                    ? (RectTransform)list.transform : null;

            TopiaForgeLayerBand ITopiaForgeDismissable.Band => band;
            void ITopiaForgeDismissable.Dismiss() => Hide();

            internal void ConfigureItemCues(float radius, bool contrast)
            {
                itemRadius = radius;
                highContrast = contrast;
            }

            protected override DropdownItem CreateItem(DropdownItem itemTemplate)
            {
                var item = base.CreateItem(itemTemplate);
                var fill = item.toggle.targetGraphic;
                var cues = TopiaForgeControlCues.Install(item.toggle, fill.rectTransform);
                cues.Apply(fill.color, item.text.color, itemRadius, highContrast);
                return item;
            }

            protected override GameObject CreateDropdownList(GameObject template)
            {
                list = base.CreateDropdownList(template);
                return list;
            }

            protected override GameObject CreateBlocker(Canvas rootCanvas)
            {
                var ownerCanvas = GetComponentInParent<Canvas>(includeInactive: true)?.rootCanvas ?? rootCanvas;
                var outside = base.CreateBlocker(ownerCanvas);
                blocker = outside;
                outside.GetComponent<Canvas>().overrideSorting = false;
                outside.transform.SetAsLastSibling();
                if (list == null) return outside;

                // TMP positions the list before creating the blocker. Preserve that position while moving
                // both popup layers above the window's content and outside its scroll masks.
                var popup = (RectTransform)list.transform;
                var owner = (RectTransform)ownerCanvas.transform;
                viewport = owner;
                preferredPosition = (Vector2)owner.InverseTransformPoint(popup.position);
                preferredSize = popup.rect.size;
                popup.SetParent(owner, worldPositionStays: false);
                popup.anchorMin = popup.anchorMax = owner.pivot;
                FitPopup(popup, owner.rect);
                popup.SetAsLastSibling();
                list.GetComponent<Canvas>().overrideSorting = false;
                band = ownerCanvas.GetComponent<TopiaForgeLayerSlot>()?.Band ?? TopiaForgeLayerBand.Window;
                TopiaForgeDismissStack.Push(this, TopiaForgeInputClaim.Navigation, list);
                return outside;
            }

            private void LateUpdate()
            {
                if (viewport == null || list == null || blocker == null) return;
                var bounds = viewport.rect;
                if (bounds != lastViewport) FitPopup((RectTransform)list.transform, bounds);
            }

            private void FitPopup(RectTransform popup, Rect bounds)
            {
                lastViewport = bounds;
                var size = Vector2.Min(preferredSize, bounds.size);
                popup.sizeDelta = size;
                var left = size.x * popup.pivot.x;
                var bottom = size.y * popup.pivot.y;
                popup.anchoredPosition = new Vector2(
                    Mathf.Clamp(preferredPosition.x, bounds.xMin + left, bounds.xMax - size.x + left),
                    Mathf.Clamp(preferredPosition.y, bounds.yMin + bottom, bounds.yMax - size.y + bottom));
            }

            protected override void DestroyBlocker(GameObject outside)
            {
                TopiaForgeDismissStack.Remove(this);
                blocker = null;
                viewport = null;
                base.DestroyBlocker(outside);
            }

            protected override void DestroyDropdownList(GameObject dropdownList)
            {
                list = null;
                base.DestroyDropdownList(dropdownList);
            }

            protected override void OnDisable()
            {
                TopiaForgeDismissStack.Remove(this);
                base.OnDisable();
            }
        }
    }
}
