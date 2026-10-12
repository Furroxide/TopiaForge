using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// A sortable table over the virtualized <see cref="TopiaForgeListView{T}"/>: column headers
    /// (fixed or flexible widths) above pooled rows whose cells are bound per column. Clicking or
    /// activating a sortable header sorts ascending (or the column's first direction) and again
    /// flips it; a chevron marks the sorted column and direction. Sorting is stable (rows that
    /// compare equal keep their supplied order) and never reorders the caller's list; the order is
    /// a unit-tested index permutation (<see cref="TopiaForgeStableSorter"/>). Selection reports the
    /// caller's index and follows its row through re-sorts; with <see cref="Keyed"/> it also
    /// follows the row through new data. Rows are the list view's buttons, so focus, keyboard and
    /// controller navigation, and <see cref="MoveSelection"/> behave exactly as in a list view.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    public sealed partial class TopiaForgeTableView<T> : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        /// <summary>The most columns a table may have.</summary>
        public const int MaxColumns = 32;

        private readonly TopiaForgeTableColumn<T>[] columns;
        private readonly TopiaForgeListView<T> list;
        private readonly SortedRows rows;
        private readonly TopiaForgeStableSorter sorter = new TopiaForgeStableSorter();
        private readonly TopiaForgeTableSortState sort = new TopiaForgeTableSortState();
        private readonly Dictionary<TopiaForgeListRow, object[]> rowCells = new Dictionary<TopiaForgeListRow, object[]>();
        private readonly TopiaForgeLabel emptyLabel;
        private IReadOnlyList<T> items = Array.Empty<T>();
        private Func<T, string>? keySelector;
        private string? selectedKey;
        private int selectedSource = -1;
        private Action<int>? onSelected;
        private bool sortFailureWarned;

        internal TopiaForgeTableView(TopiaForgeContainer parent, IReadOnlyList<TopiaForgeTableColumn<T>> definitions, float rowHeight)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("TableView"))
        {
            if (definitions == null)
            {
                throw new ArgumentNullException(nameof(definitions));
            }

            if (definitions.Count < 1 || definitions.Count > MaxColumns)
            {
                throw new ArgumentOutOfRangeException(nameof(definitions), "A table needs between 1 and " + MaxColumns + " columns.");
            }

            columns = new TopiaForgeTableColumn<T>[definitions.Count];
            for (var index = 0; index < columns.Length; index++)
            {
                columns[index] = definitions[index] ?? throw new ArgumentException("Table columns cannot be null.", nameof(definitions));
            }

            TopiaForgeLayout.ApplyColumn(Go, TopiaForgeGap.Xs, TopiaForgeGap.None);
            var body = new TopiaForgeContainer(Host, Scheme, Go);
            BuildHeader(body);
            list = body.ListView<T>(rowHeight);
            list.RowCreated = AttachCells;
            list.Bind(BindRow);
            list.OnSelected(OnListSelected);
            rows = new SortedRows(this);

            emptyLabel = body.Label("Nothing to show.", TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Muted).AlignCenter().Free();
            TopiaForgeAnchors.Stretch(emptyLabel.Rect, 0f, HeaderHeight + 12f, 0f, 0f);
            this.Flex(1f, 1f);
            ApplyTheme(Theme);
            Rebuild();
        }

        /// <summary>Gets the number of columns.</summary>
        public int ColumnCount => columns.Length;

        /// <summary>Gets the number of rows.</summary>
        public int Count => items.Count;

        /// <summary>Gets the sorted column, or -1.</summary>
        public int SortColumn => sort.Column;

        /// <summary>Gets the sort direction.</summary>
        public TopiaForgeSortDirection SortDirection => sort.Direction;

        /// <summary>Gets the selected row as an index into the supplied items, or -1.</summary>
        public int SelectedIndex => selectedSource;

        /// <summary>Gets the selected row's item.</summary>
        public T? SelectedItem => selectedSource >= 0 && selectedSource < items.Count ? items[selectedSource] : default;

        /// <summary>Registers a callback receiving the selected row's index into the supplied items.</summary>
        public TopiaForgeTableView<T> OnSelected(Action<int> handler)
        {
            onSelected = handler;
            return this;
        }

        /// <summary>Keeps the selection on the row with the same key when new data arrives (player ids).</summary>
        public TopiaForgeTableView<T> Keyed(Func<T, string> key)
        {
            keySelector = key ?? throw new ArgumentNullException(nameof(key));
            selectedKey = selectedSource >= 0 ? key(items[selectedSource]) : null;
            return this;
        }

        /// <summary>Sets the text shown when the table has no rows.</summary>
        public TopiaForgeTableView<T> EmptyText(string text)
        {
            emptyLabel.SetText(text ?? string.Empty);
            return this;
        }

        /// <summary>Replaces the rows and re-applies the current sort.</summary>
        public void SetItems(IReadOnlyList<T> next)
        {
            items = next ?? Array.Empty<T>();
            if (keySelector != null && selectedKey != null)
            {
                selectedSource = IndexOfKey(selectedKey);
            }
            else if (selectedSource >= items.Count)
            {
                selectedSource = -1;
            }

            Rebuild();
        }

        /// <summary>Re-sorts and rebinds after the supplied rows changed in place.</summary>
        public void Refresh()
        {
            Rebuild();
        }

        /// <summary>What a header activation does: sorts a new column or flips the sorted one.</summary>
        public void ToggleSort(int column)
        {
            if (column < 0 || column >= columns.Length || !columns[column].IsSortable)
            {
                return;
            }

            sort.Toggle(column, columns[column].FirstDirection);
            Rebuild();
        }

        /// <summary>Sorts by a column in a direction; an unsortable column or <see cref="TopiaForgeSortDirection.None"/> clears.</summary>
        public void SortBy(int column, TopiaForgeSortDirection direction)
        {
            if (column < 0 || column >= columns.Length || !columns[column].IsSortable)
            {
                direction = TopiaForgeSortDirection.None;
            }

            sort.Set(column, direction);
            Rebuild();
        }

        /// <summary>Returns to the supplied row order.</summary>
        public void ClearSort()
        {
            sort.Clear();
            Rebuild();
        }

        /// <summary>Selects a row by its index into the supplied items, scrolls to it, and notifies.</summary>
        public void Select(int index)
        {
            var view = sorter.ViewIndexOf(index);
            if (view >= 0)
            {
                list.Select(view);
            }
        }

        /// <summary>Selects a row by its index into the supplied items without notifying; -1 clears.</summary>
        public void SetSelectedIndex(int index)
        {
            var view = sorter.ViewIndexOf(index);
            selectedSource = view >= 0 ? index : -1;
            selectedKey = selectedSource >= 0 && keySelector != null ? keySelector(items[selectedSource]) : null;
            list.SetSelectedIndex(view);
        }

        /// <summary>Keyboard hook (Up/Down): moves the selection through the rows as displayed.</summary>
        public void MoveSelection(int delta)
        {
            list.MoveSelection(delta);
        }

        /// <summary>Sets whether rows and headers accept input.</summary>
        public void SetEnabled(bool enabled)
        {
            list.SetEnabled(enabled);
            SetHeadersEnabled(enabled);
        }

        /// <summary>Gets where a supplied row is displayed, or -1.</summary>
        public int ViewIndexOf(int index)
        {
            return sorter.ViewIndexOf(index);
        }

        /// <summary>Gets the supplied index of the row displayed at a position.</summary>
        public int SourceIndexAt(int viewIndex)
        {
            return sorter.SourceIndexAt(viewIndex);
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            PaintHeaders(theme);
        }

        private void Rebuild()
        {
            ApplySort();
            list.SetItems(rows, sorter.ViewIndexOf(selectedSource));
            var empty = items.Count == 0;
            if (emptyLabel.Go.activeSelf != empty)
            {
                emptyLabel.Go.SetActive(empty);
            }

            PaintHeaders(Theme);
        }

        private void ApplySort()
        {
            var column = sort.Column;
            var comparer = column >= 0 ? columns[column].Comparer : null;
            if (comparer == null)
            {
                sorter.Reset(items.Count);
                return;
            }

            try
            {
                sorter.Sort(items, comparer, sort.Direction);
            }
            catch (Exception exception)
            {
                // The sorter restored the supplied order; keep showing rows rather than failing the frame.
                if (!sortFailureWarned)
                {
                    sortFailureWarned = true;
                    TopiaForgeLog.Warn("Table sort failed (" + exception.GetType().Name + ": " + exception.Message + "); showing rows unsorted.");
                }
            }
        }

        private int IndexOfKey(string key)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (string.Equals(keySelector!(items[index]), key, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        private void OnListSelected(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= sorter.Count)
            {
                return;
            }

            selectedSource = sorter.SourceIndexAt(viewIndex);
            selectedKey = keySelector != null ? keySelector(items[selectedSource]) : null;
            TopiaForgeCallbacks.Invoke(onSelected, selectedSource, "Table selection");
        }

        private void AttachCells(TopiaForgeListRow row)
        {
            row.Title.Go.SetActive(false);
            row.Subtitle.Go.SetActive(false);
            row.Badge.Go.SetActive(false);
            var container = new TopiaForgeContainer(row.Host, row.Scheme, row.Go);
            var cells = new object[columns.Length];
            for (var index = 0; index < columns.Length; index++)
            {
                var cell = container.CreateChild("Cell" + index);
                ApplyColumnWidth(cell, columns[index]);
                var view = columns[index].CreateCell(cell);
                ConfigureCell(cell, view, columns[index].Alignment);
                cells[index] = view;
            }

            rowCells[row] = cells;
        }

        private void BindRow(TopiaForgeListRow row, T item, int viewIndex)
        {
            if (!rowCells.TryGetValue(row, out var cells))
            {
                return;
            }

            for (var index = 0; index < cells.Length; index++)
            {
                columns[index].BindCell(cells[index], item);
            }
        }

        private static void ConfigureCell(TopiaForgeContainer cell, object view, TopiaForgeColumnAlignment alignment)
        {
            var fills = view is TopiaForgeLabel;
            var layout = cell.Go.AddComponent<HorizontalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = fills;
            layout.childForceExpandHeight = true;
            layout.childAlignment = alignment == TopiaForgeColumnAlignment.Right ? TextAnchor.MiddleRight
                : alignment == TopiaForgeColumnAlignment.Center ? TextAnchor.MiddleCenter
                : TextAnchor.MiddleLeft;
            if (view is TopiaForgeLabel label)
            {
                if (alignment == TopiaForgeColumnAlignment.Right)
                {
                    label.AlignRight();
                }
                else if (alignment == TopiaForgeColumnAlignment.Center)
                {
                    label.AlignCenter();
                }
            }
        }

        private static void ApplyColumnWidth(TopiaForgeWidget cell, TopiaForgeTableColumn<T> column)
        {
            var layout = cell.EnsureLayoutElement();
            if (column.FixedWidth > 0f)
            {
                layout.minWidth = column.FixedWidth;
                layout.preferredWidth = column.FixedWidth;
                layout.flexibleWidth = 0f;
            }
            else
            {
                layout.minWidth = 0f;
                layout.preferredWidth = 0f;
                layout.flexibleWidth = column.FlexWeight;
            }
        }

        /// <summary>The displayed order as a list, without copying: index i is the i-th displayed row.</summary>
        private sealed class SortedRows : IReadOnlyList<T>
        {
            private readonly TopiaForgeTableView<T> owner;

            public SortedRows(TopiaForgeTableView<T> owner)
            {
                this.owner = owner;
            }

            public int Count => owner.sorter.Count;

            public T this[int index] => owner.items[owner.sorter.SourceIndexAt(index)];

            public IEnumerator<T> GetEnumerator()
            {
                for (var index = 0; index < Count; index++)
                {
                    yield return this[index];
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }
}
