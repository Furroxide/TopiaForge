using System.Runtime.InteropServices;

namespace TopiaForge.Acceptance.Windows;

internal static class ProvisioningFirewall
{
    internal sealed record Rule(bool Enabled, int Direction, int Action, int Profiles, int Protocol,
        string Application, string LocalAddresses, string RemoteAddresses, string LocalPorts, string RemotePorts,
        string InterfaceTypes, string Service, bool AllProfilesEnabled);
    internal static void Validate(Rule rule, string executable)
    {
        static bool Any(string value) => string.IsNullOrEmpty(value) || value == "*";
        if (!rule.Enabled || rule.Direction != 2 || rule.Action != 0 || rule.Profiles != int.MaxValue
            || rule.Protocol != 256 || !BoundedJson.SamePath(rule.Application, executable)
            || !Any(rule.LocalAddresses) || !Any(rule.RemoteAddresses) || !Any(rule.LocalPorts) || !Any(rule.RemotePorts)
            || rule.InterfaceTypes != "All" || !string.IsNullOrEmpty(rule.Service) || !rule.AllProfilesEnabled)
            throw new InvalidDataException("Exact unrestricted outbound-block rule with enabled firewall profiles is required.");
    }
    private static void RequireRunningService(string name)
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new InvalidOperationException("Filtering service state is unavailable.");
        try
        {
            var service = OpenService(manager, name, 4);
            if (service == IntPtr.Zero) throw new InvalidOperationException("Filtering service state is unavailable.");
            try
            {
                if (!QueryServiceStatus(service, out var status) || status.CurrentState != 4)
                    throw new InvalidOperationException("Required Windows filtering service is not running.");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    {
        internal uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string service, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    internal static void Require(string name, string executable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        RequireRunningService("BFE"); RequireRunningService("MpsSvc");
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new InvalidOperationException("Firewall policy is unavailable.");
        object? policyObject = null, rulesObject = null, ruleObject = null;
        try
        {
            dynamic policy = policyObject = Activator.CreateInstance(type)!;
            dynamic rules = rulesObject = policy.Rules;
            dynamic rule = ruleObject = rules.Item(name);
            bool profilesEnabled = policy.FirewallEnabled[1] && policy.FirewallEnabled[2] && policy.FirewallEnabled[4];
            Validate(new((bool)rule.Enabled, (int)rule.Direction, (int)rule.Action, (int)rule.Profiles, (int)rule.Protocol,
                (string)rule.ApplicationName, (string)rule.LocalAddresses, (string)rule.RemoteAddresses,
                (string)rule.LocalPorts, (string)rule.RemotePorts, (string)rule.InterfaceTypes,
                (string)rule.ServiceName, profilesEnabled), executable);
            // Restricting an interface list would leave other outbound routes uncovered.
            object? interfaces = rule.Interfaces;
            if (interfaces is Array array && array.Length != 0) throw new InvalidDataException("Firewall rule is restricted to selected interfaces.");
            if (interfaces != null && interfaces is not Array) throw new InvalidDataException("Firewall interface scope cannot be verified.");
        }
        finally
        {
            if (ruleObject != null) Marshal.FinalReleaseComObject(ruleObject);
            if (rulesObject != null) Marshal.FinalReleaseComObject(rulesObject);
            if (policyObject != null) Marshal.FinalReleaseComObject(policyObject);
        }
    }
}
