using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.ModManager;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace TopiaForge.SandboxAutomation.Unity
{
    /// <summary>Runs real production workbench widgets over deterministic non-game SDK services.</summary>
    public static partial class EditorWorkbenchChecks
    {
        private const string Owner = "topiaforge.sandbox.editor-automation";
        private const string Surface = "sandbox-editor-window";
        private static readonly List<string> Passed = new List<string>();
        private static readonly List<string> Negatives = new List<string>();
        private static string output = "";

        public static IEnumerator Run(string evidenceDirectory)
        {
            output = evidenceDirectory;
            Passed.Clear(); Negatives.Clear();
            Require(Application.unityVersion == "6000.0.23f1", "exact-editor-version");
            Require(Screen.width == 1920 && Screen.height == 1080, "measured-viewport-1920x1080");
            UnityMainThreadGuard.CaptureCurrentThread();
            using var observation = TopiaForgeUiDiagnostics.Enable(Owner);
            var baseline = Capture();
            using var fake = new FakeModContext(new ModIdentity(Owner, "Editor Sandbox", SemanticVersion.Parse("1.0.0")));
            fake.Scenes.Load("RobotopiaCity");
            fake.LocalPlayer.Snapshot = new PlayerSnapshot(Vec3.Zero, new TopiaForge.Mods.Ray(Vec3.Zero, new Vec3(0, 0, 1)));
            using var content = new FakeCreatorContentService(fake.Lifetime);
            var robots = new FakeRobotKit(fake.Lifetime);
            var editor = new FakeRobotSceneEditorService(fake.Lifetime);
            editor.AddTarget(new FakeRobotEditTarget("borrowed-editor", TransformState.Identity));
            fake.Extensions.Register<IRobotSceneEditorService>(editor);
            fake.Extensions.Register<IRobotObjectiveService>(robots.Objectives);
            var ui = new OwnerUiService(Owner, "", fake.Lifetime, fake.Logger);
            var context = new EditorContext(fake, ui);
            CreatorWorkbench workbench = null!;
            workbench = new CreatorWorkbench(context,
                new CreatorWorkbenchOptions("sandbox-editor", "SANDBOX EDITOR AUTOMATION", CreatorProjectScope.Sandbox, 16, true, false, 4, 0),
                content, robots.Agents, () => workbench.Hide(), () => workbench.EndSession());
            using (workbench)
            {
                Require(workbench.Open().Succeeded, "production-workbench-open");
                for (var frame = 0; frame < 5; frame++) yield return null;
                Canvas.ForceUpdateCanvases();
                Require(Capture().OwnerCanvasCount >= 2 && Node("hide-workbench").Visible, "production-window-and-hud-created");
                var refresh = Click("refresh-content");
                Require(refresh != null && workbench.IsVisible, "raycast-refresh-button");
                for (var frame = 0; frame < 3; frame++) yield return null;
                var search = Click("catalog-search");
                Require(Node("catalog-search").Focused, "pointer-focus-search");
                var initial = Node("hide-workbench");
                var colors = search.GetComponentsInChildren<Graphic>(true).Select(graphic => graphic.color).ToArray();
                CapturePng("workbench-default.png");
                Require(ui.ApplyAccessibility(new UiAccessibilityPreferences(true, 1.25f, true, 0f)).Succeeded, "apply-accessibility-profile");
                for (var frame = 0; frame < 3; frame++) yield return null;
                Require(Node("catalog-search").Focused, "accessibility-retains-focus");
                Require(!colors.SequenceEqual(search.GetComponentsInChildren<Graphic>(true).Select(graphic => graphic.color)), "accessibility-repaints-actual-graphics");
                var accessible = Node("hide-workbench");
                Require(accessible.HighContrast && accessible.ReducedMotion && accessible.MotionIntensity == 0f
                    && Math.Abs(accessible.UiScale - 1.25f) < 0.001f, "effective-accessibility-profile");
                Require(Math.Abs(accessible.Height - initial.Height) > 0.5f, "accessibility-changes-rendered-scale");
                AssertUsable("hide-workbench"); AssertUsable("end-session");
                foreach (var id in new[] { "load-project", "delete-project", "new-project", "save-project", "confirm-native-bindings", "run-project", "stop-project" })
                    Require(Node(id).Visible && !Node(id).Clipped, "project-action-contained:" + id);
                CapturePng("workbench-accessible.png");
                Negative("lost-focus", () =>
                {
                    EventSystem.current.SetSelectedGameObject(null);
                    Require(Node("catalog-search").Focused, "focus-oracle");
                }, () => EventSystem.current.SetSelectedGameObject(search));
                var searchRect = (RectTransform)search.transform;
                var position = searchRect.anchoredPosition;
                Negative("clipping", () =>
                {
                    searchRect.anchoredPosition += new Vector2(100000f, 0);
                    AssertUsable("catalog-search");
                }, () => searchRect.anchoredPosition = position);
                Click("hide-workbench");
                for (var frame = 0; frame < 3; frame++) yield return null;
                Require(!workbench.IsVisible && workbench.IsSessionActive && fake.LocalPlayer.ActiveControlLeaseCount == 0,
                    "hide-retains-session-releases-fake-control");
                AssertHud();
                CapturePng("workbench-hidden-hud.png");
                var hud = Resources.FindObjectsOfTypeAll<Canvas>().Single(value => value.name == Owner + ":sandbox-editor-hud");
                Negative("missing-hud", () => { hud.gameObject.SetActive(false); AssertHud(); }, () => hud.gameObject.SetActive(true));
                Require(workbench.Open().Succeeded, "reopen-session");
                for (var frame = 0; frame < 3; frame++) yield return null;
                Click("end-session");
                for (var frame = 0; frame < 3; frame++) yield return null;
                Require(Node("confirm", "$modal").Visible, "destructive-modal-visible");
                Click("cancel", "$modal");
                Require(workbench.IsSessionActive, "destructive-cancel-retains-session");
                for (var frame = 0; frame < 3; frame++) yield return null;
                Click("end-session");
                for (var frame = 0; frame < 3; frame++) yield return null;
                Click("confirm", "$modal");
                Require(!workbench.IsSessionActive && fake.LocalPlayer.ActiveControlLeaseCount == 0, "destructive-confirm-ends-session");
                for (var cycle = 0; cycle < 10; cycle++)
                {
                    Require(workbench.Open().Succeeded, "editor-cycle-open-" + cycle);
                    for (var frame = 0; frame < 2; frame++) yield return null;
                    Click("hide-workbench");
                    Require(workbench.Open().Succeeded, "editor-cycle-reopen-" + cycle);
                    Require(workbench.EndSession().Succeeded, "editor-cycle-end-" + cycle);
                    Require(content.ActiveSessionCount == 0 && fake.LocalPlayer.ActiveControlLeaseCount == 0,
                        "editor-cycle-service-baseline-" + cycle);
                }
            }
            fake.Dispose();
            TopiaForgeUi.Shutdown();
            for (var frame = 0; frame < 5; frame++) yield return null;
            AssertBaseline(baseline);
            var leaked = new GameObject("injected-leaked-canvas", typeof(Canvas));
            Negative("leaked-canvas", () => AssertBaseline(baseline), () => Object.DestroyImmediate(leaked));
            Action subscriber = () => { };
            TopiaForgeTheme.Changed += subscriber;
            Negative("leaked-subscriber", () => AssertBaseline(baseline), () => TopiaForgeTheme.Changed -= subscriber);
            AssertBaseline(baseline);
            WriteResult();
        }

        private static TopiaForgeUiDiagnosticSnapshot Capture() => TopiaForgeUiDiagnostics.Capture(Owner);
        private static TopiaForgeUiDiagnosticWidget Node(string id, string surface = Surface) =>
            Capture().Widgets.Single(value => value.SurfaceId == surface && value.NodeId == id);
        private static void AssertUsable(string id)
        {
            var node = Node(id);
            Require(node.Visible && node.Enabled && !node.Clipped && node.Width >= 16 && node.Height >= 16, "usable-geometry:" + id);
        }
        private static void AssertHud() => Require(Capture().Widgets.Any(value => value.SurfaceId == "sandbox-editor-hud"
            && value.NodeId == "$body" && value.Visible && !value.Clipped && value.Width >= 100 && value.Height > 0 && value.Text.Length > 0), "persistent-hud-oracle");
        private static GameObject Click(string id, string surface = Surface)
        {
            Canvas.ForceUpdateCanvases();
            var node = Node(id, surface);
            Require(node.Visible && node.Enabled && !node.Clipped, "click-visible-unclipped:" + id);
            var data = new PointerEventData(EventSystem.current) { position = new Vector2(node.X + node.Width / 2, node.Y + node.Height / 2), button = PointerEventData.InputButton.Left };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(data, hits);
            Require(hits.Count > 0, "raycast-hit:" + id);
            Require(TopiaForgeUiDiagnostics.MatchesHit(Owner, surface, id, hits[0].gameObject), "raycast-identity:" + id);
            var target = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject);
            Require(target != null, "raycast-click-handler:" + id);
            data.pointerCurrentRaycast = hits[0];
            ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerClickHandler);
            return target!;
        }
        private static void AssertBaseline(TopiaForgeUiDiagnosticSnapshot baseline)
        {
            var current = Capture();
            Require(current.TotalCanvasCount == baseline.TotalCanvasCount && current.HostCount == baseline.HostCount
                && current.ThemeSubscriberCount == baseline.ThemeSubscriberCount && current.CursorLeaseCount == baseline.CursorLeaseCount
                && current.DismissScopeCount == baseline.DismissScopeCount && current.Widgets.Count == 0, "resource-baseline-oracle");
        }
        private static void Negative(string id, Action injectAndAssert, Action restore)
        {
            var rejected = false;
            try { injectAndAssert(); }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("Editor assertion:", StringComparison.Ordinal)) { rejected = true; }
            finally { restore(); }
            Require(rejected, "negative-detected:" + id);
            Negatives.Add(id);
        }
        private static void Require(bool success, string id)
        {
            if (!success) throw new InvalidOperationException("Editor assertion: " + id);
            Passed.Add(id);
        }
    }
}
