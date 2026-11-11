namespace TopiaForge.ModManager.Tests
{
    internal static partial class NativeSpawnWiringTests
    {
        private const string Boundaries = """
using System;
using UnityEngine.SceneManagement;
namespace UnityEngine
{
    public enum FindObjectsSortMode { None }
    public class Object
    {
        public bool Destroyed;
        public static Object[] FindObjectsByType(Type type, FindObjectsSortMode mode) => Array.Empty<Object>();
        public static bool operator ==(Object? left, Object? right)
        {
            var firstNull = ReferenceEquals(left, null) || left!.Destroyed;
            var secondNull = ReferenceEquals(right, null) || right!.Destroyed;
            return firstNull || secondNull ? firstNull == secondNull : ReferenceEquals(left, right);
        }
        public static bool operator !=(Object? left, Object? right) => !(left == right);
        public override bool Equals(object? value) => ReferenceEquals(this, value);
        public override int GetHashCode() => base.GetHashCode();
    }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
        public T? GetComponent<T>() where T : class => gameObject.GetComponent<T>();
        public Component? GetComponent(Type type) => gameObject.GetComponent(type);
    }
    public class Behaviour : Component { public bool enabled = true; }
    public sealed class CharacterController : Component { public bool enabled = true; public float height = 2f; }
    public sealed class GameObject : Object
    {
        public bool activeInHierarchy = true;
        public Scene scene = SceneManager.World;
        public readonly Transform transform;
        public CharacterController? Controller;
        public SettlementProbe.PlayerController? Player;
        public GameObject() { transform = new Transform { gameObject = this }; }
        public T? GetComponent<T>() where T : class => GetComponent(typeof(T)) as T;
        public Component? GetComponent(Type type)
        {
            if (Player != null && type.IsInstanceOfType(Player)) return Player;
            if (Controller != null && type.IsInstanceOfType(Controller)) return Controller;
            return null;
        }
    }
    public sealed class Transform : Component
    {
        public Vector3 position;
        public Quaternion rotation = new Quaternion(0, 0, 0, 1);
        public Vector3 localScale = new Vector3(1, 1, 1);
        public Vector3 lossyScale = new Vector3(1, 1, 1);
        public string name = "Player";
        public int childCount => 0;
        public Transform GetChild(int index) => throw new ArgumentOutOfRangeException(nameof(index));
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    }
    public static class Time
    {
        private static float fixedEpoch;
        public static float timeScale;
        public static int frameCount;
        internal static Action? ReadingFixedTime;
        public static float fixedTime
        {
            get { ReadingFixedTime?.Invoke(); return fixedEpoch; }
            set { fixedEpoch = value; }
        }
    }
    public static class Debug { public static void LogWarning(object value) { } }
}
namespace UnityEngine.SceneManagement
{
    public enum LoadSceneMode { Single, Additive }
    public struct Scene
    {
        public int handle;
        public string name, path;
        public bool isLoaded;
        public bool IsValid() => handle != 0;
        public UnityEngine.GameObject[] GetRootGameObjects() => Array.Empty<UnityEngine.GameObject>();
    }
    public static class SceneManager
    {
        public static Scene World = new Scene { handle = 7, name = "World", path = "World.unity", isLoaded = true };
        public static int sceneCount => 1;
        public static Scene GetSceneAt(int index) => World;
        public static event Action<Scene, LoadSceneMode> sceneLoaded = delegate { };
        public static event Action<Scene> sceneUnloaded = delegate { };
        public static void Unload() { var previous = World; World.isLoaded = false; sceneUnloaded(previous); }
    }
}
namespace SettlementProbe
{
    public sealed class PlayerController : UnityEngine.Component
    {
        public static PlayerController? Current;
        public MovementController FPSController { get; private set; } = null!;
        public static PlayerController? FindPlayer() => Current;
        internal static PlayerController Create()
        {
            var root = new UnityEngine.GameObject();
            var player = new PlayerController { gameObject = root };
            player.FPSController = new MovementController { gameObject = root };
            root.Player = player; root.Controller = new UnityEngine.CharacterController { gameObject = root };
            return player;
        }
    }
    public sealed class MovementController : UnityEngine.Behaviour
    {
        public static int Teleports;
        public static float Lift;
        public static Action? AfterTeleport;
        private bool pendingLift;
        public void TeleportTo(UnityEngine.Vector3 position, UnityEngine.Quaternion? rotation)
        {
            Teleports++;
            transform.position = position;
            transform.rotation = rotation ?? transform.rotation;
            pendingLift = true;
            AfterTeleport?.Invoke();
        }
        internal void FixedStep()
        {
            if (!pendingLift) return;
            pendingLift = false;
            transform.position.y += Lift;
        }
    }
}
namespace TopiaForge.ModManager
{
    internal sealed class UnityEntityRegistry
    {
        internal bool TryGetGameObject(TopiaForge.Mods.IEntity entity, out UnityEngine.GameObject root)
        { root = null!; return false; }
    }
}
""";
    }
}
