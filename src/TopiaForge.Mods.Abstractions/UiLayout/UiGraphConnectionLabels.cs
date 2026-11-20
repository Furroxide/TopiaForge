using System;
using System.Collections.Generic;
using System.Globalization;

namespace TopiaForge.Mods
{
    internal readonly struct UiGraphConnectionLabels
    {
        internal UiGraphConnectionLabels(string source, string target)
        {
            Source = source;
            Target = target;
        }

        internal string Source { get; }
        internal string Target { get; }
        internal string Full => Source + " to " + Target;
    }

    internal readonly struct UiGraphConnectionLabelPages
    {
        internal const float HorizontalInset = 12f;
        private const string From = "From ";
        private const string To = "To ";
        private const string More = " \u203A";
        private readonly string? single;
        private readonly string[]? pages;

        private UiGraphConnectionLabelPages(string single)
        {
            this.single = single;
            pages = null;
        }

        private UiGraphConnectionLabelPages(string[] pages)
        {
            single = null;
            this.pages = pages;
        }

        internal int Count => pages?.Length ?? (single == null ? 0 : 1);
        internal string Display(int page) => pages == null ? single ?? string.Empty : pages[Math.Min(page, pages.Length - 1)];

        internal static UiGraphConnectionLabelPages Measure(UiGraphConnectionLabels labels, string full, UiLayoutRect box,
            Func<string, (double Width, double Height)> measure)
        {
            if (Fits(full, box, measure)) return new UiGraphConnectionLabelPages(full);

            var separate = From + labels.Source + "\n" + To + labels.Target;
            if (Fits(separate, box, measure)) return new UiGraphConnectionLabelPages(separate);

            var source = Lines(From, labels.Source, string.Empty, box, measure);
            var target = Lines(To, labels.Target, More, box, measure);
            if (source.Count == 0 || target.Count == 0) return default;

            // Reveal both endpoints in parallel. A short endpoint stays visible while the other
            // continues, so aliases with the same long prefix can still be distinguished.
            var together = new string[Math.Max(source.Count, target.Count)];
            for (var page = 0; page < together.Length; page++)
            {
                var display = source[Math.Min(page, source.Count - 1)] + "\n" + target[Math.Min(page, target.Count - 1)];
                if (!Fits(display, box, measure)) return SingleLinePages(labels, target, box, measure);
                together[page] = display;
            }

            return new UiGraphConnectionLabelPages(together);
        }

        internal static string LauncherCaption(UiLayoutRect box, Func<string, (double Width, double Height)> measure) =>
            Fits(UiGraphConnections.LauncherLabel, box, measure) ? UiGraphConnections.LauncherLabel :
            Fits("Links", box, measure) ? "Links" : string.Empty;

        internal static string RemoveCaption(string full, UiLayoutRect box, Func<string, (double Width, double Height)> measure) =>
            Fits(full, box, measure) ? full : Fits("Remove", box, measure) ? "Remove" : string.Empty;

        private static UiGraphConnectionLabelPages SingleLinePages(UiGraphConnectionLabels labels, List<string> target,
            UiLayoutRect box, Func<string, (double Width, double Height)> measure)
        {
            var source = Lines(From, labels.Source, More, box, measure);
            if (source.Count == 0) return default;

            // A short aperture can still reveal each complete endpoint without shrinking either.
            target.AddRange(source);
            return new UiGraphConnectionLabelPages(target.ToArray());
        }

        private static List<string> Lines(string prefix, string endpoint, string suffix, UiLayoutRect box,
            Func<string, (double Width, double Height)> measure)
        {
            var elements = StringInfo.ParseCombiningCharacters(endpoint);
            return Lines(prefix, endpoint, suffix, box, measure, elements)
                ?? Lines(prefix, endpoint, suffix, box, measure, ScalarStarts(endpoint))
                ?? new List<string>();
        }

        private static List<string>? Lines(string prefix, string endpoint, string suffix, UiLayoutRect box,
            Func<string, (double Width, double Height)> measure, int[] elements)
        {
            var lines = new List<string>();
            for (var first = 0; first < elements.Length;)
            {
                var start = elements[first];
                var low = first + 1;
                var high = elements.Length;
                var next = first;
                while (low <= high)
                {
                    var mid = low + (high - low) / 2;
                    var end = mid == elements.Length ? endpoint.Length : elements[mid];
                    if (Fits(prefix + endpoint.Substring(start, end - start) + suffix, box, measure))
                    {
                        next = mid;
                        low = mid + 1;
                    }
                    else high = mid - 1;
                }

                if (next == first) return null;

                // Prefer a nearby word break and keep each measured text unit intact across pages.
                var last = next;
                while (last < elements.Length && last > first + (next - first) / 2 &&
                    endpoint[elements[last]] != ' ' && endpoint[elements[last] - 1] != ' ')
                {
                    last--;
                }
                if (last > first + (next - first) / 2) next = last;
                var stop = next == elements.Length ? endpoint.Length : elements[next];
                lines.Add(prefix + endpoint.Substring(start, stop - start).Trim() + suffix);
                first = next;
                while (first < elements.Length && endpoint[elements[first]] == ' ') first++;
            }
            return lines;
        }

        private static int[] ScalarStarts(string text)
        {
            // A chained sequence can span the entire label when the active font displays its
            // glyphs separately. Reveal its constituent scalars if it cannot fit as one unit.
            var starts = new List<int>(text.Length);
            for (var index = 0; index < text.Length;)
            {
                starts.Add(index);
                index += char.IsSurrogatePair(text, index) ? 2 : 1;
            }
            return starts.ToArray();
        }

        private static bool Fits(string text, UiLayoutRect box, Func<string, (double Width, double Height)> measure)
        {
            var size = measure(text);
            return size.Width > 0d && size.Height > 0d &&
                size.Width <= box.Width - HorizontalInset * 2 && size.Height <= box.Height;
        }
    }
}
