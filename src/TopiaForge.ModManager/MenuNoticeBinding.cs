using System;

namespace TopiaForge.ModManager
{
    internal sealed class MenuNoticeBinding
    {
        private MenuLoadPresentation? shown;
        private int version = -1;
        private bool ownerCurrent;
        internal bool TryRead(MenuLoadPresentation? source, out string text, out bool pending)
        {
            var active = source?.OwnerCurrent == true;
            var nextVersion = source?.Version ?? 0;
            text = string.Empty; pending = false;
            if (ReferenceEquals(source, shown) && nextVersion == version && active == ownerCurrent) return false;
            shown = source; version = nextVersion; ownerCurrent = active;
            text = source?.Text ?? string.Empty; pending = source?.Pending == true;
            return true;
        }
        internal static bool CanFinish(object? currentBuild, object build, object currentOwner, object originalOwner,
            MenuLoadPresentation? currentSource, MenuLoadPresentation? originalSource, MenuLoadPresentation.Attempt? attempt) =>
            ReferenceEquals(currentBuild, build) && ReferenceEquals(currentOwner, originalOwner)
            && ReferenceEquals(currentSource, originalSource) && (originalSource == null || originalSource.IsCurrent(attempt));
        internal void Reset() { shown = null; version = -1; ownerCurrent = false; }
    }
}
