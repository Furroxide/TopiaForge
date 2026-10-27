using System;
using System.Collections.Generic;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods
{
    /// <summary>A surveyed entity identity and optional evidence for matching it after a republish.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class MapAnchor
    {
        /// <summary>Creates an anchor with a configurable editor-position tolerance, defaulting to one metre.</summary>
        public MapAnchor(string key, string entityId, string? name = null, string? modelId = null,
            Vec3? editorPosition = null, bool required = true, float positionTolerance = 1f)
        {
            AuthoredText.RequireId(key, "key"); AuthoredText.RequireId(entityId, "entityId");
            if (name != null) AuthoredText.RequireText(name, 128, "name");
            if (modelId != null) AuthoredText.RequireText(modelId, 256, "modelId");
            if (editorPosition.HasValue && !editorPosition.Value.IsFinite) throw new ArgumentException("editorPosition must be finite.");
            if (float.IsNaN(positionTolerance) || float.IsInfinity(positionTolerance) || positionTolerance < 0 || positionTolerance > 100)
                throw new ArgumentOutOfRangeException(nameof(positionTolerance));
            Key = key; EntityId = entityId; Name = name; ModelId = modelId; EditorPosition = editorPosition;
            Required = required; PositionTolerance = positionTolerance;
        }
        /// <summary>Gets the mod's stable anchor key.</summary>
        public string Key { get; }
        /// <summary>Gets the last surveyed authored id.</summary>
        public string EntityId { get; }
        /// <summary>Gets the exact surveyed name, or null when fallback matching is unavailable.</summary>
        public string? Name { get; }
        /// <summary>Gets the exact surveyed model id, or null when fallback matching is unavailable.</summary>
        public string? ModelId { get; }
        /// <summary>Gets the surveyed position in editor axes.</summary>
        public Vec3? EditorPosition { get; }
        /// <summary>Gets whether failure to match makes the resolution incomplete.</summary>
        public bool Required { get; }
        /// <summary>Gets the maximum fallback distance in editor metres.</summary>
        public float PositionTolerance { get; }
    }

    /// <summary>The evidence used to match an anchor; ambiguous candidates are never guessed.</summary>
    [RequiresTopiaForge("0.2.0")]
    public enum AnchorMatch
    {
        /// <summary>The surveyed id still exists.</summary>
        Id,
        /// <summary>Exactly one record matches the name, model and position tolerance.</summary>
        NameModelAndPosition,
        /// <summary>Several records match the fallback evidence.</summary>
        Ambiguous,
        /// <summary>No record matches the available evidence.</summary>
        Missing
    }

    /// <summary>The immutable outcome for one surveyed anchor.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class ResolvedAnchor
    {
        internal ResolvedAnchor(MapAnchor anchor, AnchorMatch match, AuthoredEntity? entity, bool changed)
        { Anchor = anchor; Match = match; Entity = entity; Changed = changed; }
        /// <summary>Gets the original surveyed anchor.</summary>
        public MapAnchor Anchor { get; }
        /// <summary>Gets the matching outcome.</summary>
        public AnchorMatch Match { get; }
        /// <summary>Gets the matching record, or null for missing or ambiguous results.</summary>
        public AuthoredEntity? Entity { get; }
        /// <summary>Gets whether the id, name, model or position differs from the survey.</summary>
        public bool Changed { get; }
    }

    /// <summary>Anchor matches and a plain-text drift report, including typed file or argument failures.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class MapAnchorResolution
    {
        internal MapAnchorResolution(IReadOnlyList<ResolvedAnchor> anchors, bool complete, string report, ModErrorCode errorCode = ModErrorCode.None)
        { Anchors = anchors; IsComplete = complete; Report = report; ErrorCode = errorCode; }
        /// <summary>Gets whether every required anchor has a live matching entity.</summary>
        public bool IsComplete { get; }
        /// <summary>Gets bounded plain-text outcomes and how to refresh the survey.</summary>
        public string Report { get; }
        /// <summary>Gets argument or lifecycle failure information; missing matches are ordinary outcomes.</summary>
        public ModErrorCode ErrorCode { get; }
        /// <summary>Gets the immutable per-anchor outcomes.</summary>
        public IReadOnlyList<ResolvedAnchor> Anchors { get; }
        /// <summary>Gets a live match, or throws naming only the validated anchor key.</summary>
        public AuthoredEntity this[string key]
        {
            get
            {
                AuthoredText.RequireId(key, "key");
                foreach (var item in Anchors)
                    if (item.Anchor.Key == key && item.Entity != null && item.Entity.Entity.IsAlive) return item.Entity;
                throw new InvalidOperationException("Anchor '" + key + "' has no live unambiguous match. Run topiaforge world survey to re-survey.");
            }
        }
    }
}
