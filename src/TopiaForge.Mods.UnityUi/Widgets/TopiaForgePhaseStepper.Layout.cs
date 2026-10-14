using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class TopiaForgePhaseStepper
    {
        private const float NodeSize = 24f;
        private const float RowHeight = 40f;
        private const float RowSpacing = 4f;
        private const float ConnectorThickness = 2f;
        private const float ConnectorGap = 4f;
        private const float HorizontalHeight = 68f;

        private Sprite? doneSprite;
        private Sprite? failedSprite;
        private Sprite? skippedSprite;

        private void BuildHorizontal(IReadOnlyList<string> labels)
        {
            var count = labels.Count;
            var root = new TopiaForgeContainer(Host, Scheme, Go);
            var connectors = root.CreateChild("Connectors");
            TopiaForgeAnchors.Stretch(connectors.Rect);
            var row = root.CreateChild("Steps");
            TopiaForgeAnchors.Stretch(row.Rect);
            TopiaForgeLayout.ApplyRow(row.Go, TopiaForgeGap.None, TopiaForgeGap.None, expandChildWidth: true);

            for (var index = 0; index < count; index++)
            {
                var column = row.CreateChild("Step" + index);
                TopiaForgeLayout.ApplyColumn(column.Go, TopiaForgeGap.Xs, TopiaForgeGap.None, expandChildWidth: false);
                column.Go.GetComponent<VerticalLayoutGroup>().childAlignment = TextAnchor.UpperCenter;
                var columnLayout = column.EnsureLayoutElement();
                columnLayout.minWidth = 0f;
                columnLayout.preferredWidth = 0f;
                columnLayout.flexibleWidth = 1f;

                var view = CreateNode(column.Go.transform, index);
                view.Label = column.Label(labels[index] ?? string.Empty, TopiaForgeTextStyle.Label).AlignCenter().Ellipsis().FillWidth();
                view.DetailLabel = column.Label(TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).AlignCenter().Ellipsis().PlainText().FillWidth();
                if (index < count - 1)
                {
                    var connector = CreateImage(connectors.Go.transform, "Connector" + index, TopiaForgeSprites.White, sliced: false);
                    var rect = connector.rectTransform;
                    rect.anchorMin = new Vector2((index + 0.5f) / count, 1f);
                    rect.anchorMax = new Vector2((index + 1.5f) / count, 1f);
                    rect.offsetMin = new Vector2((NodeSize * 0.5f) + ConnectorGap, -(NodeSize * 0.5f) - (ConnectorThickness * 0.5f));
                    rect.offsetMax = new Vector2(-(NodeSize * 0.5f) - ConnectorGap, -(NodeSize * 0.5f) + (ConnectorThickness * 0.5f));
                    view.Connector = connector;
                }

                steps[index] = view;
            }

            this.FixedHeight(HorizontalHeight);
        }

        private void BuildVertical(IReadOnlyList<string> labels)
        {
            var count = labels.Count;
            TopiaForgeLayout.ApplyColumn(Go, TopiaForgeGap.Xs, TopiaForgeGap.None);
            var root = new TopiaForgeContainer(Host, Scheme, Go);
            for (var index = 0; index < count; index++)
            {
                var row = root.CreateChild("Step" + index);
                TopiaForgeLayout.ApplyRow(row.Go, TopiaForgeGap.Sm, TopiaForgeGap.None);
                row.FixedHeight(RowHeight);

                var view = CreateNode(row.Go.transform, index);
                var text = row.CreateChild("Text");
                TopiaForgeLayout.ApplyColumn(text.Go, TopiaForgeGap.None, TopiaForgeGap.None);
                text.Go.GetComponent<VerticalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
                text.Flex(1f, 0f);
                view.Label = text.Label(labels[index] ?? string.Empty, TopiaForgeTextStyle.Label).Ellipsis();
                view.DetailLabel = text.Label(TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).Ellipsis().PlainText();
                if (index < count - 1)
                {
                    var connector = CreateImage(row.Go.transform, "Connector", TopiaForgeSprites.White, sliced: false);
                    connector.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                    var rect = connector.rectTransform;
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.zero;
                    rect.pivot = new Vector2(0.5f, 1f);
                    rect.anchoredPosition = new Vector2(NodeSize * 0.5f, ((RowHeight - NodeSize) * 0.5f) - ConnectorGap);
                    rect.sizeDelta = new Vector2(ConnectorThickness, RowHeight - NodeSize + RowSpacing - (ConnectorGap * 2f));
                    view.Connector = connector;
                }

                steps[index] = view;
            }

            this.FixedHeight((count * RowHeight) + ((count - 1) * RowSpacing));
        }

        private StepView CreateNode(Transform parent, int index)
        {
            doneSprite ??= TopiaForgeSprites.Icon(TopiaForgeIcon.Check);
            failedSprite ??= TopiaForgeSprites.Icon(TopiaForgeIcon.Cross);
            skippedSprite ??= TopiaForgeSprites.Icon(TopiaForgeIcon.ChevronRight);

            var node = new GameObject("Node", typeof(RectTransform));
            node.transform.SetParent(parent, false);
            var layout = node.AddComponent<LayoutElement>();
            layout.minWidth = NodeSize;
            layout.preferredWidth = NodeSize;
            layout.minHeight = NodeSize;
            layout.preferredHeight = NodeSize;

            var fill = CreateImage(node.transform, "Fill", TopiaForgeSprites.Circle(), sliced: false);
            TopiaForgeAnchors.Stretch(fill.rectTransform);
            var ring = CreateImage(node.transform, "Ring", TopiaForgeSprites.Ring(TopiaForgeRadius.Chip, TopiaForgeTokens.BorderStandard), sliced: true);
            TopiaForgeAnchors.Stretch(ring.rectTransform);
            var icon = CreateImage(node.transform, "Icon", doneSprite, sliced: false);
            var iconRect = icon.rectTransform;
            iconRect.anchorMin = new Vector2(0.5f, 0.5f);
            iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.sizeDelta = new Vector2(16f, 16f);

            var numberGo = new GameObject("Number", typeof(RectTransform));
            numberGo.transform.SetParent(node.transform, false);
            var number = TopiaForgeTmp.Create(numberGo);
            number.fontSize = TopiaForgeTokens.CaptionSize;
            number.alignment = TextAlignmentOptions.Center;
            number.textWrappingMode = TextWrappingModes.NoWrap;
            var font = TopiaForgeFonts.For(TopiaForgeTextStyle.Label);
            if (font != null)
            {
                number.font = font;
            }

            if (TopiaForgeFonts.UseFauxBold)
            {
                number.fontStyle = FontStyles.Bold;
            }

            number.text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            TopiaForgeAnchors.Stretch((RectTransform)numberGo.transform);
            return new StepView(fill, ring, icon, number);
        }

        private void PaintStep(int index, TopiaForgeStepStatus status, TopiaForgeResolvedTheme theme)
        {
            var view = steps[index];
            var showNumber = status == TopiaForgeStepStatus.Pending || status == TopiaForgeStepStatus.Active;
            view.Number.enabled = showNumber;
            view.Icon.enabled = !showNumber;
            view.RingRect.localScale = Vector3.one;
            switch (status)
            {
                case TopiaForgeStepStatus.Active:
                    view.Fill.color = theme.SelectedTint;
                    view.Ring.color = WithAlpha(theme.Primary, TopiaForgeBreath.RestAlpha);
                    view.Number.color = theme.Text;
                    view.Label.SetTone(TopiaForgeTone.Neutral);
                    break;
                case TopiaForgeStepStatus.Done:
                    view.Fill.color = theme.Success;
                    view.Ring.color = theme.Success;
                    SetIcon(view, doneSprite!, theme.OnStatus);
                    view.Label.SetTone(TopiaForgeTone.Muted);
                    break;
                case TopiaForgeStepStatus.Failed:
                    view.Fill.color = theme.Danger;
                    view.Ring.color = theme.Danger;
                    SetIcon(view, failedSprite!, theme.OnStatus);
                    view.Label.SetTone(TopiaForgeTone.Danger);
                    break;
                case TopiaForgeStepStatus.Skipped:
                    view.Fill.color = Color.clear;
                    view.Ring.color = theme.Tint;
                    SetIcon(view, skippedSprite!, theme.TextFaint);
                    view.Label.SetTone(TopiaForgeTone.Faint);
                    break;
                default:
                    view.Fill.color = theme.SurfaceSunken;
                    view.Ring.color = theme.Outline;
                    view.Number.color = theme.TextMuted;
                    view.Label.SetTone(TopiaForgeTone.Muted);
                    break;
            }

            if (view.Connector != null)
            {
                view.Connector.color = status == TopiaForgeStepStatus.Done ? theme.Success : theme.Tint;
            }
        }

        private static void SetIcon(StepView view, Sprite sprite, Color color)
        {
            if (!ReferenceEquals(view.Icon.sprite, sprite))
            {
                view.Icon.sprite = sprite;
            }

            view.Icon.color = color;
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        private static UImage CreateImage(Transform parent, string name, Sprite sprite, bool sliced)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sprite;
            image.type = sliced ? UImage.Type.Sliced : UImage.Type.Simple;
            image.raycastTarget = false;
            return image;
        }

        private sealed class StepView
        {
            public StepView(UImage fill, UImage ring, UImage icon, TextMeshProUGUI number)
            {
                Fill = fill;
                Ring = ring;
                RingRect = ring.rectTransform;
                Icon = icon;
                Number = number;
            }

            public UImage Fill { get; }

            public UImage Ring { get; }

            public RectTransform RingRect { get; }

            public UImage Icon { get; }

            public TextMeshProUGUI Number { get; }

            public TopiaForgeLabel Label { get; set; } = null!;

            public TopiaForgeLabel DetailLabel { get; set; } = null!;

            public UImage? Connector { get; set; }

            public string Detail { get; set; } = string.Empty;

            public string? ShownDetail { get; set; }

            public int ShownCurrent { get; set; }

            public int ShownTotal { get; set; }
        }
    }
}
