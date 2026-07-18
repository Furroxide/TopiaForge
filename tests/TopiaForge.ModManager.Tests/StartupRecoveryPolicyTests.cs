using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class StartupRecoveryPolicyTests
    {
        public static void Run()
        {
            TestAmbiguousRecoveryOverridesSuppliedProfile();
            TestAmbiguousRecoveryCreatesProfileWhenNoneWasSupplied();
            TestQuarantineOverridesExactProfileEnablement();
            TestCleanLaunchPreservesSuppliedProfile();
            Console.WriteLine("StartupRecoveryPolicyTests passed.");
        }

        private static void TestAmbiguousRecoveryOverridesSuppliedProfile()
        {
            var durable = State(
                Mod("alpha.mod", "2.0.0", enabled: true),
                Mod("beta.mod", "1.0.0", enabled: true));
            var requested = new ProfileLaunchConfiguration
            {
                SchemaVersion = ProfileLaunchConfiguration.CurrentSchemaVersion,
                ProfileId = "exact-profile",
                SafeMode = false,
                InheritManagerModState = false,
                EnabledMods = new List<string> { "alpha.mod" },
                SelectedVersions = new Dictionary<string, string>
                {
                    ["alpha.mod"] = "1.0.0"
                }
            };
            var recovery = new StartupRecoveryDecision(
                safeMode: true,
                quarantineModId: string.Empty,
                reason: "Previous startup did not complete.");

            var applied = StartupRecoveryPolicy.Apply(
                requested,
                durable,
                recovery,
                new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc));

            Assert(applied != null && applied.SafeMode,
                "ambiguous recovery must force safe mode over a supplied launcher profile");
            Assert(applied!.ProfileId == requested.ProfileId,
                "recovery should preserve the caller's profile identity");
            Assert(applied.EnabledMods.Count == 1 && applied.EnabledMods[0] == "alpha.mod",
                "safe-mode recovery should preserve caller selections for diagnostics and the next clean launch");
            Assert(applied.SelectedVersions["alpha.mod"] == "1.0.0",
                "safe-mode recovery should preserve caller version pins");
            Assert(!requested.SafeMode,
                "applying recovery must not mutate the caller-owned profile object");

            var effective = applied.CreateEffectiveState(durable);
            Assert(!effective.Find("alpha.mod")!.Enabled && !effective.Find("beta.mod")!.Enabled,
                "forced safe mode must disable every mod for the recovery process");
            Assert(durable.Find("alpha.mod")!.Enabled && durable.Find("beta.mod")!.Enabled,
                "ambiguous recovery must remain one-shot and leave durable enablement unchanged");
        }

        private static void TestAmbiguousRecoveryCreatesProfileWhenNoneWasSupplied()
        {
            var durable = State(Mod("alpha.mod", "1.0.0", enabled: true));
            var recovery = new StartupRecoveryDecision(
                safeMode: true,
                quarantineModId: string.Empty,
                reason: "Previous startup did not complete.");

            var applied = StartupRecoveryPolicy.Apply(
                null,
                durable,
                recovery,
                new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc));

            Assert(applied != null && applied.SafeMode,
                "ambiguous recovery without a launcher profile must create a safe-mode profile");
            Assert(applied!.ProfileId == "automatic-startup-recovery",
                "generated recovery profiles should retain the stable diagnostic identity");
            Assert(!applied.CreateEffectiveState(durable).Find("alpha.mod")!.Enabled,
                "generated recovery profile must disable installed mods");
        }

        private static void TestQuarantineOverridesExactProfileEnablement()
        {
            var durable = State(
                Mod("crashing.mod", "1.0.0", enabled: true),
                Mod("healthy.mod", "2.0.0", enabled: false));
            var requested = new ProfileLaunchConfiguration
            {
                SchemaVersion = ProfileLaunchConfiguration.CurrentSchemaVersion,
                ProfileId = "curated",
                InheritManagerModState = false,
                EnabledMods = new List<string> { "crashing.mod", "healthy.mod" },
                SelectedVersions = new Dictionary<string, string>
                {
                    ["crashing.mod"] = "1.0.0",
                    ["healthy.mod"] = "2.0.0"
                }
            };
            var recovery = new StartupRecoveryDecision(
                safeMode: false,
                quarantineModId: "crashing.mod",
                reason: "The previous process ended while this mod was loading.");
            var now = new DateTime(2026, 7, 15, 11, 12, 13, DateTimeKind.Utc);

            var applied = StartupRecoveryPolicy.Apply(requested, durable, recovery, now);

            Assert(applied != null && !applied.SafeMode,
                "precise blame should quarantine one owner instead of disabling unrelated mods");
            Assert(applied!.EnabledMods.Count == 1 && applied.EnabledMods[0] == "healthy.mod",
                "the quarantined owner must be removed from an exact profile's enabled set");
            Assert(applied.SelectedVersions.Count == 2 &&
                applied.SelectedVersions["crashing.mod"] == "1.0.0" &&
                applied.SelectedVersions["healthy.mod"] == "2.0.0",
                "quarantine should preserve exact version intent while overriding only enablement");
            Assert(requested.EnabledMods.Contains("crashing.mod"),
                "recovery must clone rather than mutate the supplied profile");

            var quarantined = durable.Find("crashing.mod")!;
            Assert(!quarantined.Enabled && !quarantined.RestartRequired,
                "quarantine must fail closed in durable manager state");
            Assert(quarantined.QuarantineReason == recovery.Reason,
                "quarantine should retain the startup-journal reason");
            Assert(quarantined.QuarantinedAtUtc == now.ToString("O"),
                "quarantine should retain its deterministic UTC timestamp");

            var effective = applied.CreateEffectiveState(durable);
            Assert(!effective.Find("crashing.mod")!.Enabled,
                "an exact profile must not re-enable the quarantined owner for the recovery launch");
            Assert(effective.Find("healthy.mod")!.Enabled,
                "unrelated exact-profile selections must continue loading after precise quarantine");
        }

        private static void TestCleanLaunchPreservesSuppliedProfile()
        {
            var durable = State(
                Mod("alpha.mod", "2.0.0", enabled: false),
                Mod("beta.mod", "1.0.0", enabled: true));
            var requested = new ProfileLaunchConfiguration
            {
                SchemaVersion = ProfileLaunchConfiguration.CurrentSchemaVersion,
                ProfileId = "normal",
                InheritManagerModState = false,
                EnabledMods = new List<string> { "alpha.mod" },
                SelectedVersions = new Dictionary<string, string>
                {
                    ["alpha.mod"] = "1.0.0"
                }
            };

            var applied = StartupRecoveryPolicy.Apply(
                requested,
                durable,
                StartupRecoveryDecision.None,
                new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc));

            Assert(ReferenceEquals(applied, requested),
                "a clean launch should use the supplied profile without rewriting it");
            var effective = applied!.CreateEffectiveState(durable);
            Assert(effective.Find("alpha.mod")!.Enabled &&
                effective.Find("alpha.mod")!.Version == "1.0.0" &&
                !effective.Find("beta.mod")!.Enabled,
                "normal profile selection must be unchanged when no recovery is required");
            Assert(!durable.Find("alpha.mod")!.Enabled && durable.Find("beta.mod")!.Enabled,
                "normal one-shot profile application must not mutate durable state");
            Assert(durable.Find("alpha.mod")!.QuarantineReason.Length == 0,
                "clean launches must not synthesize quarantine metadata");
        }

        private static ManagerState State(params InstalledModState[] mods)
        {
            return new ManagerState { Mods = new List<InstalledModState>(mods) };
        }

        private static InstalledModState Mod(string id, string version, bool enabled)
        {
            return new InstalledModState
            {
                Id = id,
                Version = version,
                Enabled = enabled
            };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Startup recovery policy test failed: " + message);
            }
        }
    }
}
