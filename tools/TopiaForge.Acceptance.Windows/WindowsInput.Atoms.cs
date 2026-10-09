using System.Diagnostics;

namespace TopiaForge.Acceptance.Windows;

/// v2 relative/held actuation. Every bound is checked again here so a manifest
/// bypass cannot widen what the OS receives; keys and buttons release in finally.
internal sealed partial class WindowsInput
{
    /// Holds a declared key or the left button for the requested time and returns the measured hold.
    internal async Task<long> Hold(string key, int milliseconds, CancellationToken cancellation)
    {
        if (milliseconds < DriverVocabulary.KeyHoldMinimumMilliseconds || milliseconds > DriverVocabulary.KeyHoldMaximumMilliseconds) throw new InvalidDataException("Key hold duration is outside its bound.");
        var code = KeyCode(key);
        cancellation.ThrowIfCancellationRequested();
        Guard();
        var clock = Stopwatch.StartNew();
        Press(code);
        try { await Task.Delay(milliseconds, cancellation); Guard(); }
        finally { Release(code); clock.Stop(); }
        return clock.ElapsedMilliseconds;
    }
    /// One relative MOUSEEVENTF_MOVE; components stay within the declared bound.
    internal Task MoveRelative(int dx, int dy, CancellationToken cancellation)
    {
        if (Math.Abs(dx) > DriverVocabulary.MouseMoveBound || Math.Abs(dy) > DriverVocabulary.MouseMoveBound) throw new InvalidDataException("Relative mouse move is outside its bound.");
        cancellation.ThrowIfCancellationRequested();
        Guard();
        Send(MouseEvent(1, dx, dy));
        return Task.CompletedTask;
    }
    /// Moves to a planned bottom-left client point, then sends one wheel burst (down = toward the user).
    internal async Task Wheel(double x, double y, int ticks, bool down, CancellationToken cancellation)
    {
        if (ticks < 1 || ticks > DriverVocabulary.ScrollTicksPerAttempt) throw new InvalidDataException("Wheel burst is outside its bound.");
        cancellation.ThrowIfCancellationRequested();
        Guard();
        MoveCursorTo(PointTarget(x, y, profile.Width, profile.Height));
        var delta = unchecked((uint)(down ? -DriverVocabulary.WheelDelta : DriverVocabulary.WheelDelta));
        for (var tick = 0; tick < ticks; tick++)
        {
            Guard();
            Send(MouseEvent(0x0800, data: delta));
            await Task.Delay(15, cancellation);
        }
    }
}
