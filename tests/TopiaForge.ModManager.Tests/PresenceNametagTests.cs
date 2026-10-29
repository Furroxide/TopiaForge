using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static class PresenceNametagTests
    {
        internal static void Run()
        {
            Visibility();
            FeedLifetime();
            FrameIsolation();
            PresenceNametagViewTests.Run();
            Console.WriteLine("PresenceNametagTests passed: visibility, bounded feed, privacy, frame isolation and production overlay lifetime.");
        }

        private static void Visibility()
        {
            var control = new FakeMultiplayerControl();
            That(!ScoreboardNametagFeed.IsVisible(null, true), "no provider hides names");
            foreach (var role in new[] { InternalSessionRole.Solo, InternalSessionRole.ListenHost, InternalSessionRole.Client })
            {
                control.Role = role;
                foreach (var live in new[] { false, true })
                {
                    control.IsLiveAvailable = live;
                    That(!ScoreboardNametagFeed.IsVisible(control, false), "menus and loading hide names");
                    That(ScoreboardNametagFeed.IsVisible(control, true) == (live && role != InternalSessionRole.Solo), "only live hosts and clients show names");
                }
            }
        }

        private static void FeedLifetime()
        {
            var source = new Tags();
            var feed = new ScoreboardNametagFeed();
            feed.Tick(source, true);
            That(feed.Active.Count(active => active) == 8 && feed.Names[0] == "<b>Sam</b>", "eight names remain literal");
            source.X = 7; feed.Tick(source, true);
            That(feed.Positions[0].X == 7, "current head position is sampled even without a roster version change");
            source.Name = "Replacement"; feed.Tick(source, true);
            That(feed.Names[0] == "Replacement", "slot reuse refreshes its display name");
            source.Name = "Sam\n127.0.0.1 " + new string('x', 60); feed.Tick(source, true);
            That(feed.Names.All(name => name == null || (name.Length <= 32 && !name.Contains("127.0.0.1", StringComparison.Ordinal) && !name.Contains('\n'))), "names are private, bounded single lines");
            source.Failed = 3; feed.Tick(source, true);
            That(!feed.Active[3] && feed.Active[4], "a lost avatar hides only its slot");
            source.Count = 50; feed.Tick(source, true);
            That(source.Highest == 31 && feed.Active.Count(active => active) == 31, "capacity bounds source reads and visible slots");
            source.Failed = -1; source.Count = 8; feed.Tick(source, true);
            That(!feed.Active.Skip(8).Any(active => active), "shrinking the roster hides retired slots");
            for (var i = 0; i < 200; i++) feed.Tick(source, true);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            long firstTick = 0, firstElapsed = 0, firstBytes = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10000; i++)
            {
                feed.Tick(source, true);
                var observed = GC.GetAllocatedBytesForCurrentThread() - before;
                if (firstTick == 0 && observed != 0)
                {
                    firstTick = i + 1; firstBytes = observed;
                    firstElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
                }
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            Console.WriteLine("Nametag feed allocation: bytes=" + allocated + " firstTick=" + firstTick + " firstBytes=" + firstBytes
                + " firstElapsedMs=" + firstElapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency
                + " elapsedMs=" + elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            That(allocated == 0, "eight-player feed allocates exactly zero bytes; actual=" + allocated);
            feed.Tick(null, true); That(!feed.Active.Any(active => active), "provider loss hides every slot");
            feed.Tick(source, true); feed.Tick(source, false);
            That(!feed.Active.Any(active => active), "leaving gameplay hides every slot");
            Console.WriteLine("PresenceNametag feed: " + allocated + " bytes; 200 warmups; 10,000 frames; 8 players.");
        }

        private static void FrameIsolation()
        {
            var rig = new SessionsRig();
            var log = new List<string>();
            var calls = new List<string>();
            var frame = new MultiplayerOverlayFrame(new MultiplayerFrameGuard(log.Add), rig.Presenter,
                () => calls.Add("outside"), () => calls.Add("tab"), _ => calls.Add("voice"),
                () => { calls.Add("names"); throw new InvalidOperationException("name view lost"); });
            frame.Tick(0.016f); frame.Tick(0.016f);
            That(calls.SequenceEqual(new[] { "voice", "names", "outside", "tab", "voice", "names", "outside", "tab" }), "nametag failure preserves the remaining frame order");
            That(log.Count == 1, "repeated nametag failure logs once");
        }

        private sealed class Tags : IPresenceNametagSource
        {
            internal int Count = 8, Failed = -1, Highest;
            internal float X;
            internal string Name = "<b>Sam</b>";
            public int NametagCount => Count;
            public int NametagVersion => 0;
            public bool TryGetNametag(int slot, out PresenceNametag tag)
            {
                Highest = Math.Max(Highest, slot);
                tag = new PresenceNametag(Name, new NetVector3(X, 2, slot + 4));
                return slot != Failed;
            }
        }
    }
}
