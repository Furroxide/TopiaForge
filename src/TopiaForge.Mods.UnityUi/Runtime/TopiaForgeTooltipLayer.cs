using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>One passive tooltip popup and one clock driver per owning canvas.</summary>
    internal sealed class TopiaForgeTooltipLayer : MonoBehaviour, ITopiaForgeDismissable
    {
        internal const double HoverDelay = 0.45d;

        private readonly Vector3[] corners = new Vector3[4];
        private Canvas? canvas;
        private RectTransform canvasRect = null!;
        private TopiaForgeScopedTooltipPanel? panel;
        private TopiaForgeScopedTooltipTrigger? trigger;
        private Selectable? focused;
        private double showAt;
        private bool pushed;

        TopiaForgeLayerBand ITopiaForgeDismissable.Band => trigger != null ? trigger.Band : TopiaForgeLayerBand.Window;
        void ITopiaForgeDismissable.Dismiss() => Stop();

        internal void Initialize(Canvas owner)
        {
            if (canvas != null) return;
            canvas = owner;
            canvasRect = (RectTransform)owner.transform;
            enabled = false;
        }

        internal void BeginHover(TopiaForgeScopedTooltipTrigger source)
        {
            if (trigger == source) return;
            Arm(source, null, Time.unscaledTimeAsDouble + HoverDelay);
        }

        internal void EndHover(TopiaForgeScopedTooltipTrigger source)
        {
            if (trigger == source && focused == null) Stop();
        }

        internal void BeginFocus(TopiaForgeScopedTooltipTrigger source, Selectable target)
        {
            if (trigger == source && focused == target) return;
            Arm(source, target, 0d);
            // Selection is already committed while Unity dispatches OnSelect.
            LateUpdate();
        }

        internal void EndFocus(TopiaForgeScopedTooltipTrigger source, Selectable target)
        {
            if (trigger == source && focused == target) Stop();
        }

        internal void Release(TopiaForgeScopedTooltipTrigger source)
        {
            if (trigger == source) Stop();
        }

        internal RectTransform? VisiblePopupFor(TopiaForgeScopedTooltipTrigger source) =>
            trigger == source && panel is { } view && view.Go != null && view.Go.activeInHierarchy ? view.Rect : null;

        private void Arm(TopiaForgeScopedTooltipTrigger source, Selectable? target, double time)
        {
            Hide();
            trigger = source;
            focused = target;
            showAt = time;
            enabled = true;
        }

        private void LateUpdate()
        {
            if (trigger == null || !trigger.isActiveAndEnabled || canvas == null || !canvas.isActiveAndEnabled)
            {
                Stop();
                return;
            }
            if (!ReferenceEquals(focused, null) && (focused == null || !focused.isActiveAndEnabled || !focused.IsInteractable() ||
                EventSystem.current == null || EventSystem.current.currentSelectedGameObject != focused.gameObject))
            {
                Stop();
                return;
            }
            if (Time.unscaledTimeAsDouble < showAt) return;

            var anchor = focused != null ? (RectTransform)focused.transform : trigger.Widget.Rect;
            var screen = canvasRect.rect;
            if (!TryVisibleBounds(anchor, screen, out var visible))
            {
                // A keyboard selection can be revealed by its enclosing scroll view later in the frame.
                Hide();
                return;
            }

            if (panel == null || panel.Go == null)
            {
                Hide();
                panel = new TopiaForgeScopedTooltipPanel(canvasRect);
            }
            if (!panel.Show(trigger, screen, visible))
            {
                Hide();
                return;
            }
            if (!pushed)
            {
                TopiaForgeDismissStack.Push(this, TopiaForgeInputClaim.Back, panel.Go);
                pushed = true;
            }
        }

        private bool TryVisibleBounds(RectTransform anchor, Rect screen, out Rect bounds)
        {
            bounds = Bounds(anchor);
            var clip = screen;
            for (var current = (Transform)anchor; current != null && current != canvasRect; current = current.parent)
            {
                if (current.TryGetComponent<CanvasGroup>(out var group) && group.enabled && group.alpha <= 0f) return false;
                if (current.TryGetComponent<RectMask2D>(out var mask) && mask.isActiveAndEnabled)
                {
                    var padding = mask.padding;
                    var maskBounds = Bounds((RectTransform)current);
                    var low = canvasRect.InverseTransformPoint(current.TransformPoint(new Vector3(padding.x, padding.y, 0f)));
                    var high = canvasRect.InverseTransformPoint(current.TransformPoint(new Vector3(padding.z, padding.w, 0f)));
                    var origin = canvasRect.InverseTransformPoint(current.position);
                    clip = Intersect(clip, Rect.MinMaxRect(maskBounds.xMin + low.x - origin.x,
                        maskBounds.yMin + low.y - origin.y, maskBounds.xMax - high.x + origin.x, maskBounds.yMax - high.y + origin.y));
                }
            }
            bounds = Intersect(bounds, clip);
            return bounds.width > 0f && bounds.height > 0f;
        }

        private Rect Bounds(RectTransform rect)
        {
            rect.GetWorldCorners(corners);
            var first = canvasRect.InverseTransformPoint(corners[0]);
            var minX = first.x;
            var minY = first.y;
            var maxX = first.x;
            var maxY = first.y;
            for (var index = 1; index < corners.Length; index++)
            {
                var point = canvasRect.InverseTransformPoint(corners[index]);
                minX = Mathf.Min(minX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxX = Mathf.Max(maxX, point.x);
                maxY = Mathf.Max(maxY, point.y);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private static Rect Intersect(Rect first, Rect second) => Rect.MinMaxRect(
            Mathf.Max(first.xMin, second.xMin), Mathf.Max(first.yMin, second.yMin),
            Mathf.Min(first.xMax, second.xMax), Mathf.Min(first.yMax, second.yMax));

        private void Stop()
        {
            Hide();
            trigger = null;
            focused = null;
            enabled = false;
        }

        private void Hide()
        {
            if (pushed)
            {
                TopiaForgeDismissStack.Remove(this);
                pushed = false;
            }
            panel?.Hide();
        }

        private void OnDisable() => Stop();
        private void OnDestroy()
        {
            Stop();
            if (panel is { } view && view.Go != null) Object.Destroy(view.Go);
            panel = null;
            canvas = null;
        }
    }
}
