using System.Runtime.InteropServices;

namespace TopiaForge.Acceptance.Windows;

internal sealed record ScreenFact(string Path, int Width, int Height, int DistinctSampleColors, string Sha256, long Length);
internal static class ScreenCapture
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size; internal NativeMethods.Rect Monitor, Work; internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, Bits;
        internal uint Compression, ImageSize; internal int XPixels, YPixels; internal uint Used, Important;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint first, uint count, byte[] bits, ref BitmapInfo info, uint usage);

    internal static string RequireMonitor(IntPtr window, string expected)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        var monitor = MonitorFromWindow(window, 0);
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || !string.Equals(info.Device, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game moved away from the admitted display.");
        return info.Device;
    }

    /// Returns the retained fact and the captured BGRA pixels for reviewed baseline comparison.
    internal static (ScreenFact Fact, byte[] Pixels) Capture(OwnedGameProbe game, DeviceProfile profile, string root, string relative)
    {
        game.RequireForeground(profile.Width, profile.Height, profile.Dpi);
        RequireMonitor(game.Window, profile.DisplayName);
        var origin = new NativeMethods.Point();
        if (!NativeMethods.ClientToScreen(game.Window, ref origin)) throw new InvalidOperationException("Cannot locate the game client.");
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new InvalidOperationException("Cannot capture the admitted display.");
        var memory = IntPtr.Zero; var bitmap = IntPtr.Zero; var previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, profile.Width, profile.Height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("Capture allocation failed.");
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new InvalidOperationException("Capture bitmap selection failed.");
            if (!BitBlt(memory, 0, 0, profile.Width, profile.Height, screen, origin.X, origin.Y, 0x00CC0020 | 0x40000000)) throw new InvalidOperationException("Display capture failed.");
            SelectObject(memory, previous); previous = IntPtr.Zero;
            var pixels = new byte[checked(profile.Width * profile.Height * 4)];
            var info = new BitmapInfo { Size = 40, Width = profile.Width, Height = -profile.Height, Planes = 1, Bits = 32, ImageSize = (uint)pixels.Length };
            if (GetDIBits(memory, bitmap, 0, (uint)profile.Height, pixels, ref info, 0) != profile.Height) throw new InvalidOperationException("Display pixels are incomplete.");
            game.RequireForeground(profile.Width, profile.Height, profile.Dpi);
            RequireMonitor(game.Window, profile.DisplayName);
            var path = BoundedJson.Child(root, relative);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0x4D42); writer.Write(54 + pixels.Length); writer.Write(0); writer.Write(54);
                writer.Write(40); writer.Write(profile.Width); writer.Write(-profile.Height); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(0); writer.Write(pixels.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(pixels);
                writer.Flush(); stream.Flush(true);
            }
            return (new(relative, profile.Width, profile.Height, CountColors(pixels), BoundedJson.Hash(path), new FileInfo(path).Length), pixels);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
    internal static int CountColors(byte[] pixels)
    {
        if (pixels.Length == 0 || pixels.Length % 4 != 0) throw new InvalidDataException("Invalid BGRA pixel buffer.");
        var colors = new HashSet<int>();
        var stride = Math.Max(1, pixels.Length / 4 / 16384) * 4;
        for (var offset = 0; offset < pixels.Length; offset += stride)
            colors.Add(pixels[offset] | pixels[offset + 1] << 8 | pixels[offset + 2] << 16);
        return colors.Count;
    }
}
