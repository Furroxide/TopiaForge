using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager.Tests
{
    internal static class GuestBrainScopeTests
    {
        internal static void Run()
        {
            foreach (var scope in Scopes()) NestedScopes(scope);
        }

        private static IEnumerable<IntentScope> Scopes()
        {
            if (IntentScope.RuntimeCanReuseContexts) yield return new IntentScope(IntentScopeMode.ReusedContext);
            yield return new IntentScope(IntentScopeMode.LogicalCallContext,
                new MultiplayerIntentScopeTests.EmulatedLogicalCallContext().Store);
            yield return new IntentScope(IntentScopeMode.AsyncLocal);
        }

        private static void NestedScopes(IntentScope scope)
        {
            var intents = new MultiplayerIntents(scope);
            intents.FollowRealm(NetRealm.ListenHost);
            var local = new IntentRecord(IntentSource.UiCallback, "io.example.local", null, intents.Epoch);
            ExecutionContext? captured = null;
            using (intents.EnterForwarded("io.example.remote", IntentSource.UiCallback, "guest"))
            {
                captured = ExecutionContext.Capture();
                Check(intents.HasForwardedParticipant, "forwarded entry carries authority");
                using (scope.Enter(null))
                {
                    Check(scope.Flowing == null && intents.HasForwardedParticipant, "cleared intent retains synchronous authority");
                    using (scope.Enter(null)) Check(intents.HasForwardedParticipant, "no-op tokens preserve ancestry");
                    scope.Run(local, _ =>
                    {
                        Check(ReferenceEquals(scope.Flowing, local) && intents.HasForwardedParticipant,
                            "Run replacement retains authority in each supported storage mode");
                        using (scope.Enter(local)) Check(intents.HasForwardedParticipant, "same-record nesting keeps authority");
                    }, null);
                    try { scope.Run(local, _ => throw new ApplicationException("scope unwind"), null); }
                    catch (ApplicationException) { }
                    Check(scope.Flowing == null && intents.HasForwardedParticipant, "throw restores both visibility and ancestry");
                    Check(!Task.Run(() => intents.HasForwardedParticipant).GetAwaiter().GetResult(), "workers never observe owner-thread authority");
                }
                Check(intents.HasForwardedParticipant, "LIFO disposal restores outer authority");
            }
            Check(!intents.HasForwardedParticipant && scope.Flowing == null, "authority ends with outer token");
            Check(captured != null, "execution context captures forwarded intent");
            ExecutionContext.Run(captured!, _ =>
            {
                Check(intents.HasForwardedParticipant, "same-thread continuation retains existing flowed intent");
                using (scope.Enter(null)) Check(intents.HasForwardedParticipant, "resumed forwarded context also protects nested replacement");
            }, null);
            Check(!intents.HasForwardedParticipant, "continuation restores trusted caller");
            using (intents.EnterForwarded("io.example.remote", IntentSource.UiCallback, "guest"))
            using (scope.Enter(null))
            {
                intents.FollowRealm(NetRealm.Offline);
                Check(!intents.HasForwardedParticipant, "offline rejects old authority");
                intents.FollowRealm(NetRealm.ListenHost);
                Check(!intents.HasForwardedParticipant, "new host epoch ignores old ancestors");
                using (intents.EnterForwarded("io.example.remote", IntentSource.UiCallback, "new-guest"))
                    Check(intents.HasForwardedParticipant, "new epoch can admit a new forwarded call");
                Check(!intents.HasForwardedParticipant, "new nested token restores only stale ancestors");
            }
            using (scope.Enter(local)) Check(!intents.HasForwardedParticipant, "trusted local scope never gains forwarded authority");
        }

        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Guest scope authority: " + message); }
    }
}
