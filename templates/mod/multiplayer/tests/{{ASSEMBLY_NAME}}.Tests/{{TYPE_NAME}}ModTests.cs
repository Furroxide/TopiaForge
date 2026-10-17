using System;
using System.Collections.Generic;
using NUnit.Framework;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    /// <summary>
    /// Plays the mod on TopiaForge's multiplayer test rig: the session engine the game runs, joined by a deterministic
    /// virtual network instead of sockets, so the test decides when every frame, packet and second happens.
    /// </summary>
    public sealed class {{TYPE_NAME}}ModTests
    {
        private const int Allowed = {{TYPE_NAME}}Mod.MaximumPressesPerSecond;

        [Test]
        public void TwoPlayersAgreeOnTheCountTheHostOwns()
        {
            using var rig = MultiplayerTestRig.CreateListenServer();
            using var host = new Player(rig.Server);
            using var guest = new Player(rig.AddRemoteClient("guest"));
            rig.Flush();

            guest.Press();
            Assert.Multiple(() =>
            {
                Assert.That(guest.Hud, Is.EqualTo(Showing(1)), "the guest predicts its own press at once");
                Assert.That(host.Hud, Is.EqualTo(Showing(0)), "nothing counts until the host runs the press");
            });

            rig.Flush();
            host.Press();
            rig.Flush();
            Assert.Multiple(() =>
            {
                Assert.That(host.Hud, Is.EqualTo(Showing(2)));
                Assert.That(guest.Hud, Is.EqualTo(Showing(2)));
            });

            using var late = new Player(rig.AddRemoteClient("late"));
            rig.Flush();
            Assert.That(late.Hud, Is.EqualTo(Showing(2)), "a player who joins later receives the host's count");

            late.Unload();
            guest.Unload();
            host.Unload();
        }

        [Test]
        public void APressPastTheRateLimitIsRefusedAndRolledBack()
        {
            using var rig = MultiplayerTestRig.CreateListenServer();
            using var host = new Player(rig.Server);
            using var guest = new Player(rig.AddRemoteClient("guest"));
            rig.Flush();

            // The rig's clock moves only when the test advances it, so every one of these presses reaches the host
            // within the same second, however many of the guest's frames they span.
            for (var press = 0; press <= Allowed; press++)
            {
                guest.Press();
            }

            Assert.That(guest.Hud, Is.EqualTo(Showing(Allowed + 1)), "the guest predicts every press");
            rig.Flush();
            guest.NextFrame();
            Assert.Multiple(() =>
            {
                Assert.That(host.Hud, Is.EqualTo(Showing(Allowed)), "the host accepts only the allowed presses");
                Assert.That(guest.Hud, Is.EqualTo(Showing(Allowed)), "the refused press rolls back");
                Assert.That(guest.Toasts, Has.Count.EqualTo(1));
                Assert.That(guest.Toasts[0].Message, Does.StartWith("Too fast"));
                Assert.That(guest.Toasts[0].Tone, Is.EqualTo(UiTone.Warning));
                Assert.That(host.Toasts, Is.Empty);
            });

            rig.AdvanceTime(TimeSpan.FromSeconds(1));
            guest.Press();
            rig.Flush();
            Assert.Multiple(() =>
            {
                Assert.That(host.Hud, Is.EqualTo(Showing(Allowed + 1)), "a second later the host takes presses again");
                Assert.That(guest.Hud, Is.EqualTo(Showing(Allowed + 1)));
            });

            guest.Unload();
            host.Unload();
        }

        [Test]
        public void ANewSessionStartsFromTheDeclaredDefault()
        {
            using var rig = MultiplayerTestRig.CreateListenServer();
            using var host = new Player(rig.Server);
            using var guest = new Player(rig.AddRemoteClient("guest"));
            rig.Flush();
            guest.Press();
            rig.Flush();
            Assert.That(host.Hud, Is.EqualTo(Showing(1)));

            rig.StartNewSession("next-round");
            rig.Flush();
            Assert.Multiple(() =>
            {
                Assert.That(host.Hud, Is.EqualTo(Showing(0)));
                Assert.That(guest.Hud, Is.EqualTo(Showing(0)));
            });

            guest.Unload();
            host.Unload();
        }

        [Test]
        public void SoloPlayRunsTheSameCodeWithTheLocalPlayerAsHost()
        {
            // A standalone session has no network at all. Its only player is the host, as in solo play offline.
            using var rig = MultiplayerTestRig.CreateStandalone();
            using var player = new Player(rig.Server);

            player.Press();
            Assert.That(player.Hud, Is.EqualTo(Showing(1)), "the host's own press counts at once");

            for (var press = 1; press <= Allowed; press++)
            {
                player.Press();
            }

            Assert.Multiple(() =>
            {
                Assert.That(player.Hud, Is.EqualTo(Showing(Allowed)), "the host's own player has the same limit");
                Assert.That(player.Toasts, Has.Count.EqualTo(1));
            });

            player.Unload();
        }

        private static string Showing(int count) => "Shared count: " + count;

        /// <summary>
        /// One player's game: the mod loaded as TopiaForge loads it, with the generated contract bound to that player's
        /// session before OnLoad runs and released with the mod's lifetime.
        /// </summary>
        private sealed class Player : IDisposable
        {
            private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);
            private readonly FakeModContext context = new FakeModContext();
            private readonly ModLifecycleRunner runner;

            internal Player(MultiplayerTestNode node)
            {
                var mod = new {{TYPE_NAME}}Mod();
                var bound = mod.BindMultiplayer(node.Session);
                if (!bound.TryGetValue(out var binding))
                {
                    throw new InvalidOperationException("The generated contract did not bind: " + bound.ErrorMessage);
                }

                context.Lifetime.Track(binding);
                runner = new ModLifecycleRunner(mod, context);
                runner.Load();
            }

            internal string Hud => context.Ui.Surfaces[0].Body;

            internal IReadOnlyList<FakeToast> Toasts => context.Ui.Toasts;

            /// <summary>Taps the press key: down for one frame, up for the next.</summary>
            internal void Press()
            {
                context.Input.SetValue({{TYPE_NAME}}Mod.PressActionName, 1f);
                NextFrame();
                context.Input.SetValue({{TYPE_NAME}}Mod.PressActionName, 0f);
                NextFrame();
            }

            /// <summary>Runs one frame, which is when the mod reads the host's answers.</summary>
            internal void NextFrame() => context.AdvanceFrame(Frame);

            /// <summary>Unloads the mod and checks that it released everything it held.</summary>
            internal void Unload()
            {
                runner.Unload();
                context.AssertNoLeaks();
            }

            // Never throws, so that a failed assertion stays the failure the test reports.
            public void Dispose() => runner.Dispose();
        }
    }
}
