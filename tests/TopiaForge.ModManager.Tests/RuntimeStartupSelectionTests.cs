using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class RuntimeStartupSelectionTests
    {
        internal static void Run()
        {
            var identities = new[] { new PackageIdentity("alpha.mod", "1.0.0"), new PackageIdentity("beta.mod", "2.0.0") };
            var descriptor = new LaunchPlanDescriptor("alpha.mod.target", "alpha.mod.mode", "alpha.mod.world", "additive-arena",
                new LaunchRequest("alpha.mod.target"), identities);
            var request = new ProfileLaunchConfigurationV4("original-profile", 7, "original-request", "launch-target", identities,
                PackageSetDigest.Of(identities), false, false, new[] { "alpha.mod", "beta.mod" }, new Dictionary<string, string>(), descriptor);
            var failures = new List<Exception>();
            foreach (var recovery in new[] { new StartupRecoveryDecision(true, "", "ambiguous"),
                new StartupRecoveryDecision(false, "alpha.mod", "entry failure"), new StartupRecoveryDecision(false, "../invalid", "invalid quarantine") })
            {
                try
                {
                    var durable = new ManagerState
                    {
                        Mods = new List<InstalledModState> {
                        new InstalledModState { Id = "alpha.mod", Version = "1.0.0", Enabled = true },
                        new InstalledModState { Id = "beta.mod", Version = "2.0.0", Enabled = true } }
                    };
                    var selection = StartupRecoveryPolicy.Prepare(request, durable, recovery, DateTime.UtcNow);
                    var effective = selection.CreateEffectiveState(durable);
                    Assert(!effective.Find("alpha.mod")!.Enabled, "Recovery must override the exact wire's enabled alpha owner.");
                    Assert(effective.Find("beta.mod")!.Enabled == (recovery.QuarantineModId == "alpha.mod"), "Only a precise quarantine may retain unrelated packages.");
                    Assert(ReferenceEquals(selection.Requested, request) && ReferenceEquals(selection.Requested.Plan, descriptor)
                        && selection.Requested.RequestId == "original-request" && selection.Requested.Packages.Count == 2 && !selection.Requested.SafeMode,
                        "Recovery must retain the original command identity, digest and plan for a correlated failure.");
                    Assert(durable.Find("beta.mod")!.Enabled, "Temporary safe mode must preserve unrelated durable enablement.");
                    if (recovery.QuarantineModId == "alpha.mod")
                        Assert(!durable.Find("alpha.mod")!.Enabled && durable.Find("alpha.mod")!.QuarantineReason == "entry failure", "Precise quarantine evidence must remain durable.");
                }
                catch (Exception error) { failures.Add(error); }
            }
            var direct = StartupRecoveryPolicy.Prepare(null, new ManagerState(), new StartupRecoveryDecision(true, "", "ambiguous"), DateTime.UtcNow);
            if (direct.Requested != null || !direct.SafeMode) failures.Add(new InvalidOperationException("Direct recovery must not invent a launcher request identity."));
            if (failures.Count != 0) throw new AggregateException("V4 startup recovery regressions failed.", failures);
            Console.WriteLine("V4 startup recovery: PASS");
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
