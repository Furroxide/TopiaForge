namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiOverlayNativeTests
    {
        private const string EscapeScenarios = """
            using System;
            using TopiaForge.Mods;
            using TopiaForge.Mods.Testing;
            using TopiaForge.TestSupport;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.InputSystem;
            using UnityEngine.InputSystem.Controls;
            using UnityEngine.InputSystem.LowLevel;

            namespace TopiaForge.Mods.UnityUi
            {
                internal static partial class OverlayProbe
                {
                    private static void EscapeRouting()
                    {
                        EscapeAndListeningField();
                        KeyboardPauseRetainsTheOverlay();
                        PressOwnershipSurvivesAChangeOfSurface();
                        HigherOwnersAndTypingKeepTheirBack();
                        DeviceResetReleasesPauseOwnership();
                        Console.WriteLine("Overlay Escape passed at the Input System and legacy dismiss boundaries.");
                    }

                    private static void EscapeAndListeningField()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        var overlay = New(host, true, dismissible: false);
                        var inputs = new FakeInputService(new FakeModLifetime());
                        using var action = inputs.RegisterAction(
                            new InputActionDefinition("probe", "Probe action", new[] { InputBinding.Key("F") })).Value!;
                        var field = new TopiaForgeInputBindingField(overlay.Content,
                            new UiKeyBindingField("probe", "probe"), host.OwnerId, () => (action, "Probe action"), (_, _) => null);
                        var keyboard = new Keyboard(40);
                        overlay.Show();
                        field.RebindButton.Button.Select();
                        var group = overlay.Go.GetComponent<CanvasGroup>();
                        group.interactable = false;
                        Check(!field.BeginCapture(), "an already-covered field cannot start listening");
                        group.interactable = true;
                        Check(field.BeginCapture() && field.CanReceiveCapture,
                            "the real field stays listening when its own Back surface disables the overlay CanvasGroup");
                        Check(!overlay.Go.GetComponent<CanvasGroup>().interactable && !field.RebindButton.Button.IsInteractable(),
                            "an active capture still suspends the lower overlay controls");
                        Check(SendEscape(keyboard, true) == 0f && !keyboard.escapeKey.isPressed &&
                            TopiaForgeBindingCaptureInput.Took(InputBinding.Key("Escape")),
                            "a listening key field owns and masks Escape before the pause action can see it");
                        TopiaForgeDismissStack.TickEscape();
                        Check(field.State == TopiaForgeInputBindingFieldState.Reserved && field.StatusText == "Reserved by Back" &&
                            action.Bindings[0].Equals(InputBinding.Key("F")) && TopiaForgeDismissStack.Top() == overlay,
                            "Escape cancels only the binding capture and leaves the action and overlay intact");
                        overlay.RefreshFocus();
                        Check(field.RebindButton.Button.IsInteractable() &&
                            EventSystem.current!.currentSelectedGameObject == field.RebindButton.Go,
                            "ending capture restores the selected overlay control");
                        Check(SendEscape(keyboard, true) == 0f, "a held captured Escape stays masked until its release");
                        SendEscape(keyboard, false);
                        TopiaForgeDismissStack.TickEscape();
                        Check(field.BeginCapture(), "capture can begin again after Escape is released");
                        var higher = new Surface(TopiaForgeLayerBand.Modal);
                        higher.Push();
                        Check(!field.CanReceiveCapture, "a newly opened higher modal keeps the listener from receiving keys");
                        field.Tick();
                        Check(field.State == TopiaForgeInputBindingFieldState.Idle && TopiaForgeDismissStack.Top() == higher,
                            "the covered listener cancels without taking higher Back ownership");
                        higher.Dispose();
                        overlay.Retire(() => { });
                    }

                    private static void KeyboardPauseRetainsTheOverlay()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        var overlay = New(host, true, dismissible: false);
                        Control(overlay);
                        var selected = Control(overlay);
                        var closeEvents = 0;
                        overlay.Closed += () => closeEvents++;
                        overlay.Show();
                        selected.Select();

                        var keyboard = new Keyboard(41);
                        var pad = new Gamepad(42);
                        Check(SendEscape(keyboard, true) == 1f && keyboard.escapeKey.isPressed &&
                            !TopiaForgeInput.BackHeld() && !TopiaForgeInput.BackPressedThisFrame(),
                            "fresh keyboard Escape passes to pause without also entering the dismiss stack");
                        Check(Send(keyboard, keyboard.upArrowKey, 1f) == 0f,
                            "yielding Escape leaves ordinary keyboard navigation owned by the overlay");
                        Send(keyboard, keyboard.upArrowKey, 0f);
                        Check(Send(pad, pad.startButton, 1f) == 1f && pad.startButton.isPressed,
                            "controller Start still reaches the game");
                        Check(Send(pad, pad.buttonEast, 1f) == 0f && TopiaForgeInput.BackPressedThisFrame(),
                            "controller B still enters the kit Back stack with Escape passed to pause");
                        TopiaForgeDismissStack.TickEscape();
                        Check(TopiaForgeDismissStack.Top() == overlay && closeEvents == 0,
                            "controller B leaves the pinned overlay open");
                        Send(pad, pad.buttonEast, 0f);

                        TopiaForgeOverlayStack.SetGamePaused(true);
                        Check(!overlay.Go.activeInHierarchy && TopiaForgeDismissStack.Top() == null && closeEvents == 0,
                            "the existing pause callback suspends the overlay without dismissing it");
                        Check(SendEscape(keyboard, false, delta: true) == 0f &&
                            !TopiaForgeInput.BackHeld() && !TopiaForgeInput.BackPressedThisFrame(),
                            "release is observed with no UI claim and does not become a Back");
                        TopiaForgeDismissStack.TickEscape();
                        Time.frameCount += 2;
                        Check(!TopiaForgeInputOwnership.SuppressesLegacyEscape, "a released pause Escape leaves no held ownership");

                        TopiaForgeOverlayStack.SetGamePaused(false);
                        Check(overlay.Go.activeInHierarchy && closeEvents == 0 && TopiaForgeDismissStack.Top() == overlay &&
                            EventSystem.current!.currentSelectedGameObject == selected.gameObject,
                            "the same overlay and selected control return when pause ends");
                        var atRest = new InputEventPtr(typeof(DeltaStateEvent), (keyboard.escapeKey, 0f));
                        var bytes = AllocationProbe.Measure("overlay Escape routing", () =>
                        {
                            Time.frameCount++;
                            InputSystem.Deliver(atRest, keyboard);
                            TopiaForgeDismissStack.TickEscape();
                        }, 10_000, 100);
                        Check(bytes == 0, "10,000 settled Escape dispatches allocate zero managed bytes");
                        Console.WriteLine("10,000 settled Escape dispatches allocated 0 bytes.");
                        overlay.Retire(() => { });
                    }

                    private static void PressOwnershipSurvivesAChangeOfSurface()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        var overlay = New(host, true, dismissible: false);
                        Control(overlay);
                        overlay.Show();
                        var keyboard = new Keyboard(43);
                        var manager = new Surface(TopiaForgeLayerPriority.Manager);
                        Check(SendEscape(keyboard, true) == 1f, "the first Escape is delivered to pause");
                        manager.Push();
                        TopiaForgeDismissStack.TickEscape();
                        Check(manager.Dismissals == 0 && SendEscape(keyboard, true, delta: true) == 1f && !TopiaForgeInput.BackHeld(),
                            "the legacy copy and repeated packets from a yielded press cannot close a newly opened manager");
                        SendEscape(keyboard, false);
                        TopiaForgeDismissStack.TickEscape();
                        Check(SendEscape(keyboard, true) == 0f, "a fresh Escape belongs to the active manager");
                        TopiaForgeDismissStack.TickEscape();
                        Check(manager.Dismissals == 1 && TopiaForgeDismissStack.Top() == overlay,
                            "normal Back dismisses the manager exactly once before returning to the overlay");
                        Check(SendEscape(keyboard, true) == 0f,
                            "a kit-owned Escape cannot open pause when the higher owner closes while it is held");
                        TopiaForgeDismissStack.TickEscape();
                        SendEscape(keyboard, false);
                        TopiaForgeDismissStack.TickEscape();
                        manager.Dispose();

                        var modal = new Surface(TopiaForgeLayerBand.Modal);
                        Check(SendEscape(keyboard, true) == 1f, "the next released Escape can reach pause");
                        Send(keyboard, keyboard.escapeKey, 0f, delta: true);
                        Input.EscapeHeld = false;
                        modal.Push();
                        TopiaForgeDismissStack.TickEscape();
                        Check(modal.Dismissals == 0,
                            "a press and release in one input frame cannot replay as legacy Back to a new modal");
                        Time.frameCount++;
                        Input.EscapeHeld = true;
                        Input.EscapePressed = true;
                        TopiaForgeDismissStack.TickEscape();
                        Check(modal.Dismissals == 0, "a one-frame-delayed legacy copy cannot dismiss the new modal either");
                        Check(Send(keyboard, keyboard.escapeKey, 1f, delta: true) == 0f,
                            "a distinct Input System press can still reach the modal while the old legacy copy is discarded");
                        TopiaForgeDismissStack.TickEscape();
                        Check(modal.Dismissals == 1, "the modal acts once on the freshly claimed Escape");
                        ReleaseBack(keyboard);
                        modal.Dispose();
                        Input.EscapePressed = false;
                        Time.frameCount++;
                        TopiaForgeDismissStack.TickEscape();
                        overlay.Retire(() => { });
                    }

                    private static void HigherOwnersAndTypingKeepTheirBack()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        var pinned = New(host, true, dismissible: false);
                        Control(pinned);
                        pinned.Show();
                        var keyboard = new Keyboard(44);

                        var picker = new Surface(TopiaForgeLayerPriority.Overlay);
                        picker.Push();
                        Check(SendEscape(keyboard, true) == 0f, "an active child picker keeps Escape in its own Back scope");
                        TopiaForgeDismissStack.TickEscape();
                        Check(picker.Dismissals == 1 && TopiaForgeDismissStack.Top() == pinned,
                            "one Escape closes the picker and leaves the pinned overlay open");
                        ReleaseBack(keyboard);
                        picker.Dispose();

                        var consentRoot = TopiaForgeLayers.CreateTrustCanvas("Consent", true);
                        var consent = new TopiaForgeModalInstance { IsTrust = true };
                        TopiaForgeDismissStack.Push(consent, TopiaForgeInputClaim.Navigation, consentRoot);
                        Check(SendEscape(keyboard, true) == 0f, "higher consent keeps Escape");
                        TopiaForgeDismissStack.TickEscape();
                        Check(consent.Dismissals == 1 && TopiaForgeDismissStack.Top() == pinned,
                            "consent receives Back before a lower overlay can yield it");
                        ReleaseBack(keyboard);
                        TopiaForgeLayers.Release(consentRoot);

                        var typing = pinned.Content.CreateChildGameObject("Typing");
                        typing.AddComponent<TopiaForgeTextEntry>();
                        var text = typing.AddComponent<TMPro.TMP_InputField>();
                        text.isFocused = true;
                        EventSystem.current!.SetSelectedGameObject(typing);
                        Check(SendEscape(keyboard, true) == 0f && TopiaForgeInputOwnership.KeyTaken((int)Key.Escape)
                            && Send(keyboard, keyboard.gallery, 1f) == 0f,
                            "a focused kit input field keeps Escape and its other keyboard input");
                        Send(keyboard, keyboard.gallery, 0f);
                        TopiaForgeDismissStack.TickEscape();
                        ReleaseBack(keyboard);
                        text.isFocused = false;

                        var ordinary = New(host, true);
                        Control(ordinary);
                        ordinary.Show();
                        var closed = 0;
                        ordinary.Closed += () => closed++;
                        Check(SendEscape(keyboard, true) == 0f, "a dismissible overlay still takes keyboard Back");
                        TopiaForgeDismissStack.TickEscape();
                        Check(closed == 1 && !ordinary.Go.activeInHierarchy && pinned.Go.activeInHierarchy,
                            "Escape dismisses the top dismissible overlay exactly once");
                        ReleaseBack(keyboard);
                        ordinary.Retire(() => { });
                        pinned.Retire(() => { });
                    }

                    private static void DeviceResetReleasesPauseOwnership()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        var overlay = New(host, true, dismissible: false);
                        Control(overlay);
                        overlay.Show();
                        var first = new Keyboard(45);
                        var second = new Keyboard(46);
                        Check(SendEscape(first, true) == 1f && Send(second, second.escapeKey, 1f) == 1f,
                            "each keyboard may supply a fresh pause Escape");
                        InputSystem.Change(first, InputDeviceChange.Removed);
                        Time.frameCount += 2;
                        Check(TopiaForgeInputOwnership.SuppressesLegacyEscape, "removing one keyboard keeps the other keyboard's press");
                        InputSystem.Deliver(new InputEventPtr(typeof(DeviceResetEvent)), second);
                        Time.frameCount += 2;
                        Check(!TopiaForgeInputOwnership.SuppressesLegacyEscape, "a device reset retires its own pass through");
                        Input.EscapeHeld = false;
                        Input.EscapePressed = false;
                        second.escapeKey.Current = 0f;

                        Check(SendEscape(second, true) == 1f, "the reset keyboard can pass a new Escape");
                        TopiaForgeInputOwnership.ForgetHeld();
                        Check(!TopiaForgeInputOwnership.SuppressesLegacyEscape, "focus loss clears pause ownership");
                        second.escapeKey.Current = 0f;
                        Input.EscapeHeld = false;
                        Input.EscapePressed = false;
                        Check(SendEscape(second, true) == 1f, "focus recovery starts without a stale press");
                        TopiaForgeInputOwnership.Reset();
                        Check(!TopiaForgeInputOwnership.SuppressesLegacyEscape &&
                            Send(second, second.enterKey, 1f) == 1f, "teardown clears ownership and removes the actual input filter");
                        Input.EscapeHeld = false;
                        Input.EscapePressed = false;
                        overlay.Retire(() => { });
                    }

                    private static void ReleaseBack(Keyboard keyboard)
                    {
                        SendEscape(keyboard, false);
                        TopiaForgeDismissStack.TickEscape();
                    }

                    private static float SendEscape(Keyboard keyboard, bool down, bool delta = false)
                    {
                        Time.frameCount++;
                        Input.EscapePressed = down && !Input.EscapeHeld;
                        Input.EscapeHeld = down;
                        return Send(keyboard, keyboard.escapeKey, down ? 1f : 0f, delta);
                    }

                    private static float Send(InputDevice device, AxisControl control, float value, bool delta = false)
                    {
                        var packet = new InputEventPtr(delta ? typeof(DeltaStateEvent) : typeof(StateEvent), (control, value));
                        InputSystem.Deliver(packet, device);
                        return packet.Value(control);
                    }
                }
            }
            """;
    }
}
