using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Virtualized fixed-row-height list: pooled TopiaForgeListRow views are rebound to the
    /// visible range as the view scrolls, so a 1,000-item list costs a dozen rows. The
    /// visible-range math lives in the unit-tested Core (TopiaForgeVirtualListMath).
    /// </summary>
    public sealed class TopiaForgeListView<T> : TopiaForgeWidget
    {
        private const float Spacing = 4f;

        private readonly ScrollRect scrollRect;
        private readonly RectTransform content;
        private readonly TopiaForgeContainer rowParent;
        private readonly List<TopiaForgeListRow> pool = new List<TopiaForgeListRow>();
        private readonly List<int> poolBinding = new List<int>();
        private readonly float rowHeight;
        private Action<TopiaForgeListRow, T, int>? binder;
        private Action<int>? onSelected;
        private IReadOnlyList<T> items = Array.Empty<T>();
        private int selectedIndex = -1;
        private int lastFirst = -1;
        private int lastCount = -1;

        internal TopiaForgeListView(TopiaForgeContainer parent, float itemHeight)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("ListView"))
        {
            rowHeight = itemHeight;

            var viewportGo = new GameObject("Viewport", typeof(RectTransform));
            viewportGo.transform.SetParent(Go.transform, false);
            viewportGo.AddComponent<RectMask2D>();
            var viewportImage = viewportGo.AddComponent<UImage>();
            viewportImage.color = Color.clear;
            viewportImage.raycastTarget = true;
            var viewportRect = (RectTransform)viewportGo.transform;
            TopiaForgeAnchors.Stretch(viewportRect);

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewportGo.transform, false);
            content = (RectTransform)contentGo.transform;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;

            rowParent = new TopiaForgeContainer(Host, Scheme, contentGo);

            scrollRect = Go.AddComponent<ScrollRect>();
            scrollRect.viewport = viewportRect;
            scrollRect.content = content;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 28f;
            scrollRect.onValueChanged.AddListener(_ => Refresh(force: false));

            this.Flex(1f, 1f);
        }

        /// <summary>Gets selected index.</summary>
        public int SelectedIndex => selectedIndex;

        /// <summary>Gets selected item.</summary>
        public T? SelectedItem => selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : default;

        /// <summary>Binder invoked for every (row, item, index) that becomes visible.</summary>
        public TopiaForgeListView<T> Bind(Action<TopiaForgeListRow, T, int> bindRow)
        {
            binder = bindRow;
            return this;
        }

        /// <summary>Registers a callback that receives the selected item index.</summary>
        public TopiaForgeListView<T> OnSelected(Action<int> handler)
        {
            onSelected = handler;
            return this;
        }

        /// <summary>Rebinds the data set; selection is preserved by index when possible.</summary>
        public void SetItems(IReadOnlyList<T> next)
        {
            items = next ?? Array.Empty<T>();
            if (selectedIndex >= items.Count)
            {
                selectedIndex = -1;
            }

            content.sizeDelta = new Vector2(0f, TopiaForgeVirtualListMath.ContentHeight(items.Count, rowHeight, Spacing));
            Refresh(force: true);
        }

        /// <summary>Selects an index programmatically, scrolls it into view, and notifies.</summary>
        public void Select(int index)
        {
            SetSelection(index, notify: true);
        }

        /// <summary>Selects an index programmatically without notifying the consumer.</summary>
        public void SetSelectedIndex(int index)
        {
            SetSelection(index, notify: false);
        }

        /// <summary>Sets whether list rows accept pointer, keyboard, or controller input.</summary>
        public void SetEnabled(bool enabled)
        {
            var group = EnsureCanvasGroup();
            group.interactable = enabled;
            group.blocksRaycasts = enabled;
        }

        private void SetSelection(int index, bool notify)
        {
            if (index < -1 || index >= items.Count)
            {
                return;
            }

            selectedIndex = index;
            if (index >= 0)
            {
                var offset = ScrollOffset();
                var target = TopiaForgeVirtualListMath.ScrollToRow(index, offset, ViewportHeight(), items.Count, rowHeight, Spacing);
                SetScrollOffset(target);
            }

            Refresh(force: true);
            if (notify && index >= 0)
            {
                TopiaForgeCallbacks.Invoke(onSelected, index, "List selection");
            }
        }

        /// <summary>Keyboard navigation hook (Up/Down): moves selection by a delta.</summary>
        public void MoveSelection(int delta)
        {
            if (items.Count == 0)
            {
                return;
            }

            var next = Mathf.Clamp(selectedIndex < 0 ? 0 : selectedIndex + delta, 0, items.Count - 1);
            Select(next);
        }

        private void Refresh(bool force)
        {
            var viewport = ViewportHeight();
            if (viewport <= 0f)
            {
                viewport = 400f; // first layout pass hasn't run yet
            }

            var offset = ScrollOffset();
            var (first, count) = TopiaForgeVirtualListMath.VisibleRange(offset, viewport, items.Count, rowHeight, Spacing);
            if (!force && first == lastFirst && count == lastCount)
            {
                RepaintSelectionOnly();
                return;
            }

            lastFirst = first;
            lastCount = count;

            EnsurePool(TopiaForgeVirtualListMath.PoolSize(viewport, rowHeight, Spacing));
            for (var poolIndex = 0; poolIndex < pool.Count; poolIndex++)
            {
                var itemIndex = first + poolIndex;
                var row = pool[poolIndex];
                if (poolIndex >= count || itemIndex >= items.Count)
                {
                    row.SetVisible(false);
                    poolBinding[poolIndex] = -1;
                    continue;
                }

                row.SetVisible(true);
                row.Rect.anchoredPosition = new Vector2(0f, -TopiaForgeVirtualListMath.RowOffset(itemIndex, rowHeight, Spacing));
                poolBinding[poolIndex] = itemIndex;
                row.SetSelected(itemIndex == selectedIndex);
                TopiaForgeCallbacks.Invoke(binder, row, items[itemIndex], itemIndex, "List row binding");
            }
        }

        private void RepaintSelectionOnly()
        {
            for (var poolIndex = 0; poolIndex < pool.Count; poolIndex++)
            {
                var bound = poolBinding[poolIndex];
                if (bound >= 0)
                {
                    pool[poolIndex].SetSelected(bound == selectedIndex);
                }
            }
        }

        private void EnsurePool(int size)
        {
            while (pool.Count < size)
            {
                var poolIndex = pool.Count;
                var row = rowParent.ListRow();
                row.Free();
                row.Rect.anchorMin = new Vector2(0f, 1f);
                row.Rect.anchorMax = new Vector2(1f, 1f);
                row.Rect.pivot = new Vector2(0.5f, 1f);
                row.Rect.sizeDelta = new Vector2(0f, rowHeight);
                row.OnClick(() =>
                {
                    var bound = poolBinding[poolIndex];
                    if (bound >= 0)
                    {
                        selectedIndex = bound;
                        RepaintSelectionOnly();
                        TopiaForgeCallbacks.Invoke(onSelected, bound, "List selection");
                    }
                });
                pool.Add(row);
                poolBinding.Add(-1);
            }
        }

        private float ViewportHeight()
        {
            return scrollRect.viewport != null ? scrollRect.viewport.rect.height : 0f;
        }

        private float ScrollOffset()
        {
            return content.anchoredPosition.y;
        }

        private void SetScrollOffset(float offset)
        {
            var position = content.anchoredPosition;
            position.y = offset;
            content.anchoredPosition = position;
        }
    }
}
