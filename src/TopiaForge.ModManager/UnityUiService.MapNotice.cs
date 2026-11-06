using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService : IInternalMapNoticeUi
    {
        public OperationResult<bool> ShowMapNotice(string text, UiTone tone) => RemoteToast(text, tone);
    }
}
