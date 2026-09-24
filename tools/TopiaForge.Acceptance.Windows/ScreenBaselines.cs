using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

/// Top-left image pixel rectangle excluded from comparison.
internal sealed record ScreenMask(int X, int Y, int Width, int Height);
internal sealed record ScreenBaselineEntry(string ScenarioId, int Cycle, string Step, string Path, string Sha256, double Tolerance, ScreenMask[] Masks);
internal sealed record ScreenBaselineFact(string Path, string Sha256, double MismatchFraction, bool WithinTolerance);

/// Reviewed BMP baselines are only ever read and compared; the broker never writes one.
internal sealed class ScreenBaselineSet
{
    /// A pixel mismatches when its largest B/G/R channel difference exceeds this value (0..255); alpha is ignored.
    internal const int ChannelThreshold = 8;
    internal string Root { get; }
    internal ScreenBaselineEntry[] Entries { get; }
    private ScreenBaselineSet(string root, ScreenBaselineEntry[] entries) { Root = root; Entries = entries; }

    internal static ScreenBaselineSet Read(JsonElement value, int width, int height)
    {
        BoundedJson.Keys(value, "root", "entries");
        var root = BoundedJson.PhysicalPath(BoundedJson.Text(value, "root", 1024));
        if (!Directory.Exists(root)) throw new InvalidDataException("Baseline root must be an existing physical directory.");
        var rows = value.GetProperty("entries");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 256) throw new InvalidDataException("Baseline inventory exceeds its bound.");
        var entries = rows.EnumerateArray().Select(row => ReadEntry(row, root, width, height)).ToArray();
        if (entries.Select(e => (e.ScenarioId, e.Cycle, e.Step)).Distinct().Count() != entries.Length) throw new InvalidDataException("Duplicate baseline for one scenario cycle step.");
        return new(root, entries);
    }
    private static ScreenBaselineEntry ReadEntry(JsonElement row, string root, int width, int height)
    {
        BoundedJson.Keys(row, "scenarioId", "cycle", "step", "path", "sha256", "tolerance", "masks");
        var scenario = BoundedJson.Text(row, "scenarioId", 64);
        if (!DriverManifest.ScenarioIds.Contains(scenario)) throw new InvalidDataException("Unknown baseline scenario.");
        var cycle = BoundedJson.Integer(row, "cycle", 1, DriverManifest.Cycles(scenario));
        var step = BoundedJson.Text(row, "step", 128);
        if (step != "prepare" && !DriverVocabulary.Actions.Contains(step)) throw new InvalidDataException("Unknown baseline step.");
        var path = BoundedJson.Text(row, "path", 256);
        if (!path.EndsWith(".bmp", StringComparison.Ordinal)) throw new InvalidDataException("Baselines must be BMP files.");
        _ = BoundedJson.Child(root, path);
        var tolerance = row.GetProperty("tolerance");
        if (tolerance.ValueKind != JsonValueKind.Number || !tolerance.TryGetDouble(out var limit) || !double.IsFinite(limit) || limit < 0 || limit > 1) throw new InvalidDataException("Baseline tolerance must be a number within 0..1.");
        var masks = row.GetProperty("masks");
        if (masks.ValueKind != JsonValueKind.Array || masks.GetArrayLength() > 64) throw new InvalidDataException("Baseline masks exceed their bound.");
        return new(scenario, cycle, step, path, BoundedJson.Digest(row, "sha256"), limit, masks.EnumerateArray().Select(mask => ReadMask(mask, width, height)).ToArray());
    }
    private static ScreenMask ReadMask(JsonElement mask, int width, int height)
    {
        BoundedJson.Keys(mask, "x", "y", "width", "height");
        var rect = new ScreenMask(BoundedJson.Integer(mask, "x", 0, width - 1), BoundedJson.Integer(mask, "y", 0, height - 1), BoundedJson.Integer(mask, "width", 1, width), BoundedJson.Integer(mask, "height", 1, height));
        if (rect.X + rect.Width > width || rect.Y + rect.Height > height) throw new InvalidDataException("Baseline mask leaves the admitted display.");
        return rect;
    }
    internal ScreenBaselineEntry? Find(string scenario, int cycle, string step) => Entries.SingleOrDefault(e => e.ScenarioId == scenario && e.Cycle == cycle && e.Step == step);

    /// Null without a matching entry. Baseline bytes must still hash to the reviewed digest before any comparison.
    internal ScreenBaselineFact? Compare(string scenario, int cycle, string step, byte[] capture, int width, int height)
    {
        var entry = Find(scenario, cycle, step);
        if (entry == null) return null;
        var bytes = BoundedJson.ReadBytes(BoundedJson.Child(Root, entry.Path), checked(54 + width * height * 4));
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != entry.Sha256) throw new InvalidDataException("Baseline bytes changed: " + entry.Path);
        var fraction = MismatchFraction(capture, ParsePixels(bytes, width, height), width, height, entry.Masks);
        return new(entry.Path, entry.Sha256, fraction, fraction <= entry.Tolerance);
    }
    /// Accepts only the broker's own top-down 32-bit layout at the admitted dimensions.
    internal static ReadOnlySpan<byte> ParsePixels(byte[] bytes, int width, int height)
    {
        var size = checked(width * height * 4);
        int Int32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        ushort UInt16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
        if (bytes.Length != 54 + size || UInt16(0) != 0x4D42 || Int32(2) != bytes.Length || Int32(6) != 0 || Int32(10) != 54 || Int32(14) != 40
            || Int32(18) != width || Int32(22) != -height || UInt16(26) != 1 || UInt16(28) != 32 || Int32(30) != 0 || Int32(34) != size
            || Int32(38) != 0 || Int32(42) != 0 || Int32(46) != 0 || Int32(50) != 0)
            throw new InvalidDataException("Baseline BMP layout or dimensions differ from the admitted display.");
        return bytes.AsSpan(54);
    }
    internal static byte[] EncodeBitmap(int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != checked(width * height * 4)) throw new InvalidDataException("Invalid BGRA pixel buffer.");
        var bytes = new byte[54 + pixels.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, 0x4D42); BinaryPrimitives.WriteInt32LittleEndian(span[2..], bytes.Length); BinaryPrimitives.WriteInt32LittleEndian(span[10..], 54);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], 40); BinaryPrimitives.WriteInt32LittleEndian(span[18..], width); BinaryPrimitives.WriteInt32LittleEndian(span[22..], -height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], 1); BinaryPrimitives.WriteUInt16LittleEndian(span[28..], 32); BinaryPrimitives.WriteInt32LittleEndian(span[34..], pixels.Length);
        pixels.CopyTo(span[54..]);
        return bytes;
    }
    /// Fraction of unmasked pixels whose largest B/G/R difference exceeds the threshold.
    internal static double MismatchFraction(ReadOnlySpan<byte> capture, ReadOnlySpan<byte> baseline, int width, int height, ScreenMask[] masks)
    {
        var pixels = checked(width * height);
        if (width < 1 || height < 1 || capture.Length != pixels * 4 || baseline.Length != pixels * 4) throw new InvalidDataException("Baseline and capture dimensions differ.");
        var masked = new bool[pixels];
        foreach (var mask in masks)
        {
            if (mask.X < 0 || mask.Y < 0 || mask.Width < 1 || mask.Height < 1 || mask.X + mask.Width > width || mask.Y + mask.Height > height) throw new InvalidDataException("Baseline mask leaves the admitted display.");
            for (var row = mask.Y; row < mask.Y + mask.Height; row++) Array.Fill(masked, true, row * width + mask.X, mask.Width);
        }
        long unmasked = 0, mismatched = 0;
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            if (masked[pixel]) continue;
            unmasked++;
            var offset = pixel * 4;
            var difference = Math.Max(Math.Abs(capture[offset] - baseline[offset]), Math.Max(Math.Abs(capture[offset + 1] - baseline[offset + 1]), Math.Abs(capture[offset + 2] - baseline[offset + 2])));
            if (difference > ChannelThreshold) mismatched++;
        }
        if (unmasked == 0) throw new InvalidDataException("Baseline masks cover the entire capture.");
        return (double)mismatched / unmasked;
    }
}
