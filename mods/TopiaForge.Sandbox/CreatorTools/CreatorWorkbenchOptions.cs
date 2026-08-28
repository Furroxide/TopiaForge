using System;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed class CreatorWorkbenchOptions
    {
        public CreatorWorkbenchOptions(
            string surfaceId,
            string title,
            CreatorProjectScope projectScope,
            int maximumInstances,
            bool showHud,
            bool conversationEnabled,
            int chatMaxTurns,
            float chatTemperature,
            string worldId = "")
        {
            SurfaceId = surfaceId ?? throw new ArgumentNullException(nameof(surfaceId));
            Title = title ?? throw new ArgumentNullException(nameof(title));
            ProjectScope = projectScope;
            MaximumInstances = Math.Max(1, Math.Min(256, maximumInstances));
            ShowHud = showHud;
            ConversationEnabled = conversationEnabled;
            ChatMaxTurns = Math.Max(1, Math.Min(24, chatMaxTurns));
            ChatTemperature = Math.Max(0f, Math.Min(2f, chatTemperature));
            WorldId = worldId ?? string.Empty;
        }

        public string SurfaceId { get; }
        public string Title { get; }
        public CreatorProjectScope ProjectScope { get; }
        public int MaximumInstances { get; }
        public bool ShowHud { get; }
        public bool ConversationEnabled { get; }
        public int ChatMaxTurns { get; }
        public float ChatTemperature { get; }
        public string WorldId { get; }
    }
}
