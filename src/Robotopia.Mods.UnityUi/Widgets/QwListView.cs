using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Virtualized fixed-row-height list: pooled QwListRow views are rebound to the
    /// visible range as the view scrolls, so a 1,000-item list costs a dozen rows. The
    /// visible-range math lives in the unit-tested Core (QwVirtualListMath).
    /// </summary>
    public sealed class QwListView<T> : QwWidget
    {
        private const float Spacing = 4f;

        private readonly ScrollRect scrollRect;
        private readonly RectTransform content;
        private readonly QwContainer rowParent;
        private readonly List<QwListRow> pool = new List<QwListRow>();
        private readonly List<int> poolBinding = new List<int>();
        private readonly float rowHeight;
        private Action<QwListRow, T, int>? binder;
        private Action<int>? onSelected;
        private IReadOnlyList<T> items = Array.Empty<T>();
        private int selectedIndex = -1;
        private int lastFirst = -1;
        private int lastCount = -1;

        internal QwListView(QwContainer parent, float itemHeight)
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
            QwAnchors.Stretch(viewportRect);

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewportGo.transform, false);
            content = (RectTransform)contentGo.transform;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;

            rowParent = new QwContainer(Host, Scheme, contentGo);

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

        public int SelectedIndex => selectedIndex;

        public T? SelectedItem => selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : default;

        /// <summary>Binder invoked for every (row, item, index) that becomes visible.</summary>
        public QwListView<T> Bind(Action<QwListRow, T, int> bindRow)
        {
            binder = bindRow;
            return this;
        }

        public QwListView<T> OnSelected(Action<int> handler)
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

            content.sizeDelta = new Vector2(0f, QwVirtualListMath.ContentHeight(items.Count, rowHeight, Spacing));
            Refresh(force: true);
        }

        /// <summary>Selects an index programmatically, scrolls it into view, and notifies.</summary>
        public void Select(int index)
        {
            if (index < -1 || index >= items.Count)
            {
                return;
            }

            selectedIndex = index;
            if (index >= 0)
            {
                var offset = ScrollOffset();
                var target = QwVirtualListMath.ScrollToRow(index, offset, ViewportHeight(), items.Count, rowHeight, Spacing);
                SetScrollOffset(target);
            }

            Refresh(force: true);
            if (index >= 0)
            {
                onSelected?.Invoke(index);
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
            var (first, count) = QwVirtualListMath.VisibleRange(offset, viewport, items.Count, rowHeight, Spacing);
            if (!force && first == lastFirst && count == lastCount)
            {
                RepaintSelectionOnly();
                return;
            }

            lastFirst = first;
            lastCount = count;

            EnsurePool(QwVirtualListMath.PoolSize(viewport, rowHeight, Spacing));
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
                row.Rect.anchoredPosition = new Vector2(0f, -QwVirtualListMath.RowOffset(itemIndex, rowHeight, Spacing));
                poolBinding[poolIndex] = itemIndex;
                row.SetSelected(itemIndex == selectedIndex);
                binder?.Invoke(row, items[itemIndex], itemIndex);
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
                        onSelected?.Invoke(bound);
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
