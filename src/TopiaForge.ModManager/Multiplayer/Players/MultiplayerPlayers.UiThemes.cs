using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        private readonly Dictionary<ParticipantId, TargetSequence> targetSequences = new Dictionary<ParticipantId, TargetSequence>();

        private bool SupportsFoundationUi(ParticipantId participant) =>
            (runtime.Control?.UiContractVersionOf(participant) ?? InternalUiPresentationContract.LegacyVersion)
                >= InternalUiPresentationContract.FoundationVersion;

        internal bool AllPeersSupportFoundationUi()
        {
            var snapshot = Snapshot;
            if (snapshot == null) return true;
            for (var index = 0; index < snapshot.Participants.Count; index++)
            {
                var participant = snapshot.Participants[index];
                if (!participant.IsLocal && participant.IsConnected && !SupportsFoundationUi(participant.Id)) return false;
            }

            return true;
        }

        internal bool TryOutgoingUiDigest(string owner, bool requireSignedRoster, out byte[]? digest)
        {
            digest = null;
            var package = FindLocalUiPackage(owner);
            if (package == null || package.NetworkClass != ModNetworkClass.Shared) return false;
            var signed = requireSignedRoster ? runtime.Control?.UiPresentationPackageDigest(owner) : package.Sha256;
            if (!string.Equals(signed, package.Sha256, StringComparison.Ordinal)) return false;
            Span<byte> buffer = stackalloc byte[32];
            if (!UiPresentationTheme.TryHexDigest(package.Sha256, buffer)) return false;
            digest = buffer.ToArray();
            return true;
        }

        internal bool AuthenticateUiTheme(UiWireEnvelope envelope, string? targetedOwner, out UiRemoteThemeSelection selection)
        {
            selection = default;
            if (targetedOwner != null && !string.Equals(targetedOwner, envelope.OwnerPackageId, StringComparison.Ordinal)) return false;
            var signed = runtime.Control?.UiPresentationPackageDigest(envelope.OwnerPackageId);
            Span<byte> digest = stackalloc byte[32];
            if (signed == null || !UiPresentationTheme.TryHexDigest(signed, digest) || !envelope.PackageDigest.Span.SequenceEqual(digest)) return false;

            // A compatible session package can differ locally. Only the exact installed archive may supply the theme.
            var local = FindLocalUiPackage(envelope.OwnerPackageId);
            if (local != null && (local.NetworkClass != ModNetworkClass.Shared || !string.Equals(local.Sha256, signed, StringComparison.Ordinal)))
                local = null;
            selection = new UiRemoteThemeSelection(envelope, local);
            return true;
        }

        private MultiplayerContentInventory.LoadedPackage? FindLocalUiPackage(string owner)
        {
            var inventory = runtime.Host.Inventory;
            var packages = inventory.Packages;
            for (var index = 0; index < packages.Count; index++)
            {
                var package = packages[index];
                if (!string.Equals(package.Manifest.Id, owner, StringComparison.Ordinal)) continue;
                if (!package.IsIdentified) _ = inventory.Readiness;
                return package.IsIdentified ? package : null;
            }

            return null;
        }

        private OperationResult<bool> StageTargetEnvelope(PlayerContext context, ParticipantId target, ulong stream,
            UiThemeDescriptor? theme, UiNode? content, Dictionary<(ParticipantId Target, PlayerContext Scope), ulong>? drafts,
            out UiWireEnvelope? envelope)
        {
            envelope = null;
            if (theme == null && !UiWireCodec.NeedsEnvelope(content)) return UiPresentationTheme.Allowed;
            if (stream == 0 || !SupportsFoundationUi(target)) return UiPresentationTheme.UnsupportedPeer;
            if (!TryOutgoingUiDigest(context.Mod, requireSignedRoster: true, out var digest)) return UiPresentationTheme.UnadmittedPackage;

            var generation = FindTargetGeneration(context, target, stream);
            if (generation == 0)
            {
                var key = (target, context);
                if (drafts == null || !drafts.TryGetValue(key, out generation))
                {
                    generation = targetSequences.TryGetValue(target, out var sequence) && sequence.Stream == stream ? sequence.Last : 0;
                    if (drafts != null)
                        foreach (var draft in drafts)
                            if (draft.Key.Target.Equals(target)) generation = Math.Max(generation, draft.Value);
                    if (!UiPresentationTheme.TryNextTargetGeneration(generation, out generation))
                        return UiPresentationTheme.TargetGenerationsExhausted;
                    drafts?.Add(key, generation);
                }
            }

            envelope = UiPresentationTheme.Envelope(context.Mod, digest!, generation, theme);
            return UiPresentationTheme.Allowed;
        }

        private ulong FindTargetGeneration(PlayerContext context, ParticipantId target, ulong stream)
        {
            for (var index = 0; index < surfaces.Count; index++)
            {
                var surface = surfaces[index];
                if (surface.Stream == stream && ReferenceEquals(surface.Context, context) && surface.Target.Id.Equals(target)
                    && surface.Envelope is { } envelope) return envelope.Generation;
            }

            return 0;
        }

        private void CommitTargetGeneration(ParticipantId target, ulong stream, UiWireEnvelope? envelope)
        {
            if (envelope == null) return;
            if (targetSequences.TryGetValue(target, out var current) && current.Stream == stream && current.Last >= envelope.Generation)
                return;
            targetSequences[target] = new TargetSequence(stream, envelope.Generation);
        }

        internal OperationResult<bool> PrepareThemeSelection(UiThemeScope changed, UiThemeRef? proposed, out Action? commit)
        {
            commit = null;
            var shared = Presentation;
            List<MultiplayerPresentation.HudThemeUpdate>? sharedUpdates = null;
            if (shared != null)
            {
                var stagedShared = shared.StageThemes(changed, proposed, preparingHost: false, out sharedUpdates);
                if (!stagedShared.Succeeded) return stagedShared;
            }

            var targeted = StageTargetedThemes(changed, proposed, out var targetUpdates);
            if (!targeted.Succeeded) return targeted;
            if (sharedUpdates != null || targetUpdates != null)
                commit = () =>
                {
                    shared?.CommitThemes(sharedUpdates);
                    if (targetUpdates == null) return;
                    foreach (var change in targetUpdates)
                    {
                        change.Surface.Theme = change.Theme;
                        change.Surface.Dirty = true;
                    }
                };
            return UiPresentationTheme.Allowed;
        }

        private OperationResult<bool> StageTargetedThemes(UiThemeScope changed, UiThemeRef? proposed,
            out List<(RemoteUiSurface Surface, UiThemeDescriptor? Theme)>? updates)
        {
            updates = null;
            var drafts = new Dictionary<(ParticipantId Target, PlayerContext Scope), ulong>();
            // Match the normal dirty-send order when planning the variable-length generation fields.
            for (var index = surfaces.Count - 1; index >= 0; index--)
            {
                var surface = surfaces[index];
                var source = surface.Context.UiService as IUiPresentationThemeSource;
                var scope = source?.PresentationThemeScope;
                if (surface.Closed || !surface.Target.IsPresent || surface.Context.Lifetime.IsStopping || scope == null) continue;
                if (!scope.TryDescribe(surface.Request.Theme, out var theme, changed, proposed)) return UiPresentationTheme.UnknownTheme;
                if (ReferenceEquals(surface.Theme, theme)) continue;
                var target = surface.Target.Id;
                var stream = Channel?.GetGeneration(target) ?? 0;
                try
                {
                    var stage = StageTargetEnvelope(surface.Context, target, stream, theme, surface.Content, drafts, out var envelope);
                    if (!stage.Succeeded) return stage;
                    var revision = surface.Revision + 1;
                    _ = UiWireCodec.MeasureTargetedSurface(surface.Context.Mod, target.Value, surface.Token, revision,
                        surface.Request, surface.Body, surface.Visible, surface.Content, UiClockEpoch, retained: true, envelope);
                }
                catch (ArgumentException error) { return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, error.Message); }
                catch (InvalidOperationException) { return Limit<bool>(); }
                (updates ??= new List<(RemoteUiSurface, UiThemeDescriptor?)>()).Add((surface, theme));
            }

            return UiPresentationTheme.Allowed;
        }

        private readonly struct TargetSequence
        {
            internal TargetSequence(ulong stream, ulong last) { Stream = stream; Last = last; }
            internal ulong Stream { get; }
            internal ulong Last { get; }
        }
    }
}
