using System;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>The hold-Tab lifetime and pooled avatar labels. All rendering and input ownership use the kit.</summary>
    internal sealed class ScoreboardOverlay : IDisposable, ITopiaForgeDismissable
    {
        private const string Owner = "io.github.furroxide.topiaforge.modmanager.scoreboard";
        private readonly TopiaForgeModManagerPlugin plugin;
        private readonly ScoreboardPresenter screen;
        private readonly ScoreboardHold hold = new ScoreboardHold();
        private readonly ScoreboardNametagFeed feed = new ScoreboardNametagFeed();
        private readonly Func<VoiceChatRuntime?> voice;
        private readonly TopiaForgeCursorLease cursor = new TopiaForgeCursorLease();
        private readonly IInputAction? key;
        private readonly TopiaForgeWorldPointAnchor[] anchors = new TopiaForgeWorldPointAnchor[ScoreboardNametagFeed.Capacity];
        private readonly TopiaForgeNametag?[] tags = new TopiaForgeNametag?[ScoreboardNametagFeed.Capacity];
        private UiHost? host;
        private TopiaForgeHudLayer? names;
        private TopiaForgeContainer? root;
        private ScoreboardView? view;
        private bool open;
        private int scene;
        internal ScoreboardOverlay(TopiaForgeModManagerPlugin plugin, MultiplayerSessionsPresenter sessions, Func<VoiceChatRuntime?> voice)
        {
            this.plugin = plugin; this.voice = voice;
            screen = sessions.CreateScoreboard(() => plugin.Players?.Scoreboard ?? Array.Empty<ScoreboardData>(), voice);
            if (plugin.Gameplay?.Inputs.Register(Owner, new InputActionDefinition("hold", "Hold scoreboard", new[] { InputBinding.Key("Tab") })).TryGetValue(out var registered) == true) key = registered;
        }
        TopiaForgeLayerBand ITopiaForgeDismissable.Band => TopiaForgeLayerBand.Window;
        void ITopiaForgeDismissable.Dismiss() { hold.Dismiss(); SetOpen(false); }

        internal void Tick()
        {
            screen.Tick();
            var inGame = plugin.Gameplay?.UiGameplay.IsGameplay == true;
            var nextScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            var top = TopiaForgeDismissStack.Top();
            var events = UnityEngine.EventSystems.EventSystem.current;
            var typing = TopiaForgeInputOwnership.KitTextFieldHasKeyboard() ||
                TopiaForgeGameTextFocus.Current.HasKeyboard(events == null ? null : events.currentSelectedGameObject);
            var blocked = typing || voice()?.PushToTalkSuspended == true || (top != null && !ReferenceEquals(top, this)) || nextScene != scene;
            scene = nextScene;
            var show = hold.Tick(key?.IsHeld == true, key?.WasPressed == true, blocked, inGame && screen.Available);
            if (host == null && inGame && screen.Available) Build();
            SetOpen(show);
            if (open) view?.Tick();
            feed.Tick(plugin.Nametags, inGame && screen.Available);
            names?.SetShown(inGame && screen.Available);
            if (host == null) return;
            for (var i = 0; i < anchors.Length; i++)
            {
                anchors[i].Active = feed.Active[i];
                if (!feed.Active[i]) continue;
                var p = feed.Positions[i]; anchors[i].Position = new Vec3(p.X, p.Y, p.Z);
                tags[i]?.SetName(feed.Names[i]);
            }
        }
        private void Build()
        {
            host = TopiaForgeUi.Create(new TopiaForgeUiOptions { OwnerId = Owner });
            host.LayerLost += Lost;
            names = host.HudLayer("player-names", persistent: true);
            var labels = names.Nametags(anchors.Length);
            for (var i = 0; i < anchors.Length; i++)
            {
                anchors[i] = new TopiaForgeWorldPointAnchor(Vec3.Zero) { Active = false };
                tags[i] = labels.Track(anchors[i], "", verticalOffset: 0f);
            }
            var layer = host.Layer("scoreboard", TopiaForgeLayerBand.Window, TopiaForgeScheme.Hud, interactive: true, persistent: true);
            root = layer.Panel(TopiaForgePanelStyle.Card);
            root.Rect.anchorMin = new UnityEngine.Vector2(0.06f, 0.10f);
            root.Rect.anchorMax = new UnityEngine.Vector2(0.94f, 0.90f);
            root.Rect.offsetMin = UnityEngine.Vector2.zero; root.Rect.offsetMax = UnityEngine.Vector2.zero;
            var body = root.Column(TopiaForgeGap.Sm, TopiaForgeGap.Md); body.Stretch();
            body.Label("PLAYERS · Hold Tab", TopiaForgeTextStyle.Title).FixedHeight(32f);
            view = new ScoreboardView(body, screen);
            root.SetShown(false);
        }
        private void SetOpen(bool value)
        {
            if (root == null || open == value) return;
            open = value; root.SetShown(value); cursor.SetActive(value);
            if (value)
            {
                TopiaForgeDismissStack.Push(this, TopiaForgeInputClaim.NavigationWhileSelected, root.Go);
                view?.Focus();
            }
            else { root.ReleaseFocus(); TopiaForgeDismissStack.Remove(this); }
        }
        private void Lost(UnityEngine.GameObject lost)
        {
            hold.Dismiss(); SetOpen(false); DropHost();
        }
        private void DropHost()
        {
            var previous = host; host = null; root = null; view = null; names = null;
            if (previous != null) { previous.LayerLost -= Lost; previous.Dispose(); }
        }
        public void Dispose()
        {
            hold.Dismiss(); SetOpen(false); cursor.Release(); TopiaForgeDismissStack.Remove(this); key?.Dispose(); DropHost();
        }
    }
}
