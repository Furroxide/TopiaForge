using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TopiaForge.Acceptance.Windows;

/// Read-only observations of the retained original process during a bounded wait. Nothing here
/// duplicates handles into the target, injects code, suspends threads, closes sockets or terminates.
/// A snapshot is diagnostic evidence about a stall; it never establishes exit, admission or success.
internal static class ProvisioningDiagnostics
{
    private const int MaximumRows = 4096;
    internal static object Snapshot(ProvisioningProcess process, string phase, long elapsedMilliseconds) =>
        Snapshot(process.Handle, process.Pid, process.PlayerLogPath, phase, elapsedMilliseconds, process.HasExited, process.ExitCode);
    internal static object Snapshot(SafeProcessHandle handle, int pid, string? playerLogPath, string phase, long elapsedMilliseconds, bool exited, int? exitCode)
    {
        var notes = new List<string>();
        object? Attempt(Func<object> read, string name)
        {
            try { return read(); }
            catch (Exception error) { notes.Add(name + "-unavailable:" + error.GetType().Name); return null; }
        }
        return new
        {
            phase, elapsedMilliseconds, capturedAtUtc = DateTime.UtcNow.ToString("O"), alive = !exited, exitCode,
            cpu = Attempt(() => Cpu(handle), "process-times"),
            handleCount = Attempt(() => (object)Handles(handle), "handle-count"),
            memory = Attempt(() => Memory(handle), "memory"),
            threads = Attempt(() => Threads(pid), "threads"),
            windows = Attempt(() => Windows(pid), "windows"),
            tcp = Attempt(() => Tcp(pid), "tcp-table"),
            udp = Attempt(() => Udp(pid), "udp-table"),
            playerLog = playerLogPath == null ? null : Attempt(() => PlayerLog(playerLogPath), "player-log"),
            notes
        };
    }
    private static object Cpu(SafeProcessHandle handle)
    {
        if (!NativeMethods.GetProcessTimes(handle, out _, out _, out var kernel, out var user)) throw new InvalidOperationException("Process times unavailable.");
        return new { kernelMilliseconds = kernel.Value / 10000, userMilliseconds = user.Value / 10000 };
    }
    private static uint Handles(SafeProcessHandle handle)
    {
        if (!NativeMethods.GetProcessHandleCount(handle, out var count)) throw new InvalidOperationException("Handle count unavailable.");
        return count;
    }
    private static object Memory(SafeProcessHandle handle)
    {
        var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        if (!GetProcessMemoryInfo(handle, ref counters, counters.Size)) throw new InvalidOperationException("Memory counters unavailable.");
        return new { workingSetBytes = (ulong)counters.WorkingSetSize, peakWorkingSetBytes = (ulong)counters.PeakWorkingSetSize,
            pagefileBytes = (ulong)counters.PagefileUsage, pageFaults = counters.PageFaultCount };
    }
    private static object Threads(int pid)
    {
        using var snapshot = CreateToolhelp32Snapshot(0x4, 0);
        if (snapshot.IsInvalid) throw new InvalidOperationException("Thread snapshot unavailable.");
        var entry = new ThreadEntry { Size = (uint)Marshal.SizeOf<ThreadEntry>() };
        var count = 0; var ids = new List<uint>();
        if (Thread32First(snapshot, ref entry))
        {
            do
            {
                if (entry.OwnerProcessId != (uint)pid) continue;
                count++;
                if (ids.Count < 256) ids.Add(entry.ThreadId);
            } while (Thread32Next(snapshot, ref entry));
        }
        return new { count, ids };
    }
    private static object Windows(int pid)
    {
        var total = 0; var visible = 0;
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var owner);
            if (owner == (uint)pid) { total++; if (NativeMethods.IsWindowVisible(window)) visible++; }
            return true;
        }, IntPtr.Zero);
        return new { total, visible };
    }
    internal static int Port(int networkOrderLowWord) => ((networkOrderLowWord & 0xff) << 8) | ((networkOrderLowWord >> 8) & 0xff);
    internal static string TcpState(int state) => state switch
    {
        1 => "CLOSED", 2 => "LISTEN", 3 => "SYN_SENT", 4 => "SYN_RCVD", 5 => "ESTABLISHED", 6 => "FIN_WAIT1", 7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT", 9 => "CLOSING", 10 => "LAST_ACK", 11 => "TIME_WAIT", 12 => "DELETE_TCB", _ => "UNKNOWN_" + state
    };
    private static object Tcp(int pid)
    {
        var rows = new List<object>();
        Table(2, 5, 24, true, (row, family) =>
        {
            if (Marshal.ReadInt32(row, 20) != pid || rows.Count >= MaximumRows) return;
            rows.Add(new { family, state = TcpState(Marshal.ReadInt32(row, 0)), localPort = Port(Marshal.ReadInt32(row, 8)),
                remoteAddress = new IPAddress((uint)Marshal.ReadInt32(row, 12)).ToString(), remotePort = Port(Marshal.ReadInt32(row, 16)) });
        });
        Table(23, 5, 56, true, (row, family) =>
        {
            if (Marshal.ReadInt32(row, 52) != pid || rows.Count >= MaximumRows) return;
            var remote = new byte[16]; Marshal.Copy(row + 24, remote, 0, 16);
            rows.Add(new { family, state = TcpState(Marshal.ReadInt32(row, 48)), localPort = Port(Marshal.ReadInt32(row, 20)),
                remoteAddress = new IPAddress(remote).ToString(), remotePort = Port(Marshal.ReadInt32(row, 44)) });
        });
        return new { count = rows.Count, rows };
    }
    private static object Udp(int pid)
    {
        var rows = new List<object>();
        Table(2, 1, 12, false, (row, family) => { if (Marshal.ReadInt32(row, 8) == pid && rows.Count < MaximumRows) rows.Add(new { family, localPort = Port(Marshal.ReadInt32(row, 4)) }); });
        Table(23, 1, 28, false, (row, family) => { if (Marshal.ReadInt32(row, 24) == pid && rows.Count < MaximumRows) rows.Add(new { family, localPort = Port(Marshal.ReadInt32(row, 20)) }); });
        return new { count = rows.Count, rows };
    }
    private static void Table(int family, int tableClass, int rowSize, bool tcp, Action<IntPtr, string> visit)
    {
        var size = 0;
        var status = tcp ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, tableClass, 0) : GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, tableClass, 0);
        if (status != 122 || size < 4 || size > 64 * 1024 * 1024) throw new InvalidOperationException("Endpoint table size unavailable.");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = tcp ? GetExtendedTcpTable(buffer, ref size, false, family, tableClass, 0) : GetExtendedUdpTable(buffer, ref size, false, family, tableClass, 0);
            if (status != 0) throw new InvalidOperationException("Endpoint table unavailable.");
            var entries = Marshal.ReadInt32(buffer);
            if (entries < 0 || entries > 262144 || 4L + (long)entries * rowSize > size) throw new InvalidOperationException("Endpoint table is malformed.");
            for (var index = 0; index < entries; index++) visit(buffer + 4 + index * rowSize, family == 2 ? "ipv4" : "ipv6");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static object PlayerLog(string path)
    {
        var file = new FileInfo(path);
        return file.Exists
            ? new { exists = true, bytes = file.Length, lastWriteUtc = (string?)file.LastWriteTimeUtc.ToString("O") }
            : new { exists = false, bytes = 0L, lastWriteUtc = (string?)null };
    }
    [StructLayout(LayoutKind.Sequential)] private struct ThreadEntry { internal uint Size, Usage, ThreadId, OwnerProcessId; internal int BasePriority, DeltaPriority; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryCounters
    {
        internal uint Size, PageFaultCount; internal UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Thread32First(SafeFileHandle snapshot, ref ThreadEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Thread32Next(SafeFileHandle snapshot, ref ThreadEntry entry);
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref MemoryCounters counters, uint size);
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
