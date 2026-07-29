using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Full-screen paper tool with safe-area chrome, Escape dismissal, cursor ownership,
    /// focus containment, and live accessibility/theme refresh.
    /// </summary>
    public sealed class TopiaForgeFullscreenTool : TopiaForgeContainer, ITopiaForgeThemeAware, ITopiaForgeDismissable
    {
        private readonly GameObject canvasRoot;
        private readonly Image backdrop;
        private readonly Image fill;
        private readonly Image ring;
        private readonly Image titleBarFill;
        private readonly TextMeshProUGUI titleLabel;
        private readonly TopiaForgeCursorLease cursorLease = new TopiaForgeCursorLease();
        private readonly TopiaForgeFocusScope focusScope;
        private bool open;
        private bool closing;
        private bool tornDown;

        internal TopiaForgeFullscreenTool(UiHost host, TopiaForgeContainer layerRoot, string title)
            : base(host, layerRoot.Scheme, layerRoot.CreateChildGameObject("FullscreenTool"))
        {
            canvasRoot = layerRoot.Go;
            TopiaForgeAnchors.Stretch(Rect, TopiaForgeTokens.SafeMargin, TopiaForgeTokens.SafeMargin,
                TopiaForgeTokens.SafeMargin, TopiaForgeTokens.SafeMargin);

            backdrop = CreateDecor(layerRoot.Go.transform, "Backdrop", TopiaForgeSprites.Fill(TopiaForgeRadius.Bar), true);
            TopiaForgeAnchors.Stretch(backdrop.rectTransform);
            backdrop.transform.SetAsFirstSibling();

            fill = CreateDecor(Go.transform, "Fill", TopiaForgeSprites.Fill(TopiaForgeRadius.Card), true);
            ring = CreateDecor(
                Go.transform,
                "Ring",
                TopiaForgeSprites.Ring(TopiaForgeRadius.Card, TopiaForgeTokens.BorderStrong),
                false);

            TopiaForgeLayout.ApplyColumn(Go, TopiaForgeGap.None, TopiaForgeGap.None);

            var titleBarGo = CreateChildGameObject("TitleBar");
            var titleLayout = titleBarGo.AddComponent<LayoutElement>();
            titleLayout.minHeight = TopiaForgeTokens.ControlLgHeight;
            titleLayout.preferredHeight = TopiaForgeTokens.ControlLgHeight;
            titleBarFill = titleBarGo.AddComponent<Image>();
            titleBarFill.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Card);
            titleBarFill.type = UnityEngine.UI.Image.Type.Sliced;
            titleBarFill.raycastTarget = true;
            TopiaForgeLayout.ApplyRow(titleBarGo, TopiaForgeGap.Sm, TopiaForgeGap.None);

            var titleBar = new TopiaForgeContainer(Host, Scheme, titleBarGo);
            var titleGo = titleBar.CreateChildGameObject("Title");
            titleLabel = TopiaForgeTmp.Create(titleGo);
            titleLabel.fontSize = TopiaForgeTokens.TitleSize;
            titleLabel.alignment = TextAlignmentOptions.Left;
            titleLabel.textWrappingMode = TextWrappingModes.NoWrap;
            var displayFont = TopiaForgeFonts.For(TopiaForgeTextStyle.Title);
            if (displayFont != null) titleLabel.font = displayFont;
            if (TopiaForgeFonts.UseFauxDisplay) titleLabel.fontStyle = FontStyles.Bold;
            titleLabel.text = title ?? string.Empty;
            titleLabel.margin = new Vector4(16f, 0f, 0f, 0f);
            var labelLayout = titleGo.AddComponent<LayoutElement>();
            labelLayout.flexibleWidth = 1f;
            labelLayout.minHeight = TopiaForgeTokens.ControlLgHeight;

            titleBar.IconButton(TopiaForgeIcon.Cross, Close, TopiaForgeButtonStyle.Ghost).Fixed(34f, 34f);

            Content = Column(TopiaForgeGap.Md, TopiaForgeGap.Lg).Flex(1f, 1f);
            focusScope = Go.AddComponent<TopiaForgeFocusScope>();
            focusScope.Initialize(Go.transform);
            Go.SetActive(false);
            backdrop.gameObject.SetActive(false);
            ApplyTheme(Theme);
        }

        /// <summary>Gets the content container below the title bar.</summary>
        public TopiaForgeContainer Content { get; }

        /// <summary>Gets whether the tool is open.</summary>
        public bool IsOpen => open;

        /// <summary>Raised after the tool closes.</summary>
        public event Action? Closed;

        TopiaForgeLayerBand ITopiaForgeDismissable.Band => TopiaForgeLayerBand.Window;

        void ITopiaForgeDismissable.Dismiss() => Close();

        /// <summary>Updates the displayed title.</summary>
        public void SetTitle(string title)
        {
            ThrowIfTornDown();
            titleLabel.text = title ?? string.Empty;
        }

        /// <summary>Shows the tool and acquires cursor and keyboard focus.</summary>
        public void Show()
        {
            ThrowIfTornDown();
            if (open && !closing) return;
            if (closing)
            {
                TopiaForgeTween.Cancel(this);
            }

            open = true;
            closing = false;
            backdrop.gameObject.SetActive(true);
            Go.SetActive(true);
            cursorLease.Acquire();
            TopiaForgeDismissStack.Push(this);
            focusScope.Activate();
            TopiaForgeMotion.WindowIn(this);
        }

        /// <summary>Closes the tool and restores the previous UI focus.</summary>
        public void Close()
        {
            if (tornDown || !open || closing) return;
            closing = true;
            cursorLease.Release();
            TopiaForgeDismissStack.Remove(this);
            focusScope.Deactivate();
            TopiaForgeMotion.WindowOut(this, () =>
            {
                closing = false;
                open = false;
                if (Go != null) Go.SetActive(false);
                if (backdrop != null) backdrop.gameObject.SetActive(false);
                TopiaForgeCallbacks.Invoke(Closed, "Fullscreen tool Closed");
            });
        }

        /// <summary>Toggles the tool between open and closed.</summary>
        public void Toggle()
        {
            if (open) Close();
            else Show();
        }

        /// <summary>Applies live theme and high-contrast colors.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            var backdropColor = theme.Backdrop;
            backdropColor.a = Math.Max(backdropColor.a, 0.82f);
            backdrop.color = backdropColor;
            fill.color = theme.Surface;
            ring.color = theme.OutlineStrong;
            titleBarFill.color = theme.SurfaceAlt;
            titleLabel.color = theme.Text;
        }

        internal GameObject CanvasRoot => canvasRoot;

        internal void Teardown()
        {
            if (tornDown) return;
            tornDown = true;
            TopiaForgeTween.Cancel(this);
            cursorLease.Release();
            TopiaForgeDismissStack.Remove(this);
            focusScope.Deactivate();
            open = false;
            closing = false;
            Closed = null;
        }

        private void ThrowIfTornDown()
        {
            if (tornDown) throw new ObjectDisposedException(nameof(TopiaForgeFullscreenTool));
        }

        private static Image CreateDecor(Transform parent, string name, Sprite sprite, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = UnityEngine.UI.Image.Type.Sliced;
            image.raycastTarget = raycast;
            TopiaForgeAnchors.Stretch((RectTransform)go.transform);
            var layout = go.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            return image;
        }
    }

    /// <summary>Retains keyboard/controller focus inside one active fullscreen tool.</summary>
    internal sealed class TopiaForgeFocusScope : MonoBehaviour
    {
        private Transform? root;
        private Selectable? first;
        private GameObject? previous;
        private bool active;

        public void Initialize(Transform owner)
        {
            root = owner;
        }

        public void Activate()
        {
            var eventSystem = EventSystem.current;
            previous = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            var choices = root != null ? root.GetComponentsInChildren<Selectable>(includeInactive: false) : Array.Empty<Selectable>();
            first = null;
            for (var index = 0; index < choices.Length; index++)
            {
                if (choices[index].IsInteractable())
                {
                    first = choices[index];
                    break;
                }
            }

            active = true;
            first?.Select();
        }

        public void Deactivate()
        {
            active = false;
            if (previous != null && previous.activeInHierarchy)
            {
                EventSystem.current?.SetSelectedGameObject(previous);
            }

            previous = null;
            first = null;
        }

        private void Update()
        {
            if (!active || root == null || first == null) return;
            var current = EventSystem.current?.currentSelectedGameObject;
            if (current == null || current.transform != root && !current.transform.IsChildOf(root))
            {
                first.Select();
            }
        }
    }
}
