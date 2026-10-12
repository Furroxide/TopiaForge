using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Horizontal alignment of a table column's header and cells.</summary>
    public enum TopiaForgeColumnAlignment
    {
        /// <summary>Left-aligned (text).</summary>
        Left,

        /// <summary>Centered (glyphs).</summary>
        Center,

        /// <summary>Right-aligned (numbers).</summary>
        Right,
    }

    /// <summary>
    /// One column of a <see cref="TopiaForgeTableView{T}"/>: a header, a fixed width or a flex share,
    /// a cell binder and an optional comparer. Build it with <see cref="Text"/>, <see cref="Number"/>
    /// or <see cref="Custom{TView}"/>, then chain sizing and sorting. The table reads a column once,
    /// when it is created.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    public sealed class TopiaForgeTableColumn<T>
    {
        private const int MaxCellChars = 256;

        private readonly Func<TopiaForgeContainer, object> create;
        private readonly Action<object, T> bind;
        private readonly IComparer<T>? natural;

        private TopiaForgeTableColumn(string header, Func<TopiaForgeContainer, object> create, Action<object, T> bind, IComparer<T>? natural)
        {
            Header = header ?? string.Empty;
            this.create = create;
            this.bind = bind;
            this.natural = natural;
        }

        /// <summary>Gets the header text.</summary>
        public string Header { get; }

        /// <summary>Gets the fixed width in pixels, or 0 for a flexible column.</summary>
        public float FixedWidth { get; private set; }

        /// <summary>Gets the flex share of the remaining width (0 for a fixed column).</summary>
        public float FlexWeight { get; private set; } = 1f;

        /// <summary>Gets the alignment.</summary>
        public TopiaForgeColumnAlignment Alignment { get; private set; }

        /// <summary>Gets the direction a first header click sorts in.</summary>
        public TopiaForgeSortDirection FirstDirection { get; private set; } = TopiaForgeSortDirection.Ascending;

        /// <summary>Gets whether the column can be sorted.</summary>
        public bool IsSortable => Comparer != null;

        internal IComparer<T>? Comparer { get; private set; }

        /// <summary>A plain-text column (markup is shown literally; long text ends in an ellipsis).</summary>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
        public static TopiaForgeTableColumn<T> Text(string header, Func<T, string?> text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            return new TopiaForgeTableColumn<T>(
                header,
                cell => cell.Label(string.Empty, TopiaForgeTextStyle.Body).Ellipsis().PlainText(),
                (view, item) => ((TopiaForgeLabel)view).SetText(TopiaForgePlainText.Sanitize(text(item), MaxCellChars)),
                new KeyComparer<string?>(text, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>An integer column; cells re-compose their text only when the value changes.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public static TopiaForgeTableColumn<T> Number(string header, Func<T, int> value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var column = new TopiaForgeTableColumn<T>(
                header,
                cell => cell.Label(string.Empty, TopiaForgeTextStyle.Body).Ellipsis(),
                (view, item) => ((TopiaForgeLabel)view).SetText(string.Empty, value(item)),
                new KeyComparer<int>(value, Comparer<int>.Default));
            column.Alignment = TopiaForgeColumnAlignment.Right;
            return column;
        }

        /// <summary>
        /// A column of custom widgets: <paramref name="create"/> builds one view per pooled row inside
        /// the cell container, and <paramref name="bind"/> updates it for the row's item.
        /// </summary>
        /// <exception cref="ArgumentNullException">A callback is null.</exception>
        public static TopiaForgeTableColumn<T> Custom<TView>(string header, Func<TopiaForgeContainer, TView> create, Action<TView, T> bind)
            where TView : class
        {
            if (create == null)
            {
                throw new ArgumentNullException(nameof(create));
            }

            if (bind == null)
            {
                throw new ArgumentNullException(nameof(bind));
            }

            return new TopiaForgeTableColumn<T>(header, cell => create(cell), (view, item) => bind((TView)view, item), null);
        }

        /// <summary>Gives the column a fixed width in pixels.</summary>
        public TopiaForgeTableColumn<T> Width(float pixels)
        {
            FixedWidth = float.IsNaN(pixels) ? 80f : Math.Max(16f, Math.Min(pixels, 2000f));
            FlexWeight = 0f;
            return this;
        }

        /// <summary>Gives the column a share of the width left after fixed columns.</summary>
        public TopiaForgeTableColumn<T> Flex(float weight = 1f)
        {
            FixedWidth = 0f;
            FlexWeight = float.IsNaN(weight) ? 1f : Math.Max(0.01f, Math.Min(weight, 100f));
            return this;
        }

        /// <summary>Aligns the header and cells.</summary>
        public TopiaForgeTableColumn<T> Align(TopiaForgeColumnAlignment alignment)
        {
            Alignment = alignment;
            return this;
        }

        /// <summary>Sorts a text or number column by its own values (text ignores case).</summary>
        /// <exception cref="InvalidOperationException">The column is custom; use <see cref="SortBy{TKey}"/>.</exception>
        public TopiaForgeTableColumn<T> Sortable()
        {
            Comparer = natural ?? throw new InvalidOperationException("Custom columns sort through SortBy.");
            return this;
        }

        /// <summary>Sorts the column with a comparer over whole rows.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="comparer"/> is null.</exception>
        public TopiaForgeTableColumn<T> SortBy(IComparer<T> comparer)
        {
            Comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
            return this;
        }

        /// <summary>Sorts the column by a key, with the key type's default comparer unless one is given.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
        public TopiaForgeTableColumn<T> SortBy<TKey>(Func<T, TKey> key, IComparer<TKey>? comparer = null)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            Comparer = new KeyComparer<TKey>(key, comparer ?? Comparer<TKey>.Default);
            return this;
        }

        /// <summary>Makes a first header click sort descending (scores, kills).</summary>
        public TopiaForgeTableColumn<T> DescendingFirst()
        {
            FirstDirection = TopiaForgeSortDirection.Descending;
            return this;
        }

        internal object CreateCell(TopiaForgeContainer cell)
        {
            return create(cell);
        }

        internal void BindCell(object view, T item)
        {
            bind(view, item);
        }

        private sealed class KeyComparer<TKey> : IComparer<T>
        {
            private readonly Func<T, TKey> key;
            private readonly IComparer<TKey> comparer;

            public KeyComparer(Func<T, TKey> key, IComparer<TKey> comparer)
            {
                this.key = key;
                this.comparer = comparer;
            }

            public int Compare(T x, T y)
            {
                return comparer.Compare(key(x), key(y));
            }
        }
    }
}
