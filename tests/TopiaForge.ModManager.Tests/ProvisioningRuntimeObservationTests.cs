using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class ProvisioningRuntimeObservationTests
    {
        internal static void Run()
        {
            var count = 0;
            void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); count++; }
            void Refuse(Action<Fixture> change, string? raw = null)
            {
                using var f = new Fixture(); change(f);
                if (raw != null) File.WriteAllText(f.RequestPath, raw);
                var rejected = false;
                try { f.Run(); } catch (Exception e) when (e is InvalidDataException || e is DecoderFallbackException || e is System.Runtime.Serialization.SerializationException) { rejected = true; }
                Check(rejected && !File.Exists(f.OutputPath), "Malformed or foreign provisioning must write no observation.");
            }
            using (var f = new Fixture())
            {
                var path = f.Run();
                using var result = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = result.RootElement;
                Check(root.GetProperty("observedRoots").GetProperty("persistentDataRoot").GetString() == f.Persistent,
                    "Observation must carry the actual runtime persistence path, absent from request.");
                Check(!root.GetProperty("isolationAdmitted").GetBoolean() && !root.GetProperty("qualifiesRelease").GetBoolean()
                    && !root.GetProperty("managerInitialized").GetBoolean(), "Provisioning cannot claim admission or manager initialization.");
                Check(Directory.GetFiles(f.Staging).Length == 2 && !File.Exists(Path.Combine(f.Manager, "state.json"))
                    && !Directory.Exists(Path.Combine(f.Manager, "packages")), "Only exact request and observation may exist.");
                var bytes = File.ReadAllBytes(path); var refused = false;
                try { f.Run(); } catch (InvalidDataException) { refused = true; }
                Check(refused && bytes.SequenceEqual(File.ReadAllBytes(path)), "Replay must preserve original bytes.");
            }
            foreach (var field in new[] { "schemaVersion", "kind", "requestId", "challenge", "gameRoot" })
                Refuse(f => { f.Request[field] = field == "schemaVersion" ? (object)2 : "bad"; f.Write(); });
            foreach (var value in new object?[] { null, "1", 1.5, true })
                Refuse(f => { f.Request["schemaVersion"] = value; f.Write(); });
            foreach (var field in new[] { "userSid", "logonId", "sessionId", "userProfile", "localAppDataLow" })
                Refuse(f => { f.ExpectedIdentity[field] = field == "sessionId" ? (object)3 : "wrong"; f.Write(); });
            foreach (var field in new[] { "pid", "nativeStartToken", "executablePath" })
                Refuse(f => { f.ExpectedProcess[field] = field == "pid" ? (object)4 : "wrong"; f.Write(); });
            Refuse(f => { f.Request["expiresAtUtc"] = f.Now.UtcDateTime.ToString("O"); f.Write(); });
            Refuse(f => { f.Request["issuedAtUtc"] = f.Now.UtcDateTime.AddSeconds(1).ToString("O"); f.Write(); });
            Refuse(f => { f.Request["expiresAtUtc"] = f.Now.UtcDateTime.AddSeconds(121).ToString("O"); f.Write(); });
            Refuse(f => { f.Request["issuedAtUtc"] = f.Now.ToString("O"); f.Write(); });
            Refuse(f => { f.Request["reviewerEvidence"] = "invented"; f.Write(); });
            Refuse(f => { f.Request["persistentDataRoot"] = f.Persistent; f.Write(); });
            Refuse(f => f.AcceptanceId = "acceptance");
            Refuse(f => f.ProfilePath = "profile");
            Refuse(f => f.Persistent = Path.Combine(f.Root.FullName, "normal", "save"));
            Refuse(f => f.ActualIdentity["sessionId"] = 7);
            Refuse(f => f.ActualProcess["nativeStartToken"] = "windows:888");
            Refuse(f => f.RewriteDuringObservation = true);
            Refuse(f => { var text = File.ReadAllText(f.RequestPath); File.WriteAllText(f.RequestPath, text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")); });
            Refuse(f => File.WriteAllText(f.RequestPath, new string('x', 65537)));
            Refuse(f => File.WriteAllBytes(f.RequestPath, new byte[] { 0xff, 0xff }));
            var lifetime = new PluginInitializationLifetime(); var effects = 0;
            lifetime.ObserveProvisioning(() => effects++);
            Check(effects == 1 && !lifetime.TryBeginTeardown(), "Provisioning owns no global teardown.");
            try { lifetime.Start(() => effects++, () => effects++, () => effects++); } catch (InvalidOperationException) { }
            Check(effects == 1, "Selected provisioning mode cannot subsequently initialize manager effects.");
            var refusedLifetime = new PluginInitializationLifetime();
            try { refusedLifetime.ObserveProvisioning(() => throw new InvalidDataException("seeded")); } catch (InvalidDataException) { }
            Check(!refusedLifetime.TryBeginTeardown(), "Failed observation owns no global teardown.");
            var normal = new PluginInitializationLifetime();
            normal.Start(() => effects++, () => effects++, () => effects++);
            Check(effects == 4 && normal.TryBeginTeardown(), "Unselected normal startup remains unchanged.");
            var deferred = new PluginInitializationLifetime(); var quits = 0;
            Check(!deferred.ProvisioningObservationRecorded && !deferred.ProvisioningQuitPending,
                "Normal unselected lifetime cannot request provisioning shutdown.");
            Check(!deferred.TryRequestProvisioningQuit(() => quits++) && quits == 0,
                "A tick before observation owns no shutdown effect.");
            deferred.ObserveProvisioning(() =>
            {
                Check(!deferred.TryRequestProvisioningQuit(() => quits++),
                    "Observation must finish before its deferred quit can be claimed.");
            });
            Check(deferred.ProvisioningObservationRecorded && deferred.ProvisioningQuitPending && quits == 0,
                "Successful observation arms a later tick without quitting during Awake.");
            Check(deferred.TryRequestProvisioningQuit(() =>
            {
                quits++;
                Check(!deferred.TryRequestProvisioningQuit(() => quits++),
                    "Reentrant tick cannot issue a second shutdown request.");
            }), "The first later tick should dispatch the quit request.");
            Check(quits == 1 && !deferred.ProvisioningQuitPending && deferred.ProvisioningObservationRecorded,
                "A request consumes only the pending quit, never claiming native exit.");
            for (var tick = 0; tick < 10; tick++)
                if (deferred.TryRequestProvisioningQuit(() => quits++)) throw new InvalidOperationException("Unexpected quit retry.");
            Check(quits == 1, "Repeated future ticks cannot replay a quit request.");
            Check(!refusedLifetime.ProvisioningObservationRecorded
                && !refusedLifetime.TryRequestProvisioningQuit(() => quits++) && quits == 1,
                "A refused observation must stay inert without scheduling shutdown.");
            Check(!normal.ProvisioningObservationRecorded && !normal.TryRequestProvisioningQuit(() => quits++) && quits == 1,
                "Admitted normal startup cannot acquire provisioning shutdown.");
            var throwingQuit = new PluginInitializationLifetime();
            throwingQuit.ObserveProvisioning(() => { });
            var threw = false;
            try { throwingQuit.TryRequestProvisioningQuit(() => throw new InvalidOperationException("seeded quit failure")); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw && !throwingQuit.ProvisioningQuitPending
                && !throwingQuit.TryRequestProvisioningQuit(() => quits++) && quits == 1,
                "A throwing quit request is consumed once; the original launcher deadline owns recovery.");
            var invalidQuit = new PluginInitializationLifetime();
            invalidQuit.ObserveProvisioning(() => { });
            var invalidRejected = false;
            try { invalidQuit.TryRequestProvisioningQuit(null!); } catch (ArgumentNullException) { invalidRejected = true; }
            Check(invalidRejected && invalidQuit.ProvisioningQuitPending, "Invalid callback cannot consume pending shutdown.");
            Check(invalidQuit.TryRequestProvisioningQuit(() => quits++) && quits == 2 && !invalidQuit.TryBeginTeardown(),
                "A deferred shutdown still owns no manager storage or global UI teardown.");
            Console.WriteLine("Provisioning runtime observation: " + count + " checks passed.");
        }

        private sealed class Fixture : IDisposable
        {
            private readonly AcceptanceIsolationTests.Fixture baseFixture = new AcceptanceIsolationTests.Fixture();
            internal DirectoryInfo Root => baseFixture.Root;
            internal string Game => baseFixture.Game;
            internal string Manager => Path.Combine(Game, "BepInEx", "TopiaForge");
            internal string Staging => Path.Combine(Manager, "staging");
            internal string Id = "probe-" + new string('a', 32);
            internal string RequestPath => Path.Combine(Staging, ProvisioningRuntimeObservation.RequestName(Id));
            internal string OutputPath => Path.Combine(Staging, ProvisioningRuntimeObservation.ObservationName(Id));
            internal DateTimeOffset Now = new DateTimeOffset(2026, 9, 9, 20, 0, 0, TimeSpan.Zero);
            internal string Persistent;
            internal string? AcceptanceId, ProfilePath;
            internal bool RewriteDuringObservation;
            internal Dictionary<string, object?> Request, ExpectedIdentity, ExpectedProcess, ActualIdentity, ActualProcess;
            internal Fixture()
            {
                Directory.CreateDirectory(Staging); Persistent = Path.Combine(baseFixture.Low, "ObservedPublisher", "ObservedGame");
                ActualIdentity = baseFixture.Identity(); ActualIdentity["userSid"] = "S-1-5-21-100-200-300-1001";
                ExpectedIdentity = new Dictionary<string, object?>(ActualIdentity);
                ActualProcess = new Dictionary<string, object?> { ["pid"] = 123, ["nativeStartToken"] = "windows:1234", ["executablePath"] = Path.Combine(Game, "Robotopia.exe") };
                ExpectedProcess = new Dictionary<string, object?>(ActualProcess);
                Request = new Dictionary<string, object?> { ["schemaVersion"] = 1, ["kind"] = "sandbox-provisioning-request-v1",
                    ["requestId"] = Id, ["challenge"] = new string('b', 64), ["issuedAtUtc"] = Now.UtcDateTime.ToString("O"),
                    ["expiresAtUtc"] = Now.UtcDateTime.AddSeconds(120).ToString("O"), ["expectedIdentity"] = ExpectedIdentity,
                    ["expectedProcess"] = ExpectedProcess, ["gameRoot"] = Game };
                Write();
            }
            internal void Write() => File.WriteAllText(RequestPath, JsonSerializer.Serialize(Request));
            internal string Run() => ProvisioningRuntimeObservation.Observe(Id, AcceptanceId, ProfilePath, Game,
                Path.Combine(Game, "BepInEx"), Manager, () =>
                {
                    if (RewriteDuringObservation) File.AppendAllText(RequestPath, " ");
                    var roots = baseFixture.Roots(); roots["persistentDataRoot"] = Persistent;
                    return JsonSerializer.Serialize(new { process = ActualProcess, observedOsIdentity = ActualIdentity, observedRoots = roots });
                }, Now);
            public void Dispose() => baseFixture.Dispose();
        }
    }
}
