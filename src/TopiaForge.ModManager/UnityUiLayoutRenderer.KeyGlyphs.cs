using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiLayoutRenderer
        {
            private readonly List<NativeKeyGlyph> keyGlyphViews = new List<NativeKeyGlyph>();
            private readonly Dictionary<UiNode, NativeKeyGlyph> keyGlyphs = new Dictionary<UiNode, NativeKeyGlyph>(NodeIdentity.Instance);

            internal bool RefreshKeyGlyphs()
            {
                var changed = false;
                for (var index = 0; index < keyGlyphViews.Count; index++) changed |= keyGlyphViews[index].Refresh();
                for (var index = 0; index < actionSlotViews.Count; index++) changed |= actionSlotViews[index].RefreshBinding();
                return changed;
            }

            private void CreateKeyGlyph(NativeNode view, UiKeyGlyph glyph)
            {
                var scope = input ?? new UiSurfaceInputContext(null, string.Empty);
                var binding = scope.BindAction(glyph.ActionName, glyph.WireDefaults);
                var native = new NativeKeyGlyph(new TopiaForgeActionKeyCap(view.Container), binding);
                view.KeyGlyph = native;
                keyGlyphs.TryAdd(glyph, native);
                keyGlyphViews.Add(native);
            }

            private sealed class NativeKeyGlyph
            {
                private readonly TopiaForgeActionKeyCap cap;
                private readonly UiNativeActionBinding binding;

                internal NativeKeyGlyph(TopiaForgeActionKeyCap cap, UiNativeActionBinding binding)
                {
                    this.cap = cap;
                    this.binding = binding;
                }

                internal bool Refresh() => binding.Refresh();

                internal UiLayoutLeafMeasurement Measure(UiLayoutConstraints limits) => cap.Measure(binding.Face, limits.MaxWidth);

                internal void Apply(UiLayoutRect bounds, UiPaintRole backdrop, bool disabled)
                {
                    cap.Apply(binding.Face, new UiLayoutRect(0, 0, bounds.Width, bounds.Height),
                        backdrop, disabled ? UiTone.Muted : UiTone.Neutral);
                }
            }
        }
    }
}
