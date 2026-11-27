using System;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal interface IUiPresentationThemeSource
    {
        UiThemeScope? PresentationThemeScope { get; }
        OperationResult<bool> DescribePresentationTheme(UiSurfaceRequest request, out UiThemeDescriptor? theme);
    }

    internal interface IRemoteThemedUiReceiver
    {
        OperationResult<IUiSurface> RemoteSurface(UiSurfaceRequest request, UiRemoteThemeSelection theme);
    }

    internal interface IRemoteThemedSurface
    {
        OperationResult<bool> SetRemotePresentation(UiNode? content, UiRemoteThemeSelection theme);
    }

    /// <summary>Authenticated remote envelope plus the viewer's exact package copy, when present.</summary>
    internal readonly struct UiRemoteThemeSelection
    {
        internal UiRemoteThemeSelection(UiWireEnvelope envelope, MultiplayerContentInventory.LoadedPackage? package)
        {
            Envelope = envelope;
            Package = package;
        }

        internal UiWireEnvelope? Envelope { get; }
        internal MultiplayerContentInventory.LoadedPackage? Package { get; }
    }

    internal static class UiPresentationTheme
    {
        internal const string ReceiverOwner = "topiaforge.presentation";
        internal const int LegacyVersion = UiWireCodec.LegacyContractVersion;
        internal const int FoundationVersion = UiWireCodec.FoundationContractVersion;
        internal const string RequiredRuntime = "This UI requires TopiaForge UI contract 2 on every recipient. Update TopiaForge to continue.";
        internal const string RequiredControlsRuntime = "This UI requires TopiaForge UI contract 3 on every recipient. Update TopiaForge to continue.";
        private static readonly byte[] DefaultDigest = new byte[32];
        private const ulong FirstTargetGeneration = 1UL << 63;
        internal static readonly OperationResult<bool> Allowed = OperationResult<bool>.Success(true);
        internal static readonly OperationResult<bool> UnsupportedPeer = OperationResult<bool>.Failure(ModErrorCode.Unavailable, RequiredRuntime);
        private static readonly OperationResult<bool> UnsupportedControlsPeer = OperationResult<bool>.Failure(ModErrorCode.Unavailable, RequiredControlsRuntime);
        internal static readonly OperationResult<bool> UnadmittedPackage = OperationResult<bool>.Failure(ModErrorCode.Unavailable,
            "The session must admit this UI's exact package before it can be shared.");
        internal static readonly OperationResult<bool> UnknownTheme = OperationResult<bool>.Failure(ModErrorCode.NotFound,
            "This package does not declare the requested UI theme.");
        internal static readonly OperationResult<bool> TargetGenerationsExhausted = OperationResult<bool>.Failure(ModErrorCode.Unavailable,
            "This player's UI stream has reached its presentation limit. Reconnect before opening another surface.");

        internal static int RequiredVersion(UiSurfaceRequest request, UiNode? content, UiThemeDescriptor? theme)
        {
            var surfaceVersion = request.Kind == UiSurfaceKind.Overlay ? UiWireCodec.ControlsContractVersion : LegacyVersion;
            return Math.Max(surfaceVersion, Math.Max(UiWireCodec.RequiredContractVersion(content), theme?.MinimumWireVersion ?? LegacyVersion));
        }

        internal static OperationResult<bool> UnsupportedVersion(int version) =>
            version >= UiWireCodec.ControlsContractVersion ? UnsupportedControlsPeer : UnsupportedPeer;

        internal static bool TryNextTargetGeneration(ulong previous, out ulong generation)
        {
            if (previous == ulong.MaxValue)
            {
                generation = 0;
                return false;
            }

            // Every value on this route has the same canonical varuint width. Another scope can publish before a
            // staged theme revision without growing its already admitted frame by crossing a varuint boundary.
            generation = Math.Max(FirstTargetGeneration, previous + 1);
            return true;
        }

        internal static UiWireEnvelope Envelope(string owner, byte[] digest, ulong generation, UiThemeDescriptor? theme) =>
            new UiWireEnvelope(owner, digest, generation, theme?.Id, theme == null ? DefaultDigest.AsSpan() : theme.Digest.Span);

        internal static UiWireEventEnvelope EventEnvelope(string owner, byte[] digest, UiThemeDescriptor? theme) =>
            new UiWireEventEnvelope(owner, digest, theme?.Id, theme == null ? DefaultDigest.AsSpan() : theme.Digest.Span);

        internal static bool SameEnvelope(UiWireEnvelope? left, UiWireEnvelope? right) => ReferenceEquals(left, right) ||
            (left != null && right != null && left.Generation == right.Generation && left.OwnerPackageId == right.OwnerPackageId
                && left.ThemeId == right.ThemeId && left.PackageDigest.Span.SequenceEqual(right.PackageDigest.Span)
                && left.DescriptorDigest.Span.SequenceEqual(right.DescriptorDigest.Span));

        internal static OperationResult<bool> Resolve(IUiService source, UiSurfaceRequest request, out UiThemeDescriptor? theme)
        {
            if (source is IUiPresentationThemeSource themed)
                return themed.DescribePresentationTheme(request, out theme);
            theme = null;
            return request.Theme == null ? Allowed
                : OperationResult<bool>.Failure(ModErrorCode.Unavailable, "This UI context cannot resolve a theme pack.");
        }

        internal static OperationResult<bool> StylesValid(UiNode? content, UiThemeDescriptor? theme) =>
            UiLayoutDecoration.AdmitsNamedStyles(content, theme ?? UiThemePackParser.Default) ? Allowed
                : OperationResult<bool>.Failure(ModErrorCode.InvalidArgument,
                    "A named UI style must be declared for its component and preserve its semantic role.");

        internal static UiThemeDescriptor? ViewerTheme(UiRemoteThemeSelection selection)
        {
            var envelope = selection.Envelope;
            return envelope == null ? null : ViewerTheme(envelope.ThemeId, envelope.DescriptorDigest.Span,
                selection.Package?.Manifest.AdmittedUi);
        }

        internal static UiThemeDescriptor? ViewerTheme(string? id, ReadOnlySpan<byte> descriptorDigest, AdmittedUiCatalogue? catalogue)
        {
            if (id == null || catalogue == null || !catalogue.TryGet(id, out var descriptor)) return null;
            return descriptor.Digest.Span.SequenceEqual(descriptorDigest) ? descriptor : null;
        }

        internal static bool ViewerAdmits(UiNode? content, UiRemoteThemeSelection selection)
        {
            var descriptor = selection.Envelope?.ThemeId == null ? UiThemePackParser.Default : ViewerTheme(selection);
            return (descriptor == null || UiLayoutDecoration.AdmitsNamedStyles(content, descriptor)) &&
                UiPresentationImages.ViewerAdmits(content, selection);
        }

        internal static OperationResult<IUiSurface> CreateRemote(IUiService service, UiSurfaceRequest request, UiRemoteThemeSelection selection)
        {
            if (selection.Envelope != null)
                return service is IRemoteThemedUiReceiver themed ? themed.RemoteSurface(request, selection)
                    : OperationResult<IUiSurface>.Failure(ModErrorCode.Unavailable, "This UI receiver cannot render the presentation theme.");
            return service is IRemoteUiReceiver plain ? plain.RemoteSurface(request) : service.CreateSurface(request);
        }

        internal static bool UpdateRemote(IUiSurface surface, UiNode? content, UiRemoteThemeSelection selection, bool wasThemed)
        {
            if (selection.Envelope != null || wasThemed)
                return surface is IRemoteThemedSurface themed && themed.SetRemotePresentation(content, selection).Succeeded;
            return surface.SetContent(content ?? new UiColumn()).Succeeded;
        }

        internal static bool TryHexDigest(string text, Span<byte> digest)
        {
            if (digest.Length != 32 || text.Length != 64) return false;
            for (var index = 0; index < digest.Length; index++)
            {
                var high = UiThemeData.HexDigit(text[index * 2]);
                var low = UiThemeData.HexDigit(text[(index * 2) + 1]);
                if (high < 0 || low < 0) return false;
                digest[index] = (byte)((high << 4) | low);
            }

            return true;
        }
    }
}
