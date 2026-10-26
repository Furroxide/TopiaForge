using System.IO;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        /// <summary>
        /// Kit scroll views following the selection (TopiaForgeScrollFollow), compiled unchanged against a small contract
        /// double of Unity's rect transforms and scroll views, since it needs Unity to run. At 1920x1080 a W at the main menu
        /// moved the UI Probe window's selection to a control scrolled out of view, and nothing showed.
        /// </summary>
        private static void AKitScrollViewFollowsTheSelection()
        {
            var kit = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
            RunProbe("ScrollFollowProbe", "ScrollFollowScenarios",
                new[]
                {
                    File.ReadAllText(Path.Combine(kit, "Core", "TopiaForgeScrollReveal.cs")),
                    File.ReadAllText(Path.Combine(kit, "Runtime", "TopiaForgeScrollFollow.cs")),
                    ScrollFollowContract,
                    ScrollFollowScenarios,
                },
                "AControlBelowTheFoldScrollsIntoView", "AControlAboveScrollsBackDown", "AControlShownInFullMovesNothing",
                "OnlyAMoveOfTheSelectionScrolls", "NothingOfTheGamesAndNothingOutsideTheContentScrolls", "NestedScrollViewsBothFollow",
                "DisabledScrollViewsDoNotFollow", "FollowingASelectionAllocatesNothing");
        }

        // A contract double, not Unity's code: rect transforms are boxes laid out by an offset from their parent, with no
        // rotation or scale, so a world corner is the sum of the offsets plus the box's own corner; a scroll view's content
        // moves by its anchored position, which is its offset.
        private const string ScrollFollowContract = """
            using System.Collections.Generic;

            namespace UnityEngine
            {
                public class Object { }
                public class Component : Object
                {
                    public GameObject gameObject = null!;
                    public Transform transform => gameObject.transform;
                }
                public class Behaviour : Component
                {
                    public bool enabled { get; set; } = true;
                    public bool isActiveAndEnabled => enabled && gameObject.activeInHierarchy;
                }
                public class MonoBehaviour : Behaviour { }
                public struct Vector2
                {
                    public float x;
                    public float y;
                    public Vector2(float x, float y) { this.x = x; this.y = y; }
                }
                public struct Vector3
                {
                    public float x;
                    public float y;
                    public float z;
                    public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
                }
                public struct Rect
                {
                    public Rect(float x, float y, float width, float height) { xMin = x; yMin = y; xMax = x + width; yMax = y + height; }
                    public float xMin { get; }
                    public float yMin { get; }
                    public float xMax { get; }
                    public float yMax { get; }
                }
                public class Transform : Component
                {
                    public Transform? parent;
                    public Vector2 Offset;
                    public bool IsChildOf(Transform other)
                    {
                        for (var at = this; at != null; at = at.parent) if (ReferenceEquals(at, other)) return true;
                        return false;
                    }
                    public bool TryGetComponent<T>(out T component) => gameObject.TryGetComponent(out component);
                    public Vector2 World()
                    {
                        var x = 0f; var y = 0f;
                        for (var at = this; at != null; at = at.parent) { x += at.Offset.x; y += at.Offset.y; }
                        return new Vector2(x, y);
                    }
                    public Vector3 InverseTransformPoint(Vector3 point)
                    {
                        var world = World();
                        return new Vector3(point.x - world.x, point.y - world.y, point.z);
                    }
                }
                public class RectTransform : Transform
                {
                    public Rect rect { get; set; }
                    public Vector2 anchoredPosition { get => Offset; set => Offset = value; }
                    public void GetWorldCorners(Vector3[] corners)
                    {
                        var world = World();
                        corners[0] = new Vector3(world.x + rect.xMin, world.y + rect.yMin, 0f);
                        corners[1] = new Vector3(world.x + rect.xMin, world.y + rect.yMax, 0f);
                        corners[2] = new Vector3(world.x + rect.xMax, world.y + rect.yMax, 0f);
                        corners[3] = new Vector3(world.x + rect.xMax, world.y + rect.yMin, 0f);
                    }
                }
                public class GameObject : Object
                {
                    private readonly List<Component> components = new List<Component>();
                    public GameObject(GameObject? parent = null)
                    {
                        var rect = new RectTransform { gameObject = this, parent = parent?.transform };
                        transform = rect;
                        components.Add(rect);
                    }
                    public Transform transform { get; }
                    public bool activeInHierarchy { get; set; } = true;
                    public T AddComponent<T>() where T : Component, new()
                    {
                        var component = new T { gameObject = this };
                        components.Add(component);
                        return component;
                    }
                    public bool TryGetComponent<T>(out T component)
                    {
                        foreach (var candidate in components) if (candidate is T match) { component = match; return true; }
                        component = default!;
                        return false;
                    }
                }
            }
            namespace UnityEngine.EventSystems
            {
                public class EventSystem : Behaviour
                {
                    public static EventSystem? current { get; set; }
                    public GameObject? currentSelectedGameObject { get; set; }
                }
            }
            namespace UnityEngine.UI
            {
                public class ScrollRect : MonoBehaviour
                {
                    public RectTransform content { get; set; } = null!;
                    public RectTransform? viewport { get; set; }
                    public bool horizontal { get; set; }
                    public bool vertical { get; set; } = true;
                    public int Stopped;
                    public void StopMovement() => Stopped++;
                }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                internal static class TopiaForgeInputOwnership
                {
                    internal static readonly HashSet<UnityEngine.GameObject> Game = new HashSet<UnityEngine.GameObject>();
                    internal static bool IsKitObject(UnityEngine.GameObject target) => !Game.Contains(target);
                }
            }
            """;

        private const string ScrollFollowScenarios = """
            using System;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.UI;

            public static class ScrollFollowScenarios
            {
                private static EventSystem system = null!;
                private static ScrollRect scroll = null!;
                private static RectTransform content = null!;
                private static GameObject[] rows = null!;

                // A window's scroll view, 300 high, over ten rows 40 high laid down its content from the top; the content
                // starts unscrolled, so rows 0 to 6 show and the rest are below the fold.
                private static void Start()
                {
                    TopiaForgeScrollFollow.Reset();
                    TopiaForgeInputOwnership.Game.Clear();
                    system = new EventSystem();
                    EventSystem.current = system;
                    var window = new GameObject();
                    scroll = window.AddComponent<ScrollRect>();
                    var viewport = new GameObject(window);
                    ((RectTransform)viewport.transform).rect = new Rect(0f, -300f, 400f, 300f);
                    var contentObject = new GameObject(viewport);
                    content = (RectTransform)contentObject.transform;
                    content.rect = new Rect(0f, -400f, 400f, 400f);
                    scroll.viewport = (RectTransform)viewport.transform;
                    scroll.content = content;
                    rows = new GameObject[10];
                    for (var index = 0; index < rows.Length; index++)
                    {
                        rows[index] = new GameObject(contentObject);
                        var rect = (RectTransform)rows[index].transform;
                        rect.Offset = new Vector2(0f, -40f * index);
                        rect.rect = new Rect(0f, -40f, 400f, 40f);
                    }

                    Select(rows[0]);
                }

                private static void Select(GameObject? target)
                {
                    system.currentSelectedGameObject = target;
                    TopiaForgeScrollFollow.Tick();
                }

                private static float Shown(int row) => content.anchoredPosition.y - (40f * row);

                private static void Check(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
                }

                public static void AControlBelowTheFoldScrollsIntoView()
                {
                    Start();
                    Select(rows[8]);
                    var bottom = Shown(8) - 40f;
                    Check(bottom >= -300f && Shown(8) <= 0f, "row 8, below the fold, scrolls up into view (its top at " + Shown(8) + ")");
                    Check(Math.Abs(bottom - (-300f + TopiaForgeScrollReveal.Margin)) < 0.001f, "just far enough, a margin inside the view's bottom");
                    Check(scroll.Stopped == 1, "and the scroll view's own motion stops, so it does not carry the content on");
                }

                public static void AControlAboveScrollsBackDown()
                {
                    Start();
                    Select(rows[9]);
                    Select(rows[1]);
                    Check(Shown(1) <= 0f && Shown(1) - 40f >= -300f, "row 1, scrolled off the top, scrolls back into view (its top at " + Shown(1) + ")");
                    Check(Math.Abs(Shown(1) - (-TopiaForgeScrollReveal.Margin)) < 0.001f, "a margin inside the view's top");
                }

                public static void AControlShownInFullMovesNothing()
                {
                    Start();
                    Select(rows[3]);
                    Check(content.anchoredPosition.y == 0f && scroll.Stopped == 0, "a row already shown in full moves nothing");
                }

                public static void OnlyAMoveOfTheSelectionScrolls()
                {
                    Start();
                    Select(rows[8]);
                    var after = content.anchoredPosition.y;
                    content.anchoredPosition = new Vector2(0f, 0f);
                    TopiaForgeScrollFollow.Tick();
                    Check(content.anchoredPosition.y == 0f, "the player scrolling away with the wheel or a drag is not undone while the selection stays");
                    Select(rows[8]);
                    Check(content.anchoredPosition.y == 0f && after > 0f, "selecting the same control again changes nothing either");
                    Select(null);
                    Select(rows[8]);
                    Check(content.anchoredPosition.y == after, "moving to it again shows it again");
                }

                public static void NothingOfTheGamesAndNothingOutsideTheContentScrolls()
                {
                    Start();
                    TopiaForgeInputOwnership.Game.Add(rows[8]);
                    Select(rows[8]);
                    Check(content.anchoredPosition.y == 0f, "a control of the game's own is never scrolled to");
                    var scrollbar = new GameObject(scroll.gameObject);
                    ((RectTransform)scrollbar.transform).rect = new Rect(0f, -900f, 10f, 10f);
                    Select(scrollbar);
                    Check(content.anchoredPosition.y == 0f, "nor the scroll view's own scrollbar, outside its content");
                    rows[9].activeInHierarchy = false;
                    Select(rows[9]);
                    Check(content.anchoredPosition.y == 0f, "nor a hidden control");
                }

                public static void NestedScrollViewsBothFollow()
                {
                    Start();
                    var outer = new GameObject();
                    var outerScroll = outer.AddComponent<ScrollRect>();
                    var outerView = new GameObject(outer);
                    ((RectTransform)outerView.transform).rect = new Rect(0f, -200f, 400f, 200f);
                    var outerContent = new GameObject(outerView);
                    ((RectTransform)outerContent.transform).rect = new Rect(0f, -1000f, 400f, 1000f);
                    outerScroll.viewport = (RectTransform)outerView.transform;
                    outerScroll.content = (RectTransform)outerContent.transform;
                    scroll.transform.parent = outerContent.transform;
                    scroll.transform.Offset = new Vector2(0f, -500f);
                    Select(rows[8]);
                    var top = rows[8].transform.World().y;
                    var view = outerView.transform.World().y;
                    Check(top <= view && top - 40f >= view - 200f, "the inner list scrolls row 8 into its view, and the outer view scrolls the list into its own (" + top + ", " + view + ")");
                }

                public static void DisabledScrollViewsDoNotFollow()
                {
                    Start();
                    scroll.enabled = false;
                    Select(rows[8]);
                    Check(content.anchoredPosition.y == 0f && scroll.Stopped == 0,
                        "a disabled scroll view keeps its content position and velocity when a control is selected");
                    scroll.enabled = true;
                    Select(rows[0]);
                    Select(rows[8]);
                    Check(content.anchoredPosition.y > 0f && scroll.Stopped == 1,
                        "an enabled scroll view follows the next selection normally");
                }

                // A held direction moves the selection every few frames: following it allocates nothing, and a frame at rest
                // nothing either.
                public static void FollowingASelectionAllocatesNothing()
                {
                    Start();
                    long Run()
                    {
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        for (var frame = 0; frame < 20000; frame++)
                        {
                            system.currentSelectedGameObject = rows[(frame / 4) % rows.Length];
                            TopiaForgeScrollFollow.Tick();
                        }

                        return GC.GetAllocatedBytesForCurrentThread() - before;
                    }

                    Run();
                    var fewest = long.MaxValue;
                    for (var pass = 0; pass < 3 && fewest != 0; pass++) fewest = Math.Min(fewest, Run());
                    Check(fewest == 0, "following the selection allocates nothing (" + fewest + " bytes)");
                }
            }
            """;
    }
}
