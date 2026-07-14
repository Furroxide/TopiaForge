using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Brand dropdown over TMP_Dropdown, template built entirely at runtime: control
    /// chip with chevron, paper list with SelectedTint rows and a check mark.
    /// </summary>
    public sealed class QwDropdown : QwWidget, IQwThemeAware
    {
        private readonly QwTmpDropdown dropdown;
        private readonly UImage fill;
        private readonly UImage ring;
        private readonly UImage chevron;
        private readonly TextMeshProUGUI caption;
        private readonly UImage templateFill;
        private readonly UImage itemBackground;
        private readonly UImage checkmark;
        private readonly TextMeshProUGUI itemLabel;

        internal QwDropdown(QwContainer parent, IReadOnlyList<string> options, int selected, Action<int> onChanged)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Dropdown"))
        {
            fill = Go.AddComponent<UImage>();
            fill.sprite = QwSprites.Fill(QwRadius.Control);
            fill.type = UImage.Type.Sliced;
            fill.raycastTarget = true;

            ring = CreateStretched(Go.transform, "Ring", QwSprites.Ring(QwRadius.Control, QwTokens.BorderStandard), false);

            var captionGo = new GameObject("Caption", typeof(RectTransform));
            captionGo.transform.SetParent(Go.transform, false);
            caption = CreateTmp(captionGo);
            QwAnchors.Stretch((RectTransform)captionGo.transform, 12f, 4f, 32f, 4f);

            var chevronGo = new GameObject("Chevron", typeof(RectTransform));
            chevronGo.transform.SetParent(Go.transform, false);
            chevron = chevronGo.AddComponent<UImage>();
            chevron.sprite = QwSprites.Icon(QwIcon.ChevronDown);
            chevron.raycastTarget = false;
            var chevronRect = (RectTransform)chevronGo.transform;
            chevronRect.anchorMin = new Vector2(1f, 0.5f);
            chevronRect.anchorMax = new Vector2(1f, 0.5f);
            chevronRect.pivot = new Vector2(1f, 0.5f);
            chevronRect.anchoredPosition = new Vector2(-10f, 0f);
            chevronRect.sizeDelta = new Vector2(16f, 16f);

            // ---- template (hidden until opened) ----
            var templateGo = new GameObject("Template", typeof(RectTransform));
            templateGo.transform.SetParent(Go.transform, false);
            var templateRect = (RectTransform)templateGo.transform;
            templateRect.anchorMin = new Vector2(0f, 0f);
            templateRect.anchorMax = new Vector2(1f, 0f);
            templateRect.pivot = new Vector2(0.5f, 1f);
            templateRect.anchoredPosition = new Vector2(0f, -4f);
            templateRect.sizeDelta = new Vector2(0f, 190f);
            templateFill = templateGo.AddComponent<UImage>();
            templateFill.sprite = QwSprites.Fill(QwRadius.Tip);
            templateFill.type = UImage.Type.Sliced;
            var templateScroll = templateGo.AddComponent<ScrollRect>();

            var viewportGo = new GameObject("Viewport", typeof(RectTransform));
            viewportGo.transform.SetParent(templateGo.transform, false);
            viewportGo.AddComponent<RectMask2D>();
            var viewportImage = viewportGo.AddComponent<UImage>();
            viewportImage.color = Color.clear;
            var viewportRect = (RectTransform)viewportGo.transform;
            QwAnchors.Stretch(viewportRect, 4f, 4f, 4f, 4f);

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewportGo.transform, false);
            var contentRect = (RectTransform)contentGo.transform;
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, 30f);

            var itemGo = new GameObject("Item", typeof(RectTransform));
            itemGo.transform.SetParent(contentGo.transform, false);
            var itemRect = (RectTransform)itemGo.transform;
            itemRect.anchorMin = new Vector2(0f, 0.5f);
            itemRect.anchorMax = new Vector2(1f, 0.5f);
            itemRect.sizeDelta = new Vector2(0f, 30f);
            var itemToggle = itemGo.AddComponent<Toggle>();

            var itemBgGo = new GameObject("Item Background", typeof(RectTransform));
            itemBgGo.transform.SetParent(itemGo.transform, false);
            itemBackground = itemBgGo.AddComponent<UImage>();
            itemBackground.sprite = QwSprites.Fill(QwRadius.Chip);
            itemBackground.type = UImage.Type.Sliced;
            QwAnchors.Stretch((RectTransform)itemBgGo.transform, 2f, 1f, 2f, 1f);

            var checkGo = new GameObject("Item Checkmark", typeof(RectTransform));
            checkGo.transform.SetParent(itemGo.transform, false);
            checkmark = checkGo.AddComponent<UImage>();
            checkmark.sprite = QwSprites.Icon(QwIcon.Check);
            checkmark.raycastTarget = false;
            var checkRect = (RectTransform)checkGo.transform;
            checkRect.anchorMin = new Vector2(0f, 0.5f);
            checkRect.anchorMax = new Vector2(0f, 0.5f);
            checkRect.pivot = new Vector2(0f, 0.5f);
            checkRect.anchoredPosition = new Vector2(8f, 0f);
            checkRect.sizeDelta = new Vector2(14f, 14f);

            var itemLabelGo = new GameObject("Item Label", typeof(RectTransform));
            itemLabelGo.transform.SetParent(itemGo.transform, false);
            itemLabel = CreateTmp(itemLabelGo);
            QwAnchors.Stretch((RectTransform)itemLabelGo.transform, 28f, 2f, 8f, 2f);

            itemToggle.targetGraphic = itemBackground;
            itemToggle.graphic = checkmark;
            itemToggle.isOn = true;

            templateScroll.viewport = viewportRect;
            templateScroll.content = contentRect;
            templateScroll.horizontal = false;
            templateScroll.movementType = ScrollRect.MovementType.Clamped;
            templateGo.SetActive(false);

            dropdown = Go.AddComponent<QwTmpDropdown>();
            dropdown.ConfigureSortingOrders(PopupOrders.Blocker, PopupOrders.List);
            dropdown.targetGraphic = fill;
            dropdown.template = templateRect;
            dropdown.captionText = caption;
            dropdown.itemText = itemLabel;

            for (var index = 0; index < options.Count; index++)
            {
                dropdown.options.Add(new TMP_Dropdown.OptionData(options[index]));
            }

            dropdown.SetValueWithoutNotify(selected);
            dropdown.RefreshShownValue();
            dropdown.onValueChanged.AddListener(next => QwCallbacks.Invoke(onChanged, next, "Dropdown change"));

            this.FixedHeight(QwTokens.ControlHeight);
            ApplyTheme(Theme);
        }

        public int Value => dropdown.value;

        public void SetValue(int index)
        {
            if (dropdown.value == index)
            {
                return;
            }

            dropdown.SetValueWithoutNotify(index);
            dropdown.RefreshShownValue();
        }

        public void SetEnabled(bool enabled)
        {
            dropdown.interactable = enabled;
        }

        /// <summary>
        /// Replaces the option list (for choices that only arrive later, e.g. once a level has loaded), clamping
        /// the selection into range. Does not raise the change callback.
        /// </summary>
        public void SetOptions(IReadOnlyList<string> options, int selected = 0)
        {
            dropdown.options.Clear();
            if (options != null)
            {
                for (var index = 0; index < options.Count; index++)
                {
                    dropdown.options.Add(new TMP_Dropdown.OptionData(options[index]));
                }
            }

            dropdown.SetValueWithoutNotify(Mathf.Clamp(selected, 0, Mathf.Max(0, dropdown.options.Count - 1)));
            dropdown.RefreshShownValue();
        }

        public void ApplyTheme(QwResolvedTheme theme)
        {
            fill.color = theme.SurfaceSunken;
            ring.color = theme.Outline;
            chevron.color = theme.TextMuted;
            caption.color = theme.Text;
            templateFill.color = theme.Surface;
            itemBackground.color = theme.SelectedTint;
            checkmark.color = theme.Primary;
            itemLabel.color = theme.Text;
        }

        private TextMeshProUGUI CreateTmp(GameObject go)
        {
            var tmp = QwTmp.Create(go);
            tmp.fontSize = QwTokens.BodySize;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            var font = QwFonts.For(QwTextStyle.Body);
            if (font != null)
            {
                tmp.font = font;
            }

            return tmp;
        }

        private static UImage CreateStretched(Transform parent, string name, Sprite sprite, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sprite;
            image.type = UImage.Type.Sliced;
            image.raycastTarget = raycast;
            QwAnchors.Stretch((RectTransform)go.transform);
            return image;
        }

        /// <summary>
        /// TMP_Dropdown.Show() hardcodes the popup list canvas to sortingOrder 30000 and the blocker to
        /// 29999 — both BELOW every kit band (windows start at <see cref="QwLayerBands.DefaultWindowBase"/>),
        /// so the opened list rendered invisibly behind its own window and clicks appeared to do nothing.
        /// Re-band both canvases to just under the toast band: above all windows and modals (so the popup
        /// is always visible and its blocker eats outside-clicks first), below toasts and the debug overlay.
        /// CreateBlocker runs at the end of Show(), after the list canvas exists, so it is the one hook
        /// where both canvases can be corrected.
        /// </summary>
        private sealed class QwTmpDropdown : TMP_Dropdown
        {
            private GameObject? list;
            private int blockerSortingOrder;
            private int listSortingOrder;

            public void ConfigureSortingOrders(int blockerOrder, int listOrder)
            {
                blockerSortingOrder = blockerOrder;
                listSortingOrder = listOrder;
            }

            protected override GameObject CreateDropdownList(GameObject template)
            {
                list = base.CreateDropdownList(template);
                return list;
            }

            protected override GameObject CreateBlocker(Canvas rootCanvas)
            {
                var blocker = base.CreateBlocker(rootCanvas);
                var listCanvas = list != null ? list.GetComponent<Canvas>() : null;
                if (listCanvas != null)
                {
                    listCanvas.overrideSorting = true;
                    QwLayers.AssignAllocatedOrder(listCanvas, listSortingOrder);
                }

                var blockerCanvas = blocker.GetComponent<Canvas>();
                if (blockerCanvas != null)
                {
                    blockerCanvas.overrideSorting = true;
                    QwLayers.AssignAllocatedOrder(blockerCanvas, blockerSortingOrder);
                }

                return blocker;
            }
        }

        private static class PopupOrders
        {
            // TMP allows only one dropdown blocker at a time, so every dropdown can safely share one
            // process-wide allocator-owned pair rather than leaking two band slots per widget rebuild.
            public static readonly int Blocker = QwLayers.Allocate(QwLayerBand.Modal, "dropdown-blocker");
            public static readonly int List = QwLayers.Allocate(QwLayerBand.Modal, "dropdown-list");
        }
    }
}
