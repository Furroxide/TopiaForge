using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Zombies
{
    /// <summary>
    /// World-projected transient labels on the HUD layer's unscaled World root: score/
    /// damage floaters (pool size from FloatingNumberMaxConcurrent, TTL from
    /// FloatingNumberRiseSeconds) and robot speech bubbles (pool of 12, TTL from
    /// SpeechBubbleSeconds). The kit pools tick themselves every frame, matching the
    /// old always-on world-label update.
    /// </summary>
    internal sealed class WorldLabelLayer
    {
        private const int SpeechCapacity = 12;

        private readonly HudContext context;
        private readonly QwFloaterLayer floaters;
        private readonly QwFloaterLayer speech;

        public WorldLabelLayer(HudContext context)
        {
            this.context = context;
            floaters = context.Hud.Floaters(Mathf.Max(1, context.Config.FloatingNumberMaxConcurrent));
            speech = context.Hud.SpeechBubbles(SpeechCapacity);
        }

        public void PushFloater(Vector3 world, string text, Color color)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            floaters.Push(world, text, color, Mathf.Max(0.05f, context.Config.FloatingNumberRiseSeconds));
        }

        public void PushSpeech(Vector3 world, string text, Color color)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            speech.Push(world, text, color, Mathf.Max(0.2f, context.Config.SpeechBubbleSeconds));
        }

        public void Clear()
        {
            floaters.Clear();
            speech.Clear();
        }
    }
}
