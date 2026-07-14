using System;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Modal dialogs: scrim backdrop + dialog card (radius 28, 3px orange border — the
    /// launcher dialogTheme). Confirm/Destructive presets replace hand-rolled two-click
    /// confirmation patterns; ESC cancels via the dismiss stack (modals beat windows).
    /// </summary>
    public sealed class TopiaForgeModals
    {
        private readonly UiHost host;

        internal TopiaForgeModals(UiHost host)
        {
            this.host = host;
        }

        /// <summary>Confirmation dialog with a primary confirm action.</summary>
        public void Confirm(string title, string body, string confirmLabel, Action onConfirm, string cancelLabel = "CANCEL")
        {
            Open(title, body, confirmLabel, onConfirm, cancelLabel, destructive: false, TopiaForgeScheme.Paper);
        }

        /// <summary>Destructive confirmation (danger-toned confirm button).</summary>
        public void Destructive(string title, string body, string confirmLabel, Action onConfirm, string cancelLabel = "CANCEL")
        {
            Open(title, body, confirmLabel, onConfirm, cancelLabel, destructive: true, TopiaForgeScheme.Paper);
        }

        /// <summary>HUD-scheme variant for in-gameplay dialogs.</summary>
        public void ConfirmHud(string title, string body, string confirmLabel, Action onConfirm, string cancelLabel = "CANCEL")
        {
            Open(title, body, confirmLabel, onConfirm, cancelLabel, destructive: false, TopiaForgeScheme.Hud);
        }

        /// <summary>
        /// Empty modal shell for custom content (gameplay conversation screens etc.).
        /// The caller fills instance.Content and calls instance.Show()/Close().
        /// </summary>
        public TopiaForgeModalInstance Custom(string title, TopiaForgeScheme scheme = TopiaForgeScheme.Paper, float width = 520f)
        {
            return new TopiaForgeModalInstance(host, title, scheme, width, showTitle: !string.IsNullOrEmpty(title));
        }

        private void Open(string title, string body, string confirmLabel, Action onConfirm, string cancelLabel, bool destructive, TopiaForgeScheme scheme)
        {
            var modal = new TopiaForgeModalInstance(host, title, scheme, 480f, showTitle: true);
            modal.Content.Label(body, TopiaForgeTextStyle.Body);
            var row = modal.Content.Row(TopiaForgeGap.Sm);
            row.Spacer();
            row.Button(cancelLabel, modal.Close, TopiaForgeButtonStyle.Ghost);
            row.Button(confirmLabel, () =>
            {
                modal.Close();
                TopiaForgeCallbacks.Invoke(onConfirm, "Modal confirmation");
            }, destructive ? TopiaForgeButtonStyle.Danger : TopiaForgeButtonStyle.Filled);
            modal.Show();
        }
    }

    /// <summary>A single modal: backdrop canvas + dialog panel, destroyed on close.</summary>
    public sealed class TopiaForgeModalInstance : ITopiaForgeDismissable
    {
        private readonly GameObject canvasRoot;
        private readonly UiHost host;
        private readonly TopiaForgeContainer dialog;
        private readonly UImage backdrop;
        private readonly TopiaForgeCursorLease cursorLease = new TopiaForgeCursorLease();
        private bool open;
        private bool closing;
        private bool tornDown;

        internal TopiaForgeModalInstance(UiHost host, string title, TopiaForgeScheme scheme, float width, bool showTitle)
        {
            this.host = host;
            var layer = host.Layer("modal", TopiaForgeLayerBand.Modal, scheme, interactive: true, persistent: false);
            canvasRoot = layer.Go;
            host.RegisterModal(this);

            var theme = host.Theme(scheme);
            backdrop = canvasRoot.AddComponent<UImage>();
            backdrop.color = theme.Backdrop;
            backdrop.raycastTarget = true;

            var panel = layer.CreateChildGameObject("Dialog");
            var panelRect = (RectTransform)panel.transform;
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(width, 200f);

            var fill = CreateDecor(panel.transform, "Fill", TopiaForgeSprites.Fill(TopiaForgeRadius.Dialog), raycast: true);
            fill.color = theme.Surface;
            var ring = CreateDecor(panel.transform, "Ring", TopiaForgeSprites.Ring(TopiaForgeRadius.Dialog, TopiaForgeTokens.BorderStrong), raycast: false);
            ring.color = theme.OutlineStrong;

            TopiaForgeLayout.ApplyColumn(panel, TopiaForgeGap.Md, TopiaForgeGap.Xl);
            var fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            dialog = new TopiaForgeContainer(host, scheme, panel);
            if (showTitle)
            {
                dialog.Label(title, TopiaForgeTextStyle.Title);
            }

            Content = dialog;
            canvasRoot.SetActive(false);
        }

        /// <summary>Dialog body — add content here before Show().</summary>
        public TopiaForgeContainer Content { get; }

        public event Action? Closed;

        internal GameObject CanvasRoot => canvasRoot;

        TopiaForgeLayerBand ITopiaForgeDismissable.Band => TopiaForgeLayerBand.Modal;

        void ITopiaForgeDismissable.Dismiss()
        {
            Close();
        }

        public void Show()
        {
            if (tornDown)
            {
                throw new ObjectDisposedException(nameof(TopiaForgeModalInstance));
            }

            if (open)
            {
                return;
            }

            open = true;
            canvasRoot.SetActive(true);
            cursorLease.Acquire();
            TopiaForgeDismissStack.Push(this);
            TopiaForgeMotion.ModalIn(dialog);
        }

        public void Close()
        {
            if (tornDown || closing)
            {
                return;
            }

            if (!open)
            {
                CompleteClose();
                return;
            }

            closing = true;
            cursorLease.Release();
            TopiaForgeDismissStack.Remove(this);
            TopiaForgeMotion.ModalOut(dialog, () =>
            {
                CompleteClose();
            });
        }

        internal void Teardown()
        {
            if (tornDown)
            {
                return;
            }

            tornDown = true;
            cursorLease.Release();
            TopiaForgeDismissStack.Remove(this);
            open = false;
            closing = false;
            host.UnregisterModal(this);
            Closed = null;
        }

        private void CompleteClose()
        {
            if (tornDown)
            {
                return;
            }

            tornDown = true;
            cursorLease.Release();
            TopiaForgeDismissStack.Remove(this);
            open = false;
            closing = false;
            host.UnregisterModal(this);
            host.DestroyLayer(canvasRoot);
            RaiseClosed();
        }

        private void RaiseClosed()
        {
            var handlers = Closed;
            Closed = null;
            TopiaForgeCallbacks.Invoke(handlers, "Modal Closed");
        }

        private static UImage CreateDecor(Transform parent, string name, Sprite sprite, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sprite;
            image.type = UImage.Type.Sliced;
            image.raycastTarget = raycast;
            TopiaForgeAnchors.Stretch((RectTransform)go.transform);
            var layout = go.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            return image;
        }
    }
}
