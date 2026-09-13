using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Explicit, owner-scoped UI observation. Disabled owners incur no frame work or retained tags.</summary>
    public static class TopiaForgeUiDiagnostics
    {
        private sealed class Owner
        {
            internal int Leases;
            internal readonly Dictionary<GameObject, Tag> Tags = new Dictionary<GameObject, Tag>();
        }
        private sealed class Tag
        {
            internal TopiaForgeWidget Widget = null!;
            internal string Surface = "", Id = "", Kind = "", Text = "", Style = "";
        }
        private sealed class Lease : IDisposable
        {
            private string? owner;
            internal Lease(string owner) { this.owner = owner; }
            public void Dispose()
            {
                var id = owner;
                owner = null;
                if (id != null && Owners.TryGetValue(id, out var state) && --state.Leases == 0) Owners.Remove(id);
            }
        }
        private static readonly Dictionary<string, Owner> Owners = new Dictionary<string, Owner>(StringComparer.Ordinal);

        /// <summary>Enables observation before constructing an owner's surfaces. Dispose the lease to release all tags.</summary>
        public static IDisposable Enable(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 256) throw new ArgumentException("A bounded owner id is required.", nameof(ownerId));
            if (!Owners.TryGetValue(ownerId, out var owner)) Owners.Add(ownerId, owner = new Owner());
            owner.Leases++;
            return new Lease(ownerId);
        }

        /// <summary>Tags an actual renderer widget; this is a no-op when its owner has no observation lease.</summary>
        public static void TagWidget(TopiaForgeWidget widget, string surfaceId, string nodeId, string kind, string text = "", string style = "")
        {
            if (!Owners.TryGetValue(widget.Host.OwnerId, out var owner)) return;
            if (owner.Tags.Count >= 8192) Prune(owner);
            if (owner.Tags.Count >= 8192) throw new InvalidOperationException("UI diagnostic widget limit exceeded.");
            owner.Tags[widget.Go] = new Tag { Widget = widget, Surface = Bound(surfaceId), Id = Bound(nodeId), Kind = Bound(kind), Text = Bound(text), Style = Bound(style) };
        }

        /// <summary>Captures fresh measured screen geometry and runtime state on the Unity main thread. Coordinates use a bottom-left origin.</summary>
        public static TopiaForgeUiDiagnosticSnapshot Capture(string ownerId)
        {
            if (!Owners.TryGetValue(ownerId, out var owner)) throw new InvalidOperationException("Enable this owner's diagnostics before capture.");
            Prune(owner);
            var nodes = owner.Tags.Values.Select(Measure).OrderBy(node => node.SurfaceId, StringComparer.Ordinal)
                .ThenBy(node => node.NodeId, StringComparer.Ordinal).ToArray();
            var canvases = Resources.FindObjectsOfTypeAll<Canvas>().Where(value => value.gameObject.scene.IsValid()).ToArray();
            return new TopiaForgeUiDiagnosticSnapshot(ownerId, nodes, TopiaForgeUi.DiagnosticHostCount(ownerId),
                canvases.Count(value => value.name.StartsWith(ownerId + ":", StringComparison.Ordinal)), canvases.Length,
                TopiaForgeTheme.DiagnosticSubscriberCount, TopiaForgeCursor.ActiveLeases, TopiaForgeDismissStack.Count, Screen.width, Screen.height);
        }

        // Friend-test-only identity check; observation never returns a Unity actuation handle.
        internal static bool MatchesHit(string ownerId, string surfaceId, string nodeId, GameObject hit)
        {
            if (hit == null || !Owners.TryGetValue(ownerId, out var owner)) return false;
            return owner.Tags.Values.Any(tag => tag.Surface == surfaceId && tag.Id == nodeId && tag.Widget.Go != null
                && (hit == tag.Widget.Go || hit.transform.IsChildOf(tag.Widget.Go.transform)));
        }

        private static void Prune(Owner owner)
        {
            foreach (var item in owner.Tags.Where(pair => pair.Key == null).ToArray()) owner.Tags.Remove(item.Key);
        }
        private static string Bound(string value) => value == null ? "" : value.Length <= 256 ? value : value.Substring(0, 256);
        private static Rect Bounds(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var canvas = rect.GetComponentInParent<Canvas>();
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            var high = low;
            foreach (var corner in corners)
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, corner);
                low = Vector2.Min(low, point); high = Vector2.Max(high, point);
            }
            return Rect.MinMaxRect(low.x, low.y, high.x, high.y);
        }
        private static TopiaForgeUiDiagnosticWidget Measure(Tag tag)
        {
            var widget = tag.Widget;
            var bounds = Bounds(widget.Rect);
            var visible = widget.Go.activeInHierarchy;
            var enabled = true;
            foreach (var group in widget.Go.GetComponentsInParent<CanvasGroup>(true))
            {
                visible &= group.alpha > 0.01f;
                enabled &= group.interactable;
            }
            var selectable = widget.Go.GetComponent<Selectable>();
            if (selectable != null) enabled &= selectable.IsInteractable();
            var clipped = bounds.width <= 0 || bounds.height <= 0 || bounds.xMin < -1 || bounds.yMin < -1
                || bounds.xMax > Screen.width + 1 || bounds.yMax > Screen.height + 1;
            foreach (var mask in widget.Go.GetComponentsInParent<RectMask2D>(true))
            {
                if (!mask.isActiveAndEnabled || mask.gameObject == widget.Go) continue;
                var clip = Bounds(mask.rectTransform);
                clipped |= bounds.xMin < clip.xMin - 1 || bounds.yMin < clip.yMin - 1 || bounds.xMax > clip.xMax + 1 || bounds.yMax > clip.yMax + 1;
            }
            var focusedObject = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            var focused = focusedObject != null && (focusedObject == widget.Go || focusedObject.transform.IsChildOf(widget.Go.transform));
            var input = widget.Go.GetComponent<TMP_InputField>();
            var label = widget.Go.GetComponent<TMP_Text>();
            var text = input != null ? input.text : label != null ? label.text : tag.Text;
            return new TopiaForgeUiDiagnosticWidget(tag.Surface, tag.Id, tag.Kind, Bound(text), tag.Style,
                bounds.x, bounds.y, bounds.width, bounds.height, visible, enabled && visible, focused, clipped,
                widget.Host.EffectiveHighContrast, widget.Host.EffectiveReducedMotion, widget.Host.EffectiveUiScale, widget.Host.EffectiveMotion);
        }
    }
}
