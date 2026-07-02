using System;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Modal dialogs: scrim backdrop + dialog card (radius 28, 3px orange border — the
    /// launcher dialogTheme). Confirm/Destructive presets replace hand-rolled two-click
    /// confirmation patterns; ESC cancels via the dismiss stack (modals beat windows).
    /// </summary>
    public sealed class QwModals
    {
        private readonly UiHost host;

        internal QwModals(UiHost host)
        {
            this.host = host;
        }

        /// <summary>Confirmation dialog with a primary confirm action.</summary>
        public void Confirm(string title, string body, string confirmLabel, Action onConfirm, string cancelLabel = "CANCEL")
        {
            Open(title, body, confirmLabel, onConfirm, cancelLabel, destructive: false, QwScheme.Paper);
        }

        /// <summary>Destructive confirmation (danger-toned confirm button).</summary>
        public void Destructive(string title, string body, string confirmLabel, Action onConfirm, string cancelLabel = "CANCEL")
        {
            Open(title, body, confirmLabel, onConfirm, cancelLabel, destructive: true, QwScheme.Paper);
        }

        /// <summary>HUD-scheme variant for in-gameplay dialogs.</summary>
        public void ConfirmHud(string title, string body, string confirmLabel, Action onConfirm, string cancelLabel = "CANCEL")
        {
            Open(title, body, confirmLabel, onConfirm, cancelLabel, destructive: false, QwScheme.Hud);
        }

        /// <summary>
        /// Empty modal shell for custom content (gameplay conversation screens etc.).
        /// The caller fills instance.Content and calls instance.Show()/Close().
        /// </summary>
        public QwModalInstance Custom(string title, QwScheme scheme = QwScheme.Paper, float width = 520f)
        {
            return new QwModalInstance(host, title, scheme, width, showTitle: !string.IsNullOrEmpty(title));
        }

        private void Open(string title, string body, string confirmLabel, Action onConfirm, string cancelLabel, bool destructive, QwScheme scheme)
        {
            var modal = new QwModalInstance(host, title, scheme, 480f, showTitle: true);
            modal.Content.Label(body, QwTextStyle.Body);
            var row = modal.Content.Row(QwGap.Sm);
            row.Spacer();
            row.Button(cancelLabel, modal.Close, QwButtonStyle.Ghost);
            row.Button(confirmLabel, () =>
            {
                modal.Close();
                onConfirm();
            }, destructive ? QwButtonStyle.Danger : QwButtonStyle.Filled);
            modal.Show();
        }
    }

    /// <summary>A single modal: backdrop canvas + dialog panel, destroyed on close.</summary>
    public sealed class QwModalInstance : IQwDismissable
    {
        private readonly GameObject canvasRoot;
        private readonly QwContainer dialog;
        private readonly UImage backdrop;
        private readonly QwCursorLease cursorLease = new QwCursorLease();
        private bool open;
        private bool closing;

        internal QwModalInstance(UiHost host, string title, QwScheme scheme, float width, bool showTitle)
        {
            canvasRoot = QwLayers.CreateCanvas(host.OwnerId + ":modal", QwLayerBand.Modal, interactive: true, persistent: false);
            var layer = new QwContainer(host, scheme, canvasRoot);

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

            var fill = CreateDecor(panel.transform, "Fill", QwSprites.Fill(QwRadius.Dialog), raycast: true);
            fill.color = theme.Surface;
            var ring = CreateDecor(panel.transform, "Ring", QwSprites.Ring(QwRadius.Dialog, QwTokens.BorderStrong), raycast: false);
            ring.color = theme.OutlineStrong;

            QwLayout.ApplyColumn(panel, QwGap.Md, QwGap.Xl);
            var fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            dialog = new QwContainer(host, scheme, panel);
            if (showTitle)
            {
                dialog.Label(title, QwTextStyle.Title);
            }

            Content = dialog;
        }

        /// <summary>Dialog body — add content here before Show().</summary>
        public QwContainer Content { get; }

        public event Action? Closed;

        QwLayerBand IQwDismissable.Band => QwLayerBand.Modal;

        void IQwDismissable.Dismiss()
        {
            Close();
        }

        public void Show()
        {
            if (open)
            {
                return;
            }

            open = true;
            cursorLease.Acquire();
            QwDismissStack.Push(this);
            QwMotion.ModalIn(dialog);
        }

        public void Close()
        {
            if (!open || closing)
            {
                return;
            }

            closing = true;
            cursorLease.Release();
            QwDismissStack.Remove(this);
            QwMotion.ModalOut(dialog, () =>
            {
                Closed?.Invoke();
                if (canvasRoot != null)
                {
                    UnityEngine.Object.Destroy(canvasRoot);
                }
            });
        }

        private static UImage CreateDecor(Transform parent, string name, Sprite sprite, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sprite;
            image.type = UImage.Type.Sliced;
            image.raycastTarget = raycast;
            QwAnchors.Stretch((RectTransform)go.transform);
            var layout = go.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            return image;
        }
    }
}
