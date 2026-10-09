using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Read-only metadata for the explicitly logged-in QA account. No input/capture.
public static class SandboxQaHostDesktop
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Bounds, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public IntPtr Handle; public uint Type; }
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int info, out IntPtr data, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int kind, StringBuilder text, int capacity, out int needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceList([Out] RawDevice[] devices, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint count);

    public static bool IsReady()
    {
        IntPtr state;
        int bytes;
        using (var process = Process.GetCurrentProcess())
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, process.SessionId, 8, out state, out bytes)) return false;
        }
        try { if (bytes < 4 || Marshal.ReadInt32(state) != 0) return false; }
        finally { WTSFreeMemory(state); }
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(256);
            int needed;
            return GetUserObjectInformation(desktop, 2, name, 512, out needed) &&
                string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    public static Dictionary<string, object> Describe()
    {
        if (!IsReady()) throw new InvalidOperationException("QA desktop is not active and unlocked.");
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero) throw new InvalidOperationException("Cannot measure physical display geometry.");
        try
        {
            var monitor = MonitorFromPoint(new Point(), 1);
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)), Device = "" };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || (info.Flags & 1) == 0)
                throw new InvalidOperationException("Primary display was not identified.");
            uint dpiX, dpiY;
            if (GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) != 0 || dpiX < 96 || dpiX > 768 || dpiY != dpiX)
                throw new InvalidOperationException("Display DPI could not be measured.");
            uint count = 0;
            var size = (uint)Marshal.SizeOf(typeof(RawDevice));
            if (GetRawInputDeviceList(null, ref count, size) == uint.MaxValue || count > 256)
                throw new InvalidOperationException("Input-device inventory is unavailable or unbounded.");
            var devices = new RawDevice[count];
            var used = GetRawInputDeviceList(devices, ref count, size);
            if (used == uint.MaxValue || used > devices.Length) throw new InvalidOperationException("Input devices changed during measurement.");
            var inputs = new List<Dictionary<string, object>>();
            for (var index = 0; index < used; index++)
            {
                if (devices[index].Type > 1) continue;
                uint length = 0;
                if (GetRawInputDeviceInfo(devices[index].Handle, 0x20000007, null, ref length) == uint.MaxValue || length == 0 || length > 4096)
                    throw new InvalidOperationException("Input-device name is unavailable or unbounded.");
                var name = new StringBuilder((int)length + 1);
                if (GetRawInputDeviceInfo(devices[index].Handle, 0x20000007, name, ref length) == uint.MaxValue || name.Length == 0)
                    throw new InvalidOperationException("Input-device name disappeared.");
                inputs.Add(new Dictionary<string, object> { { "kind", devices[index].Type == 0 ? "mouse" : "keyboard" }, { "deviceName", name.ToString() } });
            }
            if (!IsReady()) throw new InvalidOperationException("QA desktop changed during measurement.");
            return new Dictionary<string, object>
            {
                { "display", new Dictionary<string, object> { { "deviceName", info.Device }, { "width", info.Bounds.Right - info.Bounds.Left }, { "height", info.Bounds.Bottom - info.Bounds.Top }, { "dpi", dpiX } } },
                { "inputDevices", inputs }, { "inputSent", false }, { "screenCaptured", false }
            };
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
}
