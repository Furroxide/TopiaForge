namespace TopiaForge.ModManager.Tests
{
    internal static partial class WorldWidgetAdapterTests
    {
        private const string KitContract = """
            using System;
            using System.Collections.Generic;
            using TopiaForge.Mods;
            using TopiaForge.Mods.Testing;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            namespace TopiaForge.Mods.UnityUi
            {
                public enum TopiaForgeTone { Neutral, Success, Warning, Danger }
                public enum TopiaForgeGap { None, Xs }
                public interface ITopiaForgeNametagAnchor { bool TryGetPosition(out Vec3 position); }
                public interface ITopiaForgeNametagPresentation { TopiaForgeNametagFalloff Falloff { get; } bool CanShow(Vec3 camera, Vec3 position); }
                public sealed class TopiaForgeNametagFalloff
                {
                    public readonly float Maximum;
                    public TopiaForgeNametagFalloff(float near, float fade, float maximum, float farFade) { Maximum = maximum; }
                }
                public sealed class UiHost
                {
                    public event Action<GameObject>? LayerLost;
                    public int Subscribers => LayerLost?.GetInvocationList().Length ?? 0;
                    public readonly List<TopiaForgeHudLayer> Roots = new List<TopiaForgeHudLayer>();
                    public TopiaForgeHudLayer HudLayer(string name, bool persistent)
                    {
                        if (!persistent) throw new InvalidOperationException("Label root must be persistent.");
                        var root = new TopiaForgeHudLayer(); Roots.Add(root); return root;
                    }
                    public void Lose(TopiaForgeHudLayer root) { root.Go.Destroy(); LayerLost?.Invoke(root.Go); }
                }
                public sealed class TopiaForgeHudLayer
                {
                    public readonly GameObject Go = new GameObject();
                    public TopiaForgeNametagLayer? Layer;
                    public static bool FailNextNametags;
                    public TopiaForgeNametagLayer Nametags(int capacity)
                    {
                        if (FailNextNametags) { FailNextNametags = false; throw new InvalidOperationException("Pool creation failed."); }
                        return Layer = new TopiaForgeNametagLayer(capacity);
                    }
                    public void Destroy() => Go.Destroy();
                }
                public sealed class TopiaForgeNametagLayer
                {
                    public const float DefaultVerticalOffset = 0.35f;
                    public readonly GameObject Go = new GameObject();
                    public readonly List<TopiaForgeNametag> Tags = new List<TopiaForgeNametag>();
                    private readonly int capacity;
                    public TopiaForgeNametagLayer(int capacity) { this.capacity = Math.Min(64, capacity); }
                    public TopiaForgeNametag? Track(ITopiaForgeNametagAnchor anchor, string text, float offset)
                    {
                        if (Tags.FindAll(item => item.Active).Count >= capacity) return null;
                        var tag = new TopiaForgeNametag { Anchor = anchor, Text = text, Offset = offset }; Tags.Add(tag); return tag;
                    }
                }
                public sealed class TopiaForgeNametag
                {
                    public bool Active = true;
                    public ITopiaForgeNametagAnchor Anchor = null!;
                    public string Text = "";
                    public float Offset;
                    public TopiaForgeTone Tone;
                    public void Remove() { Active = false; }
                    public void SetName(string text) { Text = text; }
                    public void SetVerticalOffset(float offset) { Offset = offset; }
                    public void SetTone(TopiaForgeTone tone) { Tone = tone; }
                }
                public sealed class TopiaForgeContainer
                {
                    public readonly GameObject Go = new GameObject();
                    public readonly List<TopiaForgeContainer> Children = new List<TopiaForgeContainer>();
                    public TopiaForgeLabel? Text;
                    public TopiaForgeProgress? Progress;
                    public bool IsDynamic;
                    public TopiaForgeContainer Column(TopiaForgeGap gap, TopiaForgeGap padding)
                    { var child = new TopiaForgeContainer(); Children.Add(child); return child; }
                    public TopiaForgeContainer Dynamic() { IsDynamic = true; return this; }
                    public TopiaForgeLabel Label(string initial) => Text = new TopiaForgeLabel();
                    public TopiaForgeProgress ProgressBar() => Progress = new TopiaForgeProgress();
                }
                public sealed class TopiaForgeLabel
                {
                    public int Writes;
                    public int Seconds;
                    public string Prefix = "";
                    public bool Plain;
                    public TopiaForgeTone Color;
                    public TopiaForgeLabel PlainText() { Plain = true; return this; }
                    public TopiaForgeLabel Tone(TopiaForgeTone tone) { Color = tone; return this; }
                    public void SetText(string prefix, int seconds) { Writes++; Prefix = prefix; Seconds = seconds; }
                }
                public sealed class TopiaForgeProgress
                {
                    public float Fraction;
                    public TopiaForgeTone Color;
                    public TopiaForgeProgress Tone(TopiaForgeTone tone) { Color = tone; return this; }
                    public void SetFraction(float fraction) { Fraction = fraction; }
                }
            }
            namespace TopiaForge.ModManager
            {
                internal sealed partial class OwnerUiService
                {
                    internal readonly UiHost host = new UiHost();
                    internal readonly FakeModLifetime lifetime = new FakeModLifetime();
                    internal readonly UiGameplayGate gameplay = new UiGameplayGate();
                    private static TopiaForgeTone ToNativeTone(UiTone value) => (TopiaForgeTone)value;
                    internal TopiaForgeContainer Render(UiCountdown node)
                    { var parent = new TopiaForgeContainer(); RenderCountdown(node, parent, clock); return parent.Children[0]; }
                    internal TopiaForgeContainer Render(UiProgress node)
                    { var parent = new TopiaForgeContainer(); RenderProgress(node, parent); return parent.Children[0]; }
                    private sealed class UiCallbackGate { internal Func<double> Clock = () => 0; }
                    private sealed partial class UnityUiSurface { private readonly UiCallbackGate callbacks = new UiCallbackGate(); }
                }
            }
            """;
    }
}
