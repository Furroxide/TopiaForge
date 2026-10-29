using System.Collections.Generic;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakePlayerDirectory
    {
        private readonly Dictionary<ParticipantId, AvatarPresentation> appearances = new Dictionary<ParticipantId, AvatarPresentation>();
        private IPlayerAppearance AppearanceFor(ParticipantId id) => new AvatarAppearanceControls(() =>
        {
            if (disposed || !states.ContainsKey(id)) return null;
            if (!appearances.TryGetValue(id, out var presentation))
                appearances.Add(id, presentation = new AvatarPresentation(new AvatarAppearance(RgbaColor.White, RgbaColor.White)));
            return presentation;
        }, () => IsAuthoritative ? ModErrorCode.None : ModErrorCode.NotAuthoritative, lifetime);
        private void RetireAppearance(ParticipantId id)
        {
            if (!appearances.TryGetValue(id, out var presentation)) return;
            presentation.Retire(); appearances.Remove(id);
        }
    }
}
