using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitMultiplayerCoreTests
    {
        private static void TableSortStateToggles()
        {
            var sort = new TopiaForgeTableSortState();
            AssertEqual(sort.Column, -1, "unsorted at first");
            AssertEqual(sort.Direction, TopiaForgeSortDirection.None, "no direction at first");

            var version = sort.Version;
            sort.Toggle(2);
            AssertEqual((sort.Column, sort.Direction), (2, TopiaForgeSortDirection.Ascending), "a new column sorts ascending");
            Assert(sort.Version != version, "toggling moves the version");
            sort.Toggle(2);
            AssertEqual(sort.Direction, TopiaForgeSortDirection.Descending, "the same column flips to descending");
            sort.Toggle(2);
            AssertEqual(sort.Direction, TopiaForgeSortDirection.Ascending, "and back to ascending");
            sort.Toggle(0, TopiaForgeSortDirection.Descending);
            AssertEqual((sort.Column, sort.Direction), (0, TopiaForgeSortDirection.Descending), "a descending-first column starts descending");
            sort.Toggle(0, TopiaForgeSortDirection.Descending);
            AssertEqual(sort.Direction, TopiaForgeSortDirection.Ascending, "a descending-first column flips to ascending");
            sort.Toggle(1, TopiaForgeSortDirection.None);
            AssertEqual(sort.Direction, TopiaForgeSortDirection.Ascending, "a None first direction means ascending");
            AssertEqual(sort.DirectionOf(1), TopiaForgeSortDirection.Ascending, "the sorted column reports its direction");
            AssertEqual(sort.DirectionOf(0), TopiaForgeSortDirection.None, "other columns report no direction");
            AssertEqual(sort.DirectionOf(-1), TopiaForgeSortDirection.None, "a negative column reports no direction");

            version = sort.Version;
            sort.Set(1, TopiaForgeSortDirection.Ascending);
            AssertEqual(sort.Version, version, "setting the current sort changes nothing");
            sort.Set(3, TopiaForgeSortDirection.Descending);
            AssertEqual((sort.Column, sort.Direction), (3, TopiaForgeSortDirection.Descending), "set sorts directly");
            sort.Set(3, TopiaForgeSortDirection.None);
            AssertEqual((sort.Column, sort.Direction), (-1, TopiaForgeSortDirection.None), "setting None clears");
            version = sort.Version;
            sort.Clear();
            AssertEqual(sort.Version, version, "clearing an unsorted table changes nothing");
            AssertThrows<ArgumentOutOfRangeException>(() => sort.Toggle(-1), "a negative column cannot be toggled");
        }

        private static void StableSorterKeepsTiesInSuppliedOrder()
        {
            // (name, score): equal scores must keep their supplied order in both directions.
            var rows = new[] { ("ada", 10), ("bo", 30), ("cy", 10), ("di", 20), ("ed", 30), ("fi", 10) };
            var byScore = Comparer<(string Name, int Score)>.Create((a, b) => a.Score.CompareTo(b.Score));
            var sorter = new TopiaForgeStableSorter();

            sorter.Sort(rows, byScore, TopiaForgeSortDirection.Ascending);
            AssertEqual(Names(rows, sorter), "ada,cy,fi,di,bo,ed", "ascending keeps ties in supplied order");
            sorter.Sort(rows, byScore, TopiaForgeSortDirection.Descending);
            AssertEqual(Names(rows, sorter), "bo,ed,di,ada,cy,fi", "descending keeps ties in supplied order rather than reversing them");
            sorter.Sort(rows, byScore, TopiaForgeSortDirection.None);
            AssertEqual(Names(rows, sorter), "ada,bo,cy,di,ed,fi", "no direction keeps the supplied order");

            sorter.Sort(rows, byScore, TopiaForgeSortDirection.Descending);
            for (var view = 0; view < sorter.Count; view++)
            {
                AssertEqual(sorter.ViewIndexOf(sorter.SourceIndexAt(view)), view, "view and source indices are inverse at " + view);
            }

            AssertEqual(sorter.ViewIndexOf(4), 1, "the source row 'ed' shows second when descending");
            AssertEqual(sorter.ViewIndexOf(-1), -1, "a negative source index has no view position");
            AssertEqual(sorter.ViewIndexOf(6), -1, "a source index past the end has no view position");
            AssertThrows<ArgumentOutOfRangeException>(() => sorter.SourceIndexAt(6), "a view index past the end is rejected");

            sorter.Sort(Array.Empty<(string, int)>(), byScore, TopiaForgeSortDirection.Ascending);
            AssertEqual(sorter.Count, 0, "an empty list sorts to nothing");
            sorter.Sort(new[] { ("solo", 1) }, byScore, TopiaForgeSortDirection.Descending);
            AssertEqual(sorter.SourceIndexAt(0), 0, "a single row stays put");

            // Randomized equivalence with LINQ's documented-stable OrderBy across many sizes, both directions.
            var random = new Random(20261013);
            for (var round = 0; round < 60; round++)
            {
                var size = random.Next(0, 300);
                var keys = new int[size];
                for (var index = 0; index < size; index++)
                {
                    keys[index] = random.Next(0, 8);
                }

                var expectedAscending = Enumerable.Range(0, size).OrderBy(index => keys[index]).ToArray();
                sorter.Sort(keys, Comparer<int>.Default, TopiaForgeSortDirection.Ascending);
                for (var view = 0; view < size; view++)
                {
                    AssertEqual(sorter.SourceIndexAt(view), expectedAscending[view], "ascending matches a stable reference (round " + round + ")");
                }

                var expectedDescending = Enumerable.Range(0, size).OrderByDescending(index => keys[index]).ToArray();
                sorter.Sort(keys, Comparer<int>.Default, TopiaForgeSortDirection.Descending);
                for (var view = 0; view < size; view++)
                {
                    AssertEqual(sorter.SourceIndexAt(view), expectedDescending[view], "descending matches a stable reference (round " + round + ")");
                }
            }

            AssertThrows<ArgumentNullException>(() => sorter.Sort<int>(null!, Comparer<int>.Default, TopiaForgeSortDirection.Ascending), "null items are rejected");
            AssertThrows<ArgumentNullException>(() => sorter.Sort(new[] { 1 }, null!, TopiaForgeSortDirection.Ascending), "a null comparer is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => sorter.Reset(-1), "a negative count is rejected");
        }

        private static void StableSorterSurvivesBadComparers()
        {
            var sorter = new TopiaForgeStableSorter();
            var items = Enumerable.Range(0, 50).ToArray();

            var calls = 0;
            var throwing = Comparer<int>.Create((a, b) =>
            {
                if (++calls == 30)
                {
                    throw new InvalidOperationException("comparer failed");
                }

                return a.CompareTo(b);
            });
            AssertThrows<InvalidOperationException>(() => sorter.Sort(items, throwing, TopiaForgeSortDirection.Descending), "a comparer failure propagates");
            for (var view = 0; view < items.Length; view++)
            {
                AssertEqual(sorter.SourceIndexAt(view), view, "a failed sort restores the supplied order at " + view);
                AssertEqual(sorter.ViewIndexOf(view), view, "a failed sort restores the inverse at " + view);
            }

            var random = new Random(7);
            var inconsistent = Comparer<int>.Create((a, b) => random.Next(-1, 2));
            sorter.Sort(items, inconsistent, TopiaForgeSortDirection.Ascending);
            var seen = new bool[items.Length];
            for (var view = 0; view < items.Length; view++)
            {
                var source = sorter.SourceIndexAt(view);
                Assert(!seen[source], "an inconsistent comparer never duplicates a row");
                seen[source] = true;
            }

            Assert(seen.All(value => value), "an inconsistent comparer never loses a row");
        }

        private static void SortStateClearsWhenRowsFallBack()
        {
            var keys = new[] { 3, 1, 2, 1 };
            var sorter = new TopiaForgeStableSorter();
            var sort = new TopiaForgeTableSortState();

            sort.Set(1, TopiaForgeSortDirection.Descending);
            Assert(sort.TryApply(sorter, keys, Comparer<int>.Default, out var none) && none == null, "a working comparer applies");
            AssertEqual((sort.Column, sort.Direction), (1, TopiaForgeSortDirection.Descending), "a successful sort keeps its state");
            AssertEqual(sorter.SourceIndexAt(0), 0, "the rows are sorted (largest first)");
            AssertEqual(sorter.SourceIndexAt(1), 2, "the rows are sorted (then the 2)");

            var throwing = Comparer<int>.Create((a, b) => throw new InvalidOperationException("comparer failed"));
            var version = sort.Version;
            Assert(!sort.TryApply(sorter, keys, throwing, out var failure), "a throwing comparer reports a failure");
            Assert(failure is InvalidOperationException, "the comparer's exception is returned");
            AssertEqual((sort.Column, sort.Direction), (-1, TopiaForgeSortDirection.None), "a failed sort clears, so no header shows a sort that is not applied");
            Assert(sort.Version != version, "clearing after a failure moves the version");
            for (var view = 0; view < keys.Length; view++)
            {
                AssertEqual(sorter.SourceIndexAt(view), view, "a failed sort shows the supplied order at " + view);
            }

            sort.Set(0, TopiaForgeSortDirection.Ascending);
            Assert(sort.TryApply(sorter, keys, null, out failure) && failure == null, "a missing comparer is not a failure");
            AssertEqual(sort.Column, -1, "a sort without a comparer clears");
            AssertEqual(sorter.SourceIndexAt(0), 0, "a sort without a comparer shows the supplied order");

            version = sort.Version;
            Assert(sort.TryApply(sorter, keys, Comparer<int>.Default, out failure) && failure == null, "an unsorted table applies trivially");
            AssertEqual(sort.Version, version, "an unsorted table stays unchanged");
            AssertThrows<ArgumentNullException>(() => sort.TryApply<int>(null!, keys, null, out _), "a null sorter is rejected");
            AssertThrows<ArgumentNullException>(() => sort.TryApply<int>(sorter, null!, null, out _), "null items are rejected");
        }

        private static string Names((string Name, int Score)[] rows, TopiaForgeStableSorter sorter)
        {
            var names = new string[sorter.Count];
            for (var view = 0; view < sorter.Count; view++)
            {
                names[view] = rows[sorter.SourceIndexAt(view)].Name;
            }

            return string.Join(",", names);
        }
    }
}
