using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TopiaForge.Acceptance.Windows;

/// Query-only handle corroborates the launcher's retained original process.
/// The broker never terminates or reopens a process by PID during cleanup.
internal sealed class OwnedGameProbe : IDisposable
{
    private readonly BrokerRequest request;
    private readonly SafeProcessHandle handle;
    internal IntPtr Window { get; private set; }
    internal OwnedGameProbe(BrokerRequest request)
    {
        this.request = request;
        handle = NativeMethods.OpenProcess(0x1000 | 0x100000, false, request.Process.Pid);
        if (handle.IsInvalid) { handle.Dispose(); throw new InvalidOperationException("Original game process could not be corroborated."); }
        try { VerifyIdentity(); FindWindow(); }
        catch { handle.Dispose(); throw; }
    }
    internal void VerifyIdentity()
    {
        if (NativeMethods.WaitForSingleObject(handle, 0) != 258) throw new InvalidOperationException("Owned game process has exited or is unverifiable.");
        if (!NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _) || request.Process.NativeStartToken != "windows:" + creation.Value)
            throw new InvalidOperationException("Original native process creation token mismatches.");
        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        if (!NativeMethods.QueryFullProcessImageName(handle, 0, path, ref length) || !BoundedJson.SamePath(path.ToString(), request.Process.ExecutablePath)) throw new InvalidOperationException("Original executable identity mismatches.");
        WindowsIdentityProbe.RequireMatch(request.Identity, WindowsIdentityProbe.Read(handle.DangerousGetHandle()));
        WindowsIdentityProbe.RequireMatch(request.Identity, WindowsIdentityProbe.Read(NativeMethods.GetCurrentProcess()));
        WindowsIdentityProbe.RequireInteractiveDesktop(request.Identity.SessionId);
        var drive = Path.GetPathRoot(request.GameRoot)!.TrimEnd('\\');
        var device = new StringBuilder(32768);
        if (NativeMethods.QueryDosDevice(drive, device, device.Capacity) == 0 || !device.ToString().StartsWith("\\Device\\HarddiskVolume", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Mapped or nonphysical game drive is not admitted.");
    }
    private void FindWindow()
    {
        var candidates = new List<IntPtr>();
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var pid);
            if (pid == request.Process.Pid && NativeMethods.IsWindowVisible(window) && NativeMethods.GetClientRect(window, out var rect) && rect.Width >= 320 && rect.Height >= 240) candidates.Add(window);
            return true;
        }, IntPtr.Zero);
        if (candidates.Count != 1) throw new InvalidOperationException("One visible game client window is required.");
        Window = candidates[0];
    }
    internal NativeMethods.Rect RequireForeground(int width, int height, int dpi)
    {
        VerifyIdentity();
        NativeMethods.GetWindowThreadProcessId(Window, out var pid);
        if (pid != request.Process.Pid || NativeMethods.GetForegroundWindow() != Window || !NativeMethods.IsWindowVisible(Window) || NativeMethods.IsIconic(Window)) throw new InvalidOperationException("Original game window lost foreground or visibility.");
        if (!NativeMethods.GetClientRect(Window, out var rect) || rect.Width != width || rect.Height != height || NativeMethods.GetDpiForWindow(Window) != dpi) throw new InvalidOperationException("Admitted client geometry or DPI changed.");
        return rect;
    }
    public void Dispose() => handle.Dispose();
}
