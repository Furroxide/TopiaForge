using System;
using System.Linq;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static class ScoreboardRetirementTests
    {
        internal static void Run()
        {
            var scenarios = new[] { "departure", "rename", "key", "session", "provider", "solo", "offline" };
            foreach (var ban in new[] { false, true })
            {
                foreach (var scenario in scenarios) Retired(ban, scenario);
                foreach (var scenario in new[] { "client", "sync", "disconnected", "provider", "session", "key" }) InvalidNow(ban, scenario);
                Continuous(ban);
            }
            Console.WriteLine("ScoreboardRetirementTests passed: 14 permanent retirement, 12 current-authority rejection and 2 continuous-target cases.");
        }

        private static void Retired(bool ban, string scenario)
        {
            var rig = new Rig(); var original = rig.Row;
            rig.Ask(ban); var prompt = rig.Prompt!;
            var before = rig.Control;
            switch (scenario)
            {
                case "departure": rig.Snapshot(); break;
                case "rename": rig.Snapshot(SessionsRig.Player("p1", "Renamed", "key")); break;
                case "key": rig.Snapshot(SessionsRig.Player("p1", "Sam", "other")); break;
                case "session": rig.Control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "other", rig.Player); break;
                case "provider": rig.Control = new RecordingSessionControl { Role = before.Role, SessionSnapshot = before.SessionSnapshot }; break;
                case "solo": rig.Control.Role = InternalSessionRole.Solo; break;
                case "offline": rig.Control.IsLiveAvailable = false; break;
            }
            rig.Screen.Tick();
            // Do not poll the prompt while invalid: retirement must survive without a modal frame.
            rig.Control = before; before.Role = InternalSessionRole.ListenHost; before.IsLiveAvailable = true;
            rig.Snapshot(SessionsRig.Player("p1", "Sam", "key", ping: 99)); rig.Screen.Tick();
            That(!ReferenceEquals(original, rig.Row), "fresh authority after " + scenario);
            That(!prompt.StillHolds, "retired prompt stays invalid after " + scenario);
            prompt.Answer(true); prompt.Answer(true);
            That(prompt.IsWithdrawn && before.Calls.Count == 0, "retired confirmation cannot act after " + scenario);
            var previous = rig.Prompt;
            rig.Screen.Kick(original); rig.Screen.Ban(original); rig.Screen.ServerMute(original);
            That(ReferenceEquals(previous, rig.Prompt) && before.Calls.Count == 0, "retired controls cannot acquire replacement authority after " + scenario);
            rig.Ask(ban); rig.Prompt!.Answer(true);
            That(before.Calls.SequenceEqual(new[] { ban ? "Ban:p1" : "Kick:p1" }), "replacement authority still works after " + scenario);
        }

        private static void InvalidNow(bool ban, string scenario)
        {
            var rig = new Rig(); rig.Ask(ban); var before = rig.Control;
            switch (scenario)
            {
                case "client": rig.Control.Role = InternalSessionRole.Client; break;
                case "sync": rig.Control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Synchronizing, "s", rig.Player); break;
                case "disconnected": rig.Snapshot(SessionsRig.Player("p1", "Sam", "key", connected: false)); break;
                case "provider": rig.Control = new RecordingSessionControl { Role = before.Role, SessionSnapshot = before.SessionSnapshot }; break;
                case "session": rig.Control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "other", rig.Player); break;
                case "key": rig.Snapshot(SessionsRig.Player("p1", "Sam", "other")); break;
            }
            // Deliberately answer before Tick: checks must read current production authority.
            rig.Prompt!.Answer(true);
            That(rig.Prompt.IsWithdrawn && before.Calls.Count == 0 && rig.Control.Calls.Count == 0,
                "pending moderation rejects current " + scenario);
        }

        private static void Continuous(bool ban)
        {
            var rig = new Rig(); var original = rig.Row; rig.Ask(ban);
            for (var i = 0; i < 40; i++)
            {
                rig.Snapshot(SessionsRig.Player("p2", "Other", "two"), SessionsRig.Player("p1", "Sam", "key", ping: i));
                rig.Screen.Tick(); rig.Screen.Sort(-1); rig.Screen.Tick();
                That(ReferenceEquals(original, rig.Row) && rig.Prompt!.StillHolds, "continuous target survives refresh, sorting and participant object replacement");
            }
            for (var i = 0; i < 100; i++) { rig.Screen.Tick(); _ = rig.Prompt!.StillHolds; }
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10000; i++) { rig.Screen.Tick(); _ = rig.Prompt!.StillHolds; }
            That(GC.GetAllocatedBytesForCurrentThread() == allocated, "settled presenter and pending authority checks allocate zero");
            rig.Prompt!.Answer(true); rig.Prompt.Answer(true);
            That(!rig.Prompt.IsWithdrawn && rig.Control.Calls.SequenceEqual(new[] { ban ? "Ban:p1" : "Kick:p1" }), "continuous target acts exactly once");
        }

        private sealed class Rig
        {
            internal readonly MultiplayerParticipant Player = SessionsRig.Player("p1", "Sam", "key");
            internal RecordingSessionControl Control = new RecordingSessionControl { Role = InternalSessionRole.ListenHost };
            internal readonly ScoreboardPresenter Screen;
            internal ScreenPrompt? Prompt;
            internal ScoreboardRow Row => Screen.Rows.Single(row => !row.IsLocal && row.Name == "Sam");
            internal Rig()
            {
                Snapshot(Player);
                Screen = new ScoreboardPresenter(() => Control, () => Array.Empty<ScoreboardData>(), () => null, value => Prompt = value, (_, _) => { });
                Screen.Tick();
            }
            internal void Snapshot(params MultiplayerParticipant[] players) => Control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", players);
            internal void Ask(bool ban) { if (ban) Screen.Ban(Row); else Screen.Kick(Row); }
        }
    }
}
