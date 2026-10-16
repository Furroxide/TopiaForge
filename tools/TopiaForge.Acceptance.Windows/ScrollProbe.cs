using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

/// Measured bottom-left rectangle taken from an observer widget row.
internal sealed record MeasuredRect(double X, double Y, double Width, double Height)
{
    internal double Top => Y + Height;
    internal double Right => X + Width;
    internal bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height);
    internal bool Intersects(MeasuredRect other) => X < other.Right && other.X < Right && Y < other.Top && other.Y < Top;
    internal static MeasuredRect Of(JsonElement widget) => new(Number(widget, "x"), Number(widget, "y"), Number(widget, "width"), Number(widget, "height"));
    private static double Number(JsonElement value, string name)
    {
        var field = value.GetProperty(name);
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out var number) || !double.IsFinite(number)) throw new InvalidDataException("Invalid number: " + name);
        return number;
    }
}

/// A planned wheel point (bottom-left client pixels) and the uncovered band that contains it.
internal sealed record ScrollProbe(double X, double Y, double BandY, double BandHeight);

/// Chooses where a wheel burst lands so a nested ScrollRect cannot consume it. Every covering
/// rectangle blocks its full vertical span across the container, so a free band is free for every x.
internal static class ScrollProbePlanner
{
    internal const double Inset = 4;
    /// Widget kinds owning their own ScrollRect: scroll views and virtual lists (catalog-list, roster-list, project-list).
    internal static readonly string[] NestedKinds = ["scroll", "list"];
    internal static ScrollProbe Plan(MeasuredRect container, IReadOnlyList<MeasuredRect> covered)
    {
        if (!container.Finite || covered.Any(rect => !rect.Finite)) throw new InvalidDataException("Native geometry unavailable.");
        var minimum = 2 * Inset + 1;
        if (container.Width < minimum || container.Height < minimum) throw new InvalidOperationException("Scroll container is too small for an inset wheel point.");
        var bands = new List<(double Low, double High)>();
        var cursor = container.Y;
        foreach (var block in covered.Where(rect => rect.Intersects(container)).OrderBy(rect => rect.Y))
        {
            if (block.Y > cursor) bands.Add((cursor, block.Y));
            cursor = Math.Max(cursor, Math.Min(block.Top, container.Top));
        }
        if (cursor < container.Top) bands.Add((cursor, container.Top));
        // The tallest band wins; equal heights prefer the lowest band so the choice is deterministic.
        var usable = bands.Where(band => band.High - band.Low >= minimum).OrderByDescending(band => band.High - band.Low).ThenBy(band => band.Low).ToList();
        if (usable.Count == 0) throw new InvalidOperationException("Scroll container has no wheel point outside its nested scrolling widgets.");
        var (low, high) = usable[0];
        return new(container.X + container.Width / 2, (low + high) / 2, low, high - low);
    }
}
