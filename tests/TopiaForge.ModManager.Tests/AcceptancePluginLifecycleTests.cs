using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TopiaForge.ModManager;

namespace TopiaForge.ModManager.Tests
{
    internal static class AcceptancePluginLifecycleTests
    {
        internal static void Run()
        {
            var failures = new List<Exception>();
            var cases = new Action[] { DeniedAdmissionIsInert, AdmissionPrecedesEveryEffect,
                StorageFailureDoesNotOwnUi, PartialUiStartupStillCleans, CleanupIsAttemptedOnce,
                NormalStartupTearsDownOnce };
            foreach (var test in cases)
            {
                try { test(); }
                catch (Exception error) { failures.Add(new InvalidOperationException(test.Method.Name, error)); }
            }
            if (failures.Count != 0) throw new AggregateException("Plugin lifecycle regressions failed.", failures);
            Console.WriteLine("Acceptance plugin lifecycle: " + cases.Length + " behavioral checks passed.");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void DeniedAdmissionIsInert()
        {
            using var f = new AcceptanceIsolationTests.Fixture();
            var (paths, store) = AcceptanceIsolationStagingTests.Prepare(f);
            var request = f.Request();
            request["profileRequestSha256"] = new string('f', 64);
            File.WriteAllText(store.AcceptanceRequestPath("Request-A"), JsonSerializer.Serialize(request));
            var lifetime = new PluginInitializationLifetime();
            var persisted = false;
            var globalUiShutdowns = 0;
            var denied = false;
            try
            {
                lifetime.Start(() => store.AdmitAcceptance("Request-A", store.RequestPath("Request-A"),
                    () => f.Observation, f.Now), () => persisted = true, paths.EnsureCreated);
            }
            catch (InvalidDataException) { denied = true; }
            if (lifetime.TryBeginTeardown()) lifetime.ShutdownUi(() => globalUiShutdowns++);
            Check(denied && !persisted && globalUiShutdowns == 0,
                "Denied admission must neither persist a Unity object nor shut down unrelated process UI.");
            Check(!Directory.Exists(paths.Logs) && !Directory.Exists(paths.Packages) && !File.Exists(paths.StateFile),
                "Denied admission must not initialize manager storage.");
            Check(File.ReadAllText(store.RequestPath("Request-A")) == f.Profile,
                "The denied one-shot profile must remain unchanged.");
        }

        private static void AdmissionPrecedesEveryEffect()
        {
            var lifetime = new PluginInitializationLifetime();
            var calls = new List<string>();
            lifetime.Start(() => calls.Add("admission"), () => calls.Add("persistence"), () => calls.Add("storage"));
            Check(string.Join(",", calls) == "admission,persistence,storage", "Admission must precede native and storage effects.");
        }

        private static void StorageFailureDoesNotOwnUi()
        {
            var lifetime = new PluginInitializationLifetime();
            var failure = new IOException("Injected storage initialization failure.");
            var shutdowns = 0;
            try { lifetime.Start(() => { }, () => { }, () => throw failure); }
            catch (IOException error) { Check(ReferenceEquals(error, failure), "Preserve storage failure."); }
            Check(lifetime.TryBeginTeardown(), "Partially initialized storage still owns its local cleanup.");
            lifetime.ShutdownUi(() => shutdowns++);
            Check(shutdowns == 0, "Storage failure before UI initialization cannot own global UI teardown.");
        }

        private static void PartialUiStartupStillCleans()
        {
            var lifetime = new PluginInitializationLifetime();
            lifetime.Start(() => { }, () => { }, () => { });
            var allocationCount = 0;
            var cleanupCount = 0;
            var failure = new InvalidOperationException("Injected mod/UI constructor failure after allocation.");
            try { lifetime.InitializeUi(() => { allocationCount++; throw failure; }); }
            catch (InvalidOperationException error) { Check(ReferenceEquals(error, failure), "Preserve initialization failure."); }
            Check(lifetime.TryBeginTeardown(), "A failed startup must retain its cleanup ownership.");
            lifetime.ShutdownUi(() => cleanupCount++);
            Check(allocationCount == 1 && cleanupCount == 1, "Partial UI initialization must clean before ever becoming ready.");
        }

        private static void CleanupIsAttemptedOnce()
        {
            var lifetime = new PluginInitializationLifetime();
            lifetime.Start(() => { }, () => { }, () => { });
            lifetime.InitializeUi(() => { });
            Check(lifetime.TryBeginTeardown(), "The initialized plugin owns teardown.");
            var calls = 0;
            var failure = new IOException("Injected UI cleanup failure.");
            try { lifetime.ShutdownUi(() => { calls++; throw failure; }); }
            catch (IOException error) { Check(ReferenceEquals(error, failure), "Preserve the cleanup failure for independent logging."); }
            lifetime.ShutdownUi(() => calls++);
            Check(calls == 1, "A throwing cleanup cannot release the same global ownership twice.");
        }

        private static void NormalStartupTearsDownOnce()
        {
            var lifetime = new PluginInitializationLifetime();
            lifetime.Start(() => { }, () => { }, () => { });
            lifetime.InitializeUi(() => { });
            Check(lifetime.TryBeginTeardown() && !lifetime.TryBeginTeardown(), "Repeated Unity destruction must not duplicate teardown.");
            var calls = 0;
            lifetime.ShutdownUi(() => calls++);
            lifetime.ShutdownUi(() => calls++);
            Check(calls == 1, "Normal startup releases its global UI ownership once.");
        }
    }
}
