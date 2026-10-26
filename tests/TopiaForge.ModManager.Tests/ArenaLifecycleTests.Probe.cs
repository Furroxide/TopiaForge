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
                public class Object
                {
                    public static List<string> Changes = new List<string>();
                    public bool DestroyScheduled;
                    public bool Destroyed;
                    public static void Destroy(Object value) => value.DestroyScheduled = true;
                    // As in Unity, a destroyed object compares equal to null.
                    public static bool operator ==(Object? left, Object? right) => Alive(left) ? Alive(right) && ReferenceEquals(left, right) : !Alive(right);
                    public static bool operator !=(Object? left, Object? right) => !(left == right);
                    public override bool Equals(object? other) => ReferenceEquals(this, other);
                    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
                    private static bool Alive(Object? value) => !ReferenceEquals(value, null) && !value!.Destroyed;
                }
                public struct Scene { public bool Valid; public bool IsValid() => Valid; }
                public sealed class GameObject : Object { public Scene scene = new Scene { Valid = true }; public bool Active = true; public void SetActive(bool value) { Active = value; Changes.Add("inactive"); } }
                public enum LightType { Directional, Point }
                public sealed class Light : Object
                {
                    public LightType type; private bool state = true; public GameObject gameObject = new GameObject();
                    public bool enabled
                    {
                        get => state;
                        set { if (Destroyed) throw new InvalidOperationException("The destroyed light was used."); state = value; Changes.Add("light:" + value); }
                    }
                }
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
                public sealed class NotReadyPlayer { public Movement? FPSController => null; }
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
                    var unloaded = new Light();
                    var kept = new Light();
                    var held = new ArenaSunSuppression();
                    held.Apply(new[] { unloaded, kept });
                    unloaded.Destroyed = true;
                    held.Dispose();
                    Require(kept.enabled, "a light whose scene unloaded while held is skipped, and the others are restored");
                    var reloaded = new Light();
                    using (var next = new ArenaSunSuppression())
                    {
                        next.Apply(new[] { reloaded, unloaded });
                        Require(!reloaded.enabled, "a later lease suppresses a reloaded scene's sun and skips the destroyed one");
                    }
                    Require(reloaded.enabled, "the later lease restores the reloaded sun");
                    var player = new Player();
                    NativePlayerTeleport.PlaceController(player, new Vector3 { X = 12 }, new Quaternion { W = 1 });
                    Require(player.FPSController.Calls == 1 && player.FPSController.Position.X == 12 && player.FPSController.Rotation?.W == 1,
                        "teleport uses the exact native nullable-rotation contract");
                    foreach (var invalid in new object[] { new Missing(), new WrongPlayer() })
                    {
                        try { NativePlayerTeleport.PlaceController(invalid, default, default); throw new Exception("Unsupported native movement was accepted."); }
                        catch (InvalidOperationException error)
                        {
                            Require(error.Message == NativePlayerTeleport.Unsupported, "a game build without the contract asks for an update, not a retry: " + error.Message);
                        }
                    }
                    try { NativePlayerTeleport.PlaceController(new NotReadyPlayer(), default, default); throw new Exception("A missing movement controller was accepted."); }
                    catch (InvalidOperationException error)
                    {
                        Require(error.Message.Contains("Wait for the level to load", StringComparison.Ordinal), "a controller not set yet asks the player to wait: " + error.Message);
                    }
                    try { NativePlayerTeleport.PlaceController(new ThrowingPlayer(), default, default); throw new Exception("Native failure was swallowed."); }
                    catch (TargetInvocationException error) { Require(error.InnerException is ApplicationException, "native failures propagate"); }
                }
                private static void Require(bool value, string message) { if (!value) throw new Exception("Arena lifecycle failed: " + message); }
            }
            """;
    }
}
