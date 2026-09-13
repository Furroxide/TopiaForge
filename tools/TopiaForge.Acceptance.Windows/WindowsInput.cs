using System.Runtime.InteropServices;

namespace TopiaForge.Acceptance.Windows;

internal sealed partial class WindowsInput : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { internal int X, Y; internal uint Data, Flags, Time; internal UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { internal ushort Key, Scan; internal uint Flags, Time; internal UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct Union { [FieldOffset(0)] internal Mouse Mouse; [FieldOffset(0)] internal Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { internal uint Type; internal Union Data; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private const ushort MouseLeftCode = 0x01;
    private readonly OwnedGameProbe game;
    private readonly DeviceProfile profile;
    private readonly HashSet<ushort> heldKeys = new();
    private bool heldMouse;
    private readonly HashSet<ushort> heldUnicode = new();
    internal bool Released { get; private set; } = true;
    internal int SentEvents { get; private set; }
    internal WindowsInput(OwnedGameProbe game, DeviceProfile profile) { this.game = game; this.profile = profile; }
    internal void Guard()
    {
        game.RequireForeground(profile.Width, profile.Height, profile.Dpi);
        ScreenCapture.RequireMonitor(game.Window, profile.DisplayName);
        foreach (ushort key in new ushort[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, 0x1b })
            if (!heldKeys.Contains(key) && GetAsyncKeyState(key) < 0) throw new InvalidOperationException("Operator modifier or Escape interrupted the driver.");
    }
    private void Send(Input input)
    {
        if (SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) != 1) throw new InvalidOperationException("Windows refused an input event; cleanup remains required.");
        SentEvents++;
    }
    private static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new Union { Keyboard = new Keyboard { Key = key, Flags = up ? 2u : 0u } } };
    private static Input MouseEvent(uint flags, int x = 0, int y = 0, uint data = 0) => new() { Type = 0, Data = new Union { Mouse = new Mouse { X = x, Y = y, Data = data, Flags = flags } } };
    /// Virtual-key code for a declared key; MouseLeft names the left button rather than a keyboard key.
    internal static ushort KeyCode(string key) => key switch
    {
        "F5" => (ushort)0x74, "Escape" => (ushort)0x1b, "Tab" => (ushort)0x09, "W" => (ushort)0x57, "Down" => (ushort)0x28, "Up" => (ushort)0x26, "Return" => (ushort)0x0d, "Space" => (ushort)0x20, "MouseLeft" => MouseLeftCode,
        _ => throw new InvalidDataException("Undeclared key.")
    };
    private void UpdateReleased() => Released = heldKeys.Count == 0 && heldUnicode.Count == 0 && !heldMouse;
    private void Press(ushort code)
    {
        if (GetAsyncKeyState(code) < 0) throw new InvalidOperationException("A requested key or button is already held by the operator.");
        if (code == MouseLeftCode) { heldMouse = true; Released = false; Send(MouseEvent(2)); return; }
        heldKeys.Add(code); Released = false; Send(Key(code));
    }
    private void Release(ushort code)
    {
        // A refused release keeps the key recorded as held so Dispose retries it.
        Send(code == MouseLeftCode ? MouseEvent(4) : Key(code, true));
        if (code == MouseLeftCode) heldMouse = false; else heldKeys.Remove(code);
        UpdateReleased();
    }
    internal async Task Tap(string key, CancellationToken cancellation)
    {
        var code = KeyCode(key);
        cancellation.ThrowIfCancellationRequested();
        Guard();
        Press(code);
        try { await Task.Delay(100, cancellation); Guard(); }
        finally { Release(code); }
    }
    internal async Task Click(double x, double y, double width, double height, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Guard();
        MoveCursorTo(TargetPoint(x, y, width, height, profile.Width, profile.Height));
        Guard(); heldMouse = true; Released = false;
        try { Send(MouseEvent(2)); await Task.Delay(70, cancellation); Guard(); }
        finally { Send(MouseEvent(4)); heldMouse = false; UpdateReleased(); }
    }
    /// Absolute move to a top-left client point derived only from observed geometry (a widget centre or a planned wheel probe).
    private void MoveCursorTo(NativeMethods.Point point)
    {
        if (!NativeMethods.ClientToScreen(game.Window, ref point)) throw new InvalidOperationException("Client screen transform failed.");
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var virtualWidth = GetSystemMetrics(78); var virtualHeight = GetSystemMetrics(79);
        if (virtualWidth < 2 || virtualHeight < 2) throw new InvalidOperationException("Virtual screen unavailable.");
        if (GetAsyncKeyState(1) < 0 || GetAsyncKeyState(2) < 0) throw new InvalidOperationException("Operator mouse button is already held.");
        var absolute = AbsolutePoint(point.X, point.Y, left, top, virtualWidth, virtualHeight);
        Send(MouseEvent(0x8000 | 0x4000 | 1, absolute.X, absolute.Y));
    }
    /// Selects all and types the replacement; an empty replacement deletes the selection instead.
    internal async Task ReplaceText(string text, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (text.Length > 1024 || text.Any(char.IsControl)) throw new InvalidDataException("Fixture text must be bounded and printable.");
        Guard();
        if (GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x41) < 0) throw new InvalidOperationException("Operator modifier is already held.");
        heldKeys.Add(0x11); heldKeys.Add(0x41); Released = false;
        try { Send(Key(0x11)); Send(Key(0x41)); }
        finally { Send(Key(0x41, true)); heldKeys.Remove(0x41); Send(Key(0x11, true)); heldKeys.Remove(0x11); UpdateReleased(); }
        await Task.Delay(50, cancellation);
        if (text.Length == 0)
        {
            Guard();
            Press(0x08);
            try { await Task.Delay(30, cancellation); } finally { Release(0x08); }
        }
        foreach (var character in text)
        {
            cancellation.ThrowIfCancellationRequested();
            Guard();
            heldUnicode.Add(character); Released = false;
            Send(new Input { Type = 1, Data = new Union { Keyboard = new Keyboard { Scan = character, Flags = 4 } } });
            Send(new Input { Type = 1, Data = new Union { Keyboard = new Keyboard { Scan = character, Flags = 4 | 2 } } });
            heldUnicode.Remove(character); UpdateReleased();
        }
        await Task.Delay(100, cancellation);
    }
    internal static NativeMethods.Point AbsolutePoint(int x, int y, int left, int top, int width, int height)
    {
        var offsetX = (long)x - left; var offsetY = (long)y - top;
        if (width < 2 || height < 2 || offsetX < 0 || offsetY < 0 || offsetX >= width || offsetY >= height)
            throw new InvalidOperationException("Input target is outside the actual virtual display.");
        return new NativeMethods.Point { X = (int)(offsetX * 65535L / (width - 1)), Y = (int)(offsetY * 65535L / (height - 1)) };
    }
    /// Bottom-left client point to a top-left client point; the point must lie strictly inside the admitted client.
    internal static NativeMethods.Point PointTarget(double x, double y, int clientWidth, int clientHeight)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 1 || y < 1 || x > clientWidth - 1 || y > clientHeight - 1)
            throw new InvalidDataException("Requested wheel point is outside the admitted client.");
        return new NativeMethods.Point { X = (int)Math.Round(x), Y = (int)Math.Round(clientHeight - y) };
    }
    internal static NativeMethods.Point TargetPoint(double x, double y, double width, double height, int clientWidth, int clientHeight)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) || x < 0 || y < 0 || width < 2 || height < 2 || x + width > clientWidth || y + height > clientHeight)
            throw new InvalidDataException("Requested widget is clipped or outside the admitted client.");
        return new NativeMethods.Point { X = (int)Math.Round(x + width / 2), Y = (int)Math.Round(clientHeight - y - height / 2) };
    }
    public void Dispose()
    {
        var failures = new List<Exception>();
        foreach (var key in heldKeys.ToArray()) try { Send(Key(key, true)); heldKeys.Remove(key); } catch (Exception error) { failures.Add(error); }
        foreach (var character in heldUnicode.ToArray()) try { Send(new Input { Type = 1, Data = new Union { Keyboard = new Keyboard { Scan = character, Flags = 6 } } }); heldUnicode.Remove(character); } catch (Exception error) { failures.Add(error); }
        if (heldMouse) try { Send(MouseEvent(4)); heldMouse = false; } catch (Exception error) { failures.Add(error); }
        Released = heldKeys.Count == 0 && heldUnicode.Count == 0 && !heldMouse && failures.Count == 0;
        if (failures.Count != 0) throw new AggregateException("Owned input release is unconfirmed.", failures);
    }
}
