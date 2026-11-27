using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class TopiaForgeChatLog
    {
        private const float Inset = 6f;
        private const float ScrollbarWidth = 6f;
        private const float LineSpacing = 2f;

        private UImage backdrop = null!;
        private ScrollRect scrollRect = null!;
        private RectTransform content = null!;
        private UImage viewportCatcher = null!;
        private GameObject scrollbarRoot = null!;
        private UImage scrollbarTrack = null!;
        private UImage scrollbarHandle = null!;
        private GraphicRaycaster raycaster = null!;
        private Color lineBacking = Color.clear;

        private void BuildChrome()
        {
            // An own canvas keeps fading lines from dirtying the parent canvas, and an own raycaster
            // keeps scrollback usable while open even on a HUD layer whose raycaster is off.
            this.Dynamic();
            raycaster = TopiaForgeComponents.GetOrAdd<GraphicRaycaster>(Go);
            TopiaForgeEventSystems.Track(raycaster);

            backdrop = CreateChromeImage(Go.transform, "Backdrop", TopiaForgeSprites.Fill(TopiaForgeRadius.Control), sliced: true);
            TopiaForgeAnchors.Stretch(backdrop.rectTransform);

            var viewportGo = new GameObject("Viewport", typeof(RectTransform));
            viewportGo.transform.SetParent(Go.transform, false);
            viewportGo.AddComponent<RectMask2D>();
            viewportCatcher = viewportGo.AddComponent<UImage>();
            viewportCatcher.color = Color.clear;
            var viewport = (RectTransform)viewportGo.transform;
            TopiaForgeAnchors.Stretch(viewport, Inset, Inset, Inset + ScrollbarWidth + 2f, Inset);

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewportGo.transform, false);
            content = (RectTransform)contentGo.transform;
            content.anchorMin = Vector2.zero;
            content.anchorMax = new Vector2(1f, 0f);
            content.pivot = new Vector2(0.5f, 0f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            var stack = contentGo.AddComponent<VerticalLayoutGroup>();
            stack.spacing = LineSpacing;
            stack.childControlWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;
            stack.childAlignment = TextAnchor.LowerLeft;
            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollbarRoot = new GameObject("Scrollbar", typeof(RectTransform));
            scrollbarRoot.transform.SetParent(Go.transform, false);
            scrollbarTrack = scrollbarRoot.AddComponent<UImage>();
            scrollbarTrack.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Bar);
            scrollbarTrack.type = UImage.Type.Sliced;
            var scrollbarRect = (RectTransform)scrollbarRoot.transform;
            scrollbarRect.anchorMin = new Vector2(1f, 0f);
            scrollbarRect.anchorMax = new Vector2(1f, 1f);
            scrollbarRect.pivot = new Vector2(1f, 0.5f);
            scrollbarRect.offsetMin = new Vector2(-Inset - ScrollbarWidth, Inset);
            scrollbarRect.offsetMax = new Vector2(-Inset, -Inset);
            var scrollbar = scrollbarRoot.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            var handleArea = new GameObject("HandleArea", typeof(RectTransform));
            handleArea.transform.SetParent(scrollbarRoot.transform, false);
            TopiaForgeAnchors.Stretch((RectTransform)handleArea.transform, 1f, 1f, 1f, 1f);
            scrollbarHandle = CreateChromeImage(handleArea.transform, "Handle", TopiaForgeSprites.Fill(TopiaForgeRadius.Bar), sliced: true);
            TopiaForgeAnchors.Stretch(scrollbarHandle.rectTransform);
            scrollbar.handleRect = scrollbarHandle.rectTransform;
            scrollbar.targetGraphic = scrollbarHandle;

            scrollRect = Go.AddComponent<ScrollRect>();
            scrollRect.viewport = viewport;
            scrollRect.content = content;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 28f;
            scrollRect.verticalScrollbar = scrollbar;
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        }

        private void ApplyOpenState()
        {
            backdrop.enabled = open;
            viewportCatcher.raycastTarget = open;
            raycaster.enabled = open;
            scrollRect.enabled = open;
            if (scrollbarRoot.activeSelf != open)
            {
                scrollbarRoot.SetActive(open);
            }

            if (!open)
            {
                // Closed: the newest line rests on the bottom edge and older ones clip off the top.
                scrollRect.velocity = Vector2.zero;
                var position = content.anchoredPosition;
                position.y = 0f;
                content.anchoredPosition = position;
            }
        }

        private LineView CreateLine()
        {
            var go = new GameObject("Line", typeof(RectTransform));
            go.transform.SetParent(content, false);
            var group = go.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            var backing = go.AddComponent<UImage>();
            backing.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Chip);
            backing.type = UImage.Type.Sliced;
            backing.raycastTarget = false;
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 3, 3);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(go.transform, false);
            var text = TopiaForgeTmp.Create(textGo);
            text.fontSize = TopiaForgeTokens.BodySize;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.richText = false;
            text.parseCtrlCharacters = false;
            var font = TopiaForgeFonts.For(TopiaForgeTextStyle.Body);
            if (font != null)
            {
                text.font = font;
            }

            return new LineView(go, group, backing, text);
        }

        private static UImage CreateChromeImage(Transform parent, string name, Sprite sprite, bool sliced)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sprite;
            image.type = sliced ? UImage.Type.Sliced : UImage.Type.Simple;
            image.raycastTarget = false;
            return image;
        }

        private sealed class LineView
        {
            public LineView(GameObject root, CanvasGroup group, UImage backing, TextMeshProUGUI text)
            {
                Root = root;
                Group = group;
                Backing = backing;
                Text = text;
            }

            public GameObject Root { get; }

            public CanvasGroup Group { get; }

            public UImage Backing { get; }

            public TextMeshProUGUI Text { get; }

            public TopiaForgeChatLineKind Kind { get; set; }

            public float Alpha { get; set; } = 1f;
        }
    }
}
