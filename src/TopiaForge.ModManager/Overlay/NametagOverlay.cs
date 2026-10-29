using System;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>Presence labels with a bounded, persistent kit pool independent of interactive overlays.</summary>
    internal sealed class NametagOverlay : IDisposable
    {
        private const string Owner = "io.github.furroxide.topiaforge.modmanager.nametags";
        private readonly Func<IPresenceNametagSource?> source;
        private readonly Func<bool> visible;
        private readonly ScoreboardNametagFeed feed = new ScoreboardNametagFeed();
        private readonly LiveAnchor[] anchors = new LiveAnchor[ScoreboardNametagFeed.Capacity];
        private readonly TopiaForgeNametag?[] tags = new TopiaForgeNametag?[ScoreboardNametagFeed.Capacity];
        private UiHost? host;
        private TopiaForgeHudLayer? names;
        private bool disposed;

        internal NametagOverlay(Func<IPresenceNametagSource?> source, Func<bool> visible)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.visible = visible ?? throw new ArgumentNullException(nameof(visible));
        }

        internal void Tick()
        {
            if (disposed) return;
            feed.Tick(source(), visible());
            var any = false;
            for (var i = 0; i < feed.Active.Length; i++) any |= feed.Active[i];
            if (host == null && any) Build();
            if (host == null) return;
            names!.SetShown(any);
            for (var i = 0; i < anchors.Length; i++)
            {
                anchors[i].Active = feed.Active[i];
                if (!feed.Active[i]) continue;
                tags[i]?.SetName(feed.Names[i]);
            }
        }

        // The kit queries these after avatar interpolation, rather than retaining Update's pose.
        private sealed class LiveAnchor : ITopiaForgeNametagAnchor
        {
            private readonly NametagOverlay owner;
            private readonly int slot;
            internal bool Active;

            internal LiveAnchor(NametagOverlay owner, int slot) { this.owner = owner; this.slot = slot; }

            public bool TryGetPosition(out Vec3 position)
            {
                position = Vec3.Zero;
                if (!Active || owner.disposed || !owner.visible()) return false;
                var current = owner.source();
                if (current == null || slot >= current.NametagCount || !current.TryGetNametag(slot, out var tag)) return false;
                position = new Vec3(tag.Anchor.X, tag.Anchor.Y, tag.Anchor.Z);
                return true;
            }
        }

        private void Build()
        {
            var created = TopiaForgeUi.Create(new TopiaForgeUiOptions { OwnerId = Owner });
            try
            {
                var layer = created.HudLayer("player-names", persistent: true);
                var labels = layer.Nametags(anchors.Length);
                for (var i = 0; i < anchors.Length; i++)
                {
                    anchors[i] = new LiveAnchor(this, i);
                    tags[i] = labels.Track(anchors[i], "", verticalOffset: 0f);
                }
                names = layer;
                host = created;
                created.LayerLost += Lost;
            }
            catch
            {
                created.Dispose();
                throw;
            }
        }

        private void Lost(UnityEngine.GameObject lost) => DropHost();

        private void DropHost()
        {
            var previous = host;
            host = null; names = null;
            if (previous != null) { previous.LayerLost -= Lost; previous.Dispose(); }
            Array.Clear(anchors, 0, anchors.Length);
            Array.Clear(tags, 0, tags.Length);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            DropHost();
        }
    }
}
