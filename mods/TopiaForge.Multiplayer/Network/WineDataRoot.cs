using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>Converts Unix launcher paths inside the game's active Wine prefix, without assuming a drive mapping.</summary>
    internal static class WineDataRoot
    {
        internal static bool IsWindowsAbsolute(string? path) => !string.IsNullOrEmpty(path) &&
            path!.IndexOf('\0') < 0 &&
            ((path.Length >= 3 && IsDriveLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) ||
             (path.StartsWith("\\\\", StringComparison.Ordinal) && path.Length > 2));

        private static bool IsDriveLetter(char value) => (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

        internal static string? Translate(string unixPath)
        {
            if (unixPath.IndexOf('\0') >= 0) return null;
            IntPtr converted = IntPtr.Zero;
            try
            {
                // Wine takes UTF-8 and returns an allocated UTF-16 path in this prefix's DOS namespace.
                // The export does not exist on native Windows: an accidental Unix override fails closed there.
                converted = wine_get_dos_file_name(Encoding.UTF8.GetBytes(unixPath + '\0'));
                return converted == IntPtr.Zero ? null : Marshal.PtrToStringUni(converted);
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            finally
            {
                if (converted != IntPtr.Zero) HeapFree(GetProcessHeap(), 0, converted);
            }
        }

        [DllImport("kernel32.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr wine_get_dos_file_name(byte[] unixPath);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetProcessHeap();

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);
    }
}
