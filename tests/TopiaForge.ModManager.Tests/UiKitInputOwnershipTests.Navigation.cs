using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        // Exact in binary, and not Unity's defaults (0.5 s, then every 0.1 s), so a delay the driver made up shows.
        private const float ModuleDelay = 0.25f;
        private const float ModuleRate = 0.125f;
        private const float FrameTime = 0.0625f;

        private static void TheKitNavigatesAtTheUiModulesPace()
        {
            var driver = new TopiaForgeNavigationDriver();
            var moves = new List<int>();
            Assert(driver.Tick(true, true, false, 0f, 0f, false, FrameTime, ModuleDelay, ModuleRate).IsEmpty, "a claim beginning sends nothing");
            for (var frame = 0; frame < 12; frame++)
            {
                var step = driver.Tick(true, true, false, 0f, -1f, false, FrameTime, ModuleDelay, ModuleRate);
                Assert(!step.Submit && !step.SelectFirst, "a held Down only moves (frame " + frame + ")");
                if (step.Move != TopiaForgeMoveDirection.None)
                {
                    AssertEqual(step.Move, TopiaForgeMoveDirection.Down, "Down moves down");
                    moves.Add(frame);
                }
            }

            AssertEqual(string.Join(",", moves), "0,4,6,8,10",
                "a direction moves at once, again after the UI module's repeat delay, then at its repeat rate");

            var released = driver.Tick(true, true, false, 0f, 0f, false, FrameTime, ModuleDelay, ModuleRate);
            Assert(released.IsEmpty, "letting go sends nothing");
            AssertEqual(driver.Tick(true, true, false, 0.9f, 0.2f, false, FrameTime, ModuleDelay, ModuleRate).Move, TopiaForgeMoveDirection.Right,
                "a stick moves along its longer axis, at once");
            AssertEqual(driver.Tick(true, true, false, 0.3f, 0.1f, false, FrameTime, ModuleDelay, ModuleRate).Move, TopiaForgeMoveDirection.None,
                "and not at all short of a push");

            var submit = driver.Tick(true, true, false, 0f, 0f, true, FrameTime, ModuleDelay, ModuleRate);
            Assert(submit.Submit && submit.Move == TopiaForgeMoveDirection.None, "Enter, Space or A submits the selected control");

            var focus = driver.Tick(true, false, false, 0f, 0f, true, FrameTime, ModuleDelay, ModuleRate);
            Assert(focus.SelectFirst && !focus.Submit, "with none of the surface's controls selected, a press selects its first control and submits nothing");
            var focusByMove = driver.Tick(true, false, false, -1f, 0f, false, FrameTime, ModuleDelay, ModuleRate);
            Assert(focusByMove.SelectFirst && focusByMove.Move == TopiaForgeMoveDirection.None, "and so does a move");

            driver.Tick(true, true, false, 0f, 0f, false, FrameTime, ModuleDelay, ModuleRate);
            var typing = driver.Tick(true, true, true, 0f, 1f, true, FrameTime, ModuleDelay, ModuleRate);
            Assert(typing.IsEmpty, "a text field with the keyboard keeps its own arrows and Enter, as the UI module leaves them");
        }

        private static void TheKitNavigatesOnlyWhileItsSurfaceClaimsNavigation()
        {
            var driver = new TopiaForgeNavigationDriver();
            driver.Tick(true, true, false, 0f, -1f, false, FrameTime, ModuleDelay, ModuleRate);
            for (var frame = 0; frame < 20; frame++)
            {
                var step = driver.Tick(false, true, false, 0f, -1f, frame % 3 == 0, FrameTime, ModuleDelay, ModuleRate);
                Assert(step.IsEmpty, "once the claim ends, nothing is sent, held direction and submits alike (frame " + frame + ")");
            }

            // A direction still held when the next claim begins was pressed for something else: it moves nothing.
            for (var frame = 0; frame < 20; frame++)
            {
                var step = driver.Tick(true, true, false, 0f, -1f, false, FrameTime, ModuleDelay, ModuleRate);
                AssertEqual(step.Move, TopiaForgeMoveDirection.None, "a Down held across surfaces never moves the next one (frame " + frame + ")");
            }

            Assert(driver.Tick(true, true, false, 0f, 0f, false, FrameTime, ModuleDelay, ModuleRate).IsEmpty, "its release sends nothing");
            AssertEqual(driver.Tick(true, true, false, 0f, -1f, false, FrameTime, ModuleDelay, ModuleRate).Move, TopiaForgeMoveDirection.Down,
                "a fresh press moves at once");
            Assert(driver.Tick(true, true, false, 0f, 0f, true, FrameTime, ModuleDelay, ModuleRate).Submit, "and a fresh submit submits");
        }

        private static void NothingIsAllocatedAtRestOrWhileHeld()
        {
            var rig = new Rig();
            var driver = new TopiaForgeNavigationDriver();
            var looked = 0;

            // At rest: a mouse-move flood, and keyboard and controller events with nothing claimed, all pass at once.
            [MethodImpl(MethodImplOptions.NoInlining)]
            long AtRest(int count)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < count; index++)
                {
                    var claims = rig.Order.Claims();
                    looked += rig.Gate.Examines(TopiaForgeInputDevice.Other, claims) ? 1 : 0;
                    looked += rig.Gate.Examines(TopiaForgeInputDevice.Keyboard, claims) ? 1 : 0;
                    looked += rig.Gate.Examines(TopiaForgeInputDevice.Gamepad, claims) ? 1 : 0;
                    looked += rig.Order.TakeOnBack(false, false) == null ? 0 : 1;
                    looked += driver.Tick(rig.Order.FocusRoot() != null, false, false, 0f, 0f, false, FrameTime, ModuleDelay, ModuleRate).IsEmpty ? 0 : 1;
                    looked += TopiaForge.ModManager.InputActionYield.Yields(KeyU, new TopiaForge.ModManager.UiInputFocus(claims, rig.Order.HoldsFocus(), false, false)) ? 1 : 0;
                    looked += rig.Navigation.KeyCount + (rig.Navigation.Moves(W) ? 1 : 0);
                }

                return GC.GetAllocatedBytesForCurrentThread() - before;
            }

            BindTheGamesNavigation(rig.Navigation);
            AssertEqual(Fewest(AtRest), 0L, "at rest the filter's front door, the claims, Back and the navigation allocate nothing");
            AssertEqual(looked, 0, "and nothing at rest is looked at: every event passes at once");

            // Held: the overlay shows, Down and the stick are held, and keyboard, controller and mouse events keep coming.
            var overlayRoot = new Root();
            rig.Order.Push(new Surface(TopiaForgeLayerBand.Window), TopiaForgeInputClaim.Navigation, overlayRoot);
            rig.Order.Push(new Surface(TopiaForgeLayerBand.Window), TopiaForgeInputClaim.NavigationWhileSelected, new Root());
            rig.Navigation.Listening = true;
            rig.Key(DownArrow, true);
            rig.Stick(LeftStick, 0f, -0.9f);
            var mouse = 0;
            var steered = 0f;

            [MethodImpl(MethodImplOptions.NoInlining)]
            long Held(int count)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < count; index++)
                {
                    var claims = rig.Order.Claims();
                    mouse += rig.Gate.Examines(TopiaForgeInputDevice.Other, claims) ? 1 : 0;
                    rig.Gate.Take(KeyboardId, TopiaForgeInputDevice.Keyboard, DownArrow, TopiaForgeInputRole.Direction, 1f, 0f, 0f, -1f, claims, index);
                    rig.Gate.Take(KeyboardId, TopiaForgeInputDevice.Keyboard, W, TopiaForgeInputRole.Key, 1f, 1f, 0f, 0f, claims, index);
                    rig.Gate.Take(GamepadId, TopiaForgeInputDevice.Gamepad, LeftStick, TopiaForgeInputRole.Direction, 0.9f, 0f, 0f, -0.9f, claims, index);
                    rig.Navigation.Direction(W, out var keyX, out var keyY);
                    steered += rig.Navigation.RoleOf(W) == TopiaForgeInputRole.Direction ? keyY : keyX;
                    rig.Gate.Direction(out var x, out var y);
                    rig.Order.TakeOnBack(rig.Gate.BackHeld, rig.Gate.BackPressedIn(index));
                    TopiaForge.ModManager.InputActionYield.Yields(KeyW, new TopiaForge.ModManager.UiInputFocus(claims, false, false, true, rig.Navigation));
                    driver.Tick(rig.Order.FocusRoot() != null, true, false, x, y, rig.Gate.SubmitPressedIn(index), FrameTime, ModuleDelay, ModuleRate);
                }

                return GC.GetAllocatedBytesForCurrentThread() - before;
            }

            AssertEqual(Fewest(Held), 0L, "while keys are held, every event and frame allocates nothing");
            AssertEqual(mouse, 0, "and a mouse moving while the overlay shows still passes at once");
            Assert(steered > 0f, "the game's navigation keys were looked up throughout, W pointing up");
            Assert(rig.Gate.IsTaken(KeyboardId, DownArrow) && rig.Gate.IsTaken(GamepadId, LeftStick), "the held presses stayed TopiaForge's throughout");
        }

        /// <summary>
        /// The fewest bytes a run allocates, over one unmeasured run and up to three measured ones: work the runtime does on
        /// this thread while code first runs happens once, while an allocation on a per-event path shows in every run.
        /// </summary>
        private static long Fewest(Func<int, long> run)
        {
            run(20000);
            var fewest = long.MaxValue;
            for (var pass = 0; pass < 3 && fewest != 0; pass++)
            {
                fewest = Math.Min(fewest, run(20000));
            }

            return fewest;
        }
    }
}
