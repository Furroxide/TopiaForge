using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class RuntimeLaunchCommandTests
    {
        internal static void Run(string root)
        {
            var failures = new List<Exception>();
            foreach (var mode in new[] { "menu-correlation", "profile-drift", "revision-drift", "package-drift", "target-correlation" })
            {
                try { Check(root + "/" + mode, mode); Console.WriteLine("Runtime launch command " + mode + ": PASS"); }
                catch (Exception error) { failures.Add(error); Console.WriteLine("Runtime launch command " + mode + ": FAIL " + error.Message); }
            }
            if (failures.Count != 0) throw new AggregateException("Runtime launch command regressions failed.", failures);
        }
        private static void Check(string root, string mode)
        {
            using var fixture = new SessionFixture(root);
            var identities = fixture.Profile.Packages.Select(package => package.Identity).ToList();
            if (mode == "package-drift") identities.Add(new PackageIdentity("missing.mod", "1.0.0"));
            var target = mode == "target-correlation";
            var request = new ProfileLaunchConfigurationV4(mode == "profile-drift" ? "other-profile" : fixture.Profile.ProfileId,
                fixture.Profile.Revision + (mode == "revision-drift" ? 1 : 0), "explicit-request", target ? "launch-target" : "main-menu",
                identities, PackageSetDigest.Of(identities), false, false, identities.Select(package => package.Id), new Dictionary<string, string>(),
                target ? fixture.Plan.Descriptor : null);
            var operation = fixture.Hosted.ExecuteCommandAsync(request);
            fixture.Wait(operation);
            var expectedSuccess = mode == "menu-correlation" || target;
            Assert(operation.Result.Succeeded == expectedSuccess, "Profile/revision/package drift must fail before native work.");
            Assert(fixture.Outcomes.Count == 1 && fixture.Outcomes[0].RequestId == "explicit-request"
                && fixture.Outcomes[0].Command == request.Command, "The runtime must acknowledge the original request and command exactly once.");
            if (!expectedSuccess) Assert(fixture.MenuLoads == 0 && fixture.Outcomes[0].Blocks.Any(block => block.Code == LaunchBlockCode.PlanPackageSetMismatch),
                "A rejected snapshot needs structured package-set evidence without loading a scene.");
            if (target) Assert(fixture.Outcomes[0].Phase == "running", "Target success requires Running, not process or preparation success.");
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
