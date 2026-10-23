using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// How long SDK UI surfaces live, and when they show. A surface lives as long as the mod or scope that made it, never
    /// as long as a scene: in the game a HUD made in OnLoad never reached the screen, because mods load before the game's
    /// first scene and loading it destroyed the HUD, and a window made in a world vanished at the next world load while
    /// IUiSurface still reported it visible. A gameplay HUD now hides by itself at the main menu and during loads.
    /// </summary>
    internal static class UiSurfaceLifetimeTests
    {
        private const string MainMenu = GameScenes.MainMenuSceneName;

        public static void Run()
        {
            TheGateFollowsTheActiveSceneAndLoads();
            TheGateTellsEachListenerOnceAndIsolatesFailures();
            AHudMadeInOnLoadShowsInEveryWorldAndNowhereElse();
            AHudOutlivesAThousandSceneLoads();
            AHudShownEverywhereIgnoresGameplay();
            HidingAndShowingFollowTheModNotTheGate();
            ADestroyedSurfaceIsNeverReportedVisible();
            APlayerClosedWindowIsDismissedOnce();
            DisposalNeverDismisses();
            DefaultAnchorsAvoidTheGamesOwnHud();
            LowerHudStacksClearSubtitlesAtBothSizesAndScales();
            ALostWindowCannotReopenFromItsDismissal();
            EveryAnchorDocksSomewhereOtherThanTheCrosshair();
            SettledFramesWithAShownHudAllocateNothing();
            Console.WriteLine("UiSurfaceLifetimeTests passed.");
        }

        private static void TheGateFollowsTheActiveSceneAndLoads()
        {
            Assert(!UiGameplayGate.IsGameplayScene(MainMenu) && !UiGameplayGate.IsGameplayScene("Boot")
                && !UiGameplayGate.IsGameplayScene(null) && !UiGameplayGate.IsGameplayScene(" "),
                "the menu, boot scenes and no scene at all are not gameplay");
            Assert(UiGameplayGate.IsGameplayScene("City Streets") && UiGameplayGate.IsGameplayScene("IntroSewer"),
                "levels are gameplay");

            var gate = new UiGameplayGate();
            Assert(!gate.IsGameplay, "before the game's first scene, nothing is gameplay");
            gate.Observe(activeIsGameplayScene: false, activeLoaded: true, sceneCount: 1, loadedSceneCount: 1);
            Assert(!gate.IsGameplay, "the main menu is not gameplay");
            gate.Observe(false, true, 2, 1);
            Assert(!gate.IsGameplay, "while City Streets loads beside the menu, still not");
            gate.Observe(true, true, 1, 1);
            Assert(gate.IsGameplay, "City Streets loaded and active is gameplay");
            gate.Observe(true, true, 2, 1);
            Assert(!gate.IsGameplay, "a load from a world hides gameplay HUDs until it finishes");
            gate.Observe(true, false, 1, 1);
            Assert(!gate.IsGameplay, "an active scene that has not finished loading is not gameplay");
            gate.Observe(true, true, 1, 1);
            Assert(gate.IsGameplay, "and gameplay resumes when it has");
        }

        private static void TheGateTellsEachListenerOnceAndIsolatesFailures()
        {
            var gate = new UiGameplayGate();
            var heard = new List<bool>();
            using (gate.Subscribe(_ => throw new InvalidOperationException("expected listener failure")))
            using (gate.Subscribe(heard.Add))
            {
                Assert(gate.Observe(true, true, 1, 1) && !gate.Observe(true, true, 1, 1), "only a change is reported");
                Assert(gate.Observe(false, true, 1, 1), "leaving gameplay is reported");
                Assert(heard.Count == 2 && heard[0] && !heard[1], "each listener hears each change once, despite another failing");
            }

            gate.Observe(true, true, 1, 1);
            Assert(heard.Count == 2, "a disposed subscription hears nothing more");
            AssertThrows<ArgumentNullException>(() => gate.Subscribe(null!), "a listener is required");
        }

        private static void AHudMadeInOnLoadShowsInEveryWorldAndNowhereElse()
        {
            var gate = new UiGameplayGate();
            var hud = new UiSurfacePresence(followsGameplay: true, gameplay: gate.IsGameplay);
            var native = new NativeView(hud);
            using var _ = gate.Subscribe(gameplay => native.Apply(hud.SetGameplay(gameplay)));
            native.Present();
            Assert(!hud.IsVisible && !native.OnScreen, "made in OnLoad, before the first scene, it is hidden, not lost");

            gate.Observe(false, true, 1, 1);
            Assert(!hud.IsVisible && !native.OnScreen, "hidden at the main menu");
            gate.Observe(true, true, 1, 1);
            Assert(hud.IsVisible && native.OnScreen, "shown in City Streets, with no scene handling by the mod");
            gate.Observe(true, true, 2, 1);
            Assert(!hud.IsVisible && !native.OnScreen, "hidden while the next scene loads");
            gate.Observe(false, true, 1, 1);
            Assert(!hud.IsVisible, "hidden at the main menu again");
            gate.Observe(true, true, 1, 1);
            Assert(hud.IsVisible && native.OnScreen && native.Dismissals == 0,
                "shown again in the next world, the same surface, and TopiaForge's hiding never dismissed it");
        }

        private static void AHudOutlivesAThousandSceneLoads()
        {
            var gate = new UiGameplayGate();
            var hud = new UiSurfacePresence(true, gate.IsGameplay);
            var native = new NativeView(hud);
            using var _ = gate.Subscribe(gameplay => native.Apply(hud.SetGameplay(gameplay)));
            for (var load = 0; load < 1000; load++)
            {
                // Each load runs from the scene before it, while the next one loads, to the next scene: a world on even
                // loads, the main menu on odd ones.
                var world = load % 2 == 0;
                gate.Observe(!world, true, 2, 1);
                gate.Observe(world, true, 1, 1);
                Assert(hud.IsLive && hud.IsVisible == world, "load " + load + ": the HUD is alive and follows gameplay");
            }

            Assert(native.Dismissals == 0 && native.Shows == 500 && native.Hides == 500,
                "a thousand loads showed and hid one HUD, and never dismissed or lost it");
        }

        private static void AHudShownEverywhereIgnoresGameplay()
        {
            var everywhere = new UiSurfacePresence(followsGameplay: false, gameplay: false);
            Assert(everywhere.IsVisible, "a HUD shown everywhere shows before the first scene");
            Assert(everywhere.SetGameplay(false) == UiPresenceEffects.None && everywhere.SetGameplay(true) == UiPresenceEffects.None,
                "and is never hidden or shown by the gate");
        }

        private static void HidingAndShowingFollowTheModNotTheGate()
        {
            var hud = new UiSurfacePresence(true, gameplay: false);
            Assert(hud.Hide() == UiPresenceEffects.None, "hiding a HUD the gate keeps hidden dismisses nothing: it was not showing");
            Assert(hud.SetGameplay(true) == UiPresenceEffects.None && !hud.IsVisible, "a HUD the mod hid stays hidden in gameplay");
            Assert(hud.Show() == UiPresenceEffects.ShowNative && hud.IsVisible, "shown in gameplay, it appears");
            Assert(hud.Show() == UiPresenceEffects.None, "showing it again changes nothing");
            Assert(hud.Hide() == (UiPresenceEffects.HideNative | UiPresenceEffects.Dismissed), "hiding a showing HUD dismisses it");
            hud.SetGameplay(false);
            Assert(hud.Show() == UiPresenceEffects.None && !hud.IsVisible && hud.Requested,
                "shown at the main menu it waits, reported not visible, and appears with gameplay");
            Assert(hud.SetGameplay(true) == UiPresenceEffects.ShowNative && hud.IsVisible, "here it is");
        }

        private static void ADestroyedSurfaceIsNeverReportedVisible()
        {
            var hud = new UiSurfacePresence(true, gameplay: true);
            Assert(hud.IsVisible, "a HUD shows in a world");
            Assert(hud.Lose() == UiPresenceEffects.Dismissed, "another mod destroys its canvas: a visible surface is dismissed");
            Assert(!hud.IsVisible && hud.IsLost && !hud.IsLive, "it is never reported visible again");
            Assert(hud.Show() == UiPresenceEffects.None && hud.SetGameplay(false) == UiPresenceEffects.None
                && hud.SetGameplay(true) == UiPresenceEffects.None && !hud.IsVisible,
                "showing it, or gameplay returning, cannot bring back a canvas that no longer exists");
            Assert(hud.Lose() == UiPresenceEffects.None && hud.Hide() == UiPresenceEffects.None, "and it is reported once");

            var hidden = new UiSurfacePresence(true, gameplay: false);
            Assert(hidden.Lose() == UiPresenceEffects.None && !hidden.IsVisible, "a surface lost while hidden dismisses nothing");
            var window = new UiSurfacePresence(false, gameplay: true);
            window.Lose();
            Assert(!window.IsVisible && window.ClosedByPlayer() == UiPresenceEffects.None, "a lost window ignores a late close");
        }

        private static void APlayerClosedWindowIsDismissedOnce()
        {
            var window = new UiSurfacePresence(followsGameplay: false, gameplay: false);
            Assert(window.ClosedByPlayer() == UiPresenceEffects.Dismissed && !window.IsVisible, "the close button dismisses it");
            Assert(window.ClosedByPlayer() == UiPresenceEffects.None, "a second close notice dismisses nothing");
            Assert(window.Show() == UiPresenceEffects.ShowNative && window.IsVisible, "the mod shows it again");
            Assert(window.Hide() == (UiPresenceEffects.HideNative | UiPresenceEffects.Dismissed), "the mod hides it");
            Assert(window.ClosedByPlayer() == UiPresenceEffects.None, "the close that the mod's own Hide caused is not a second dismissal");
        }

        private static void DisposalNeverDismisses()
        {
            var hud = new UiSurfacePresence(true, true);
            hud.Dispose();
            Assert(!hud.IsVisible && hud.IsDisposed && !hud.IsLive, "a disposed surface is not visible");
            Assert(hud.Lose() == UiPresenceEffects.None && hud.Show() == UiPresenceEffects.None && hud.Hide() == UiPresenceEffects.None,
                "and nothing it hears afterwards dismisses or shows it");
        }

        private static void DefaultAnchorsAvoidTheGamesOwnHud()
        {
            // In every level the game draws its microphone label at the top left, its version at the top, a level's
            // objective at the top right, and dialogue subtitles across the bottom middle. At the main menu, TopiaForge's
            // own buttons sit at the bottom left.
            var gameplay = UiHudAnchors.CornerOf(UiHudAnchor.Default, UiHudVisibility.Gameplay);
            var everywhere = UiHudAnchors.CornerOf(UiHudAnchor.Default, UiHudVisibility.Always);
            Assert(gameplay == TopiaForgeCorner.BottomLeft, "a gameplay HUD docks at the bottom left, clear of the game's HUD");
            Assert(everywhere == TopiaForgeCorner.Left, "a HUD shown everywhere docks at the middle of the left edge, clear of the menu's buttons too");
            foreach (var taken in new[] { TopiaForgeCorner.TopLeft, TopiaForgeCorner.Top, TopiaForgeCorner.TopRight, TopiaForgeCorner.Bottom, TopiaForgeCorner.Center })
            {
                Assert(gameplay != taken && everywhere != taken, "no default sits where the game draws " + taken);
            }
        }

        private static void LowerHudStacksClearSubtitlesAtBothSizesAndScales()
        {
            foreach (var height in new[] { 1080f, 1440f })
            {
                foreach (var scale in new[] { 1f, 1.5f })
                {
                    foreach (var corner in new[] { TopiaForgeCorner.BottomLeft, TopiaForgeCorner.Bottom, TopiaForgeCorner.BottomRight })
                    {
                        var reserve = UiHudAnchors.SubtitleReserve(corner) * height;
                        Assert(Math.Abs(reserve - height * 0.12f) < 0.001f, "subtitles keep the bottom 12% at both resolutions");
                        var dock = new TopiaForgeHudDock();
                        var first = dock.Add(corner, 230f, scale);
                        var second = dock.Add(corner, 130f, 1f);
                        dock.SetShown(first, true);
                        dock.SetShown(second, true);
                        Assert(dock.TryGetPosition(first, out _, out var firstY)
                            && dock.TryGetPosition(second, out _, out _), "both panels have positions");
                        dock.TryGetPosition(second, out _, out var secondY);
                        var screenScale = height / TopiaForgeTokens.ReferenceHeight;
                        var firstBottom = reserve + firstY * scale * screenScale;
                        var secondBottom = reserve + secondY * screenScale;
                        Assert(firstBottom > reserve, "the first panel's bottom stays above subtitles plus the safe margin");
                        Assert(secondBottom > firstBottom + 230f * scale * screenScale,
                            "mixed-scale panels keep their gap above the same subtitle reserve");
                    }
                }
            }

            foreach (var corner in new[] { TopiaForgeCorner.TopLeft, TopiaForgeCorner.Top, TopiaForgeCorner.TopRight, TopiaForgeCorner.Left, TopiaForgeCorner.Right })
            {
                Assert(UiHudAnchors.SubtitleReserve(corner) == 0f, "other edges keep their anchors");
            }
        }

        private static void ALostWindowCannotReopenFromItsDismissal()
        {
            var window = new UiSurfacePresence(false, true);
            var dismissals = 0;
            void Dismiss(UiPresenceEffects effects)
            {
                if ((effects & UiPresenceEffects.Dismissed) == 0) return;
                dismissals++;
                Assert(window.Show() == UiPresenceEffects.None, "a dismissal callback cannot touch the lost native view");
                Assert(window.Hide() == UiPresenceEffects.None, "a callback cannot hide or dismiss it again");
            }

            // The host releases native state, then reports the lost layer, then invokes the captured Closed callback.
            Dismiss(window.Lose());
            Dismiss(window.ClosedByPlayer());
            Dismiss(window.Lose());
            Assert(dismissals == 1 && !window.IsVisible, "a lost window dismisses exactly once even when its callback calls Show");
        }

        private static void EveryAnchorDocksSomewhereOtherThanTheCrosshair()
        {
            var expected = new Dictionary<UiHudAnchor, TopiaForgeCorner>
            {
                [UiHudAnchor.TopLeft] = TopiaForgeCorner.TopLeft,
                [UiHudAnchor.Top] = TopiaForgeCorner.Top,
                [UiHudAnchor.TopRight] = TopiaForgeCorner.TopRight,
                [UiHudAnchor.Left] = TopiaForgeCorner.Left,
                [UiHudAnchor.Right] = TopiaForgeCorner.Right,
                [UiHudAnchor.BottomLeft] = TopiaForgeCorner.BottomLeft,
                [UiHudAnchor.Bottom] = TopiaForgeCorner.Bottom,
                [UiHudAnchor.BottomRight] = TopiaForgeCorner.BottomRight,
            };
            foreach (UiHudAnchor anchor in Enum.GetValues(typeof(UiHudAnchor)))
            {
                foreach (UiHudVisibility visibility in Enum.GetValues(typeof(UiHudVisibility)))
                {
                    var corner = UiHudAnchors.CornerOf(anchor, visibility);
                    Assert(corner != TopiaForgeCorner.Center, anchor + " never docks over the crosshair");
                    Assert(anchor == UiHudAnchor.Default || corner == expected[anchor], anchor + " docks where it says");
                }
            }
        }

        private static void SettledFramesWithAShownHudAllocateNothing()
        {
            // A persistent HUD shown in a world: every frame the runtime samples the scene state and the mod writes the same
            // text. Nothing changes, so nothing is allocated.
            var gate = new UiGameplayGate();
            var hud = new UiSurfacePresence(true, gate.IsGameplay);
            var native = new NativeView(hud);
            using var subscription = gate.Subscribe(gameplay => native.Apply(hud.SetGameplay(gameplay)));
            var dock = new TopiaForgeHudDock();
            var handle = dock.Add(TopiaForgeCorner.BottomLeft, 96f, 1f);
            gate.Observe(true, true, 1, 1);
            dock.SetShown(handle, hud.IsVisible);
            Assert(hud.IsVisible && native.OnScreen, "the HUD shows before the measurement");
            var allocated = Seam.Allocated(() =>
            {
                gate.Observe(true, true, 1, 1);
                hud.Show();
                dock.SetShown(handle, hud.IsVisible);
                _ = hud.IsVisible;
            });
            Assert(allocated == 0, "a settled frame with the HUD shown allocates nothing (" + allocated + " bytes)");

            // A load begins and the next world arrives: the gate tells its listener, the presence hides and shows the HUD,
            // and the dock gives the HUD's space up and back. None of that allocates either, so a scene change costs the
            // gate, the presence and the dock nothing.
            var transitions = Seam.Allocated(() =>
            {
                gate.Observe(true, true, 2, 1);
                dock.SetShown(handle, hud.IsVisible);
                gate.Observe(true, true, 1, 1);
                dock.SetShown(handle, hud.IsVisible);
            });
            Assert(hud.IsVisible && native.OnScreen && native.Dismissals == 0 && dock.TryGetPosition(handle, out _, out _),
                "after the transitions the HUD is back on screen and in its stack, never dismissed");
            Assert(transitions == 0, "the gate's transitions allocate nothing in the gate, the presence or the dock (" + transitions + " bytes)");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }

        private static void AssertThrows<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException("Assertion failed: " + message);
        }

        /// <summary>Stands in for the native view: applies what each change asks for, as the runtime's surface does.</summary>
        private sealed class NativeView
        {
            private readonly UiSurfacePresence presence;

            public NativeView(UiSurfacePresence presence) => this.presence = presence;

            public bool OnScreen { get; private set; }

            public int Shows { get; private set; }

            public int Hides { get; private set; }

            public int Dismissals { get; private set; }

            public void Present() => Apply(presence.IsVisible ? UiPresenceEffects.ShowNative : UiPresenceEffects.HideNative);

            public void Apply(UiPresenceEffects effects)
            {
                if ((effects & UiPresenceEffects.ShowNative) != 0)
                {
                    OnScreen = true;
                    Shows++;
                }
                else if ((effects & UiPresenceEffects.HideNative) != 0)
                {
                    OnScreen = false;
                    Hides++;
                }

                if ((effects & UiPresenceEffects.Dismissed) != 0)
                {
                    Dismissals++;
                }
            }
        }
    }
}
