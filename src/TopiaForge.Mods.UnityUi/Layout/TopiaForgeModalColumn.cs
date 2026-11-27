using System;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    // Fixed-height scroll bodies reserve room for the modal's other children before the dialog fits to its content.
    internal sealed class TopiaForgeModalColumn : VerticalLayoutGroup
    {
        private RectTransform? viewport;
        private TopiaForgeModalScrollLimit? body;
        private float measuredViewportHeight;
        private float motionReserve;

        internal void LimitScrollBody(RectTransform canvas, float motionReserve = 0f)
        {
            RectTransform? scroll = null;
            LayoutElement? requested = null;
            for (var index = 0; index < transform.childCount; index++)
            {
                if (!(transform.GetChild(index) is RectTransform child) || !child.gameObject.activeSelf ||
                    child.GetComponent<ScrollRect>() == null) continue;
                var layout = child.GetComponent<LayoutElement>();
                if (layout == null || layout.ignoreLayout || layout.preferredHeight <= 0f ||
                    layout.minHeight != layout.preferredHeight) continue;
                if (scroll != null) return;
                scroll = child;
                requested = layout;
            }

            if (scroll == null || requested == null) return;
            viewport = canvas;
            this.motionReserve = motionReserve;
            body = scroll.gameObject.AddComponent<TopiaForgeModalScrollLimit>();
            body.Initialize(scroll, requested);
        }

        public override void CalculateLayoutInputVertical()
        {
            if (body != null && viewport != null)
            {
                // Unity has measured the other children before calling the parent layout group's vertical pass.
                var fixedHeight = padding.vertical + spacing * Math.Max(0, rectChildren.Count - 1);
                for (var index = 0; index < rectChildren.Count; index++)
                {
                    var child = rectChildren[index];
                    if (child != body.Rect) fixedHeight += LayoutUtility.GetPreferredHeight(child);
                }

                measuredViewportHeight = viewport.rect.height;
                body.Limit = TopiaForgeModalSize.LimitScrollBody(body.RequestedHeight, measuredViewportHeight, fixedHeight, motionReserve);
            }

            base.CalculateLayoutInputVertical();
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            if (viewport != null && viewport.rect.height != measuredViewportHeight) SetDirty();
        }
    }

    // This higher-priority height leaves the original layout element available to the caller's sizing chainers.
    internal sealed class TopiaForgeModalScrollLimit : MonoBehaviour, ILayoutElement
    {
        private LayoutElement requested = null!;
        internal RectTransform Rect { get; private set; } = null!;
        internal float RequestedHeight => requested.preferredHeight;
        internal float Limit { get; set; } = float.PositiveInfinity;

        internal void Initialize(RectTransform rect, LayoutElement layout)
        {
            Rect = rect;
            requested = layout;
        }

        public float minWidth => -1f;
        public float preferredWidth => -1f;
        public float flexibleWidth => -1f;
        public float minHeight => Math.Min(requested.minHeight, Limit);
        public float preferredHeight => Math.Min(requested.preferredHeight, Limit);
        public float flexibleHeight => -1f;
        public int layoutPriority => requested.layoutPriority + 1;
        public void CalculateLayoutInputHorizontal() { }
        public void CalculateLayoutInputVertical() { }
    }
}
