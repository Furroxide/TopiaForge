using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

internal static class ProvisioningLauncher
{
    internal static int Run(string inputPath)
    {
        var input = ProvisioningLaunch.Read(inputPath);
        WindowsIdentityProbe.RequireMatch(input.Identity, WindowsIdentityProbe.Read(NativeMethods.GetCurrentProcess()));
        WindowsIdentityProbe.RequireInteractiveDesktop(input.Identity.SessionId);
        ProvisioningFirewall.Require(input.OutboundBlockRuleName, input.Executable);
        input.VerifyFiles();
        using var inputLease = new ProvisioningInputLease(input);
        using var mutex = new Mutex(false, @"Global\TopiaForgeSandboxAcceptanceV1");
        var ownsMutex = false;
        var recoveryPath = BoundedJson.Child(input.OutputRoot, "sandbox-recovery-required.json");
        try
        {
            try { ownsMutex = mutex.WaitOne(0); }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
                if (!File.Exists(recoveryPath)) BoundedJson.WriteNew(recoveryPath, new { schemaVersion = 1, kind = "sandbox-provisioning-recovery-v1", reason = "abandoned-mutex", isolationAdmitted = false, qualifiesRelease = false });
                throw new InvalidOperationException("Prior workstation ownership was abandoned; operator recovery is required.");
            }
            if (!ownsMutex) throw new InvalidOperationException("Another Editor/game lane owns this workstation.");
            if (File.Exists(recoveryPath) || Directory.Exists(recoveryPath)) throw new InvalidOperationException("Existing recovery marker blocks provisioning.");
            var id = "probe-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var challenge = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var runRoot = BoundedJson.Child(input.OutputRoot, id);
            if (File.Exists(runRoot) || Directory.Exists(runRoot)) throw new InvalidOperationException("Provisioning output must be new.");
            Directory.CreateDirectory(runRoot);
            BoundedJson.WriteNew(recoveryPath, new { schemaVersion = 1, kind = "sandbox-provisioning-recovery-v1", requestId = id, challenge, isolationAdmitted = false, qualifiesRelease = false });
            var recoverySha = BoundedJson.Hash(recoveryPath);
            var outcome = Execute(input, id, challenge, runRoot);
            BoundedJson.WriteNew(Path.Combine(runRoot, "provisioning-result.json"), outcome);
            if (outcome.OriginalProcessExitConfirmed && BoundedJson.Hash(recoveryPath) == recoverySha)
                File.Delete(recoveryPath); // Only this exact marker, after confirmed original exit.
            return outcome.Status == "observed" ? 0 : 1;
        }
        finally { if (ownsMutex) mutex.ReleaseMutex(); }
    }

    private static ProvisioningResult Execute(ProvisioningLaunch input, string id, string challenge, string runRoot)
    {
        var timer = Stopwatch.StartNew();
        var interrupted = 0;
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; Interlocked.Exchange(ref interrupted, 1); };
        Console.CancelKeyPress += cancel;
        ProvisioningProcess? process = null;
        var processStarted = false; var originalExit = false; var forced = false;
        var failures = new List<string>(); JsonElement? observation = null;
        string? observationSha = null; ProcessStamp? stamp = null;
        var requestPath = Path.Combine(input.ManagerRoot, "staging", "provisioning-request-" + id + ".json");
        var observationPath = Path.Combine(input.ManagerRoot, "staging", "provisioning-observation-" + id + ".json");
        try
        {
            // This is fresh provisioning storage only; no manager state/package/config tree is allowed.
            if (Directory.Exists(input.ManagerRoot)) throw new InvalidDataException("Provisioning needs an unused manager-state root.");
            Directory.CreateDirectory(Path.GetDirectoryName(requestPath)!);
            WindowsIdentityProbe.RequireMatch(input.Identity, WindowsIdentityProbe.Read(NativeMethods.GetCurrentProcess()));
            WindowsIdentityProbe.RequireInteractiveDesktop(input.Identity.SessionId);
            ProvisioningFirewall.Require(input.OutboundBlockRuleName, input.Executable);
            process = ProvisioningProcess.StartSuspended(input.Executable, input.GameRoot, id);
            processStarted = true;
            process.Prepare(input.Identity);
            stamp = process.Stamp;
            var issued = DateTime.UtcNow;
            BoundedJson.WriteNew(requestPath, new
            {
                schemaVersion = 1, kind = "sandbox-provisioning-request-v1", requestId = id, challenge,
                issuedAtUtc = issued.ToString("O"), expiresAtUtc = issued.AddSeconds(120).ToString("O"),
                expectedIdentity = input.Identity, expectedProcess = stamp, gameRoot = input.GameRoot
            });
            var requestSha = BoundedJson.Hash(requestPath);
            using var requestLease = new FileStream(requestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            BoundedJson.WriteNew(Path.Combine(runRoot, "launch-receipt.json"), new
            {
                schemaVersion = 1, kind = "sandbox-provisioning-owned-launch-v1", requestId = id, challenge,
                inputSha256 = input.InputSha256, requestSha256 = requestSha, process = stamp, identity = input.Identity,
                gameFiles = input.Files, outboundBlockRuleName = input.OutboundBlockRuleName, isolationAdmitted = false, qualifiesRelease = false
            });
            process.Resume(input.Identity);
            while (!process.HasExited && Volatile.Read(ref interrupted) == 0 && timer.Elapsed < TimeSpan.FromSeconds(90)) Thread.Sleep(50);
            if (Volatile.Read(ref interrupted) != 0) failures.Add("operator-interruption");
            if (!process.HasExited) { forced = true; failures.Add("original-process-timeout"); }
            originalExit = process.HasExited;
            if (!originalExit) failures.Add("original-process-exit-unconfirmed");
            // Read only after original exit; no partial-writer polling or final-file substitution.
            if (originalExit && !forced)
            {
                var bytes = BoundedJson.ReadBytes(observationPath, 65536);
                observation = BoundedJson.Parse(bytes, 65536);
                VerifyObservation(observation.Value, id, challenge, requestSha, stamp, input.Identity, input.GameRoot, issued, issued.AddSeconds(120));
                observationSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
                File.Copy(observationPath, Path.Combine(runRoot, "runtime-observation.json"), false);
                if (BoundedJson.Hash(Path.Combine(runRoot, "runtime-observation.json")) != observationSha)
                    throw new InvalidDataException("Provisioning observation changed while retaining its bytes.");
            }
            ProvisioningFirewall.Require(input.OutboundBlockRuleName, input.Executable);
            if (BoundedJson.Hash(input.InputPath) != input.InputSha256 || BoundedJson.Hash(requestPath) != requestSha)
                throw new InvalidDataException("Provisioning request/input changed during observation.");
        }
        catch (Exception error)
        {
            failures.Add("provisioning-refused-" + error.GetType().Name);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (process != null)
            {
                try
                {
                    if (!process.HasExited) forced = true;
                    originalExit = process.HasExited || process.TerminateAndConfirm();
                }
                catch { originalExit = false; failures.Add("original-process-cleanup-unconfirmed"); }
                process.Dispose();
            }
            else originalExit = !processStarted;
        }
        if (forced && !failures.Contains("original-process-timeout")) failures.Add("original-process-force-terminated");
        return new(1, "sandbox-provisioning-result-v1", id, challenge, input.InputSha256,
            observation != null && originalExit && !forced && failures.Count == 0 ? "observed" : "failed",
            DateTime.UtcNow, processStarted, stamp, originalExit, forced, observationSha,
            failures.AsReadOnly(), false, false);
    }

    internal static void VerifyObservation(JsonElement root, string id, string challenge, string requestSha,
        ProcessStamp process, IdentityStamp identity, string gameRoot, DateTime issuedAtUtc, DateTime expiresAtUtc)
    {
        BoundedJson.Keys(root, "schemaVersion", "kind", "requestId", "challenge", "requestSha256", "observedAtUtc",
            "process", "observedOsIdentity", "observedRoots", "managerInitialized", "isolationAdmitted", "qualifiesRelease");
        if (BoundedJson.Integer(root, "schemaVersion", 1, 1) != 1 || BoundedJson.Text(root, "kind") != "sandbox-provisioning-runtime-observation-v1"
            || BoundedJson.Text(root, "requestId") != id || BoundedJson.Digest(root, "challenge") != challenge
            || BoundedJson.Digest(root, "requestSha256") != requestSha)
            throw new InvalidDataException("Provisioning observation correlation differs.");
        foreach (var field in new[] { "managerInitialized", "isolationAdmitted", "qualifiesRelease" })
            if (root.GetProperty(field).ValueKind != JsonValueKind.False) throw new InvalidDataException("Provisioning never grants initialization or admission.");
        WindowsIdentityProbe.RequireMatch(identity, ProvisioningLaunch.ReadIdentity(root.GetProperty("observedOsIdentity")));
        var actualProcess = root.GetProperty("process");
        BoundedJson.Keys(actualProcess, "pid", "nativeStartToken", "executablePath");
        if (BoundedJson.Integer(actualProcess, "pid", 1, int.MaxValue) != process.Pid
            || BoundedJson.Text(actualProcess, "nativeStartToken") != process.NativeStartToken
            || !BoundedJson.SamePath(BoundedJson.Text(actualProcess, "executablePath"), process.ExecutablePath))
            throw new InvalidDataException("Provisioning observation is from another process.");
        var roots = root.GetProperty("observedRoots");
        BoundedJson.Keys(roots, "gameRoot", "bepInExRoot", "managerRoot", "persistentDataRoot");
        if (!BoundedJson.SamePath(BoundedJson.PhysicalPath(BoundedJson.Text(roots, "gameRoot")), gameRoot)
            || !BoundedJson.SamePath(BoundedJson.PhysicalPath(BoundedJson.Text(roots, "bepInExRoot")), Path.Combine(gameRoot, "BepInEx"))
            || !BoundedJson.SamePath(BoundedJson.PhysicalPath(BoundedJson.Text(roots, "managerRoot")), Path.Combine(gameRoot, "BepInEx", "TopiaForge"))
            || !ProvisioningLaunch.Within(BoundedJson.PhysicalPath(BoundedJson.Text(roots, "persistentDataRoot")), identity.LocalAppDataLow))
            throw new InvalidDataException("Measured provisioning persistence is outside the QA known folder.");
        if (!DateTime.TryParseExact(BoundedJson.Text(root, "observedAtUtc"), "yyyy-MM-ddTHH:mm:ss.fffffff'Z'",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var observedAtUtc)
            || observedAtUtc < issuedAtUtc || observedAtUtc > expiresAtUtc)
            throw new InvalidDataException("Provisioning observation time is not canonical UTC.");
    }
}
internal sealed record ProvisioningResult(int SchemaVersion, string Kind, string RequestId, string Challenge,
    string InputSha256, string Status, DateTime CompletedAtUtc, bool ProcessStarted, ProcessStamp? Process,
    bool OriginalProcessExitConfirmed, bool ForceTerminated, string? ObservationSha256, IReadOnlyList<string> Failures,
    bool IsolationAdmitted, bool QualifiesRelease);
