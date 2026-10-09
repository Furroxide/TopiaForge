using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TopiaForge.Acceptance.Windows;

internal static class WindowsIdentityProbe
{
    internal static IdentityStamp Read(IntPtr process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native acceptance requires Windows.");
        if (!NativeMethods.OpenProcessToken(process, 0x0008 | 0x0004, out var token)) throw new InvalidOperationException("Primary process token could not be opened.");
        using (token)
        {
            var sid = Information(token, 1, data =>
            {
                if (!NativeMethods.ConvertSidToStringSid(Marshal.ReadIntPtr(data), out var text)) throw new InvalidOperationException("Primary token SID could not be read.");
                try { return Marshal.PtrToStringUni(text) ?? throw new InvalidDataException("Empty SID."); }
                finally { NativeMethods.LocalFree(text); }
            });
            var logon = Information(token, 10, data => unchecked((ulong)Marshal.ReadInt64(data, 8)).ToString("x16"));
            var session = Information(token, 12, Marshal.ReadInt32);
            return new(sid, logon, session, Folder(token, "5e6c858f-0e22-4760-9afe-ea3317b67173"), Folder(token, "a520a1a4-1780-4ff6-bd18-167343c5af16"));
        }
    }
    private static T Information<T>(SafeAccessTokenHandle token, int kind, Func<IntPtr, T> convert)
    {
        NativeMethods.GetTokenInformation(token, kind, IntPtr.Zero, 0, out var needed);
        if (needed < 4 || needed > 65536) throw new InvalidDataException("Token data bound is invalid.");
        var data = Marshal.AllocHGlobal(needed);
        try
        {
            if (!NativeMethods.GetTokenInformation(token, kind, data, needed, out var used) || used > needed) throw new InvalidOperationException("Token data could not be read.");
            return convert(data);
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    private static string Folder(SafeAccessTokenHandle token, string id)
    {
        var guid = new Guid(id);
        if (NativeMethods.SHGetKnownFolderPath(ref guid, 0, token, out var path) != 0) throw new InvalidOperationException("Native known folder is unavailable; sign in normally first.");
        try { return BoundedJson.PhysicalPath(Marshal.PtrToStringUni(path) ?? throw new InvalidDataException("Known folder absent.")); }
        finally { Marshal.FreeCoTaskMem(path); }
    }
    internal static void RequireMatch(IdentityStamp expected, IdentityStamp actual)
    {
        if (expected.UserSid != actual.UserSid || expected.LogonId != actual.LogonId || expected.SessionId != actual.SessionId || !BoundedJson.SamePath(expected.UserProfile, actual.UserProfile) || !BoundedJson.SamePath(expected.LocalAppDataLow, actual.LocalAppDataLow))
            throw new InvalidOperationException("The primary-token identity or native known folders changed.");
    }
    internal static void RequireInteractiveDesktop(int session)
    {
        if (!NativeMethods.WTSQuerySessionInformation(IntPtr.Zero, session, 8, out var state, out var size)) throw new InvalidOperationException("Session state is unavailable.");
        try { if (size < 4 || Marshal.ReadInt32(state) != 0) throw new InvalidOperationException("QA session is disconnected or inactive."); }
        finally { NativeMethods.WTSFreeMemory(state); }
        var desktop = NativeMethods.OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) throw new InvalidOperationException("Input desktop is inaccessible or locked.");
        try
        {
            var name = new StringBuilder(256);
            if (!NativeMethods.GetUserObjectInformation(desktop, 2, name, 512, out _) || !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The default interactive desktop is not active.");
        }
        finally { NativeMethods.CloseDesktop(desktop); }
    }
}
