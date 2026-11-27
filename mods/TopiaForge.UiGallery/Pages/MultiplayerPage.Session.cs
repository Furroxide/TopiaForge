using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    internal static partial class MultiplayerPage
    {
        private const int SyncPhase = 5;
        private const int SyncItems = 7;

        private static readonly string[] JoinPhases =
        {
            "RESOLVING", "CONNECTING", "AUTHENTICATING", "VERIFYING MODS", "LOADING WORLD", "SYNCHRONIZING", "SPAWNING",
        };

        private static readonly string[] ChatNames = { "Ada", "Bo", "Cy", "Gil", "Ivy" };

        private static readonly string[] ChatLines =
        {
            "anyone up for zombies?", "brb, my robot is on fire", "nice shot!", "where is the spawn pad?",
            "gg", "lag spike, sorry", "who built the tower lol", "follow me to the plaza",
        };

        private static void BuildStepper(TopiaForgeContainer page, Demo demo)
        {
            page.SectionHeader("PHASE STEPPER");
            page.Label(
                "The joining flow. Auto walks the phases, counts Synchronizing to 7/7 and loops; FAIL pauses it until RETRY or RESET. "
                + "Status reads without color: numbers, check, cross and chevron.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            var stepper = page.PhaseStepper(JoinPhases);
            demo.Stepper = stepper;
            var actions = page.Row(TopiaForgeGap.Sm);
            actions.Button("ADVANCE", demo.Step, TopiaForgeButtonStyle.Outline);
            actions.Button("SKIP MODS", () => stepper.Skip(3), TopiaForgeButtonStyle.Ghost);
            actions.Button("FAIL", () =>
            {
                if (stepper.Fail())
                {
                    stepper.SetDetail(stepper.FailedIndex, "Host refused");
                }
            }, TopiaForgeButtonStyle.Danger);
            actions.Button("RETRY", () =>
            {
                var failed = stepper.FailedIndex;
                if (failed >= 0)
                {
                    stepper.SetDetail(failed, "Retrying");
                    stepper.MoveTo(failed);
                }
            }, TopiaForgeButtonStyle.Ghost);
            actions.Button("RESET", demo.ResetStepper, TopiaForgeButtonStyle.Ghost);
            actions.Toggle("Auto", demo.AutoWalk, value => demo.AutoWalk = value);

            page.Label("Vertical rail: a failed join with a skipped step and detail lines.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            var rail = page.VerticalPhaseStepper("RESOLVING", "CONNECTING", "AUTHENTICATING", "VERIFYING MODS", "LOADING WORLD");
            rail.Advance();
            rail.Advance();
            rail.SetDetail(1, "Direct");
            rail.Advance();
            rail.Skip(3);
            rail.Fail();
            rail.SetDetail(2, "Mod set differs from the host");
        }

        private static void BuildChat(TopiaForgeContainer page)
        {
            page.SectionHeader("CHAT LOG");
            page.Label(
                "Holds 24 lines here and evicts the oldest. Closed, the newest 6 fade after 6 seconds (instantly under reduced motion); "
                + "open, the whole history scrolls. Markup, escapes and line breaks in messages stay literal.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            var random = new Random(7);
            var counter = 0;
            var chat = page.ChatLog(capacity: 24).Timing(6f, 1.5f).ClosedLines(6).FixedHeight(200f);
            chat.Push("Welcome to the gallery session.", TopiaForgeChatLineKind.System);

            var messages = page.Row(TopiaForgeGap.Sm);
            messages.Button("PLAYER", () => chat.Push(ChatNames[random.Next(ChatNames.Length)], ChatLines[random.Next(ChatLines.Length)], TopiaForgeChatLineKind.Player), TopiaForgeButtonStyle.Outline);
            messages.Button("SYSTEM", () => chat.Push("Ivy joined the session.", TopiaForgeChatLineKind.System), TopiaForgeButtonStyle.Outline);
            messages.Button("WHISPER", () => chat.Push("Ada (whisper)", "meet me at the tower", TopiaForgeChatLineKind.Whisper), TopiaForgeButtonStyle.Outline);
            messages.Button("ERROR", () => chat.Push("Unknown command: /tp. Type /help for commands.", TopiaForgeChatLineKind.Error), TopiaForgeButtonStyle.Outline);

            var stress = page.Row(TopiaForgeGap.Sm);
            stress.Button("SPAM 30", () =>
            {
                for (var index = 0; index < 30; index++)
                {
                    counter++;
                    chat.Push("Bo", "spam line " + counter, TopiaForgeChatLineKind.Player);
                }
            }, TopiaForgeButtonStyle.Ghost);
            stress.Button("LONG", () => chat.Push("Cy", "This message keeps going " + new string('.', 400) + " and the log bounds it.", TopiaForgeChatLineKind.Player), TopiaForgeButtonStyle.Ghost);
            stress.Button("MARKUP", () => chat.Push("<b>Mal</b>", "<size=80>HUGE</size> <color=red>red</color>\nsecond line\r\nthird", TopiaForgeChatLineKind.Player), TopiaForgeButtonStyle.Ghost);
            stress.Toggle("Open", false, chat.SetOpen);
            stress.Button("CLEAR", chat.Clear, TopiaForgeButtonStyle.Ghost);
        }

        private sealed partial class Demo
        {
            private float stepTimer;
            private float restartTimer;
            private int syncProgress;

            public TopiaForgePhaseStepper? Stepper { get; set; }

            public bool AutoWalk { get; set; } = true;

            public void Step()
            {
                if (Stepper == null || !Stepper.Advance())
                {
                    return;
                }

                stepTimer = 0f;
                if (Stepper.ActiveIndex == 1)
                {
                    Stepper.SetDetail(1, "Relay");
                }
                else if (Stepper.ActiveIndex == SyncPhase)
                {
                    syncProgress = 0;
                    Stepper.SetProgress(SyncPhase, 0, SyncItems);
                }
            }

            public void ResetStepper()
            {
                Stepper?.Reset();
                stepTimer = 0f;
                restartTimer = 0f;
                syncProgress = 0;
            }

            private void TickStepper(float deltaTime)
            {
                if (!AutoWalk || Stepper == null || Stepper.FailedIndex >= 0)
                {
                    return;
                }

                if (Stepper.IsComplete)
                {
                    restartTimer += deltaTime;
                    if (restartTimer >= 2f)
                    {
                        ResetStepper();
                    }

                    return;
                }

                stepTimer += deltaTime;
                if (Stepper.ActiveIndex == SyncPhase)
                {
                    if (stepTimer >= 0.3f)
                    {
                        stepTimer = 0f;
                        syncProgress++;
                        Stepper.SetProgress(SyncPhase, syncProgress, SyncItems);
                        if (syncProgress >= SyncItems)
                        {
                            Step();
                        }
                    }

                    return;
                }

                if (stepTimer >= 1.1f)
                {
                    Step();
                }
            }
        }
    }
}
