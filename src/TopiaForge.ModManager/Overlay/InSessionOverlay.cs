using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine.InputSystem;
using InputBinding = TopiaForge.Mods.InputBinding;

namespace TopiaForge.ModManager
{
    /// <summary>The game-side HUD lifetime and registered input adapter. The actual views use only kit widgets.</summary>
    internal sealed class InSessionOverlay : IDisposable
    {
        private const string Owner = "io.github.furroxide.topiaforge.modmanager.chat";
        private readonly TopiaForgeModManagerPlugin plugin;
        private readonly InSessionPresenter screen;
        private readonly InSessionPauseEntries pause;
        private readonly IWorldPauseMenuService? pauseService;
        private readonly CoreGameplayServices? gameplay;
        private readonly List<UnityEngine.InputSystem.InputAction> nativeActions = new List<UnityEngine.InputSystem.InputAction>();
        private readonly InputBinding enterBinding = InputBinding.Key("Return");
        private readonly IInputAction? openKey;
        private readonly IInputAction? returnKey;
        private UiHost? host;
        private ChatOverlay? chat;
        private ConnectionIndicator? connection;
        private TopiaForgeContainer? root;
        private int dock = -1;
        private int scene;

        internal InSessionOverlay(TopiaForgeModManagerPlugin plugin, MultiplayerSessionsPresenter screens, Action showSession)
        {
            this.plugin = plugin;
            screen = screens.InSession;
            gameplay = plugin.Gameplay;
            pauseService = plugin.GetPauseMenu();
            pause = new InSessionPauseEntries(screens.Session, showSession);
            if (gameplay == null) return;
            if (gameplay.Inputs.Register(Owner, new InputActionDefinition("open", "Open session chat", new[] { InputBinding.Key("T") })).TryGetValue(out var key)) openKey = key;
            if (gameplay.Inputs.Register(Owner, new InputActionDefinition("open-return", "Open session chat when Return is free", new[] { enterBinding })).TryGetValue(out var enter)) returnKey = enter;
        }

        internal void Tick()
        {
            var inGame = gameplay?.UiGameplay.IsGameplay == true;
            var world = plugin.GetSessionService()?.Current;
            pause.Tick(pauseService, world, inGame);
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene != activeScene || !inGame || !screen.IsAvailable) { screen.Close(); chat?.Close(); scene = activeScene; }
            if (screen.IsAvailable && host == null) Build();
            if (inGame && screen.IsAvailable && !screen.IsOpen && TopiaForgeDismissStack.Count == 0 &&
                (openKey?.WasPressed == true || (returnKey?.WasPressed == true && ReturnIsFree()))) screen.Open();
            chat?.Tick(inGame);
            connection?.Tick(inGame);
            root?.SetShown(inGame && screen.IsAvailable);
            if (dock >= 0) gameplay!.HudPlacement.SetShown(dock, inGame && screen.IsAvailable);
            if (plugin.Multiplayer != null) plugin.Multiplayer.IsLocalPlayerTyping = screen.IsTyping;
        }

        private bool ReturnIsFree()
        {
            if (gameplay!.Inputs.IsBoundByOther(Owner, enterBinding)) return false;
            nativeActions.Clear();
            InputSystem.ListEnabledActions(nativeActions);
            for (var index = 0; index < nativeActions.Count; index++)
            {
                var controls = nativeActions[index].controls;
                for (var control = 0; control < controls.Count; control++)
                    if (controls[control].device is Keyboard && (controls[control].name == "enter" || controls[control].name == "numpadEnter")) return false;
            }
            return true;
        }

        private void Build()
        {
            host = TopiaForgeUi.Create(new TopiaForgeUiOptions { OwnerId = Owner });
            host.LayerLost += Lost;
            var layer = host.Layer("chat", TopiaForgeLayerBand.Hud, TopiaForgeScheme.Hud, interactive: true, persistent: true);
            root = layer.Stack("InSession").Size(600f, 330f);
            if (gameplay != null) dock = gameplay.HudPlacement.Add(host, root.Rect, TopiaForgeCorner.BottomLeft, 330f);
            var column = root.Column(TopiaForgeGap.Sm);
            column.Stretch();
            connection = new ConnectionIndicator(column, screen);
            var body = column.Stack("Chat").Flex(1f, 1f);
            chat = new ChatOverlay(body, screen);
        }

        private void Lost(UnityEngine.GameObject lost)
        {
            // A canvas destroyed outside the kit releases typing immediately. Rebuild on the next frame.
            screen.Close();
            if (plugin.Multiplayer != null) plugin.Multiplayer.IsLocalPlayerTyping = false;
            chat?.Lost();
            chat = null;
            connection = null;
            root = null;
            if (dock >= 0) gameplay!.HudPlacement.Remove(dock);
            dock = -1;
            var previous = host;
            host = null;
            if (previous != null) { previous.LayerLost -= Lost; previous.Dispose(); }
        }

        public void Dispose()
        {
            screen.Close();
            if (plugin.Multiplayer != null) plugin.Multiplayer.IsLocalPlayerTyping = false;
            chat?.Dispose();
            pause.Dispose();
            openKey?.Dispose();
            returnKey?.Dispose();
            if (dock >= 0) gameplay!.HudPlacement.Remove(dock);
            dock = -1;
            if (host != null) { host.LayerLost -= Lost; host.Dispose(); host = null; }
        }
    }
}
