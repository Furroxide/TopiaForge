namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotInteractionAuthorityTests
    {
        private const string Engine = """
using System;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
namespace Cysharp.Threading.Tasks
{
    public readonly struct UniTask { public static UniTask CompletedTask => default; }
}
namespace UnityEngine
{
    public class Component
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
    }
    public class Behaviour : Component
    {
        public bool enabled = true;
        public bool isActiveAndEnabled => enabled;
    }
    public class MonoBehaviour : Behaviour { }
    public sealed class Transform { public Vector3 position; }
    public sealed class GameObject
    {
        public readonly Transform transform = new Transform();
        public T AddComponent<T>() where T : Component
        {
            var component = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
            component.gameObject = this;
            typeof(T).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(component, null);
            return component;
        }
    }
    public readonly struct Vector3
    {
        public readonly float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static float Distance(Vector3 left, Vector3 right) =>
            (float)Math.Sqrt(Math.Pow(left.x - right.x, 2) + Math.Pow(left.y - right.y, 2) + Math.Pow(left.z - right.z, 2));
    }
    public static class Mathf { public static float Max(float left, float right) => Math.Max(left, right); }
}
public interface IInteractable
{
    GameObject GameObject { get; }
    float ScreenRectExpansion { get; }
    Behaviour AsComponent();
    bool CanInteract(Transform hand);
    UniTask OnInteractAttempt(Transform hand, CancellationToken token);
}
public sealed class ProbeSignal<T>
{
    public T Value = default!;
    public event Action<T>? Changed;
    public void Set(T value) { Value = value; Changed?.Invoke(value); }
}
public static class UIState
{
    public static readonly ProbeSignal<IInteractable?> InteractTarget = new();
    public static readonly ProbeSignal<string> InteractAction = new();
}
public static class ListenableExt
{
    public static void WithState<T>(object owner, ProbeSignal<T> signal, Action<T> callback) => signal.Changed += callback;
}
namespace TopiaForge.RobotKit
{
    internal static class GameReflection
    {
        internal static Component? FindComponent(GameObject gameObject, string name) =>
            name == "Interactable" ? new Component { gameObject = gameObject } : null;
    }
}
""";
    }
}
