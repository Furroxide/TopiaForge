using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>A table column's sort direction.</summary>
    public enum TopiaForgeSortDirection
    {
        /// <summary>Unsorted: rows keep the order they were supplied in.</summary>
        None,

        /// <summary>Smallest first.</summary>
        Ascending,

        /// <summary>Largest first.</summary>
        Descending,
    }

    /// <summary>
    /// Which column a table is sorted by. Activating a new column starts at that column's first
    /// direction; activating the sorted column again flips the direction.
    /// </summary>
    public sealed class TopiaForgeTableSortState
    {
        /// <summary>Gets the sorted column, or -1.</summary>
        public int Column { get; private set; } = -1;

        /// <summary>Gets the sort direction; <see cref="TopiaForgeSortDirection.None"/> when unsorted.</summary>
        public TopiaForgeSortDirection Direction { get; private set; }

        /// <summary>Gets a counter that changes whenever the column or direction changes.</summary>
        public int Version { get; private set; }

        /// <summary>A header activation: flips the sorted column, or sorts a new one in <paramref name="firstDirection"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="column"/> is negative.</exception>
        public void Toggle(int column, TopiaForgeSortDirection firstDirection = TopiaForgeSortDirection.Ascending)
        {
            if (column < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(column));
            }

            if (column == Column && Direction != TopiaForgeSortDirection.None)
            {
                Direction = Direction == TopiaForgeSortDirection.Ascending
                    ? TopiaForgeSortDirection.Descending
                    : TopiaForgeSortDirection.Ascending;
            }
            else
            {
                Column = column;
                Direction = firstDirection == TopiaForgeSortDirection.Descending
                    ? TopiaForgeSortDirection.Descending
                    : TopiaForgeSortDirection.Ascending;
            }

            Version++;
        }

        /// <summary>Sorts by a column in a direction; a negative column or <see cref="TopiaForgeSortDirection.None"/> clears.</summary>
        public void Set(int column, TopiaForgeSortDirection direction)
        {
            if (column < 0 || direction == TopiaForgeSortDirection.None)
            {
                Clear();
                return;
            }

            if (column == Column && direction == Direction)
            {
                return;
            }

            Column = column;
            Direction = direction;
            Version++;
        }

        /// <summary>Returns to the supplied order.</summary>
        public void Clear()
        {
            if (Column < 0 && Direction == TopiaForgeSortDirection.None)
            {
                return;
            }

            Column = -1;
            Direction = TopiaForgeSortDirection.None;
            Version++;
        }

        /// <summary>Gets the direction a column is sorted in, <see cref="TopiaForgeSortDirection.None"/> unless it is the sorted column.</summary>
        public TopiaForgeSortDirection DirectionOf(int column)
        {
            return column >= 0 && column == Column ? Direction : TopiaForgeSortDirection.None;
        }
    }

    /// <summary>
    /// A stable sort over row indices: the source list is never reordered, and rows that compare
    /// equal keep their supplied order in both directions. Bottom-up merge sort into reused buffers,
    /// so re-sorting the same number of rows allocates nothing, and an inconsistent comparer can
    /// never make it loop or lose rows.
    /// </summary>
    public sealed class TopiaForgeStableSorter
    {
        private int[] order = Array.Empty<int>();
        private int[] scratch = Array.Empty<int>();
        private int[] positions = Array.Empty<int>();

        /// <summary>Gets the number of ordered rows.</summary>
        public int Count { get; private set; }

        /// <summary>Gets the source index shown at a view position.</summary>
        public int SourceIndexAt(int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(viewIndex));
            }

            return order[viewIndex];
        }

        /// <summary>Gets the view position of a source row, or -1 when it is out of range.</summary>
        public int ViewIndexOf(int sourceIndex)
        {
            return sourceIndex >= 0 && sourceIndex < Count ? positions[sourceIndex] : -1;
        }

        /// <summary>Orders <paramref name="count"/> rows as supplied.</summary>
        public void Reset(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            Ensure(count);
            Count = count;
            for (var index = 0; index < count; index++)
            {
                order[index] = index;
                positions[index] = index;
            }
        }

        /// <summary>
        /// Orders the rows of <paramref name="items"/> by <paramref name="comparer"/>. If the comparer
        /// throws, the supplied order is restored and the exception propagates.
        /// </summary>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        public void Sort<T>(IReadOnlyList<T> items, IComparer<T> comparer, TopiaForgeSortDirection direction)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (comparer == null)
            {
                throw new ArgumentNullException(nameof(comparer));
            }

            var count = items.Count;
            Reset(count);
            if (direction == TopiaForgeSortDirection.None || count < 2)
            {
                return;
            }

            var descending = direction == TopiaForgeSortDirection.Descending;
            try
            {
                var source = order;
                var target = scratch;
                for (var width = 1; width < count; width *= 2)
                {
                    for (var left = 0; left < count; left += 2 * width)
                    {
                        var middle = Math.Min(left + width, count);
                        var right = Math.Min(left + (2 * width), count);
                        Merge(items, comparer, descending, source, target, left, middle, right);
                    }

                    var swap = source;
                    source = target;
                    target = swap;
                }

                if (!ReferenceEquals(source, order))
                {
                    Array.Copy(source, order, count);
                }
            }
            catch
            {
                Reset(count);
                throw;
            }

            for (var view = 0; view < count; view++)
            {
                positions[order[view]] = view;
            }
        }

        private static void Merge<T>(
            IReadOnlyList<T> items,
            IComparer<T> comparer,
            bool descending,
            int[] source,
            int[] target,
            int left,
            int middle,
            int right)
        {
            var i = left;
            var j = middle;
            var k = left;
            while (i < middle && j < right)
            {
                // Take from the right run only when it strictly precedes the left one: ties keep supplied order.
                var rightFirst = descending
                    ? comparer.Compare(items[source[i]], items[source[j]]) < 0
                    : comparer.Compare(items[source[j]], items[source[i]]) < 0;
                target[k++] = rightFirst ? source[j++] : source[i++];
            }

            while (i < middle)
            {
                target[k++] = source[i++];
            }

            while (j < right)
            {
                target[k++] = source[j++];
            }
        }

        private void Ensure(int count)
        {
            if (order.Length >= count)
            {
                return;
            }

            var size = Math.Max(count, Math.Max(16, order.Length * 2));
            order = new int[size];
            scratch = new int[size];
            positions = new int[size];
        }
    }
}
