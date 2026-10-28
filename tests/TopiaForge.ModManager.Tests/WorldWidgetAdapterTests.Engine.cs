namespace TopiaForge.ModManager.Tests
{
    internal static partial class WorldWidgetAdapterTests
    {
        private const string EngineContract = """
            using System;
            using System.Collections.Generic;
            using System.Reflection;
            using TopiaForge.Mods;
            using UnityEngine.SceneManagement;
            namespace UnityEngine
            {
                public class MonoBehaviour
                {
                    public GameObject gameObject = null!;
                    public void Frame(string method) => GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(this, null);
                }
                public sealed class GameObject
                {
                    public bool Destroyed;
                    public Scene scene = SceneManager.Active;
                    public readonly Transform transform = new Transform();
                    public readonly List<MonoBehaviour> Components = new List<MonoBehaviour>();
                    public T AddComponent<T>() where T : MonoBehaviour, new()
                    {
                        if (FailNextComponent) { FailNextComponent = false; throw new InvalidOperationException("Component creation failed."); }
                        var item = new T { gameObject = this }; Components.Add(item); return item;
                    }
                    public static bool FailNextComponent;
                    public void Destroy()
                    {
                        Destroyed = true;
                        foreach (var component in Components) component.Frame("OnDestroy");
                    }
                }
                public sealed class Transform
                {
                    public Transform? Parent;
                    public bool IsChildOf(Transform target) => ReferenceEquals(this, target) || (Parent?.IsChildOf(target) ?? false);
                }
                public struct Vector3
                {
                    public float x, y, z;
                    public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
                    public float magnitude => (float)Math.Sqrt(x*x + y*y + z*z);
                    public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x-b.x, a.y-b.y, a.z-b.z);
                    public static Vector3 operator /(Vector3 a, float b) => new Vector3(a.x/b, a.y/b, a.z/b);
                }
                public sealed class Collider { public Transform transform = new Transform(); }
                public struct RaycastHit { public Collider collider; }
                public enum QueryTriggerInteraction { Ignore }
                public static class Physics
                {
                    public const int DefaultRaycastLayers = -5;
                    public static RaycastHit[] Results = Array.Empty<RaycastHit>();
                    public static int Calls;
                    public static int RaycastNonAlloc(Vector3 start, Vector3 direction, RaycastHit[] into, float distance, int mask, QueryTriggerInteraction triggers)
                    {
                        Calls++;
                        if (mask != DefaultRaycastLayers || triggers != QueryTriggerInteraction.Ignore || distance <= 0)
                            throw new InvalidOperationException("Invalid raycast policy.");
                        var count = Math.Min(Results.Length, into.Length); Array.Copy(Results, into, count); return count;
                    }
                }
                public static class Time
                {
                    private static double now;
                    public static int Reads;
                    public static double realtimeSinceStartupAsDouble { get { Reads++; return now; } set { now = value; } }
                }
            }
            namespace UnityEngine.SceneManagement
            {
                public sealed class SceneData { public bool Loaded = true; }
                public struct Scene { public SceneData Data; public bool isLoaded => Data != null && Data.Loaded; }
                public static class SceneManager
                {
                    public static Scene Active = new Scene { Data = new SceneData() };
                    public static Scene GetActiveScene() => Active;
                }
            }
            namespace TopiaForge.ModManager
            {
                internal sealed class UiGameplayGate { public bool IsGameplay = true; }
                internal sealed class UnityEntityRegistry
                {
                    internal readonly Dictionary<IEntity, UnityEngine.GameObject> Objects = new Dictionary<IEntity, UnityEngine.GameObject>();
                    internal bool TryGetGameObject(IEntity entity, out UnityEngine.GameObject value) => Objects.TryGetValue(entity, out value!);
                }
            }
            """;
    }
}
