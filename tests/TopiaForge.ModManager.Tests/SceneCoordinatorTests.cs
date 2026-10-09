using System;

namespace TopiaForge.ModManager.Tests
{
    // Exercises the manager-owned scene-transition arbiter (SceneCoordinator is compiled into this assembly
    // via <Compile Include>; it is deliberately Unity-free claim bookkeeping).
    internal static class SceneCoordinatorTests
    {
        public static void Run()
        {
            TestAutomaticApprovedWhenIdle();
            TestAutomaticRefusedWhileClaimHeld();
            TestUserInitiatedSupersedes();
            TestDisposeReleasesClaim();
            TestDisposeIsIdempotent();
            TestReleaseOwnerClearsAllClaims();
            TestThrowingLoggerCannotChangeDecisions();
            TestAuthorityPolicyDeniesBeforeClaiming();
            Console.WriteLine("All scene coordinator tests passed.");
        }

        private static void TestAutomaticApprovedWhenIdle()
        {
            var coordinator = new SceneCoordinator();
            Assert(!coordinator.IsSceneBusy, "a fresh coordinator holds no claims");

            var decision = coordinator.RequestTransition(new SceneTransitionRequest(
                "a.mod", "SceneA", SceneTransitionPriority.Automatic, "auto"));

            Assert(decision.Approved && decision.Claim != null, "automatic is approved while nothing holds the scene");
            Assert(coordinator.IsSceneBusy && coordinator.ActiveClaims.Count == 1, "the approval registers a claim");
            Assert(coordinator.ActiveClaims[0].OwnerModId == "a.mod" && coordinator.ActiveClaims[0].SceneName == "SceneA",
                "the claim carries owner and scene");
        }

        private static void TestAutomaticRefusedWhileClaimHeld()
        {
            var coordinator = new SceneCoordinator();
            var holder = coordinator.RequestTransition(new SceneTransitionRequest(
                "holder.mod", "SceneA", SceneTransitionPriority.UserInitiated, "world session"));
            Assert(holder.Approved, "the holder's claim should be approved");

            var refused = coordinator.RequestTransition(new SceneTransitionRequest(
                "auto.mod", "SceneB", SceneTransitionPriority.Automatic, "auto-connect"));

            Assert(!refused.Approved && refused.Claim == null, "automatic must be refused while a claim is active");
            Assert(refused.Message.Contains("holder.mod"), "the refusal names the blocking owner: " + refused.Message);
            Assert(coordinator.ActiveClaims.Count == 1, "a refusal registers no claim");
        }

        private static void TestUserInitiatedSupersedes()
        {
            var coordinator = new SceneCoordinator();
            var first = coordinator.RequestTransition(new SceneTransitionRequest(
                "first.mod", "SceneA", SceneTransitionPriority.UserInitiated));
            var second = coordinator.RequestTransition(new SceneTransitionRequest(
                "second.mod", "SceneB", SceneTransitionPriority.UserInitiated));

            Assert(first.Approved && !second.Approved, "competing user-initiated requests must be Busy");
            Assert(coordinator.ActiveClaims.Count == 1, "only one admission owner may exist");
        }

        private static void TestDisposeReleasesClaim()
        {
            var coordinator = new SceneCoordinator();
            var decision = coordinator.RequestTransition(new SceneTransitionRequest(
                "a.mod", "SceneA", SceneTransitionPriority.UserInitiated));

            decision.Claim!.Dispose();

            Assert(!coordinator.IsSceneBusy, "disposing the claim releases it");
            var auto = coordinator.RequestTransition(new SceneTransitionRequest(
                "b.mod", "SceneB", SceneTransitionPriority.Automatic));
            Assert(auto.Approved, "automatic unblocks once the claim is released");
        }

        private static void TestDisposeIsIdempotent()
        {
            var coordinator = new SceneCoordinator();
            var first = coordinator.RequestTransition(new SceneTransitionRequest("a.mod", "SceneA", SceneTransitionPriority.UserInitiated));
            first.Claim!.Dispose();
            var second = coordinator.RequestTransition(new SceneTransitionRequest("b.mod", "SceneB", SceneTransitionPriority.UserInitiated));
            first.Claim.Dispose();
            Assert(second.Approved && coordinator.ActiveClaims.Count == 1, "stale disposal must not release a later owner");
            second.Claim!.Dispose();
        }

        private static void TestReleaseOwnerClearsAllClaims()
        {
            var coordinator = new SceneCoordinator();
            coordinator.RequestTransition(new SceneTransitionRequest("gone.mod", "SceneA", SceneTransitionPriority.UserInitiated));
            coordinator.ReleaseOwner("other.mod");
            Assert(coordinator.IsSceneBusy, "unrelated owner cleanup must not release admission");
            coordinator.ReleaseOwner("GONE.MOD");
            Assert(!coordinator.IsSceneBusy, "owner cleanup closes an idle reservation");
        }

        private static void TestThrowingLoggerCannotChangeDecisions()
        {
            var coordinator = new SceneCoordinator(_ => throw new InvalidOperationException("disk full"));
            var first = coordinator.RequestTransition(new SceneTransitionRequest("first.mod", "SceneA", SceneTransitionPriority.UserInitiated));
            Assert(first.Approved, "logging cannot prevent admission");
            var takeover = coordinator.RequestTransition(new SceneTransitionRequest("second.mod", "SceneB", SceneTransitionPriority.UserInitiated));
            Assert(!takeover.Approved && coordinator.ActiveClaims.Count == 1, "logging cannot allow competing admission");
            first.Claim!.Dispose();
        }

        private static void TestAuthorityPolicyDeniesBeforeClaiming()
        {
            var coordinator = new SceneCoordinator(authorityPolicy: new DenyAuthorityPolicy());

            var decision = coordinator.RequestTransition(new SceneTransitionRequest(
                "client.mod",
                "SharedWorld",
                SceneTransitionPriority.UserInitiated));

            Assert(!decision.Approved
                && decision.ErrorCode == TopiaForge.Mods.ModErrorCode.NotAuthoritative
                && decision.Claim == null
                && !coordinator.IsSceneBusy,
                "an authority denial returns NotAuthoritative before coordinator state or native work changes");
        }

        private sealed class DenyAuthorityPolicy : ISceneTransitionAuthorityPolicy
        {
            public SceneTransitionAuthorityDecision Evaluate(SceneTransitionRequest request) =>
                SceneTransitionAuthorityDecision.Deny("Only the server can replace the shared world.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
