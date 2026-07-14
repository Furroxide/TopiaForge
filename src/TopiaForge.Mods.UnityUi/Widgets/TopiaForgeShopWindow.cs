using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Entry point for the ten-line shop: <c>ui.ShopWindow(...)</c>.</summary>
    public static class TopiaForgeShopUi
    {
        /// <summary>
        /// A brand window hosting a <see cref="TopiaForgeShopPane"/>. The window contributes ESC/X close,
        /// cursor lease, drag + rect persistence; wire <see cref="TopiaForgeShopPane.Purchased"/> for effects
        /// and <see cref="TopiaForgeShopWindow.Closed"/> to resume whatever the shop paused.
        /// </summary>
        public static TopiaForgeShopWindow ShopWindow(
            this UiHost host,
            string id,
            string title,
            IReadOnlyList<ShopItem> catalog,
            IShopWallet wallet,
            TopiaForgeShopPaneOptions? options = null,
            float width = 560f,
            float height = 520f,
            bool persistent = false)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            var window = host.Window(id, title, width, height, TopiaForgeScheme.Paper, persistent);
            var pane = new TopiaForgeShopPane(window.Content, catalog, wallet, options);
            return new TopiaForgeShopWindow(window, pane);
        }
    }

    /// <summary>A <see cref="TopiaForgeShopPane"/> hosted in a standard kit window.</summary>
    public sealed class TopiaForgeShopWindow
    {
        internal TopiaForgeShopWindow(TopiaForgeWindow window, TopiaForgeShopPane pane)
        {
            Window = window;
            Pane = pane;
        }

        public TopiaForgeWindow Window { get; }
        public TopiaForgeShopPane Pane { get; }

        public bool IsOpen => Window.IsOpen;

        /// <summary>Fires when the window closes — ESC and the X button alike.</summary>
        public event Action? Closed
        {
            add => Window.Closed += value;
            remove => Window.Closed -= value;
        }

        public void Show()
        {
            Window.Show();
        }

        public void Close()
        {
            Window.Close();
        }

        public void Toggle()
        {
            Window.Toggle();
        }

        /// <summary>Per-frame poke while open; dirty-checked and free at steady state.</summary>
        public void Tick()
        {
            if (Window.IsOpen)
            {
                Pane.Tick();
            }
        }
    }
}
