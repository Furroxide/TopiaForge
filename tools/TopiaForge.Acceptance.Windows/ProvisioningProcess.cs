using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TopiaForge.Acceptance.Windows;

/// Original CreateProcess handles own all resume/exit/termination actions; no PID reopening.
internal sealed class ProvisioningProcess : IDisposable
{
    private readonly SafeProcessHandle process;
    private readonly SafeWaitHandle thread;
    private readonly SafeFileHandle job;
    internal ProcessStamp Stamp { get; private set; }
    private ProvisioningProcess(SafeProcessHandle process, SafeWaitHandle thread, SafeFileHandle job, ProcessStamp stamp)
    { this.process = process; this.thread = thread; this.job = job; Stamp = stamp; }
    internal static ProvisioningProcess StartSuspended(string executable, string gameRoot, string requestId)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables()) environment[(string)item.Key] = (string)item.Value!;
        foreach (var forbidden in new[] { "TOPIAFORGE_LAUNCH_PROFILE", "TOPIAFORGE_ACCEPTANCE_REQUEST_ID", "TOPIAFORGE_PROVISIONING_PROBE_ID" })
            if (environment.ContainsKey(forbidden)) throw new InvalidDataException("Conflicting inherited launch mode.");
        environment.Add("TOPIAFORGE_PROVISIONING_PROBE_ID", requestId);
        var block = Marshal.StringToHGlobalUni(string.Join('\0', environment.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); Marshal.FreeHGlobal(block); throw new InvalidOperationException("Provisioning job could not be created."); }
        SafeProcessHandle? process = null; SafeWaitHandle? thread = null;
        IntPtr attributes = IntPtr.Zero, jobValue = IntPtr.Zero;
        var attributesInitialized = false;
        try
        {
            var limits = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 | 0x8, ActiveProcessLimit = 1 } };
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimit>())) throw new InvalidOperationException("Provisioning job limits could not be set.");
            nuint attributeBytes = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
            if (attributeBytes == 0 || attributeBytes > 65536) throw new InvalidOperationException("Provisioning startup attributes unavailable.");
            attributes = Marshal.AllocHGlobal(checked((int)attributeBytes));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeBytes)) throw new InvalidOperationException("Provisioning startup attributes failed.");
            attributesInitialized = true;
            jobValue = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobValue, job.DangerousGetHandle());
            // PROC_THREAD_ATTRIBUTE_JOB_LIST assigns ownership atomically at creation, before any child can exist unowned.
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x0002000D, jobValue, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("Atomic provisioning job assignment unavailable.");
            var startup = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Desktop = @"winsta0\default", Flags = 1, ShowWindow = 0 }, Attributes = attributes };
            // No command interpreter, arbitrary arguments, inherited handles or interactive input.
            if (!CreateProcess(executable, new StringBuilder("\"" + executable + "\" -batchmode -nographics -noaudio"),
                IntPtr.Zero, IntPtr.Zero, false, 0x4 | 0x400 | 0x80000, block, gameRoot, ref startup, out var information))
                throw new InvalidOperationException("Suspended provisioning process could not be created.");
            process = new SafeProcessHandle(information.Process, true);
            thread = new SafeWaitHandle(information.Thread, true);
            return new(process, thread, job, new(checked((int)information.Pid), "", executable));
        }
        catch
        {
            if (process != null && !process.IsInvalid)
            {
                TerminateProcess(process, 1);
                NativeMethods.WaitForSingleObject(process, 15000);
            }
            thread?.Dispose(); process?.Dispose(); job.Dispose(); throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero) { if (attributesInitialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (jobValue != IntPtr.Zero) Marshal.FreeHGlobal(jobValue);
            Marshal.FreeHGlobal(block);
        }
    }
    internal void Prepare(IdentityStamp identity)
    {
        if (!IsProcessInJob(process, job, out var assigned) || !assigned) throw new InvalidOperationException("Original process job ownership differs.");
        WindowsIdentityProbe.RequireMatch(identity, WindowsIdentityProbe.Read(process.DangerousGetHandle()));
        var path = new StringBuilder(32768); uint capacity = (uint)path.Capacity;
        if (!NativeMethods.QueryFullProcessImageName(process, 0, path, ref capacity) || !BoundedJson.SamePath(path.ToString(), Stamp.ExecutablePath)
            || !NativeMethods.GetProcessTimes(process, out var created, out _, out _, out _))
            throw new InvalidOperationException("Original suspended process identity could not be verified.");
        Stamp = Stamp with { NativeStartToken = "windows:" + created.Value };
    }
    internal void Resume(IdentityStamp identity)
    {
        WindowsIdentityProbe.RequireMatch(identity, WindowsIdentityProbe.Read(process.DangerousGetHandle()));
        if (NativeMethods.WaitForSingleObject(process, 0) != 258 || ResumeThread(thread) != 1)
            throw new InvalidOperationException("Original suspended process could not resume exactly once.");
    }
    internal bool HasExited => NativeMethods.WaitForSingleObject(process, 0) switch
    { 0 => true, 258 => false, _ => throw new InvalidOperationException("Original process liveness unavailable.") };
    internal bool TerminateAndConfirm()
    {
        if (HasExited) return true;
        if (!TerminateProcess(process, 1)) return HasExited;
        return NativeMethods.WaitForSingleObject(process, 15000) == 0;
    }
    public void Dispose()
    {
        // Closing this job terminates only its originally assigned, single allowed process.
        job.Dispose(); thread.Dispose(); process.Dispose();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size; internal string? Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedBytes; internal IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process, Thread; internal uint Pid, Tid; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    {
        internal long PerProcessUserTime, PerJobUserTime; internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet; internal uint ActiveProcessLimit;
        internal UIntPtr Affinity; internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
    {
        internal BasicLimit Basic; internal IoCounters Io;
        internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimit limits, int length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool assigned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
}
