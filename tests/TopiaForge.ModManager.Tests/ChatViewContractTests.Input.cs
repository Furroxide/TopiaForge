namespace TopiaForge.ModManager.Tests
{
    internal static partial class ChatViewContractTests
    {
        // Real views, dismiss order, event gate, typing-start state and text input. Only Unity/TMP dispatch is doubled;
        // UiKitInputOwnershipConventionTests pins the running filter's wiring to these same decisions.
        private const string InputProbe = """
            using System;
            using TopiaForge.ModManager.Sessions;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.EventSystems;

            public static class InputProbe
            {
                private static TopiaForgeInputGate gate = new TopiaForgeInputGate();
                private static readonly bool[] physical = new bool[128];
                private static readonly float[] game = new float[128];
                private static int frame;
                public static bool AnyKeyHeld => Array.IndexOf(physical, true) >= 0 || gate.HoldsAnyOn(TopiaForgeInputDevice.Keyboard);
                private static TopiaForgeInputField Field => TopiaForgeInputField.Last!;

                public static void Run()
                {
                    TypingAndSubmitReachNothingBeneath();
                    EscapeClosesOnlyChat();
                    OpeningWaitsForEveryHeldKey(false);
                    OpeningWaitsForEveryHeldKey(true);
                    RefocusingASelectedFieldWaitsAndCancels();
                    SelectionAndHudStaySeparate();
                }

                private static (ChatOverlay, Probe.Screen) Start(bool open = true)
                {
                    gate = new TopiaForgeInputGate(); Array.Clear(physical); Array.Clear(game); frame = 0;
                    TopiaForgeDismissStack.Order.Clear(); EventSystem.current = new EventSystem();
                    var parent = new TopiaForgeContainer();
                    var screen = new Probe.Screen();
                    var view = new ChatOverlay(parent, screen);
                    var hud = new ConnectionIndicator(parent, screen); hud.Tick(true);
                    if (open) { screen.Open(); view.Tick(true); Field.Advance(); view.Tick(true); }
                    return (view, screen);
                }

                private static bool Key(int key, bool down)
                {
                    physical[key] = down;
                    var claims = TopiaForgeDismissStack.Order.Claims();
                    if (Field.IsFocused) claims |= TopiaForgeInputClaim.Keyboard;
                    var role = key == 60 ? TopiaForgeInputRole.Back : key == 2 || key == 1 ? TopiaForgeInputRole.Submit
                        : key == 61 ? TopiaForgeInputRole.Direction : TopiaForgeInputRole.Key;
                    var before = game[key];
                    var value = down ? 1f : 0f;
                    if (gate.Examines(TopiaForgeInputDevice.Keyboard, claims)
                        && gate.Take(1, TopiaForgeInputDevice.Keyboard, key, role, value, before, 0, 0, claims, frame)) value = 0;
                    game[key] = value;
                    return before == 0 && value != 0;
                }

                private static void EndFrame(ChatOverlay view, string typed = "")
                {
                    TopiaForgeDismissStack.Order.TakeOnBack(gate.BackHeld, gate.BackPressedIn(frame))?.Dismiss();
                    Field.Advance(typed); view.Tick(true); frame++;
                }

                private static void TypingAndSubmitReachNothingBeneath()
                {
                    var (view, screen) = Start();
                    foreach (var key in new[] { 37, 15, 33, 18, 1, 19 })
                    {
                        Check(!Key(key, true), "typing movement, jump and interaction never reaches the game");
                        EndFrame(view, "x"); Key(key, false); EndFrame(view);
                    }
                    Check(Field.Text == "xxxxxx" && screen.IsTyping, "the composer receives the typed characters");
                    Check(!Key(2, true) && gate.SubmitPressedIn(frame), "Enter belongs to the composer, never the game");
                    Field.Submit();
                    Check(!screen.IsOpen && !screen.IsTyping && TopiaForgeDismissStack.Count == 0, "submit closes and releases chat");
                    for (var index = 0; index < 10; index++)
                    {
                        Check(!Key(2, true), "held Enter never presses beneath the closed composer"); EndFrame(view);
                    }
                    Key(2, false); EndFrame(view);
                    Check(Key(2, true), "only a fresh Enter is available after submit"); Key(2, false); view.Dispose();
                }

                private static void EscapeClosesOnlyChat()
                {
                    var (view, screen) = Start();
                    Check(!Key(60, true), "Escape never opens pause while chat owns it"); EndFrame(view);
                    Check(!screen.IsOpen && !screen.IsTyping && !TopiaForgeCursorLease.Active, "Escape closes chat and releases its cursor");
                    for (var index = 0; index < 10; index++)
                    {
                        Check(!Key(60, true), "holding Escape after chat closes never opens pause"); EndFrame(view);
                    }
                    Key(60, false); EndFrame(view);
                    Check(Key(60, true), "a fresh Escape with only the HUD visible reaches pause"); Key(60, false); view.Dispose();
                }

                private static void OpeningWaitsForEveryHeldKey(bool reverse)
                {
                    var (view, screen) = Start(false);
                    Check(Key(37, true) && Key(34, true), "W and T began in gameplay");
                    screen.Open(); view.Tick(true);
                    for (var index = 0; index < 10; index++) EndFrame(view, "wt");
                    Check(!Field.IsFocused && !screen.IsTyping && Field.Text.Length == 0, "opening chat cannot bypass the held-key wait");
                    Key(reverse ? 34 : 37, false); EndFrame(view, "w");
                    Check(!Field.IsFocused && Field.Text.Length == 0, "releasing only one held key starts nothing");
                    Key(reverse ? 37 : 34, false); EndFrame(view);
                    Check(Field.IsFocused && screen.IsTyping && Field.Text.Length == 0, "releasing every key starts an empty composer");
                    Check(!Key(37, true), "the next fresh W belongs to chat"); EndFrame(view, "w"); Key(37, false);
                    Check(Field.Text == "w", "the fresh key types once"); view.Dispose();
                }

                private static void SelectionAndHudStaySeparate()
                {
                    var (view, screen) = Start();
                    Field.Blur();
                    EventSystem.current!.SetSelectedGameObject(TopiaForgeLabel.Last!.Go);
                    Check(TopiaForgeDismissStack.Order.FocusRoot() == null, "a selection on the connection HUD never claims chat navigation");
                    Check(Key(2, true) && Key(37, true), "HUD selection leaves Enter and movement to the game");
                    Key(2, false); Key(37, false);
                    EventSystem.current.SetSelectedGameObject(TopiaForgeChatLog.Last!.Go);
                    Check(TopiaForgeDismissStack.Order.FocusRoot() == TopiaForgeDismissStack.PushedRoot,
                        "a selection in chat's scrollback navigates inside chat");
                    Check(!Key(2, true) && Key(37, true), "selected chat takes submit but a non-typing window leaves movement to gameplay");
                    Key(2, false); Key(37, false);
                    view.Close();
                    Check(TopiaForgeDismissStack.Count == 0 && Key(61, true), "closed chat and the visible HUD take no navigation");
                    Key(61, false);
                    screen.Open(); view.Tick(true); Field.Advance(); view.Tick(true);
                    TopiaForgeDismissStack.PushedRoot!.activeSelf = false;
                    Field.Blur();
                    Check(TopiaForgeDismissStack.Order.Claims() == TopiaForgeInputClaim.None && Key(60, true),
                        "a hidden chat root takes no input even before removal");
                    Key(60, false); view.Dispose();
                }

                private static void RefocusingASelectedFieldWaitsAndCancels()
                {
                    var (view, screen) = Start();
                    Field.EndEditKeepingSelection();
                    Check(Key(37, true), "W starts in gameplay after editing ends");
                    Field.Focus(); EndFrame(view, "w");
                    Check(!Field.IsFocused && Field.Text.Length == 0, "refocusing a selected field also waits, without another selection event");
                    Key(37, false); EndFrame(view);
                    Check(Field.IsFocused, "the selected field starts after release");
                    Field.EndEditKeepingSelection(); Key(37, true); Field.Focus();
                    var elsewhere = new GameObject(); EventSystem.current!.SetSelectedGameObject(elsewhere);
                    Key(37, false); EndFrame(view);
                    Check(!Field.IsFocused && EventSystem.current.currentSelectedGameObject == elsewhere,
                        "a pending programmatic focus never steals selection back");
                    view.Dispose();
                }

                private static void Check(bool condition, string message)
                { if (!condition) throw new InvalidOperationException("Chat input: " + message); }
            }
            """;
    }
}
