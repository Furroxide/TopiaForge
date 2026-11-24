using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiSurface
        {
            private UiSurfaceArtwork? PrepareArtwork(UiNode content) =>
                PrepareArtwork(content, remoteComposition, remoteCatalogue, remoteAcquisitions);

            private UiSurfaceArtwork? PrepareArtwork(UiNode content, bool isRemote, AdmittedUiCatalogue? catalogue,
                UiThemeAcquisitions? acquisitions) =>
                UiSurfaceArtwork.Stage(content, isRemote, localThemeScope, catalogue, acquisitions, value =>
                {
                    if (ReferenceEquals(artwork, value) && presence.IsLive) artworkVersion++;
                });

            private void RetireArtwork(UiSurfaceArtwork? previous)
            {
                try { previous?.Dispose(); }
                catch (Exception error)
                {
                    try { logger.Error(error, "TopiaForgeUi could not retire a surface's artwork."); }
                    catch { }
                }
            }

            private void ReleaseArtwork()
            {
                var previous = artwork;
                artwork = null;
                RetireArtwork(previous);
            }
        }

        /// <summary>Owns the art references of one staged or displayed composition.</summary>
        private sealed class UiSurfaceArtwork : IDisposable
        {
            private readonly Dictionary<UiImage, ImageEntry> images = new Dictionary<UiImage, ImageEntry>();
            private readonly Dictionary<UiThemeDescriptor, UiThemeDemand?> demands = new Dictionary<UiThemeDescriptor, UiThemeDemand?>();

            internal bool HasPendingDemand()
            {
                foreach (var demand in demands.Values)
                    if (demand != null && demand.Outcome == null) return true;
                return false;
            }

            internal static UiSurfaceArtwork? Stage(UiNode content, bool remote, UiThemeScope? scope,
                AdmittedUiCatalogue? catalogue, UiThemeAcquisitions? acquisitions, Action<UiSurfaceArtwork> changed)
            {
                UiSurfaceArtwork? artwork = null;
                try
                {
                    Visit(content);
                    return artwork;
                }
                catch
                {
                    artwork?.Dispose();
                    throw;
                }

                void Visit(UiNode node)
                {
                    if (node is UiImage image)
                    {
                        artwork ??= new UiSurfaceArtwork();
                        if (artwork.images.ContainsKey(image)) return;
                        UiThemeDescriptor? pack = null;
                        if (remote)
                        {
                            if (catalogue != null && catalogue.TryGet(image.Image.PackId, out var found) &&
                                found.Digest.Span.SequenceEqual(image.PackDescriptorDigest.Span)) pack = found;
                        }
                        else scope?.TryDescribe(image.Image.PackId, out pack);

                        if (pack != null && UiImageProjection.Find(pack, image.Image.ImageId) is { } metadata)
                        {
                            var frame = UiImageProjection.ResolveFrame(image, metadata);
                            if (!artwork.demands.ContainsKey(pack))
                            {
                                var current = artwork;
                                void Settled(OperationResult<bool> _) => changed(current);
                                var demand = remote
                                    ? acquisitions?.Demand(pack, Settled, retryFailed: false)
                                    : scope?.DemandSurface(pack, Settled, retryFailed: false);
                                artwork.demands.Add(pack, demand);
                            }
                            artwork.images.Add(image, new ImageEntry(pack, frame));
                        }
                        else if (!remote) throw new UiImageNotDeclaredException();
                        else if (image.PackDescriptorDigest.Length == 32 && image.Width.HasValue && image.Height.HasValue)
                        {
                            // Without the exact pack there is no sprite to fit or tint. The admitted dimensions still hold.
                            artwork.images.Add(image, new ImageEntry(null, UiImageFrame.Resolve(image, 1, 1, isMask: true)));
                        }
                        else throw new ArgumentException("The image must identify a verified sprite in this package's catalogue.");
                        return;
                    }

                    var children = UiNodeChildren.Of(node);
                    for (var index = 0; index < children.Count; index++) Visit(children[index]);
                }
            }

            internal BoundImage Resolve(UiImage image)
            {
                var entry = images[image];
                var sprite = entry.Pack == null ? null : demands[entry.Pack]?.Registration?.Image<Sprite>(image.Image.ImageId);
                return new BoundImage(entry.Frame, sprite);
            }

            public void Dispose()
            {
                foreach (var demand in demands.Values) demand?.Dispose();
                demands.Clear();
                images.Clear();
            }

            private readonly struct ImageEntry
            {
                internal ImageEntry(UiThemeDescriptor? pack, UiImageFrame frame) { Pack = pack; Frame = frame; }
                internal UiThemeDescriptor? Pack { get; }
                internal UiImageFrame Frame { get; }
            }
        }

        private sealed class UiImageNotDeclaredException : ArgumentException
        {
            internal UiImageNotDeclaredException() : base("$.images: This package does not declare the requested UI image.") { }
        }

        private readonly struct BoundImage
        {
            internal BoundImage(UiImageFrame frame, Sprite? sprite) { Frame = frame; Sprite = sprite; }
            internal UiImageFrame Frame { get; }
            internal Sprite? Sprite { get; }
        }
    }
}
