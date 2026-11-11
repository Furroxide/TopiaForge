using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService : IInternalMapNoticeUi
    {
        OperationResult<bool> IInternalMapNoticeUi.ShowMapNotice(string text, UiTone tone) => ShowToast(text, tone);
    }
}
