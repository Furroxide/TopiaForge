using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class LaunchStagingTests
    {
        internal static void Run()
        {
            var failures = new List<Exception>();
            foreach (var test in new (string Name, Action Run)[] {
                ("filename-correlation", FilenameCorrelation), ("leaf-link", LeafLink),
                ("staging-ancestor-link", () => AncestorLink("TopiaForge")),
                ("bepinex-link", () => AncestorLink("BepInEx")), ("owned-consumption", OwnedConsumption), ("strict-rejection-correlation", RejectionCorrelation), ("atomic-interruption", AtomicInterruption), ("publication-link", PublicationLink) })
            {
                try { test.Run(); Console.WriteLine("Launch staging " + test.Name + ": PASS"); }
                catch (Exception error) { failures.Add(error); Console.WriteLine("Launch staging " + test.Name + ": FAIL " + error.Message); }
            }
            if (failures.Count != 0) throw new AggregateException("Launch staging regressions failed.", failures);
        }
        private static ProfileLaunchConfigurationV4 Request(string id = "Request-A") => new ProfileLaunchConfigurationV4(
            "Profile-A", 7, id, "main-menu", Array.Empty<PackageIdentity>(), PackageSetDigest.Of(Array.Empty<PackageIdentity>()),
            false, false, Array.Empty<string>(), new Dictionary<string, string>());
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Reject(Action operation, string message)
        {
            try { operation(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException(message);
        }
        private static void FilenameCorrelation()
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeStagingCorrelation-");
            try
            {
                var paths = new ManagerPaths(owned.CreateSubdirectory("game").CreateSubdirectory("BepInEx").FullName);
                paths.EnsureCreated();
                var store = new LaunchStagingStore(paths);
                var path = store.RequestPath("Different-Request");
                File.WriteAllText(path, LaunchTransportJson.WriteProfile(Request()));
                Reject(() => store.ConsumeRequest(path), "A request must not consume a different request's filename.");
                Assert(File.Exists(path), "Rejected foreign input must remain untouched.");
            }
            finally { owned.Delete(true); }
        }
        private static void LeafLink()
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeStagingLeaf-");
            FileSystemInfo? link = null;
            try
            {
                var paths = new ManagerPaths(owned.CreateSubdirectory("game").CreateSubdirectory("BepInEx").FullName);
                paths.EnsureCreated();
                var store = new LaunchStagingStore(paths);
                var outside = Path.Combine(owned.FullName, "outside.json");
                File.WriteAllText(outside, LaunchTransportJson.WriteProfile(Request()));
                link = File.CreateSymbolicLink(store.RequestPath("Request-A"), outside);
                Reject(() => store.ConsumeRequest(link.FullName), "A request symlink must not be read or deleted.");
                Assert(File.Exists(outside), "A foreign target must survive rejection.");
            }
            finally { if (link != null && File.Exists(link.FullName)) link.Delete(); owned.Delete(true); }
        }
        private static void AncestorLink(string segment)
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeStagingAncestor-");
            FileSystemInfo? link = null;
            try
            {
                var game = owned.CreateSubdirectory("game");
                var outside = owned.CreateSubdirectory("outside");
                var bepinex = Path.Combine(game.FullName, "BepInEx");
                var linkPath = segment == "BepInEx" ? bepinex : Path.Combine(Directory.CreateDirectory(bepinex).FullName, "TopiaForge");
                link = Directory.CreateSymbolicLink(linkPath, outside.FullName);
                var paths = new ManagerPaths(bepinex);
                var outsideStaging = segment == "BepInEx" ? outside.CreateSubdirectory("TopiaForge").CreateSubdirectory("staging") : outside.CreateSubdirectory("staging");
                var filename = "launch-profile-" + LaunchStorageKeys.Request("Request-A") + ".json";
                var outsideFile = Path.Combine(outsideStaging.FullName, filename);
                File.WriteAllText(outsideFile, LaunchTransportJson.WriteProfile(Request()));
                Reject(() => new LaunchStagingStore(paths).ConsumeRequest(Path.Combine(paths.Staging, filename)),
                    "A linked " + segment + " ancestor must be rejected before transport access.");
                Assert(File.Exists(outsideFile), "Rejected ancestor escape must not delete the outside request.");
            }
            finally { link?.Delete(); owned.Delete(true); }
        }
        private static void RejectionCorrelation()
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeStagingReject-");
            try
            {
                var paths = new ManagerPaths(owned.CreateSubdirectory("game").CreateSubdirectory("BepInEx").FullName); paths.EnsureCreated();
                var store = new LaunchStagingStore(paths); var path = store.RequestPath("Request-A");
                var malformed = LaunchTransportJson.WriteProfile(Request()).Replace("\"packages\":[]", "\"packages\":42");
                File.WriteAllText(path, malformed);
                Reject(() => store.ConsumeRequest(path), "Malformed V4 must be rejected.");
                Assert(store.TryReadCorrelation(path)?.RequestId == "Request-A" && File.Exists(path), "A strictly matching header can receive a failure while malformed input is retained.");
                foreach (var raw in new[] { malformed.Replace("\"schemaVersion\":4", "\"schemaVersion\":3"),
                    malformed.Replace("\"requestId\":\"Request-A\"", "\"requestId\":\"Request-A\",\"requestId\":\"Request-A\""),
                    malformed.Replace("Request-A", "Request-a") })
                {
                    File.WriteAllText(path, raw);
                    Assert(store.TryReadCorrelation(path) == null, "Wrong version, duplicate headers and another ordinal request cannot produce an acknowledgement.");
                }
            }
            finally { owned.Delete(true); }
        }
        private static void AtomicInterruption()
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeStagingAtomic-");
            try
            {
                var paths = new ManagerPaths(owned.CreateSubdirectory("game").CreateSubdirectory("BepInEx").FullName); paths.EnsureCreated();
                var store = new LaunchStagingStore(paths); store.WriteProgress(new LaunchProgress("Request-A", 1, "preparing"));
                var path = store.ProgressPath("Request-A"); var previous = File.ReadAllText(path);
                var interrupted = new LaunchStagingStore(paths, () => throw new IOException("injected before commit"));
                try { interrupted.WriteProgress(new LaunchProgress("Request-A", 2, "loading-world")); throw new Exception("Expected interruption."); }
                catch (IOException error) { Assert(error.Message == "injected before commit", "Primary atomic failure survives cleanup."); }
                Assert(File.ReadAllText(path) == previous && Directory.GetFiles(paths.Staging).Length == 1,
                    "An interrupted atomic write must preserve the prior publication and leave no backup or temporary record.");
                store.WriteProgress(new LaunchProgress("Request-A", 3, "starting-mode"));
                Assert(LaunchTransportJson.ReadProgress(File.ReadAllText(path)).Sequence == 3 && !File.Exists(path + ".bak"), "Successful replacement remains atomic without a backup record.");
            }
            finally { owned.Delete(true); }
        }
        private static void PublicationLink()
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeStagingWriteLink-"); FileSystemInfo? link = null;
            try
            {
                var paths = new ManagerPaths(owned.CreateSubdirectory("game").CreateSubdirectory("BepInEx").FullName); paths.EnsureCreated();
                var store = new LaunchStagingStore(paths); var outside = Path.Combine(owned.FullName, "foreign.json"); File.WriteAllText(outside, "foreign");
                link = File.CreateSymbolicLink(store.ProgressPath("Request-A"), outside);
                Reject(() => store.WriteProgress(new LaunchProgress("Request-A", 1, "preparing")), "Publication must not replace a linked leaf.");
                Assert(File.ReadAllText(outside) == "foreign", "Publication must preserve foreign linked data.");
            }
            finally { link?.Delete(); owned.Delete(true); }
        }
        private static void OwnedConsumption()
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeStagingOwned-");
            try
            {
                var paths = new ManagerPaths(owned.CreateSubdirectory("game").CreateSubdirectory("BepInEx").FullName);
                paths.EnsureCreated();
                var store = new LaunchStagingStore(paths);
                var path = store.RequestPath("Request-A");
                var unrelated = store.RequestPath("Request-a");
                Assert(!string.Equals(path, unrelated, StringComparison.OrdinalIgnoreCase), "Ordinal request IDs must not alias on Windows.");
                File.WriteAllText(path, LaunchTransportJson.WriteProfile(Request()));
                File.WriteAllText(unrelated, LaunchTransportJson.WriteProfile(Request("Request-a")));
                Assert(store.ConsumeRequest(path).RequestId == "Request-A", "The owned V4 command must be preserved.");
                Assert(!File.Exists(path) && File.Exists(unrelated), "Consumption must delete exactly its validated request.");
            }
            finally { owned.Delete(true); }
        }
    }
}
