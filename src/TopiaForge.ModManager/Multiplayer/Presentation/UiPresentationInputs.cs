using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Provides registered action definitions within the presenting mod's own package.</summary>
    internal interface IUiInputDefaultsSource
    {
        bool TryGetUiInputDefaults(string actionName, out IReadOnlyList<InputBinding> defaults);
    }

    internal static class UiPresentationInputs
    {
        internal static OperationResult<bool> Prepare(IUiService ui, UiNode? content, out UiNode? presentation)
        {
            presentation = null;
            try
            {
                var source = ui as IUiInputDefaultsSource;
                presentation = UiWireCodec.ProjectInputDefaults(content, source == null ? null : GetDefaults);
                return UiPresentationTheme.Allowed;

                IReadOnlyList<InputBinding>? GetDefaults(string name) =>
                    source!.TryGetUiInputDefaults(name, out var defaults) ? defaults : null;
            }
            catch (ArgumentException error)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, error.Message);
            }
        }
    }
}
