using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class VersionUtilTests
    {
        public static void Run()
        {
            TestBepInExPluginVersion();
            TestStrictSemanticVersions();
            TestPrereleasePrecedence();
            TestRangeParity();
        }

        private static void TestBepInExPluginVersion()
        {
            Assert(System.Version.TryParse(TopiaForgeVersions.BepInExPluginVersion, out var pluginVersion),
                "BepInEx plugin metadata must use a System.Version-compatible value");
            Assert(VersionUtil.TryParse(TopiaForgeVersions.LoaderVersion, out var loaderVersion),
                "the TopiaForge loader version must remain valid SemVer");
            Assert(pluginVersion == loaderVersion,
                "the BepInEx plugin version must match the core of the semantic loader version");
        }

        private static void TestStrictSemanticVersions()
        {
            Assert(VersionUtil.TryParse("1.2.3-alpha.1-rc+build.001.sha-abcdef", out var core),
                "valid prerelease and build identifiers should parse");
            Assert(core == new System.Version(1, 2, 3),
                "the legacy System.Version surface should retain the strict SemVer core");

            foreach (var valid in new[]
                     {
                         "0.0.0-0",
                         "1.0.0-alpha.01a",
                         "1.0.0+001",
                         "2147483647.2147483647.2147483647",
                         "999999999999999999999999999999.888888888888888888888888888888.777777777777777777777777777777"
                     })
            {
                Assert(VersionUtil.TryParse(valid, out _), "strict semantic version should parse: " + valid);
            }

            foreach (var invalid in new[]
                     {
                         "1",
                         "1.0",
                         "01.0.0",
                         "1.01.0",
                         "1.0.01",
                         "1.0.0-",
                         "1.0.0+",
                         "1.0.0-01",
                         "1.0.0-alpha.01",
                         "1.0.0-alpha..1",
                         "1.0.0+build..1",
                         "1.0.0-alpha_1",
                         "1.0.0+build_1",
                         "1.0.0-alpha+build+again",
                         "1.2.3.4",
                         "1.2.3-bad!",
                         "v1.0.0",
                         " 1.0.0",
                         "1.0.0 "
                     })
            {
                Assert(!VersionUtil.TryParse(invalid, out _),
                    "malformed or out-of-range semantic version should be rejected: " + invalid);
            }
        }

        private static void TestPrereleasePrecedence()
        {
            var precedence = new[]
            {
                "1.0.0-alpha",
                "1.0.0-alpha.1",
                "1.0.0-alpha.beta",
                "1.0.0-beta",
                "1.0.0-beta.2",
                "1.0.0-beta.11",
                "1.0.0-rc.1",
                "1.0.0"
            };
            for (var index = 0; index < precedence.Length - 1; index++)
            {
                var lower = precedence[index];
                var higher = precedence[index + 1];
                Assert(VersionUtil.AllowsRange(higher, ">" + lower),
                    lower + " must precede " + higher);
                Assert(!VersionUtil.IsAtLeast(lower, higher),
                    lower + " must not satisfy an at-least check for " + higher);
                Assert(VersionUtil.IsAtLeast(higher, lower),
                    higher + " must satisfy an at-least check for " + lower);
            }

            var hugeNumeric = "1.0.0-999999999999999999999999999999";
            var largerNumeric = "1.0.0-1000000000000000000000000000000";
            Assert(VersionUtil.AllowsRange(largerNumeric, ">" + hugeNumeric),
                "numeric prerelease identifiers must compare without integer overflow");
            Assert(VersionUtil.AllowsRange("1.0.0-alpha", ">1.0.0-10"),
                "numeric prerelease identifiers must precede non-numeric identifiers");

            Assert(VersionUtil.AllowsRange("1.2.3-rc.1+build.2", "=1.2.3-rc.1+build.1"),
                "build metadata should be ignored by exact range precedence equality");
            Assert(VersionUtil.IsAtLeast("1.2.3+build.1", "1.2.3+build.2") &&
                   VersionUtil.IsAtLeast("1.2.3+build.2", "1.2.3+build.1"),
                "build metadata should be ignored for at-least precedence");

            var hugeCore = "999999999999999999999999999999.0.0";
            Assert(VersionUtil.IsAtLeast(hugeCore, "2147483648.999999999999999999.999999999999999999")
                   && VersionUtil.AllowsRange(hugeCore, ">2147483648.0.0"),
                "SemVer core precedence must compare arbitrary-length numeric identifiers without Int32 caps");
        }

        private static void TestRangeParity()
        {
            Assert(!VersionUtil.TryParseRange("1") && !VersionUtil.TryParseRange("1.2") &&
                   !VersionUtil.TryParseRange(">=1 <2.1"),
                "range versions must use canonical three-component SemVer");

            Assert(VersionUtil.TryParseRange(">=1.0.0 <2.0.0"), "valid comparator range should parse");
            Assert(VersionUtil.AllowsRange("1.5.0", ">=1.0.0 <2.0.0"), "valid comparator range should allow matching version");
            Assert(!VersionUtil.AllowsRange("2.0.0", ">=1.0.0 <2.0.0"), "valid comparator range should reject upper bound");
            Assert(!VersionUtil.AllowsRange("2.0.0", ">2.0.0 >=1.0.0"),
                "a weaker later lower bound must not erase the stronger bound's exclusivity");
            Assert(VersionUtil.AllowsRange("2.0.1", ">2.0.0 >=1.0.0"),
                "a version above the strongest exclusive lower bound should pass");
            Assert(VersionUtil.AllowsRange("2.0.0", "<=2.0.0 <3.0.0"),
                "a weaker later upper bound must not erase the stronger bound's inclusivity");
            Assert(VersionUtil.TryParseRange("1.2.x") && VersionUtil.AllowsRange("1.2.99", "1.2.x"),
                "ordinary wildcard ranges should remain supported");
            Assert(VersionUtil.AllowsRange("1.0.0-alpha.10", ">=1.0.0-alpha.2 <1.0.0") &&
                   !VersionUtil.AllowsRange("1.0.0-alpha.1", ">=1.0.0-alpha.2 <1.0.0"),
                "ranges should use numeric prerelease precedence");
            Assert(VersionUtil.TryParseRange(">=1.2.3-alpha.1+ci.7 <2.0.0-rc.1"),
                "range bounds should retain valid prerelease and build syntax");
            Assert(VersionUtil.AllowsRange("1.0.0-x.2", ">=1.0.0-x.1 <1.0.0") &&
                   VersionUtil.AllowsRange("1.0.0+linux.x64", "=1.0.0+windows.x64"),
                "x characters inside prerelease/build identifiers must not be mistaken for wildcard components");

            Assert(VersionUtil.TryParseRange("999999999999999999999.x")
                   && VersionUtil.AllowsRange(
                       "999999999999999999999.42.7",
                       "999999999999999999999.x"),
                "wildcard bounds must increment arbitrary-length SemVer identifiers");
            Assert(VersionUtil.TryParseRange("1.999999999999999999999.x")
                   && VersionUtil.AllowsRange(
                       "1.999999999999999999999.42",
                       "1.999999999999999999999.x"),
                "minor wildcard bounds must not inherit Int32 limits");

            foreach (var invalidWildcard in new[] { "1.x.2" })
            {
                Assert(!VersionUtil.TryParseRange(invalidWildcard),
                    "wildcard range should reject overflowing or structurally invalid input: " + invalidWildcard);
                Assert(!VersionUtil.AllowsRange("1.2.3", invalidWildcard),
                    "wildcard matching should be total for invalid input: " + invalidWildcard);
            }

            foreach (var invalid in new[]
                     {
                         ">=1.0.0 garbage",
                         "garbage >=1.0.0",
                         ">=1.0.0 <2.0.0 trailing",
                         ">=1.0.0<2.0.0",
                         ">2.0.0 <=2.0.0",
                         ">=2.0.0 <2.0.0",
                         "=1.0.0 =2.0.0"
                     })
            {
                Assert(!VersionUtil.TryParseRange(invalid), "range parser should reject unconsumed text: " + invalid);
                Assert(!VersionUtil.AllowsRange("1.5.0", invalid), "range matcher should reject unconsumed text: " + invalid);
            }

            foreach (var invalid in new[]
                     {
                         "01.x",
                         "1.01.x",
                         ">=01",
                         ">=1.0.0-01",
                         "1.0.0+build_1",
                         ">=1.0.0-alpha..1",
                         "1..x"
                     })
            {
                Assert(!VersionUtil.TryParseRange(invalid),
                    "range parser should reject malformed SemVer components: " + invalid);
            }

            Assert(VersionUtil.TryParseRange(">=1.0.0 =2.0.0 <3.0.0") &&
                   VersionUtil.AllowsRange("2.0.0+different-build", ">=1.0.0 =2.0.0+ci.1 <3.0.0"),
                "exact constraints should intersect and ignore build metadata for precedence equality");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new System.InvalidOperationException(message);
            }
        }
    }
}
