using System;

namespace TopiaForge.Mods
{
    internal enum UiOverlayContentKind
    {
        PresentationOnly,
        Interactive,
    }

    /// <summary>Checks the initial content class and protects it across retained overlay updates.</summary>
    internal static class UiOverlayAdmission
    {
        private static readonly OperationResult<UiOverlayContentKind> PresentationOnly =
            OperationResult<UiOverlayContentKind>.Success(UiOverlayContentKind.PresentationOnly);
        private static readonly OperationResult<UiOverlayContentKind> Interactive =
            OperationResult<UiOverlayContentKind>.Success(UiOverlayContentKind.Interactive);
        private static readonly OperationResult<UiOverlayContentKind> ButtonRequired =
            OperationResult<UiOverlayContentKind>.Failure(ModErrorCode.InvalidArgument,
                "A non-dismissible overlay requires a reachable enabled button.");
        private static readonly OperationResult<UiOverlayContentKind> DeliveryChanged =
            OperationResult<UiOverlayContentKind>.Failure(ModErrorCode.InvalidArgument,
                "An overlay keeps its initial interactive or presentation-only delivery class.");

        internal static OperationResult<UiOverlayContentKind> Check(UiNode? content, bool dismissible, UiOverlayContentKind? admitted = null)
        {
            try { UiComposition.Validate(content); }
            catch (ArgumentException error)
            {
                return OperationResult<UiOverlayContentKind>.Failure(ModErrorCode.InvalidArgument, error.Message);
            }

            if (!dismissible && !HasEnabledButton(content)) return ButtonRequired;
            var result = UiComposition.ContainsInteractive(content) ? Interactive : PresentationOnly;
            return admitted.HasValue && result.Value != admitted.Value ? DeliveryChanged : result;
        }

        private static bool HasEnabledButton(UiNode? node)
        {
            if (node == null || UiLayoutTree.IsDisabled(node)) return false;
            if (node is UiButton) return true;
            if (node is UiTabs { Enabled: false } tabs)
                return HasEnabledButton(tabs.Tabs[tabs.IndexOf(tabs.SelectedTabId)].Child);

            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++)
                if (HasEnabledButton(children[index])) return true;

            return false;
        }
    }
}
