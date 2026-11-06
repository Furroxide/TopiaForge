using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.Worlds.Maps
{
    internal static class MapNotices
    {
        internal static void Offline(IUiService ui, string? displayName, string saved, MapOfflineReason reason)
        {
            var name = TopiaForgePlainText.Sanitize(displayName, 128);
            if (name.Length == 0) name = "this map";
            var explanation = reason == MapOfflineReason.Slow ? "the public map service was too slow."
                : reason == MapOfflineReason.ServiceBusy ? "the public map service is temporarily unavailable."
                : "the public map service could not be reached.";
            var text = TopiaForgePlainText.Sanitize("Playing the copy of " + name + " saved on " + saved + "; " + explanation, 512);
            if (ui is IInternalMapNoticeUi plain) plain.ShowMapNotice(text, UiTone.Warning);
            else ui.ShowToast("Playing a saved map copy because the public map service is temporarily unavailable.", UiTone.Warning);
        }
    }
}
