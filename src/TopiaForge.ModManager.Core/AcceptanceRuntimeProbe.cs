using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Read-only private QA probe; primary token and OS known folders never come from environment overrides.</summary>
    internal static class AcceptanceRuntimeProbe
    {
        internal static string Read(string gameRoot, string bepInExRoot, string managerRoot, string persistentDataRoot)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("Isolated RC1 acceptance requires Windows.");
            var process = GetCurrentProcess();
            if (!OpenProcessToken(process, 0x0008 | 0x0004, out var token)) throw NativeFailure();
            try
            {
                var sid = Information(token, 1, IntPtr.Size, pointer =>
                {
                    if (!ConvertSidToStringSidW(Marshal.ReadIntPtr(pointer), out var text)) throw NativeFailure();
                    try { return Marshal.PtrToStringUni(text) ?? throw new InvalidDataException("Native SID is empty."); }
                    finally { LocalFree(text); }
                });
                var logon = Information(token, 10, 56, pointer => unchecked((uint)Marshal.ReadInt32(pointer, 12)).ToString("x8", CultureInfo.InvariantCulture)
                    + unchecked((uint)Marshal.ReadInt32(pointer, 8)).ToString("x8", CultureInfo.InvariantCulture));
                var session = Information(token, 12, 4, pointer => checked((int)unchecked((uint)Marshal.ReadInt32(pointer))));
                var userProfile = Folder(token, new Guid("5e6c858f-0e22-4760-9afe-ea3317b67173"));
                var low = Folder(token, new Guid("a520a1a4-1780-4ff6-bd18-167343c5af16"));
                if (!GetProcessTimes(process, out var creation, out _, out _, out _) || creation <= 0) throw NativeFailure();
                var executable = new StringBuilder(32768); var capacity = executable.Capacity;
                if (!QueryFullProcessImageNameW(process, 0, executable, ref capacity) || capacity <= 0 || capacity >= executable.Capacity) throw NativeFailure();
                var processJson = AcceptanceIsolationJson.Object(("pid", GetCurrentProcessId().ToString(CultureInfo.InvariantCulture)),
                    ("nativeStartToken", Q("windows:" + creation.ToString(CultureInfo.InvariantCulture))), ("executablePath", Q(Path.GetFullPath(executable.ToString()))));
                var identity = AcceptanceIsolationJson.Object(("userSid", Q(sid)), ("logonId", Q(logon)), ("sessionId", session.ToString(CultureInfo.InvariantCulture)),
                    ("userProfile", Q(userProfile)), ("localAppDataLow", Q(low)));
                var roots = AcceptanceIsolationJson.Object(("gameRoot", Q(Path.GetFullPath(gameRoot))), ("bepInExRoot", Q(Path.GetFullPath(bepInExRoot))),
                    ("managerRoot", Q(Path.GetFullPath(managerRoot))), ("persistentDataRoot", Q(Path.GetFullPath(persistentDataRoot))));
                return AcceptanceIsolationJson.Object(("process", processJson), ("observedOsIdentity", identity), ("observedRoots", roots));
            }
            finally { CloseHandle(token); }
        }
        private static string Q(string value) => AcceptanceIsolationJson.Quote(value);
        private static T Information<T>(IntPtr token, int kind, int minimum, Func<IntPtr, T> read)
        {
            GetTokenInformation(token, kind, IntPtr.Zero, 0, out var length);
            if (length < minimum || length > 65536) throw new InvalidDataException("Native token size cannot be verified.");
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, kind, buffer, length, out var actual) || actual < minimum || actual > length) throw NativeFailure();
                return read(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        private static string Folder(IntPtr token, Guid folder)
        {
            var result = SHGetKnownFolderPath(ref folder, 0, token, out var pointer);
            try
            {
                if (result != 0) Marshal.ThrowExceptionForHR(result);
                var path = Marshal.PtrToStringUni(pointer);
                if (string.IsNullOrEmpty(path)) throw new InvalidDataException("Native known folder is empty.");
                return AcceptanceIsolationJson.LocalPath(path!);
            }
            finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
        }
        private static Win32Exception NativeFailure() => new Win32Exception(Marshal.GetLastWin32Error(), "Acceptance native identity could not be verified.");
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder value, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
        [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, int size, out int required);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
    }
}
