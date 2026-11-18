using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Identifies a failed accessibility check on a captured UI tree.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public enum UiAccessibilityIssueKind
    {
        /// <summary>Ink and fill do not provide enough contrast.</summary>
        Contrast = 0,
        /// <summary>Visible text falls below the readable height.</summary>
        TextSize = 1,
        /// <summary>A meaningful group has no semantic label.</summary>
        MissingLabel = 2,
        /// <summary>A state is communicated only by color.</summary>
        ColorOnly = 3,
        /// <summary>The viewport clips meaningful content.</summary>
        ClippedContent = 4,
        /// <summary>An asset could not be resolved.</summary>
        MissingAsset = 5,
        /// <summary>A trust surface uses a mod-owned theme.</summary>
        TrustTheme = 6,
        /// <summary>The group cannot be drawn within its layout constraints.</summary>
        InvalidLayout = 7,
        /// <summary>A later paint covers meaningful content.</summary>
        OccludedContent = 8
    }

    /// <summary>Describes an accessibility issue at a stable capture path.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiAccessibilityIssue
    {
        internal UiAccessibilityIssue(UiAccessibilityIssueKind kind, string path, string message)
        {
            Kind = kind;
            Path = path;
            Message = message;
        }

        /// <summary>Gets the failed check.</summary>
        public UiAccessibilityIssueKind Kind { get; }
        /// <summary>Gets the stable path of the affected group.</summary>
        public string Path { get; }
        /// <summary>Gets a description of the issue.</summary>
        public string Message { get; }
    }

    /// <summary>Provides an immutable snapshot and its accessibility checks.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiTreeCapture
    {
        private readonly IReadOnlyList<UiAccessibilityIssue> issues;

        internal UiTreeCapture(List<UiCapturedNode> nodes, bool highContrast, float minimumHitSize = 0f)
        {
            Nodes = nodes.AsReadOnly();
            issues = FindIssues(Nodes, highContrast, minimumHitSize);
        }

        /// <summary>Gets the semantic and paint groups in tree order.</summary>
        public IReadOnlyList<UiCapturedNode> Nodes { get; }

        /// <summary>Checks captured contrast, text size, labels, bounds, state cues, assets, and trust isolation.</summary>
        public IReadOnlyList<UiAccessibilityIssue> CheckAccessibility() => issues;

        private static IReadOnlyList<UiAccessibilityIssue> FindIssues(IReadOnlyList<UiCapturedNode> nodes, bool highContrast,
            float minimumHitSize)
        {
            var result = new List<UiAccessibilityIssue>();
            var hitTargets = new List<UiCapturedNode>();
            foreach (var node in nodes)
            {
                if (Has(node, UiCaptureNodeFlags.MissingAsset))
                    Add(node, UiAccessibilityIssueKind.MissingAsset, "The declared asset is unavailable.");
                if (Has(node, UiCaptureNodeFlags.IncorrectTrustTheme))
                    Add(node, UiAccessibilityIssueKind.TrustTheme, "This trust surface must use the kit theme.");
                if (Has(node, UiCaptureNodeFlags.InvalidLayout) || !ValidBounds(node))
                    Add(node, UiAccessibilityIssueKind.InvalidLayout, "The resolved bounds are invalid.");
                if (!node.IsRendered || node.Decorative) continue;

                if (Has(node, UiCaptureNodeFlags.HitTarget))
                {
                    if (node.Width + 0.001f < minimumHitSize || node.Height + 0.001f < minimumHitSize)
                        Add(node, UiAccessibilityIssueKind.InvalidLayout, "The hit target is smaller than the scaled minimum.");
                    foreach (var previous in hitTargets)
                    {
                        if (!Overlaps(node, previous)) continue;
                        Add(node, UiAccessibilityIssueKind.InvalidLayout, "The hit target overlaps " + previous.Path + ".");
                        break;
                    }
                    hitTargets.Add(node);
                }

                if (node.Text.Length > 0)
                {
                    CheckContrast(node, highContrast ? 7f : 4.5f);
                    if (node.TextHeightPixels + 0.0001f < node.ExpectedTextHeight)
                        Add(node, UiAccessibilityIssueKind.TextSize, "Text is below the readable height for its role.");
                }
                else if (Has(node, UiCaptureNodeFlags.SemanticMark))
                {
                    CheckContrast(node, highContrast ? 4.5f : 3f);
                }

                if (Has(node, UiCaptureNodeFlags.RequiresLabel) && string.IsNullOrWhiteSpace(node.SemanticLabel))
                    Add(node, UiAccessibilityIssueKind.MissingLabel, "A meaningful group needs a plain-text label.");
                if (Has(node, UiCaptureNodeFlags.RequiresNonColorCue) && !Has(node, UiCaptureNodeFlags.HasNonColorCue))
                    Add(node, UiAccessibilityIssueKind.ColorOnly, "The state needs a word, symbol, or positional cue.");
                if (node.Clipped && !Has(node, UiCaptureNodeFlags.ScrollableClip) &&
                    (node.Text.Length > 0 || Has(node, UiCaptureNodeFlags.RequiresLabel)))
                    Add(node, UiAccessibilityIssueKind.ClippedContent, "The viewport clips meaningful content.");
                if (node.Occluded)
                    Add(node, UiAccessibilityIssueKind.OccludedContent, "A later paint covers meaningful content.");
            }

            return result.Count == 0 ? Array.Empty<UiAccessibilityIssue>() : result.AsReadOnly();

            void CheckContrast(UiCapturedNode node, float minimum)
            {
                if (node.InkColor == null || node.FillColor == null || node.ContrastRatio < minimum)
                    Add(node, UiAccessibilityIssueKind.Contrast, "The ink and fill do not meet the contrast target.");
            }

            void Add(UiCapturedNode node, UiAccessibilityIssueKind kind, string message) =>
                result.Add(new UiAccessibilityIssue(kind, node.Path, message));
        }

        private static bool Has(UiCapturedNode node, UiCaptureNodeFlags flag) => (node.Flags & flag) != 0;

        private static bool Overlaps(UiCapturedNode first, UiCapturedNode second) =>
            Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X) > 0.001f &&
            Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y) > 0.001f;

        private static bool ValidBounds(UiCapturedNode node) => IsFinite(node.X) && IsFinite(node.Y) &&
            IsFinite(node.Width) && IsFinite(node.Height) && node.Width >= 0 && node.Height >= 0;

        private static bool IsFinite(float number) => !float.IsNaN(number) && !float.IsInfinity(number);
    }
}
