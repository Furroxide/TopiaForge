using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        /// <summary>
        /// Revision of the catalog snapshot last applied, so an unchanged
        /// snapshot does not rebuild the list or disturb the selection.
        /// </summary>
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
        }

    }
}
