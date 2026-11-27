namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private const string GraphConnectionRecorder = """
            using System.Collections.Generic;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine.UI;

            namespace TopiaForge.ModManager
            {
                internal sealed partial class UiGalleryNativeTree
                {
                    private readonly List<Observation> observations = new();

                    internal string CapturedCaption { get; private set; } = string.Empty;
                    internal bool CapturedLabelFits { get; private set; }

                    internal bool CaptureLauncher(TopiaForgeGraphConnections connections) => GraphConnectionsLauncher(connections);

                    /* recorder methods */

                    private static int EffectiveColor(Image image) => image.color;

                    private static UiThemeColor? ToRgb(int color) => null;

                    private bool GraphConnectionControl(string path, string? id, string kind, TopiaForgeGraphConnectionControl control,
                        string display, string semantic, UiThemeColor? backdrop, bool selected, bool labelFits = true)
                    {
                        CapturedCaption = display;
                        CapturedLabelFits = labelFits;
                        return control.SemanticLabel == semantic;
                    }

                    private sealed record Observation(string path, string? id, string kind, UiCaptureRect bounds, UiCaptureRect clip,
                        UiCaptureNodeContent content, UiCaptureNodePaint paint, bool rendered, bool clipped, bool occluded);
                }
            }
            """;
    }
}
