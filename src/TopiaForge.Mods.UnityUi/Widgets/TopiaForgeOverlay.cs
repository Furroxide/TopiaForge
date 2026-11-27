using System;
using System.Collections.Generic;
using TMPro;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>The kit's gameplay overlay. Only its surface owner can construct it.</summary>
    internal sealed class TopiaForgeOverlay : TopiaForgeContainer, ITopiaForgeThemeAware, ITopiaForgeDismissable
    {
        private readonly TopiaForgeContainer scrimView;
        private readonly Image scrim;
        private readonly TopiaForgeContainer panel;
        private readonly TopiaForgeRoundedImage shadow;
        private readonly TopiaForgeRoundedImage fill;
        private readonly TopiaForgeRoundedImage edge;
        private readonly GraphicRaycaster raycaster;
        private readonly TopiaForgeLabel title;
        private readonly TopiaForgeCursorLease cursor = new TopiaForgeCursorLease();
        private readonly List<Selectable> focusCandidates = new List<Selectable>();
        private readonly UiLayoutExtent requested;
        private readonly bool interactive;
        private readonly bool trust;
        private readonly TopiaForgeModalColumn? trustedLayout;
        private readonly Action finishClose;
        private GameObject? lastSelection;
        private UiLayoutExtent panelSize;
        private Action? afterRetirement;
        private bool open;
        private bool closing;
        private bool tornDown;
        private bool ownsInput;
        private bool controlsEnabled;
        private bool compositionReady = true;
        private bool focusCandidatesDirty = true;
        private int screenWidth;
        private int screenHeight;
        private float uiScale;
        private Rect screenSafeArea;

        internal TopiaForgeOverlay(TopiaForgeContainer layer, string title, float width, float height,
            UiOverlayContentKind kind, bool dismissible, bool trust = false)
            : base(layer.Host, layer.Scheme, layer.CreateChildGameObject("Overlay"))
        {
            CanvasRoot = layer.Go;
            Canvas = CanvasRoot.GetComponent<Canvas>();
            raycaster = CanvasRoot.GetComponent<GraphicRaycaster>();
            requested = new UiLayoutExtent(width, height);
            interactive = kind == UiOverlayContentKind.Interactive;
            this.trust = trust;
            Dismissible = dismissible;
            finishClose = FinishClose;

            scrimView = Stack("Overlay scrim");
            scrim = scrimView.Go.AddComponent<Image>();
            scrim.raycastTarget = false;
            if (interactive)
            {
                var shield = Stack("Overlay input shield").Go.AddComponent<Image>();
                shield.color = Color.clear;
                shield.raycastTarget = true;
            }

            panel = new TopiaForgeContainer(Host, Scheme, CreateChildGameObject("Overlay panel"));
            panel.Rect.anchorMin = panel.Rect.anchorMax = new Vector2(0.5f, 0.5f);
            shadow = MakePlate("Shadow");
            fill = MakePlate("Surface");
            edge = MakePlate("Boundary");
            Content = trust ? panel : panel.Stack("Overlay content");
            Content.Go.AddComponent<RectMask2D>();
            if (trust)
            {
                trustedLayout = Content.Go.AddComponent<TopiaForgeModalColumn>();
                TopiaForgeLayout.ConfigureColumn(trustedLayout, TopiaForgeGap.Md, TopiaForgeGap.Xl);
                Content.Go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }
            else TopiaForgeLayout.ApplyColumn(Content.Go, TopiaForgeGap.Md, TopiaForgeGap.Lg);
            this.title = Content.Label(title, TopiaForgeTextStyle.Title).AlignCenter();
            if (interactive) Go.AddComponent<TopiaForgeOverlayFocusFrame>().Initialize(this);
            EnsureCanvasGroup().interactable = false;
            Go.SetActive(false);
            RefreshViewport();
            if (!trust) TopiaForgeOverlayStack.Add(this);
        }

        internal GameObject CanvasRoot { get; }
        internal Canvas Canvas { get; }
        internal TopiaForgeContainer Content { get; }
        internal TopiaForgeLabel TitleLabel => title;
        internal bool Dismissible { get; }
        internal bool IsRetired => tornDown;
        internal int ViewportRevision { get; private set; }
        internal bool HasNavigationFocus => ownsInput && TopiaForgeDismissStack.FocusRoot == Go;
        internal bool IsPainted => (open || closing) && !tornDown && Go != null;
        internal bool AcceptsInput => open && !closing && interactive && compositionReady;
        internal event Action? Closed;

        TopiaForgeLayerBand ITopiaForgeDismissable.Band => trust ? TopiaForgeLayerPriority.TrustOverlay : TopiaForgeLayerPriority.Overlay;

        internal void LimitTrustedScrollBody() => trustedLayout?.LimitScrollBody(Rect, 2f * UiOverlayLayout.EnterTravel);

        void ITopiaForgeDismissable.Dismiss()
        {
            if (!Dismissible || !ownsInput) return;
            Close();
            TopiaForgeCallbacks.Invoke(Closed, "Overlay Closed");
        }

        internal void Show()
        {
            if (tornDown || open) return;
            TopiaForgeTween.Cancel(this);
            var group = EnsureCanvasGroup();
            var scrimAlpha = closing ? group.alpha * scrimView.EnsureCanvasGroup().alpha : 0f;
            var startY = closing ? panel.Rect.anchoredPosition.y : -UiOverlayLayout.EnterTravel;
            group.alpha = 1f;
            closing = false;
            open = true;
            RefreshViewport();
            RefreshVisibility();
            TopiaForgeMotion.OverlayIn(scrimView, panel, scrimAlpha, startY);
        }

        internal void Close()
        {
            if (tornDown || !open) return;
            open = false;
            closing = true;
            RefreshVisibility();
            TopiaForgeTween.Cancel(scrimView);
            TopiaForgeTween.Cancel(panel);
            TopiaForgeMotion.OverlayOut(this, finishClose);
        }

        internal void Retire(Action retired)
        {
            if (tornDown)
            {
                retired();
                return;
            }

            afterRetirement = retired;
            if (open) Close();
            else if (!closing) FinishClose();
        }

        internal void Teardown()
        {
            if (tornDown) return;
            tornDown = true;
            open = false;
            closing = false;
            if (trust)
            {
                if (ownsInput) SetInputOwner(false);
            }
            else TopiaForgeOverlayStack.Remove(this);
            TopiaForgeTween.Cancel(this);
            TopiaForgeTween.Cancel(scrimView);
            TopiaForgeTween.Cancel(panel);
            Closed = null;
            focusCandidates.Clear();
            lastSelection = null;
            var retired = afterRetirement;
            afterRetirement = null;
            retired?.Invoke();
        }

        internal void ApplyPauseVisibility(bool paused)
        {
            if (Go != null) SetShown(IsPainted && !paused);
        }

        internal void SetCompositionReady(bool ready)
        {
            title.SetShown(ready);
            if (compositionReady != ready)
            {
                compositionReady = ready;
                RefreshVisibility();
            }
            if (ready) RefreshFocus();
        }

        internal void SetInputOwner(bool value)
        {
            ownsInput = value;
            if (raycaster != null) raycaster.enabled = value;
            cursor.SetActive(value);
            if (value)
            {
                TopiaForgeDismissStack.Push(this, TopiaForgeInputClaim.Navigation, Go, removesOnBack: Dismissible);
                TopiaForgeEventSystems.EnsureEventSystem();
                RefreshFocus();
                return;
            }

            TopiaForgeDismissStack.Remove(this);
            var selected = EventSystem.current?.currentSelectedGameObject;
            lastSelection = IsInOverlay(selected) ? selected : null;
            SetControlsEnabled(false);
            ReleaseFocus();
        }

        internal void CompositionChanged()
        {
            focusCandidatesDirty = true;
            RefreshFocus();
        }

        internal void RefreshFocus()
        {
            if (!ownsInput) return;
            var events = EventSystem.current;
            if (events == null) return;
            var selected = events.currentSelectedGameObject;
            var ownsNavigation = TopiaForgeDismissStack.FocusRoot == Go;
            SetControlsEnabled(ownsNavigation);
            if (!ownsNavigation)
            {
                if (IsInOverlay(selected))
                {
                    lastSelection = selected;
                    ReleaseFocus();
                }
                return;
            }
            if (IsInOverlay(selected)) return;
            if (IsInOverlay(lastSelection) && lastSelection!.GetComponent<Selectable>() is { } remembered &&
                remembered.IsInteractable())
            {
                remembered.Select();
                return;
            }

            if (focusCandidatesDirty)
            {
                Go.GetComponentsInChildren(includeInactive: true, focusCandidates);
                focusCandidatesDirty = false;
            }
            foreach (var candidate in focusCandidates)
            {
                if (!candidate.isActiveAndEnabled || !candidate.IsInteractable()) continue;
                candidate.Select();
                break;
            }
        }

        private void SetControlsEnabled(bool value)
        {
            if (controlsEnabled == value) return;
            controlsEnabled = value;
            EnsureCanvasGroup().interactable = value;
        }

        internal void RefreshViewport()
        {
            var width = Screen.width;
            var height = Screen.height;
            var safe = Screen.safeArea;
            var scale = Host.EffectiveUiScale;
            if (width <= 0 || height <= 0 || (width == screenWidth && height == screenHeight &&
                scale == uiScale && safe == screenSafeArea)) return;

            screenWidth = width;
            screenHeight = height;
            uiScale = scale;
            screenSafeArea = safe;
            ViewportRevision++;
            Rect.anchorMin = new Vector2(safe.xMin / width, safe.yMin / height);
            Rect.anchorMax = new Vector2(safe.xMax / width, safe.yMax / height);
            Rect.offsetMin = Rect.offsetMax = Vector2.zero;
            var factor = scale * Mathf.Sqrt(width / TopiaForgeTokens.ReferenceWidth * (height / TopiaForgeTokens.ReferenceHeight));
            panelSize = UiOverlayLayout.Clamp(requested, new UiLayoutExtent(safe.width / factor, safe.height / factor),
                TopiaForgeTokens.SafeMargin);
            panel.Rect.sizeDelta = new Vector2((float)panelSize.Width, (float)panelSize.Height);
            ApplyTheme(ResolvedTheme);
        }

        internal UiLayoutConstraints MeasureContent(TopiaForgeLabel body)
        {
            RefreshViewport();
            var width = panelSize.Width - 2 * (int)TopiaForgeGap.Lg;
            var height = panelSize.Height - 2 * (int)TopiaForgeGap.Lg;
            if (width <= 0 || height <= 0)
                throw new UiLayoutException(UiLayoutFailure.InvalidLayout, "overlay", "The overlay panel cannot fit this viewport.");
            height -= MeasureLineGroup(title, (float)width) + MeasureLineGroup(body, (float)width);
            if (height < 0)
                throw new UiLayoutException(UiLayoutFailure.ClippedContent, "overlay", "The overlay title or body cannot fit this viewport.");
            return new UiLayoutConstraints(width, width, 0, height);
        }

        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            var surface = theme.Surface;
            surface.a = 1f;
            fill.color = surface;
            surface.a = UiOverlayLayout.ScrimOpacity;
            scrim.color = surface;
            var descriptor = theme.Descriptor ?? UiThemePackParser.Default;
            var component = UiLayoutDecoration.ResolveComponent(descriptor, UiThemeTarget.Overlay, panelSize, theme.HighContrast);
            fill.SetShape((float)component.CornerRadius);
            edge.color = theme.HighContrast ? theme.OutlineStrong : theme.Outline;
            edge.SetShape((float)component.CornerRadius, (float)component.BorderWidth);
            edge.enabled = component.BorderWidth > 0;
            shadow.color = theme.Shadow;
            shadow.SetShape((float)component.CornerRadius);
            shadow.rectTransform.anchoredPosition = new Vector2((float)component.ShadowX, (float)-component.ShadowY);
            shadow.enabled = component.ShadowX != 0 || component.ShadowY != 0;
        }

        private TopiaForgeRoundedImage MakePlate(string name)
        {
            var handle = panel.Stack(name);
            var image = handle.Go.AddComponent<TopiaForgeRoundedImage>();
            image.raycastTarget = false;
            if (trust) handle.Go.AddComponent<LayoutElement>().ignoreLayout = true;
            return image;
        }

        private void FinishClose()
        {
            closing = false;
            RefreshVisibility();
            if (afterRetirement != null)
            {
                if (CanvasRoot != null) Host.DestroyLayer(CanvasRoot);
                else Teardown();
            }
        }

        private void RefreshVisibility()
        {
            if (!trust)
            {
                TopiaForgeOverlayStack.Refresh(this);
                return;
            }

            if (Go != null) SetShown(IsPainted);
            if (ownsInput != AcceptsInput) SetInputOwner(AcceptsInput);
        }

        private bool IsInOverlay(GameObject? selection) => selection != null && selection.activeInHierarchy &&
            Go != null && selection.transform.IsChildOf(Go.transform);

        private static float MeasureLineGroup(TopiaForgeLabel label, float width)
        {
            var text = label.Go.GetComponent<TMP_Text>();
            var measured = text.GetPreferredValues(text.text, width, 0f);
            if (measured.x > width + 0.1f)
                throw new UiLayoutException(UiLayoutFailure.ClippedContent, "overlay", "The overlay text cannot fit this viewport.");
            return measured.y + (int)TopiaForgeGap.Md;
        }
    }

    internal sealed class TopiaForgeOverlayFocusFrame : MonoBehaviour
    {
        private TopiaForgeOverlay? overlay;

        internal void Initialize(TopiaForgeOverlay owner) => overlay = owner;

        private void Update() => overlay?.RefreshFocus();
    }
}
