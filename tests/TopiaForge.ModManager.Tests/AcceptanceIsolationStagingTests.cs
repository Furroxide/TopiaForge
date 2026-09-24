using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class AcceptanceIsolationStagingTests
    {
        internal static void Run()
        {
            SuccessAndReplay(); DenialPreservesRequest(); InvalidCorrelation(); ChangedProfile(); ProbeFailure(); AckFailureAfterConsumption(); RacingAckAfterConsumption(); NativeProbe();
            Console.WriteLine("Acceptance isolation staging: 8 checks passed.");
        }
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Expected isolated admission refusal."); }
        internal static (ManagerPaths Paths, LaunchStagingStore Store) Prepare(AcceptanceIsolationTests.Fixture f)
        {
            var paths = new ManagerPaths(Path.Combine(f.Game, "BepInEx"));
            Directory.CreateDirectory(paths.Staging); // Only the launcher-owned staging exists before manager admission.
            var store = new LaunchStagingStore(paths);
            File.WriteAllText(store.RequestPath("Request-A"), f.Profile, new UTF8Encoding(false));
            File.WriteAllText(store.AcceptanceRequestPath("Request-A"), JsonSerializer.Serialize(f.Request()), new UTF8Encoding(false));
            return (paths, store);
        }
        private static void NoManagerEffects(ManagerPaths paths) => Check(!Directory.Exists(paths.Logs) && !Directory.Exists(paths.Packages)
            && !File.Exists(paths.StateFile) && !File.Exists(paths.StartupJournalFile), "Admission must not provision normal manager state or packages.");
        private static void SuccessAndReplay()
        {
            using var f = new AcceptanceIsolationTests.Fixture(); var (paths, store) = Prepare(f);
            var request = store.AdmitAcceptance("Request-A", store.RequestPath("Request-A"), () => f.Observation, f.Now);
            Check(request.RequestId == "Request-A" && !File.Exists(store.RequestPath("Request-A")), "Admitted exact request consumed once.");
            var ack = File.ReadAllBytes(store.AcceptanceAckPath("Request-A"));
            using var json = JsonDocument.Parse(ack);
            Check(json.RootElement.GetProperty("status").GetString() == "admitted", "Admission ack must be explicit.");
            Check(json.RootElement.GetProperty("requestSha256").GetString() == AcceptanceIsolationTests.Fixture.Hash(File.ReadAllText(store.AcceptanceRequestPath("Request-A"))), "Ack binds exact private bytes.");
            Reject(() => store.AdmitAcceptance("Request-A", store.RequestPath("Request-A"), () => f.Observation, f.Now));
            Check(ack.AsSpan().SequenceEqual(File.ReadAllBytes(store.AcceptanceAckPath("Request-A"))), "Replay must not replace ack.");
            NoManagerEffects(paths);
        }
        private static void DenialPreservesRequest()
        {
            using var f = new AcceptanceIsolationTests.Fixture(); var (paths, store) = Prepare(f);
            var request = f.Request(); request["profileRequestSha256"] = new string('f', 64);
            File.WriteAllText(store.AcceptanceRequestPath("Request-A"), JsonSerializer.Serialize(request));
            Reject(() => store.AdmitAcceptance("Request-A", store.RequestPath("Request-A"), () => f.Observation, f.Now));
            Check(File.ReadAllText(store.RequestPath("Request-A")) == f.Profile, "Denied profile remains untouched.");
            using var ack = JsonDocument.Parse(File.ReadAllText(store.AcceptanceAckPath("Request-A")));
            Check(ack.RootElement.GetProperty("status").GetString() == "rejected", "Valid correlation may receive structured rejection.");
            NoManagerEffects(paths);
        }
        private static void InvalidCorrelation()
        {
            using var f = new AcceptanceIsolationTests.Fixture(); var (paths, store) = Prepare(f);
            Reject(() => store.AdmitAcceptance("Request-A", store.RequestPath("Foreign"), () => f.Observation, f.Now));
            var request = f.Request(); request["challenge"] = "bad";
            File.WriteAllText(store.AcceptanceRequestPath("Request-A"), JsonSerializer.Serialize(request));
            Reject(() => store.AdmitAcceptance("Request-A", store.RequestPath("Request-A"), () => f.Observation, f.Now));
            Check(!File.Exists(store.AcceptanceAckPath("Request-A")), "Malformed correlation cannot authorize an ack."); NoManagerEffects(paths);
        }
        private static void ChangedProfile()
        {
            using var f = new AcceptanceIsolationTests.Fixture(); var (paths, store) = Prepare(f);
            Reject(() => store.AdmitAcceptance("Request-A", store.RequestPath("Request-A"), () =>
            {
                File.AppendAllText(store.RequestPath("Request-A"), " "); return f.Observation;
            }, f.Now));
            Check(File.ReadAllText(store.RequestPath("Request-A")) == f.Profile + " ", "Changed profile must not be consumed.");
            Check(!File.Exists(store.AcceptanceAckPath("Request-A")), "Changed bytes cannot receive admitted acknowledgement."); NoManagerEffects(paths);
        }
        private static void ProbeFailure()
        {
            using var f = new AcceptanceIsolationTests.Fixture(); var (paths, store) = Prepare(f);
            Reject(() => store.AdmitAcceptance("Request-A", store.RequestPath("Request-A"), () => throw new InvalidDataException("Injected native probe failure."), f.Now));
            Check(!File.Exists(store.AcceptanceAckPath("Request-A")) && File.Exists(store.RequestPath("Request-A")), "Probe failure leaves request and writes nothing."); NoManagerEffects(paths);
        }
        private static void AckFailureAfterConsumption()
        {
            using var f = new AcceptanceIsolationTests.Fixture();
            var (paths, original) = Prepare(f);
            var sidecar = File.ReadAllBytes(original.AcceptanceRequestPath("Request-A"));
            var failure = new IOException("Injected failure after V4 consumption and before ACK commit.");
            var calls = 0;
            var store = new LaunchStagingStore(paths, () =>
            {
                calls++;
                Check(!File.Exists(original.RequestPath("Request-A")), "V4 must be consumed before the ACK publication hook.");
                Check(Directory.GetFiles(paths.Staging, "*.tmp-*").Length == 1, "The flushed ACK temporary must exist at the boundary.");
                throw failure;
            });
            try
            {
                store.AdmitAcceptance("Request-A", store.RequestPath("Request-A"), () => f.Observation, f.Now);
                throw new InvalidOperationException("ACK publication failure must reject startup.");
            }
            catch (IOException error) { Check(ReferenceEquals(error, failure), "Preserve the actual ACK publication failure."); }
            Check(calls == 1 && !File.Exists(store.AcceptanceAckPath("Request-A")), "Failed ACK commit must publish nothing.");
            AssertFailedAckState(paths, store, sidecar);
            try
            {
                store.AdmitAcceptance("Request-A", store.RequestPath("Request-A"), () => f.Observation, f.Now);
                throw new InvalidOperationException("Consumed request cannot be reused after publication failure.");
            }
            catch (IOException) { }
            Check(calls == 1 && !File.Exists(store.AcceptanceAckPath("Request-A")), "Retry requires a fresh request ID, not automatic profile restoration.");
            AssertFailedAckState(paths, store, sidecar);
            NoManagerEffects(paths);
        }

        private static void RacingAckAfterConsumption()
        {
            using var f = new AcceptanceIsolationTests.Fixture();
            var (paths, original) = Prepare(f);
            var sidecar = File.ReadAllBytes(original.AcceptanceRequestPath("Request-A"));
            var competingBytes = Encoding.UTF8.GetBytes("A competing writer owns these exact bytes.\n");
            var previousState = Encoding.UTF8.GetBytes("{\"unrecognized\":\"preserve these durable bytes\"}\n");
            File.WriteAllBytes(paths.StateFile, previousState);
            var calls = 0;
            var store = new LaunchStagingStore(paths, () =>
            {
                calls++;
                Check(!File.Exists(original.RequestPath("Request-A")), "A racing ACK arrives only after V4 consumption.");
                Check(Directory.GetFiles(paths.Staging, "*.tmp-*").Length == 1, "The ACK temporary must be flushed before the competing writer.");
                using var competing = new FileStream(original.AcceptanceAckPath("Request-A"), FileMode.CreateNew, FileAccess.Write);
                competing.Write(competingBytes);
            });
            try
            {
                store.AdmitAcceptance("Request-A", store.RequestPath("Request-A"), () => f.Observation, f.Now);
                throw new InvalidOperationException("A competing ACK must prevent admission publication.");
            }
            catch (IOException) { }
            Check(calls == 1 && competingBytes.SequenceEqual(File.ReadAllBytes(store.AcceptanceAckPath("Request-A"))),
                "A competing ACK must survive byte-for-byte; it cannot be overwritten or removed.");
            AssertFailedAckState(paths, store, sidecar);
            Check(previousState.SequenceEqual(File.ReadAllBytes(paths.StateFile)), "Competing ACK failure must not rewrite existing manager state.");
            Check(!Directory.Exists(paths.Logs) && !Directory.Exists(paths.Packages) && !File.Exists(paths.StartupJournalFile),
                "Competing ACK failure must not provision other manager storage.");
        }

        private static void AssertFailedAckState(ManagerPaths paths, LaunchStagingStore store, byte[] sidecar)
        {
            Check(!File.Exists(store.RequestPath("Request-A")), "Failed ACK publication never restores a consumed V4 request.");
            Check(sidecar.SequenceEqual(File.ReadAllBytes(store.AcceptanceRequestPath("Request-A"))), "Private admission request must remain unchanged.");
            Check(Directory.GetFiles(paths.Staging, "*.tmp-*").Length == 0, "Failed ACK publication must remove its owned temporary.");
        }

        private static void NativeProbe()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) { Console.WriteLine("Acceptance Windows identity probe: platform skip."); return; }
            using var f = new AcceptanceIsolationTests.Fixture();
            string Probe() => AcceptanceRuntimeProbe.Read(f.Game, Path.Combine(f.Game, "BepInEx"), Path.Combine(f.Game, "BepInEx", "TopiaForge"), Path.Combine(f.Low, "Publisher", "Game"));
            var before = Probe(); var old = Environment.GetEnvironmentVariable("USERPROFILE");
            try
            {
                Environment.SetEnvironmentVariable("USERPROFILE", f.Root.FullName);
                using var first = JsonDocument.Parse(before); using var second = JsonDocument.Parse(Probe());
                Check(first.RootElement.GetProperty("observedOsIdentity").GetRawText() == second.RootElement.GetProperty("observedOsIdentity").GetRawText(), "Environment spoof cannot change primary-token known folders.");
                var process = first.RootElement.GetProperty("process");
                Check(process.GetProperty("pid").GetInt32() == Environment.ProcessId, "Probe retains actual process identity.");
                using var current = Process.GetCurrentProcess();
                Check(process.GetProperty("nativeStartToken").GetString() == "windows:" + current.StartTime.ToUniversalTime().ToFileTimeUtc(), "Native creation token agrees with actual owned process.");
            }
            finally { Environment.SetEnvironmentVariable("USERPROFILE", old); }
        }
    }
}
