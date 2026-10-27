namespace TopiaForge.ModManager.Tests
{
    internal static partial class ScoreboardViewContractTests
    {
        private const string OverlayContracts = """
            using System;
            using System.Collections.Generic;
            using TopiaForge.ModManager.Sessions;
            using TopiaForge.ModManager.Voice;
            using TopiaForge.Mods;
            using UnityEngine;
            namespace TopiaForge.Mods
            {
                public readonly struct Vec3 { public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; } public readonly float X, Y, Z; public static Vec3 Zero => default; }
                public struct InputBinding { public static InputBinding Key(string key) => default; }
                public class InputActionDefinition { public InputActionDefinition(string name, string title, InputBinding[] bindings) { } }
                public interface IInputAction : IDisposable { bool IsHeld { get; } bool WasPressed { get; } }
                public class KeyAction : IInputAction { public bool IsHeld { get; set; } public bool WasPressed { get; set; } public bool Disposed; public void Dispose() => Disposed = true; }
                public class Registration { public KeyAction Key = new KeyAction(); public bool TryGetValue(out IInputAction key) { key = Key; return true; } }
                public class Inputs { private Registration result = new Registration(); public KeyAction Key => result.Key; public Registration Register(string owner, InputActionDefinition definition) => result; }
            }
            namespace TopiaForge.ModManager.Voice { public class VoiceChatRuntime { public bool PushToTalkSuspended; } }
            namespace TopiaForge.ModManager
            {
                public class ScoreboardData { }
                public class Gameplay { public Inputs Inputs = new Inputs(); public Gameplay UiGameplay => this; public bool IsGameplay = true; }
                public class Players { public IReadOnlyList<ScoreboardData> Scoreboard => Array.Empty<ScoreboardData>(); }
                public class TopiaForgeModManagerPlugin
                { public Gameplay Gameplay = new Gameplay(); public Players Players = new Players(); public object? Nametags; public VoiceChatRuntime Voice = new VoiceChatRuntime(); }
            }
            namespace TopiaForge.ModManager.Sessions
            {
                internal enum VoiceIndicator { Idle }
                internal class ScoreboardPresenter : IScoreboardScreen
                {
                    public readonly List<ScoreboardRow> Players = new List<ScoreboardRow>();
                    public readonly List<string> Titles = new List<string>();
                    public IReadOnlyList<ScoreboardRow> Rows => Players; public IReadOnlyList<string> Columns => Titles;
                    public bool Available = true; public void Tick() { }
                    public string Note => ""; public int SortColumn => -1; public bool Descending => false;
                    public int LastColumn; public ScoreboardRow? LastRow;
                    public void Sort(int column) => LastColumn = column;
                    public void Mute(ScoreboardRow row) => LastRow = row;
                    public void Volume(ScoreboardRow row, float value) => LastRow = row;
                    public void ServerMute(ScoreboardRow row) => LastRow = row;
                    public void Kick(ScoreboardRow row) => LastRow = row;
                    public void Ban(ScoreboardRow row) => LastRow = row;
                }
                internal class MultiplayerSessionsPresenter
                { public ScoreboardPresenter CreateScoreboard(Func<IReadOnlyList<ScoreboardData>> scores, Func<VoiceChatRuntime?> voice) => new ScoreboardPresenter(); }
                internal class ScoreboardNametagFeed
                {
                    public const int Capacity = 32; public bool[] Active = new bool[Capacity];
                    public Vec3[] Positions = new Vec3[Capacity]; public string[] Names = new string[Capacity];
                    public void Tick(object? source, bool active) { }
                }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                public class TopiaForgeUiOptions { public string OwnerId = ""; }
                public static class TopiaForgeUi { public static UiHost Create(TopiaForgeUiOptions options) => new UiHost(); }
                public class UiHost : IDisposable
                {
                    public static UiHost? Last; public UiHost() { Last = this; }
                    public event Action<GameObject>? LayerLost;
                    public void Lose() => LayerLost?.Invoke(new GameObject());
                    public void Dispose() { }
                    public TopiaForgeHudLayer HudLayer(string id, bool persistent) => new TopiaForgeHudLayer();
                    public Layer Layer(string id, TopiaForgeLayerBand band, TopiaForgeScheme scheme, bool interactive, bool persistent) => new Layer();
                }
                public class Layer { public TopiaForgeContainer Panel(TopiaForgePanelStyle style) => new TopiaForgeContainer(); }
                public class TopiaForgeHudLayer : Widget { public Tags Nametags(int count) => new Tags(); }
                public class Tags { public TopiaForgeNametag Track(TopiaForgeWorldPointAnchor anchor, string name, float verticalOffset) => new TopiaForgeNametag(); }
                public class TopiaForgeWorldPointAnchor { public TopiaForgeWorldPointAnchor(Vec3 position) { } public Vec3 Position; public bool Active; }
                public class TopiaForgeNametag { public void SetName(string name) { } }
            }
            """;
    }
}
