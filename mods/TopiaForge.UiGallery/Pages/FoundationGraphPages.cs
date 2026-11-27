using System;
using TopiaForge.Mods;

namespace TopiaForge.UiGallery.Pages
{
    internal static class FoundationGraphPages
    {
        internal static UiNode Connections(Action<string> report) => new UiColumn(
            new UiText("Relay map", UiTextStyle.Heading),
            new UiGraphCanvas("relay-map",
                new[]
                {
                    Source("west-high", "West ridge", -192, 82),
                    Source("west-low", "West relay", -192, -82),
                    Target("east-high", "East ridge", 192, 82),
                    Target("east-low", "East relay", 192, -82),
                },
                new[]
                {
                    new UiGraphEdge("lower-relay", "west-high", "send", "east-low", "run", UiTone.Primary),
                    new UiGraphEdge("upper-relay", "west-low", "send", "east-high", "run", UiTone.Accent),
                }, _ => { }, height: 320f,
                connectionRemoved: edge => report("Remove requested for " + edge + ".")),
            new UiText("Station map", UiTextStyle.Heading),
            new UiGraphCanvas("station-map",
                new[] { Source("station", "Station", -192, 0), Target("lookout", "Lookout", 192, 0) },
                new[] { new UiGraphEdge("lookout-relay", "station", "send", "lookout", "run") },
                _ => { }, height: 172f,
                connectionRemoved: edge => report("Remove requested for " + edge + ".")));

        private static UiGraphNode Source(string id, string label, float x, float y) =>
            new UiGraphNode(id, "Relay", label, new Vec2(x, y),
                new[] { new UiGraphPort("send", "Send", UiGraphPortDirection.Output) });

        private static UiGraphNode Target(string id, string label, float x, float y) =>
            new UiGraphNode(id, "Relay", label, new Vec2(x, y),
                new[] { new UiGraphPort("run", "Run", UiGraphPortDirection.Input) });
    }
}
