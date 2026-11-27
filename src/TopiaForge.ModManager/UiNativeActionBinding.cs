using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>Samples one authored name from the viewer's package on the current input device.</summary>
    internal sealed class UiNativeActionBinding
    {
        private readonly Func<IInputAction?> find;
        private readonly IReadOnlyList<UiInputBindingHint> defaults;
        private IReadOnlyList<InputBinding>? previousBindings;
        private UiInputDevice previousDevice;
        private bool sampled;

        internal UiNativeActionBinding(Func<IInputAction?> find, IReadOnlyList<UiInputBindingHint> defaults)
        {
            this.find = find;
            this.defaults = defaults;
            Refresh();
        }

        internal UiActionBindingPresentation Face { get; private set; }

        internal bool Refresh()
        {
            var live = find()?.Bindings;
            var device = TopiaForgeInputOwnership.LastViewerDevice;
            if (sampled && device == previousDevice && ReferenceEquals(previousBindings, live)) return false;

            var next = UiActionBindingPresentation.Resolve(live, defaults, device);
            var changed = !sampled || !string.Equals(Face.Label, next.Label, StringComparison.Ordinal) ||
                !string.Equals(Face.SpokenControl, next.SpokenControl, StringComparison.Ordinal) || Face.Cue != next.Cue;
            previousBindings = live;
            previousDevice = device;
            Face = next;
            sampled = true;
            return changed;
        }
    }
}
