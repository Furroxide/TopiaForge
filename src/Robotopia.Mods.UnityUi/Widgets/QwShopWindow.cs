using System;
using System.Collections.Generic;
using Robotopia.Mods;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>Entry point for the ten-line shop: <c>ui.ShopWindow(...)</c>.</summary>
    public static class QwShopUi
    {
        /// <summary>
        /// A brand window hosting a <see cref="QwShopPane"/>. The window contributes ESC/X close,
        /// cursor lease, drag + rect persistence; wire <see cref="QwShopPane.Purchased"/> for effects
        /// and <see cref="QwShopWindow.Closed"/> to resume whatever the shop paused.
        /// </summary>
        public static QwShopWindow ShopWindow(
            this UiHost host,
            string id,
            string title,
            IReadOnlyList<ShopItem> catalog,
            IShopWallet wallet,
            QwShopPaneOptions? options = null,
            float width = 560f,
            float height = 520f,
            bool persistent = false)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            var window = host.Window(id, title, width, height, QwScheme.Paper, persistent);
            var pane = new QwShopPane(window.Content, catalog, wallet, options);
            return new QwShopWindow(window, pane);
        }
    }

    /// <summary>A <see cref="QwShopPane"/> hosted in a standard kit window.</summary>
    public sealed class QwShopWindow
    {
        internal QwShopWindow(QwWindow window, QwShopPane pane)
        {
            Window = window;
            Pane = pane;
        }

        public QwWindow Window { get; }
        public QwShopPane Pane { get; }

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
