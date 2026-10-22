using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The pause guard (WS3.13): offline it touches nothing; online a player's own pause menu opens the menu only and the
    /// shared world keeps the session's time; and leaving a session behind the menu hands the clock back as single player
    /// expects.
    /// </summary>
    internal static class MultiplayerPauseGuardTests
    {
        private static readonly InternalSessionTime Slow = new InternalSessionTime(0.5f, InternalTimeMode.Slowed);
        private static readonly InternalSessionTime Slower = new InternalSessionTime(0.25f, InternalTimeMode.Slowed);
        private static readonly InternalSessionTime Frozen = new InternalSessionTime(0f, InternalTimeMode.Paused);

        internal static void Run()
        {
            OfflineTheGuardTouchesNothing();
            OnlineTheMenuNeverFreezesTheWorld();
            AStaleRestoreIsCorrectedAsTheMenuCloses();
            LeavingBehindTheMenuHandsTheClockBack();
            TheCompositionRootRunsTheGuardOnline();
        }

        private static void OfflineTheGuardTouchesNothing()
        {
            var guard = new MultiplayerPauseGuard();
            var clock = new FakeGameClock();
            clock.OpenPauseMenu();
            for (var frame = 0; frame < 5; frame++) guard.Run(clock, Slow);
            That(!guard.IsArmed && clock.Touches == 0 && clock.Scale == 0f, "offline the menu freezes the world, and the guard never looks");
            clock.ClosePauseMenu();
            guard.Run(clock, Slow);
            That(clock.Touches == 0 && clock.Scale == 1f, "and closing it puts back what the menu found");
        }

        private static void OnlineTheMenuNeverFreezesTheWorld()
        {
            foreach (var realm in new[] { NetRealm.ListenHost, NetRealm.Client })
            {
                var guard = new MultiplayerPauseGuard();
                var clock = new FakeGameClock();
                guard.RealmChanged(realm);
                That(guard.IsArmed, realm + ": online the guard runs");
                guard.Run(clock, InternalSessionTime.Realtime);
                That(clock.ScaleWrites == 0, realm + ": with the menu closed it writes nothing");

                clock.OpenPauseMenu();
                guard.Run(clock, InternalSessionTime.Realtime);
                That(clock.Scale == 1f, realm + ": the menu opens, and the world keeps running at real time");

                // The session slows while the menu is open: Chronos writes the slow, and the guard keeps it.
                clock.Set(0.5f);
                guard.Run(clock, Slow);
                That(clock.Scale == 0.5f, realm + ": the session's slow holds behind the menu");

                // The game writes zero again, as a menu that reopens does: the guard puts the session's scale back each time.
                clock.Set(0f);
                guard.Run(clock, Slow);
                That(clock.Scale == 0.5f, realm + ": every zero the menu writes is put back");

                // A frozen session stays frozen, and a single-frame step the host takes while frozen is left to run.
                clock.Set(0f);
                var writes = clock.ScaleWrites;
                guard.Run(clock, Frozen);
                That(clock.Scale == 0f && clock.ScaleWrites == writes, realm + ": a frozen session is not the menu's doing");
                clock.Set(1f);
                guard.Run(clock, Frozen);
                That(clock.Scale == 1f && clock.ScaleWrites == writes, realm + ": a step's frame is not the menu's either");
            }
        }

        private static void AStaleRestoreIsCorrectedAsTheMenuCloses()
        {
            var guard = new MultiplayerPauseGuard();
            var clock = new FakeGameClock();
            guard.RealmChanged(NetRealm.ListenHost);
            clock.Set(0.5f);
            guard.Run(clock, Slow);

            // The menu opens over a slow, and keeps 0.5 to put back. The session moves on to 0.25 while it is open.
            clock.OpenPauseMenu();
            guard.Run(clock, Slow);
            clock.Set(0.25f);
            guard.Run(clock, Slower);
            clock.ClosePauseMenu();
            That(clock.Scale == 0.5f, "closing, the menu puts back the stale 0.5");
            guard.Run(clock, Slower);
            That(clock.Scale == 0.25f, "and the guard puts the session's 0.25 back at once");

            // When the menu puts back the right scale, nothing more is written.
            clock.OpenPauseMenu();
            guard.Run(clock, Slower);
            clock.ClosePauseMenu();
            var writes = clock.ScaleWrites;
            guard.Run(clock, Slower);
            That(clock.Scale == 0.25f && clock.ScaleWrites == writes, "a restore that is right stays as it is");
        }

        private static void LeavingBehindTheMenuHandsTheClockBack()
        {
            // A joined player leaves with its menu open: single player freezes the world behind the menu, and once it closes
            // the world runs at this game's own time, whatever the menu kept from the session.
            var guard = new MultiplayerPauseGuard();
            var clock = new FakeGameClock();
            guard.RealmChanged(NetRealm.Client);
            clock.Set(0.5f);
            guard.Run(clock, Slow);
            clock.OpenPauseMenu();
            guard.Run(clock, Slow);
            That(clock.Scale == 0.5f, "online the session's slow runs behind the menu");
            guard.RealmChanged(NetRealm.Offline);
            guard.Run(clock, InternalSessionTime.Realtime);
            That(clock.Scale == 0f && guard.IsHandingBack && guard.IsArmed, "leaving, the menu freezes the world again, as in single player");
            var touches = clock.ScaleWrites;
            for (var frame = 0; frame < 5; frame++) guard.Run(clock, InternalSessionTime.Realtime);
            That(clock.Scale == 0f && clock.ScaleWrites == touches, "and the guard leaves it frozen while the menu stays open");
            clock.ClosePauseMenu();
            That(clock.Scale == 0.5f, "closing, the menu puts back the session's slow it kept");
            guard.Run(clock, InternalSessionTime.Realtime);
            That(clock.Scale == 1f && !guard.IsHandingBack && !guard.IsArmed, "and the guard hands the clock back at this game's own time, real time");
            clock.ResetCounts();
            for (var frame = 0; frame < 5; frame++) guard.Run(clock, InternalSessionTime.Realtime);
            That(clock.Touches == 0, "after which it does nothing at all");

            // A host leaves while its gamemode keeps a slow of its own: offline the game runs at that slow.
            guard = new MultiplayerPauseGuard();
            clock = new FakeGameClock();
            guard.RealmChanged(NetRealm.ListenHost);
            clock.Set(0.25f);
            clock.OpenPauseMenu();
            guard.Run(clock, Slower);
            guard.RealmChanged(NetRealm.Offline);
            guard.Run(clock, Slower);
            clock.ClosePauseMenu();
            guard.Run(clock, Slower);
            That(clock.Scale == 0.25f && !guard.IsArmed, "a host that leaves runs at its own slow once the menu closes");

            // Leaving with the menu closed leaves nothing to hand back.
            guard = new MultiplayerPauseGuard();
            clock = new FakeGameClock();
            guard.RealmChanged(NetRealm.Client);
            guard.Run(clock, Slow);
            guard.RealmChanged(NetRealm.Offline);
            var writes = clock.ScaleWrites;
            guard.Run(clock, InternalSessionTime.Realtime);
            That(!guard.IsArmed && clock.ScaleWrites == writes, "leaving with the menu closed writes nothing and stops the guard");

            // Going online again before the menu closes ends the hand-back: online rules apply again.
            guard = new MultiplayerPauseGuard();
            clock = new FakeGameClock();
            guard.RealmChanged(NetRealm.ListenHost);
            clock.OpenPauseMenu();
            guard.Run(clock, InternalSessionTime.Realtime);
            guard.RealmChanged(NetRealm.Offline);
            guard.Run(clock, InternalSessionTime.Realtime);
            guard.RealmChanged(NetRealm.ListenHost);
            guard.Run(clock, Slow);
            That(clock.Scale == 0.5f && !guard.IsHandingBack, "hosting again behind the menu keeps the world running at the session's time");

            // Without a game clock there is nothing to guard.
            guard = new MultiplayerPauseGuard();
            guard.RealmChanged(NetRealm.Client);
            guard.Run(null, Slow);
            guard.RealmChanged(NetRealm.Offline);
            guard.Run(null, Slow);
            That(!guard.IsArmed, "and with no clock the guard simply stops once offline");
        }

        private static void TheCompositionRootRunsTheGuardOnline()
        {
            var (runtime, control, _) = Runtime(() => SessionPhase.Running);
            var clock = new FakeGameClock();
            runtime.GameClock = clock;
            runtime.Connect();
            clock.OpenPauseMenu();
            Frame(runtime);
            That(clock.Scale == 0f && clock.Touches == 0, "offline the runtime never runs the guard");
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            That(clock.Scale == 1f && runtime.SessionTime.PauseGuard.IsArmed, "opened to other players, the world runs on behind the host's menu");
            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            That(clock.Scale == 0f && runtime.SessionTime.PauseGuard.IsHandingBack, "stopping hosting behind the menu freezes the world, as single player does");
            clock.ClosePauseMenu();
            Frame(runtime);
            That(clock.Scale == 1f && !runtime.SessionTime.PauseGuard.IsArmed, "and closing it hands the clock back");
        }
    }
}
