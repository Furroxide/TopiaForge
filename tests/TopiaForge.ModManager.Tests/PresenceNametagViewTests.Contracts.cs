namespace TopiaForge.ModManager.Tests
{
    internal static partial class PresenceNametagViewTests
    {
        private const string Contracts = """
            using System;
            using System.Collections.Generic;
            using UnityEngine;
            using TopiaForge.Mods;
            using TopiaForge.ModManager.Core.Multiplayer;
            namespace UnityEngine
            {
                public class GameObject { }
                public class MonoBehaviour
                {
                    public void RunLateUpdate() => GetType().GetMethod("LateUpdate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(this, null);
                }
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class DefaultExecutionOrder : Attribute
                {
                    public int Order;
                    public DefaultExecutionOrder(int order) { Order = order; }
                }
            }
            namespace TopiaForge.Mods.Internal
            {
                internal enum InternalSessionRole { Solo, ListenHost, Client }
                internal interface IInternalMultiplayerControl { bool IsLiveAvailable { get; } InternalSessionRole Role { get; } }
            }
            namespace TopiaForge.ModManager.Core.Multiplayer
            {
                internal readonly struct NetVector3
                {
                    public readonly float X, Y, Z;
                    public NetVector3(float x, float y, float z) { X = x; Y = y; Z = z; }
                }
            }
            namespace TopiaForge.ModManager
            {
                internal sealed class CoreGameplayServices
                {
                    public Action Interpolate;
                    private void BeginFixedUpdate() { }
                    private void BeginLateUpdate() => Interpolate();
                    // The extracted production loop is nested here, as it is at runtime.
                    CORE_GAMEPLAY_LOOP
                    internal UnityEngine.MonoBehaviour Loop() => new CoreGameplayLoop { Owner = this };
                }
                internal readonly struct PresenceNametag
                {
                    public PresenceNametag(string name, NetVector3 anchor) { DisplayName = name; Anchor = anchor; }
                    public string DisplayName { get; }
                    public NetVector3 Anchor { get; }
                }
                internal interface IPresenceNametagSource
                { int NametagCount { get; } int NametagVersion { get; } bool TryGetNametag(int slot, out PresenceNametag tag); }
            }
            namespace TopiaForge.ModManager.Sessions
            {
                // Privacy and sanitization run against real HostText in PresenceNametagTests.
                internal static class HostText { internal static string ForDisplay(string value, int maximum) => value; }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                public sealed class TopiaForgeUiOptions { public string OwnerId; }
                public static class TopiaForgeUi { public static UiHost Create(TopiaForgeUiOptions options) => new UiHost(); }
                public sealed class UiHost : IDisposable
                {
                    public static int Created; public static UiHost Last;
                    public int Disposals; public bool Persistent;
                    public TopiaForgeHudLayer Layer = new TopiaForgeHudLayer();
                    public event Action<UnityEngine.GameObject> LayerLost;
                    public UiHost() { Created++; Last = this; }
                    public TopiaForgeHudLayer HudLayer(string id, bool persistent) { Persistent = persistent; return Layer; }
                    public void Lose() => LayerLost?.Invoke(new UnityEngine.GameObject());
                    public void Dispose() { Disposals++; LayerLost?.Invoke(new UnityEngine.GameObject()); }
                }
                public sealed class TopiaForgeHudLayer
                {
                    public bool Shown; public TopiaForgeNametagLayer Tags;
                    public TopiaForgeNametagLayer Nametags(int capacity) => Tags = new TopiaForgeNametagLayer(capacity);
                    public void SetShown(bool value) => Shown = value;
                }
                public interface ITopiaForgeNametagAnchor
                {
                    bool TryGetPosition(out Vec3 position);
                }
                public sealed class TopiaForgeNametagLayer
                {
                    public static int FailAt = -1;
                    public readonly List<TopiaForgeNametag> Items = new List<TopiaForgeNametag>();
                    public void Tick()
                    {
                        foreach (var tag in Items) tag.Visible = tag.Anchor.TryGetPosition(out tag.Projected);
                    }
                    private readonly int capacity;
                    public TopiaForgeNametagLayer(int capacity) { this.capacity = capacity; }
                    public TopiaForgeNametag Track(ITopiaForgeNametagAnchor anchor, string name, float verticalOffset)
                    {
                        if (Items.Count == FailAt) throw new InvalidOperationException("pool build failed");
                        if (Items.Count == capacity) throw new Exception("pool exceeded capacity");
                        var tag = new TopiaForgeNametag { Anchor = anchor, Name = name, Offset = verticalOffset };
                        Items.Add(tag); return tag;
                    }
                }
                public sealed class TopiaForgeNametag
                {
                    public ITopiaForgeNametagAnchor Anchor; public string Name; public float Offset;
                    public bool Visible; public Vec3 Projected;
                    public void SetName(string value) => Name = value;
                }
            }
            """;
    }
}
