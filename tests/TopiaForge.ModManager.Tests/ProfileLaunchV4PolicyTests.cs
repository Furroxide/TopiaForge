using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class ProfileLaunchV4PolicyTests
    {
        internal static void Run()
        {
            var identities = new[] { new PackageIdentity("alpha.mod", "1.0.0") };
            foreach (var inherits in new[] { false, true })
            {
                var requested = new ProfileLaunchConfigurationV4("exact-profile", 8, "request", "main-menu", identities,
                    PackageSetDigest.Of(identities), false, inherits, new[] { "alpha.mod" }, new Dictionary<string, string>());
                var durable = new ManagerState
                {
                    Mods = new List<InstalledModState> {
                    new InstalledModState { Id = "alpha.mod", Version = "2.0.0", Enabled = true },
                    new InstalledModState { Id = "beta.mod", Version = "1.0.0", Enabled = true } }
                };
                var effective = requested.CreateEffectiveState(durable);
                Assert(effective.Find("alpha.mod")!.Version == "1.0.0" && effective.Find("alpha.mod")!.VersionPinned,
                    "Every V4 package identity must be temporarily exact even when the player did not pin it.");
                Assert(!effective.Find("beta.mod")!.Enabled, "Resolved inheritance must not admit newly enabled manager packages.");
                effective.Mods.Add(new InstalledModState { Id = "gamma.mod", Enabled = true });
                requested.ApplyTo(effective);
                Assert(!effective.Find("gamma.mod")!.Enabled, "Reapplying after discovery must retain the exact package set.");
                Assert(durable.Find("alpha.mod")!.Version == "2.0.0" && !durable.Find("alpha.mod")!.VersionPinned
                    && durable.Find("beta.mod")!.Enabled, "One-process exact choices must not overwrite durable manager choices.");
            }
            Console.WriteLine("V4 exact profile policy: PASS");
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
