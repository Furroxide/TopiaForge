using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        /// <summary>Built-in curated build-2309 item source.</summary>
        private const string CuratedItemsSourceId = "robotopia.items";
        /// <summary>Built-in UGC prop source.</summary>
        private const string UgcPropsSourceId = "robotopia.ugc-props";
        /// <summary>Native vehicle source, which has no validated adapter.</summary>
        private const string NativeVehiclesSourceId = "robotopia.vehicles";

        private readonly HashSet<string> knownCatalogSources =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> unloadedCatalogSources =
            new HashSet<string>(StringComparer.Ordinal);
        /// <summary>
        /// Sources observed producing a character or vehicle, i.e. custom
        /// self-contained factories rather than the curated built-in sources.
        /// </summary>
        private readonly HashSet<string> customFactorySources =
            new HashSet<string>(StringComparer.Ordinal);
        private long catalogRevision = -1;

        private void RefreshCatalog()
        {
            var refreshed = content.RefreshCatalog();
            ApplyCatalogSnapshot(refreshed.TryGetValue(out var snapshot) ? snapshot : content.Catalog);
        }

        private void RefreshCatalogIfChanged()
        {
            if (creatorSession == null) return;
            var snapshot = content.Catalog;
            if (snapshot.Revision == catalogRevision) return;
            ApplyCatalogSnapshot(snapshot);
            if (window?.IsVisible == true) RefreshUi();
        }

        private void ApplyCatalogSnapshot(CreatorCatalogSnapshot snapshot)
        {
            catalogRevision = snapshot.Revision;
            catalog.Clear();
            foreach (var type in robots.RobotTypes)
            {
                catalog.Add(new CreatorCatalogEntry(
                    "robotkit:" + type.Id,
                    type.DisplayName,
                    "RobotKit robot with programmable behavior and personality preview.",
                    CreatorContentKind.Robot));
            }
            if (robots.IsAvailable && catalog.All(entry => !entry.IsRobotKit))
            {
                catalog.Add(new CreatorCatalogEntry(
                    "robotkit:default",
                    "Default robot",
                    "The scene's default RobotKit agent.",
                    CreatorContentKind.Robot));
            }
            foreach (var descriptor in snapshot.Entries)
            {
                catalog.Add(new CreatorCatalogEntry(
                    "content:" + descriptor.ContentId,
                    descriptor.DisplayName,
                    descriptor.Description,
                    descriptor.Kind));
            }
            catalog.Sort((left, right) =>
            {
                var kind = left.Kind.CompareTo(right.Kind);
                return kind != 0 ? kind : string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            if (catalog.Count == 0) selectedCatalogId = string.Empty;
            else if (FindCatalog(selectedCatalogId) == null) selectedCatalogId = catalog[0].Id;
            ObserveCatalogSources(snapshot);
        }

        /// <summary>
        /// Reports source-level catalog transitions the acceptance run requires:
        /// a native vehicle source that is visibly empty rather than inventing
        /// support, and a custom source whose entries disappeared after unload.
        /// </summary>
        private void ObserveCatalogSources(CreatorCatalogSnapshot snapshot)
        {
            if (recorder == null) return;
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var descriptor in snapshot.Entries)
            {
                present.Add(descriptor.SourceId);
            }
            foreach (var status in snapshot.Sources)
            {
                if (string.Equals(
                        status.SourceId,
                        NativeVehiclesSourceId,
                        StringComparison.Ordinal)
                    && status.EntryCount == 0)
                {
                    recorder.Observe(
                        CreatorObservation.NativeVehicleSourceReportedEmpty);
                }
            }
            foreach (var previous in knownCatalogSources)
            {
                if (present.Contains(previous)) continue;
                // A source that was serving entries and now serves none was
                // unloaded, and its catalog entries went with it.
                recorder.Observe(CreatorObservation.UnloadedContentSource);
                recorder.Observe(
                    CreatorObservation.RemovedSourceCatalogEntries);
                unloadedCatalogSources.Add(previous);
            }
            knownCatalogSources.Clear();
            foreach (var sourceId in present) knownCatalogSources.Add(sourceId);
        }
    }
}
