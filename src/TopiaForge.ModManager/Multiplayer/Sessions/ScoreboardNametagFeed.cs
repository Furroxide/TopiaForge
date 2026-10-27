using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>Bounded display-only projection of the presence avatars. Slot positions never authorize player actions.</summary>
    internal sealed class ScoreboardNametagFeed
    {
        internal const int Capacity = 32;
        internal readonly string[] Names = new string[Capacity];
        internal readonly NetVector3[] Positions = new NetVector3[Capacity];
        private readonly string?[] raw = new string?[Capacity];
        internal readonly bool[] Active = new bool[Capacity];
        internal void Tick(IPresenceNametagSource? source, bool inGame)
        {
            for (var i = 0; i < Capacity; i++)
            {
                Active[i] = false;
                if (!inGame || source == null || i >= source.NametagCount || !source.TryGetNametag(i, out var tag)) continue;
                Positions[i] = tag.Anchor; Active[i] = true;
                if (raw[i] == tag.DisplayName) continue;
                raw[i] = tag.DisplayName; Names[i] = HostText.ForDisplay(tag.DisplayName, 32);
            }
        }
    }
}
