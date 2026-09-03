using System;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Covers the V6 gamemode binding contract: that a declared gamemode names a type that exists,
    /// implements the factory, and answers with the id its manifest declares.
    /// </summary>
    /// <remarks>
    /// V5's defect was that none of that was checkable. A manifest could name a gamemode nothing
    /// implemented, and the only symptom was a menu entry that did nothing.
    /// </remarks>
    internal static class GamemodeBindingTests
    {
        /// <summary>The gamemode id Worlds published on Sandbox's behalf until the ownership move.</summary>
        private const string RetiredSandboxGamemodeId = "io.github.furroxide.topiaforge.worlds.sandbox";

        public static void Run()
        {
            FreePlayDeclaresTheIdItImplements();
            FreePlayRunsNoRulesAndReleasesNothing();
            FreePlayRejectsAMissingSession();
            TheWorldsProviderNoLongerOwnsTheSandboxGamemode();
            Console.WriteLine("Gamemode binding tests passed.");
        }

        private static void FreePlayDeclaresTheIdItImplements()
        {
            var factory = new FreePlayGamemode();

            Assert(factory.GamemodeId == FreePlayGamemode.FreePlayGamemodeId,
                "a factory must answer with the id its manifest declares, since that is what the runtime matches on");
            Assert(factory.GamemodeId == "io.github.furroxide.topiaforge.worlds.freeplay",
                "the free-play id is namespaced under the Worlds package that implements it");
            Assert(FreePlayGamemode.MenuEntryId.StartsWith(
                    FreePlayGamemode.FreePlayGamemodeId, StringComparison.Ordinal),
                "the launch target must sit under the gamemode's own namespace, not the provider's root");

            // Activator.CreateInstance is how ModRuntime already builds a declared entryType, so a declared
            // implementation has to be constructible the same way.
            Assert(Activator.CreateInstance(typeof(FreePlayGamemode)) is IGamemodeFactory,
                "a declared implementation must be constructible with no arguments");
        }

        private static void FreePlayRunsNoRulesAndReleasesNothing()
        {
            var session = new StubSession("io.github.furroxide.topiaforge.worlds.open_sandbox");
            var created = new FreePlayGamemode().CreateController(session);

            Assert(created.TryGetValue(out var controller),
                "free play must produce a controller: " + created.ErrorMessage);

            // Free play is the absence of rules. A controller that claimed anything would be doing
            // something free play is not, so nothing may be registered against the session.
            Assert(session.PauseActionsAdded == 0, "free play must not add pause actions");
            Assert(!session.Ended, "free play must not end the session it was just given");

            controller!.Dispose();
            controller.Dispose();
            Assert(!session.Ended, "disposing a free-play controller must not end anything it does not own");
        }

        private static void FreePlayRejectsAMissingSession()
        {
            var threw = false;
            try
            {
                new FreePlayGamemode().CreateController(null!);
            }
            catch (ArgumentNullException)
            {
                threw = true;
            }

            Assert(threw, "a factory handed no session must fail loudly rather than build a controller for nothing");
        }

        /// <summary>
        /// The Worlds provider used to declare the sandbox gamemode on Sandbox's behalf, so the creator
        /// gameplay and the world infrastructure shared one identity. The id must not survive anywhere in
        /// the SDK surface, or a manifest could still bind to a mode with no controller behind it.
        /// </summary>
        private static void TheWorldsProviderNoLongerOwnsTheSandboxGamemode()
        {
            // Bound directly rather than reflected off FreePlayGamemode's assembly: free play is
            // compiled into this harness too, so that would have found the test assembly.
            var constants = typeof(WellKnownWorldIds)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral)
                .Select(field => (string?)field.GetRawConstantValue() ?? string.Empty)
                .ToList();
            Assert(!constants.Contains(RetiredSandboxGamemodeId, StringComparer.Ordinal),
                "the retired sandbox gamemode id must not remain a well-known id");
            Assert(constants.Contains("io.github.furroxide.topiaforge.worlds.open_sandbox", StringComparer.Ordinal),
                "the open sandbox world id stays: it is a world, and Worlds still owns it");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Gamemode binding: " + message);
            }
        }

        /// <summary>
        /// A session that records what a gamemode did to it. Deliberately not a mock framework: the
        /// point is to observe that free play does nothing at all.
        /// </summary>
        private sealed class StubSession : IGamemodeSession
        {
            public StubSession(string worldId) => WorldId = worldId;

            public string GamemodeId => FreePlayGamemode.FreePlayGamemodeId;

            public string WorldId { get; }

            public string LaunchTargetId => FreePlayGamemode.MenuEntryId;

            public IModContext Mod =>
                throw new InvalidOperationException("free play must not need a mod context");

            public WorldSession World =>
                throw new InvalidOperationException("free play must not need the world session");

            public int PauseActionsAdded { get; private set; }

            public bool Ended { get; private set; }

            public OperationResult<IDisposable> AddPauseAction(WorldPauseAction action)
            {
                PauseActionsAdded++;
                return OperationResult<IDisposable>.Success(new NoopHandle());
            }

            public void End(WorldSessionEndReason reason) => Ended = true;

            private sealed class NoopHandle : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }
    }
}
