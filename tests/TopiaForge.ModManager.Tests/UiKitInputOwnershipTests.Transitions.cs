using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        private static void HeldNavigationDoesNotCrossDirectSurfaceChanges()
        {
            var driver = new TopiaForgeNavigationDriver();
            var overlay = new object();
            var modal = new object();
            driver.Tick(true, true, false, 0f, 0f, false, FrameTime, ModuleDelay, ModuleRate, modal);
            AssertEqual(driver.Tick(true, true, false, 0f, -1f, false, FrameTime, ModuleDelay, ModuleRate, modal).Move,
                TopiaForgeMoveDirection.Down, "Down begins navigating the modal");

            // Back removes the modal before navigation runs. The overlay beneath still claims navigation, so there is
            // no unclaimed frame between the two surfaces. A submit queued with Back belongs to the dismissed modal too.
            for (var frame = 0; frame < 12; frame++)
            {
                var step = driver.Tick(true, true, false, 0f, -1f, frame == 0, FrameTime, ModuleDelay, ModuleRate, overlay);
                Assert(step.IsEmpty, "the modal's held Down and simultaneous submit never act on the overlay (frame " + frame + ")");
            }

            driver.Tick(true, true, false, 0f, 0f, false, FrameTime, ModuleDelay, ModuleRate, overlay);
            AssertEqual(driver.Tick(true, true, false, 0f, -1f, false, FrameTime, ModuleDelay, ModuleRate, overlay).Move,
                TopiaForgeMoveDirection.Down, "release and a fresh Down navigate the overlay");

            // Opening a modal while holding a direction is the same handover in the other direction.
            for (var frame = 0; frame < 12; frame++)
            {
                Assert(driver.Tick(true, false, false, 0f, -1f, false, FrameTime, ModuleDelay, ModuleRate, modal).IsEmpty,
                    "the overlay's held direction cannot select or move the new modal (frame " + frame + ")");
            }

            driver.Tick(true, false, false, 0f, 0f, false, FrameTime, ModuleDelay, ModuleRate, modal);
            Assert(driver.Tick(true, false, false, 0f, -1f, false, FrameTime, ModuleDelay, ModuleRate, modal).SelectFirst,
                "a fresh direction selects the new modal's first control");
        }

        private static void BackOnlyWindowsDoNotBlockTheNavigationOwner()
        {
            var rig = new Rig();
            var overlay = new Surface(TopiaForgeLayerBand.Window);
            var overlayRoot = new Root();
            var window = new Surface(TopiaForgeLayerBand.Window);
            var windowRoot = new Root();
            rig.Order.Push(overlay, TopiaForgeInputClaim.Navigation, overlayRoot);
            rig.Order.Push(window, TopiaForgeInputClaim.NavigationWhileSelected, windowRoot);
            AssertSame(rig.Order.Top(), window, "Back still dismisses the latest window first");
            Assert(!rig.Key(DownArrow, true), "the overlay still takes navigation from the game");
            AssertSame(rig.Order.FocusRoot(), overlayRoot, "an unselected window cannot strand the overlay's navigation");

            rig.Selection = windowRoot;
            AssertSame(rig.Order.FocusRoot(), windowRoot, "selecting the later window makes it the navigation owner");
            var later = new Surface(TopiaForgeLayerBand.Window);
            rig.Order.Push(later, TopiaForgeInputClaim.NavigationWhileSelected, new Root());
            AssertSame(rig.Order.FocusRoot(), windowRoot, "another unselected window cannot starve the selected one");

            var capture = new Surface(TopiaForgeLayerBand.Modal);
            var captureRoot = new Root();
            rig.Order.Push(capture, TopiaForgeInputClaim.Keyboard, captureRoot);
            AssertSame(rig.Order.FocusRoot(), null, "a key capture blocks navigation beneath it");
            captureRoot.Live = false;
            AssertSame(rig.Order.FocusRoot(), windowRoot, "a hidden or destroyed capture no longer blocks navigation");
            windowRoot.Live = false;
            AssertSame(rig.Order.FocusRoot(), overlayRoot, "a hidden selected window gives navigation back to the overlay");
            overlayRoot.Live = false;
            AssertSame(rig.Order.FocusRoot(), null, "only Back-only windows remain, so no surface owns navigation");
            AssertEqual(rig.Claims, TopiaForgeInputClaim.Back, "no hidden surface strands a navigation claim");
        }

        private static void DeviceAndControlBoundsDoNotStealOtherInput()
        {
            var gate = new TopiaForgeInputGate();
            for (var device = 1; device <= TopiaForgeInputGate.DeviceSlots; device++)
            {
                Assert(gate.Take(device, TopiaForgeInputDevice.Keyboard, Escape, TopiaForgeInputRole.Back,
                    1f, 0f, 0f, 0f, TopiaForgeInputClaim.Back, 1), "each available slot holds its own keyboard");
            }

            foreach (var invalid in new[] { -1, TopiaForgeInputGate.ControlsPerDevice, int.MaxValue })
            {
                Assert(!gate.Take(1, TopiaForgeInputDevice.Keyboard, invalid, TopiaForgeInputRole.Back,
                    1f, 0f, 0f, 0f, AllClaims, 2), "an unsupported key is never taken");
                Assert(!gate.IsTaken(1, invalid) && !gate.KeyPressedIn(invalid, 2), "an unsupported key aliases no tracked key");
            }

            Assert(!gate.Take(99, TopiaForgeInputDevice.Keyboard, Escape, TopiaForgeInputRole.Back,
                1f, 0f, 0f, 0f, AllClaims, 2), "an overflow device cannot evict an existing held key");
            gate.Forget(99);
            AssertEqual(gate.Held, TopiaForgeInputGate.DeviceSlots, "forgetting an overflow device releases no other device");
            for (var device = 1; device <= TopiaForgeInputGate.DeviceSlots; device++)
            {
                Assert(gate.IsTaken(device, Escape), "overflow leaves each original key owned until its release");
                Assert(!gate.Take(device, TopiaForgeInputDevice.Keyboard, B, TopiaForgeInputRole.Key,
                    1f, 0f, 0f, 0f, TopiaForgeInputClaim.None, 2), "held Back never broadens the claim to another key");
            }

            gate.ForgetAll();
            AssertEqual(gate.Held, 0, "all device slots can be released after overflow");
            Assert(gate.Take(99, TopiaForgeInputDevice.Keyboard, Escape, TopiaForgeInputRole.Back,
                1f, 0f, 0f, 0f, TopiaForgeInputClaim.Back, 3), "a freed slot can then hold the new keyboard");
        }
    }
}
