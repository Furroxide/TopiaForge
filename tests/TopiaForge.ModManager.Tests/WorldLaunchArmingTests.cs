using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldLaunchArmingTests
    {
        public static void Run()
        {
            var remembered = LaunchSelection.Target(new LaunchRequest("example.mode.target"));
            var menu = new ProfileLaunchConfigurationV4("profile", 0, "menu-request", "main-menu", Array.Empty<PackageIdentity>(),
                PackageSetDigest.Of(Array.Empty<PackageIdentity>()), false, false, Array.Empty<string>(), new Dictionary<string, string>());
            foreach (var request in new ProfileLaunchConfigurationV4?[] { null, menu })
                foreach (var safe in new[] { false, true })
                    foreach (var auto in new[] { false, true })
                    {
                        var startup = StartupRecoveryPolicy.Prepare(request, new ManagerState(), new StartupRecoveryDecision(safe, "", "test"), DateTime.UtcNow);
                        var armed = WorldLaunchArming.Resolve(startup, remembered, auto);
                        var expected = request == null && !safe && auto;
                        if ((armed != null) != expected || (armed != null && !ReferenceEquals(armed, remembered)))
                            throw new InvalidOperationException("Only direct startup without recovery may use an opted-in remembered selection; launcher menu always wins.");
                    }
            Console.WriteLine("V4 startup arming: PASS");
        }
    }
}
