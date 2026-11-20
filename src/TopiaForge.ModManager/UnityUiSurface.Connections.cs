using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiSurface
        {
            private TopiaForgeGraphConnections? graphConnections;

            partial void SyncGraphConnections()
            {
                if (remoteComposition || (window == null && fullscreen == null)) return;
                if (graphConnections == null)
                {
                    var actionable = false;
                    foreach (var graph in retainedGraphs.Values)
                    {
                        if (!graph.HasActionableConnections) continue;
                        actionable = true;
                        break;
                    }

                    var scroll = actionable ? compositionParent?.Go.GetComponentInParent<ScrollRect>(includeInactive: true) : null;
                    if (scroll == null) return;
                    var titleBar = window?.TitleBar ?? fullscreen!.TitleBar;
                    var content = window?.Content ?? fullscreen!.Content;
                    graphConnections = new TopiaForgeGraphConnections(titleBar, content, scroll);
                }

                graphConnections.Bind(retainedGraphs);
            }

            partial void ResetGraphConnections(bool retire)
            {
                var connections = graphConnections;
                if (retire) graphConnections = null;
                connections?.Reset(retire);
            }
        }
    }
}
