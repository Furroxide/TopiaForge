using System;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiLayoutRenderer
        {
            private sealed class NativeStatusCountdown
            {
                private static readonly string[] Cells = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", ":" };

                private readonly UiCountdown node;
                private readonly UiCallbackGate callbacks;
                private readonly UiCountdownDisplay display;
                private readonly int reservedDigits;
                private TMP_FontAsset? measuredFont;
                private float measuredSize = -1;
                private float measuredTracking;
                private float cellWidth;
                private float cellHeight;

                internal NativeStatusCountdown(TopiaForgeContainer parent, UiCountdown node, UiCallbackGate callbacks)
                {
                    this.node = node;
                    this.callbacks = callbacks;
                    display = new UiCountdownDisplay(node);
                    Text = TopiaForgeTmp.Create(parent.CreateChildGameObject("Countdown digits"));
                    Text.richText = false;
                    Text.parseCtrlCharacters = false;
                    Text.textWrappingMode = TextWrappingModes.NoWrap;
                    Text.alignment = TextAlignmentOptions.Left;
                    var font = TopiaForgeFonts.For(TopiaForgeTextStyle.Mono);
                    if (font != null) Text.font = font;
                    Text.OnPreRenderText += ApplyTabularAdvance;

                    UseTypography(parent.ResolvedTheme, parent.Host.EffectiveUiScale);
                    reservedDigits = UiCountdownFormatting.WriteDigits(int.MaxValue, node.Format, display.Digits);
                    // Prime TMP's full buffer and every glyph used by a clock, including the separator.
                    "0123456789::".AsSpan().CopyTo(display.Digits);
                    Text.SetCharArray(display.Digits, 0, display.Digits.Length);
                    Text.ForceMeshUpdate(ignoreActiveState: true);
                    Refresh();
                }

                internal TMP_Text Text { get; }
                internal UiLayoutExtent Extent => new UiLayoutExtent(cellWidth * reservedDigits, cellHeight);
                internal UiStatusPresentation Status => display.Status;

                internal bool Refresh()
                {
                    var remaining = node.ClockEpoch == null || node.ClockEpoch == callbacks.ClockEpoch()
                        ? node.RemainingTime(callbacks.Clock()) : 0d;
                    var (digitsChanged, statusChanged) = display.Update(remaining);
                    if (digitsChanged) Text.SetCharArray(display.Digits, 0, display.Length);
                    return statusChanged;
                }

                internal void UseTypography(TopiaForgeResolvedTheme theme, float uiScale)
                {
                    TopiaForgeTypography.ApplyControlText(Text, theme, TopiaForgeTextStyle.Mono, uiScale);
                    if (Text.font == measuredFont && Text.fontSize == measuredSize && Text.characterSpacing == measuredTracking) return;
                    measuredFont = Text.font;
                    measuredSize = Text.fontSize;
                    measuredTracking = Text.characterSpacing;
                    cellWidth = 0;
                    cellHeight = 0;
                    foreach (var cell in Cells)
                    {
                        var measure = Text.GetPreferredValues(cell);
                        cellWidth = Mathf.Max(cellWidth, measure.x);
                        cellHeight = Mathf.Max(cellHeight, measure.y);
                    }
                }

                private void ApplyTabularAdvance(TMP_TextInfo info)
                {
                    // Keep the Mono role and its admitted font, including proportional fallback glyphs. Fix every cell's
                    // origin so the digits sit steadily beside the caption. No authored text enters this mesh.
                    var left = Text.rectTransform.rect.xMin;
                    for (var index = 0; index < info.characterCount; index++)
                    {
                        ref var character = ref info.characterInfo[index];
                        if (!character.isVisible) continue;
                        var origin = left + cellWidth * index;
                        var shift = origin - character.origin;
                        var vertices = info.meshInfo[character.materialReferenceIndex].vertices;
                        for (var vertex = character.vertexIndex; vertex < character.vertexIndex + 4; vertex++)
                            vertices[vertex].x += shift;
                        character.bottomLeft.x += shift;
                        character.bottomRight.x += shift;
                        character.topLeft.x += shift;
                        character.topRight.x += shift;
                        character.origin = origin;
                        character.xAdvance = origin + cellWidth;
                    }
                }
            }
        }
    }
}
