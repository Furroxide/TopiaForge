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

        /// <summary>
        /// Destructive confirmation that also says when it ends without confirming: the cancel button, ESC, or anything
        /// else that closes it. <paramref name="onCancel"/> runs once, after the dialog has closed; confirming never runs it.
        /// With <paramref name="plainText"/>, the title and body render exactly as written, markup unparsed, for text that
        /// names what another player wrote, such as their name. With <paramref name="persistent"/>, the dialog survives
        /// scene loads. It returns the dialog, so its owner can close it once the decision no longer applies, which counts
        /// as cancelling.
        /// </summary>
        public TopiaForgeModalInstance Destructive(
            string title,
            string body,
            string confirmLabel,
            Action onConfirm,
            Action onCancel,
            string cancelLabel = "CANCEL",
            bool plainText = false,
            bool persistent = false)
        {
            if (onCancel == null)
            {
                throw new ArgumentNullException(nameof(onCancel));
            }

            return Open(title, body, confirmLabel, onConfirm, cancelLabel, destructive: true, TopiaForgeScheme.Paper, onCancel, plainText, persistent);
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

        /// <summary>
        /// Empty modal shell for custom content that, when <paramref name="persistent"/>, survives scene loads: a dialog
        /// that follows a flow across them, as joining a session from a running game returns to the main menu first. A
        /// modal that is not persistent is destroyed with the scene it opened in. The owner closes it either way, and
        /// disposing the host tears it down.
        /// </summary>
        public TopiaForgeModalInstance Custom(string title, TopiaForgeScheme scheme, float width, bool persistent)
        {
            return new TopiaForgeModalInstance(host, title, scheme, width, showTitle: !string.IsNullOrEmpty(title), persistent: persistent);
        }

        private TopiaForgeModalInstance Open(
            string title,
            string body,
            string confirmLabel,
            Action onConfirm,
            string cancelLabel,
            bool destructive,
            TopiaForgeScheme scheme,
            Action? onCancel = null,
            bool plainText = false,
            bool persistent = false)
        {
            var modal = new TopiaForgeModalInstance(host, title, scheme, 480f, showTitle: true, plainTitle: plainText, persistent: persistent);
            var confirmed = false;
            var bodyLabel = modal.Content.Label(body, TopiaForgeTextStyle.Body);
            if (plainText)
            {
                bodyLabel.PlainText();
            }
            var row = modal.Content.Row(TopiaForgeGap.Sm);
            row.Spacer();
            row.Button(cancelLabel, modal.Close, TopiaForgeButtonStyle.Ghost);
            row.Button(confirmLabel, () =>
            {
                confirmed = true;
                modal.Close();
                TopiaForgeCallbacks.Invoke(onConfirm, "Modal confirmation");
            }, destructive ? TopiaForgeButtonStyle.Danger : TopiaForgeButtonStyle.Filled);
            if (onCancel != null)
            {
                modal.Closed += () =>
                {
                    if (!confirmed)
                    {
                        TopiaForgeCallbacks.Invoke(onCancel, "Modal cancellation");
                    }
                };
            }

            modal.Show();
            return modal;
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

        internal TopiaForgeModalInstance(UiHost host, string title, TopiaForgeScheme scheme, float width, bool showTitle, bool plainTitle = false, bool persistent = false)
        {
            this.host = host;
            var layer = host.Layer("modal", TopiaForgeLayerBand.Modal, scheme, interactive: true, persistent: persistent);
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
                var titleLabel = dialog.Label(title, TopiaForgeTextStyle.Title);
                if (plainTitle)
                {
                    titleLabel.PlainText();
                }
            }

            Content = dialog;
            canvasRoot.SetActive(false);
        }

        /// <summary>Dialog body — add content here before Show().</summary>
        public TopiaForgeContainer Content { get; }

        /// <summary>Raised after the modal closes.</summary>
        public event Action? Closed;

        internal GameObject CanvasRoot => canvasRoot;

        /// <summary>Gets the owner of the host the modal belongs to: whose action dismissing it is.</summary>
        internal string OwnerId => host.OwnerId;

        TopiaForgeLayerBand ITopiaForgeDismissable.Band => TopiaForgeLayerBand.Modal;

        void ITopiaForgeDismissable.Dismiss()
        {
            Close();
        }

        /// <summary>Shows this UI element.</summary>
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

        /// <summary>Closes this UI element.</summary>
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
