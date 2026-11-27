using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SessionFollowerTests
    {
        private static void FollowCleanupDiagnosticsAreScrubbed(string root)
        {
            const string privateReason = "Content at 192.0.2.4 /levels/private.unity failed.";
            using var test = GamemodeParticipantRealmTests.InRealm(root, NetRealm.Client);
            var diagnostics = new List<Exception>();
            test.Hosted.DiagnosticFailure += diagnostics.Add;
            SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld(() =>
                throw new InvalidOperationException("Cleanup failed.", new InvalidOperationException(privateReason)))));
            That(SessionFollowTests.Follow(test, SessionFollowTests.Plan(test, SessionFollowTests.NewSessionId(), launchRequestId: "planned-join")).Succeeded,
                "a planned join runs before its cleanup fails");
            var menu = test.Hosted.FollowMainMenuAsync();
            test.Wait(menu);
            var error = test.Outcomes.Single().Error!;
            That(!error.Message.Contains("192.0.2.4") && !error.Message.Contains("private.unity"), "a followed world's terminal error is scrubbed");
            That(diagnostics.Count > 0 && diagnostics.All(item => !item.ToString().Contains("192.0.2.4") && !item.ToString().Contains("private.unity")),
                "cleanup diagnostics keep neither the original exception nor its inner exception");
            test.Menu = _ => throw new InvalidOperationException(privateReason);
            var failedMenu = test.Hosted.FollowMainMenuAsync();
            test.Wait(failedMenu);
            That(!failedMenu.Result.Succeeded && diagnostics.All(item => !item.ToString().Contains("192.0.2.4")),
                "a follower's native menu failure uses the same diagnostic boundary");
        }
    }
}
