namespace TopiaForge.ModManager.Tests
{
    internal static partial class ArenaLifecycleTests
    {
        // Execute production native adapters against only the Unity members they consume. No game code is copied.
        private const string Probe = """
            using System;
            using System.Collections.Generic;
            using System.Reflection;
            using UnityEngine;
            using TopiaForge.Worlds;
            using TopiaForge.Mods.GameBridge;
            namespace UnityEngine
            {
                public class Object { public static List<string> Changes = new List<string>(); public bool DestroyScheduled; public static void Destroy(Object value) => value.DestroyScheduled = true; }
                public struct Scene { public bool Valid; public bool IsValid() => Valid; }
                public sealed class GameObject : Object { public Scene scene = new Scene { Valid = true }; public bool Active = true; public void SetActive(bool value) { Active = value; Changes.Add("inactive"); } }
                public enum LightType { Directional, Point }
                public sealed class Light : Object { public LightType type; private bool state = true; public bool enabled { get => state; set { state = value; Changes.Add("light:" + value); } } public GameObject gameObject = new GameObject(); }
                public sealed class Transform { public object? GetComponent(Type type) => null; }
                public struct Vector3 { public float X; }
                public struct Quaternion { public float W; }
            }
            namespace TopiaForge.Worlds
            {
                public sealed class WorldResourceScope : IDisposable
                {
                    private readonly List<IDisposable> values = new List<IDisposable>();
                    public T Add<T>(T value) where T : IDisposable { values.Add(value); return value; }
                    public void Dispose() { for (var index = values.Count - 1; index >= 0; index--) values[index].Dispose(); }
                }
            }
            public static class ArenaProbe
            {
                public sealed class Player { public Movement FPSController { get; } = new Movement(); }
                public sealed class Movement
                {
                    public int Calls; public Vector3 Position; public Quaternion? Rotation;
                    public void TeleportTo(Vector3 position, Quaternion? rotation) { Calls++; Position = position; Rotation = rotation; }
                    public void TeleportTo(Vector3 position, Quaternion rotation) { throw new Exception("Wrong native overload."); }
                }
                public sealed class Missing { }
                public sealed class WrongPlayer { public WrongMovement FPSController { get; } = new WrongMovement(); }
                public sealed class WrongMovement { public bool TeleportTo(Vector3 position, Quaternion? rotation) => true; }
                public sealed class ThrowingPlayer { public ThrowingMovement FPSController { get; } = new ThrowingMovement(); }
                public sealed class ThrowingMovement { public void TeleportTo(Vector3 position, Quaternion? rotation) => throw new ApplicationException("Native refusal"); }
                public static void Run()
                {
                    var sun = new Light();
                    var alreadyDisabled = new Light { enabled = false };
                    var point = new Light { type = LightType.Point };
                    var asset = new Light { gameObject = new GameObject { scene = new Scene() } };
                    var first = new ArenaSunSuppression();
                    first.Apply(new[] { sun, alreadyDisabled, point, asset, sun });
                    Require(!sun.enabled && !alreadyDisabled.enabled && point.enabled && asset.enabled, "only scene directional lights are disabled");
                    var second = new ArenaSunSuppression(); second.Apply(new[] { sun });
                    first.Dispose(); first.Dispose();
                    Require(!sun.enabled && !alreadyDisabled.enabled, "overlap retains suppression and originally disabled state");
                    second.Dispose();
                    Require(sun.enabled && !alreadyDisabled.enabled, "the last owner restores exact inherited state");
                    try { first.Apply(new[] { sun }); throw new Exception("Disposed scope accepted work."); }
                    catch (ObjectDisposedException) { }
                    var root = new GameObject();
                    var scope = new WorldResourceScope();
                    scope.Add(new ArenaSunSuppression()).Apply(new[] { sun });
                    UnityWorldResources.Own(scope, root);
                    UnityEngine.Object.Changes.Clear();
                    scope.Dispose();
                    Require(!root.Active && root.DestroyScheduled && sun.enabled, "owned sun is inactive before deferred destruction and borrowed state restoration");
                    Require(UnityEngine.Object.Changes.IndexOf("inactive") < UnityEngine.Object.Changes.IndexOf("light:True"), "the owned sun stops before inherited light returns");
                    IEnumerable<Light> Interrupted() { yield return sun; throw new ApplicationException("Discovery interrupted"); }
                    try { using var failed = new WorldResourceScope(); failed.Add(new ArenaSunSuppression()).Apply(Interrupted()); }
                    catch (ApplicationException) { }
                    Require(sun.enabled, "partial acquisition restores borrowed light on failure");
                    using (var retry = new ArenaSunSuppression()) { retry.Apply(new[] { sun }); Require(!sun.enabled, "replacement suppresses the restored sun"); }
                    Require(sun.enabled, "replacement restores the sun");
                    var player = new Player();
                    NativePlayerTeleport.PlaceController(player, new Vector3 { X = 12 }, new Quaternion { W = 1 });
                    Require(player.FPSController.Calls == 1 && player.FPSController.Position.X == 12 && player.FPSController.Rotation?.W == 1,
                        "teleport uses the exact native nullable-rotation contract");
                    foreach (var invalid in new object[] { new Missing(), new WrongPlayer() })
                    {
                        try { NativePlayerTeleport.PlaceController(invalid, default, default); throw new Exception("Unsupported native movement was accepted."); }
                        catch (InvalidOperationException) { }
                    }
                    try { NativePlayerTeleport.PlaceController(new ThrowingPlayer(), default, default); throw new Exception("Native failure was swallowed."); }
                    catch (TargetInvocationException error) { Require(error.InnerException is ApplicationException, "native failures propagate"); }
                }
                private static void Require(bool value, string message) { if (!value) throw new Exception("Arena lifecycle failed: " + message); }
            }
            """;
    }
}
