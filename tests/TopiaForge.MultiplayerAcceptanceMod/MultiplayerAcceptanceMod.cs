using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.MultiplayerAcceptance
{
    /// <summary>Observes the public session and records explicit tester answers. It never opens a connection.</summary>
    public sealed class MultiplayerAcceptanceMod : TopiaForgeMod
    {
        private AcceptanceConfig? config;
        private IMultiplayerSession? session;
        private IUiSurface? window;
        private readonly HashSet<string> recorded = new HashSet<string>(StringComparer.Ordinal);
        private bool readyRecorded;

        protected override void OnLoad()
        {
            var loaded = Context.Config.Load(new ConfigDefinition<AcceptanceConfig>(1,
                () => new AcceptanceConfig(), AcceptanceConfig.Validate));
            if (!loaded.TryGetValue(out config) || !Context.TryGetMultiplayer(out session) || session == null)
            {
                Context.Logger.Error("Acceptance needs its reviewed config and multiplayer provider. Restart the runner.");
                return;
            }
            var children = new List<UiNode>();
            foreach (var check in config.Checks)
            {
                if (check.Mode != "manual" || !SupportsManual(check.Id)) continue;
                var current = check;
                children.Add(new UiText(check.Steps));
                children.Add(new UiRow(
                    new UiButton("pass-" + check.Id, "I checked this: pass", () => Confirm(current, true)),
                    new UiButton("fail-" + check.Id, "I checked this: fail", () => Confirm(current, false), UiButtonStyle.Secondary)));
            }
            var created = Context.Ui.CreateSurface(new UiSurfaceRequest("acceptance", "MULTIPLAYER ACCEPTANCE",
                "Waiting for both peers. F9 opens these checks. Confirm only what you performed and observed.",
                UiSurfaceKind.Window, width: 720, height: 600,
                content: new UiScroll(new UiColumn(children), height: 420)));
            created.TryGetValue(out window);
            var input = Context.Input.RegisterAction(new InputActionDefinition("acceptance-window", "Toggle acceptance checks", new[] { InputBinding.Key("F9") }));
            if (input.TryGetValue(out var toggle)) Context.Events.SubscribeUpdate(_ =>
            {
                if (!toggle.WasPressed || window == null) return;
                if (window.IsVisible) window.Hide(); else window.Show();
            });
            Context.Lifetime.Track(session.SubscribeChanged(Observe));
            Observe(session.Snapshot);
        }

        private bool Ready(MultiplayerSessionSnapshot snapshot)
        {
            if (config == null || snapshot.State != MultiplayerSessionState.Ready || !snapshot.LocalParticipantId.HasValue) return false;
            var server = (snapshot.ExecutionSides & MultiplayerExecutionSide.Server) != 0;
            if (server != (config.Peer == "host")) return false;
            var connected = 0;
            for (var i = 0; i < snapshot.Participants.Count; i++) if (snapshot.Participants[i].IsConnected) connected++;
            return connected >= 2;
        }

        private void Observe(MultiplayerSessionSnapshot snapshot)
        {
            if (readyRecorded || !Ready(snapshot)) return;
            readyRecorded = true;
            Context.Logger.Info("TF-MP-SESSION|READY|" + config!.Challenge + "|" + config.Peer);
            window?.SetBody("Both peers are in session. Perform each check, then record its result. F9 toggles this window.");
            foreach (var check in config.Checks)
            {
                if (check.Mode == "manual" && SupportsManual(check.Id)) continue;
                // Host startup is independently checked against V5 outcome and
                // progress by the runner. Other scenarios need their own driver.
                Record(check, check.Id == "multiplayer.host-start" && config.Peer == "host" ? "PASS" : "NOT-RUN");
            }
        }

        private static bool SupportsManual(string id) => id != "multiplayer.voice-ptt" &&
            id != "multiplayer.voice-dialogue" && id != "multiplayer.host-quit" && id != "multiplayer.kick-ban";

        private void Confirm(AcceptanceCheck check, bool passed)
        {
            if (session == null || !Ready(session.Snapshot))
            {
                Context.Ui.ShowToast("Both peers must be in session before recording this check.", UiTone.Warning);
                return;
            }
            Record(check, passed ? "PASS" : "FAIL");
            Context.Ui.ShowToast(passed ? "Check recorded as passed." : "Check recorded as failed.", passed ? UiTone.Success : UiTone.Danger);
        }

        private void Record(AcceptanceCheck check, string result)
        {
            // A failure remains reportable after a pass. Repeated identical
            // observations are silent, including per-frame session callbacks.
            if (!recorded.Add(check.Id + ":" + result)) return;
            var marker = "TF-MP-ACCEPT|" + result + "|" + config!.Challenge + "|" + config.Peer + "|" + check.Id + "|" + check.Mode;
            if (result == "FAIL") Context.Logger.Error(marker); else Context.Logger.Info(marker);
        }
    }
}
