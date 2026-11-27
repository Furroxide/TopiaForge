using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPresentation
    {
        private ulong lastPresentationGeneration;

        internal int RequiredUiVersion
        {
            get
            {
                var required = UiPresentationTheme.LegacyVersion;
                for (var index = 0; index < authoredHuds.Count; index++)
                {
                    var hud = authoredHuds[index];
                    if (hud.IsAlive && !hud.Context.Lifetime.IsStopping)
                        required = Math.Max(required, UiPresentationTheme.RequiredVersion(hud.PresentationContent, hud.Theme));
                }

                return required;
            }
        }

        internal OperationResult<bool> StageEnvelope(MirroredHudPresenter hud, UiThemeDescriptor? theme, UiNode? content,
            out UiWireEnvelope? envelope)
        {
            var position = lastPresentationGeneration;
            return StageEnvelope(hud, theme, content, preparingHost: false, null, ref position, out envelope);
        }

        private OperationResult<bool> StageEnvelope(MirroredHudPresenter hud, UiThemeDescriptor? theme, UiNode? content,
            bool preparingHost, Dictionary<RuntimeReplication, ulong>? drafts, ref ulong position,
            out UiWireEnvelope? envelope)
        {
            envelope = null;
            try
            {
                var required = UiPresentationTheme.RequiredVersion(content, theme);
                if (required == UiPresentationTheme.LegacyVersion) return UiPresentationTheme.Allowed;
                if (Hosting && !players.AllPeersSupportUiVersion(required)) return UiPresentationTheme.UnsupportedVersion(required);
                var owner = hud.Context.OwnerModId;
                if (!players.TryOutgoingUiDigest(owner, requireSignedRoster: Hosting, out var digest))
                    return Hosting || preparingHost ? UiPresentationTheme.UnadmittedPackage : UiPresentationTheme.Allowed;

                var generation = GenerationFor(hud.Context);
                if (generation == 0)
                {
                    if (drafts == null || !drafts.TryGetValue(hud.Context, out generation))
                    {
                        generation = checked(++position);
                        drafts?.Add(hud.Context, generation);
                    }
                }

                envelope = UiPresentationTheme.Envelope(owner, digest!, generation, theme);
                return UiPresentationTheme.Allowed;
            }
            catch (ArgumentException error)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, error.Message);
            }
        }

        private ulong GenerationFor(RuntimeReplication scope)
        {
            for (var index = 0; index < surfaces.Count; index++)
            {
                var current = surfaces[index];
                if (ReferenceEquals(current.Context, scope) && current.Encoded?.Envelope is { } envelope) return envelope.Generation;
            }

            return 0;
        }

        private void RememberGeneration(MirroredHudPresenter.EncodedHud? payload)
        {
            if (payload?.Envelope is { } envelope) lastPresentationGeneration = Math.Max(lastPresentationGeneration, envelope.Generation);
        }

        internal OperationResult<bool> PrepareHosting()
        {
            if (RequiredUiVersion == UiPresentationTheme.LegacyVersion) return UiPresentationTheme.Allowed;
            var result = StageThemes(null, null, preparingHost: true, out var themeChanges);
            if (!result.Succeeded) return result;
            CommitThemes(themeChanges);
            return UiPresentationTheme.Allowed;
        }

        internal OperationResult<bool> StageThemes(UiThemeScope? changed, UiThemeRef? proposed, bool preparingHost,
            out List<HudThemeUpdate>? updates)
        {
            updates = null;
            var generations = new Dictionary<RuntimeReplication, ulong>();
            var position = lastPresentationGeneration;
            var bytes = surfaceBytes;
            var count = surfaces.Count;
            for (var index = 0; index < authoredHuds.Count; index++)
            {
                var hud = authoredHuds[index];
                if (!hud.IsAlive || hud.Context.Lifetime.IsStopping) continue;
                var theme = hud.Theme;
                if (preparingHost)
                {
                    if (UiPresentationTheme.RequiredVersion(hud.PresentationContent, theme) == UiPresentationTheme.LegacyVersion) continue;
                }
                else
                {
                    var scope = hud.ThemeSource?.PresentationThemeScope;
                    if (scope == null) continue;
                    if (!scope.TryDescribe(hud.Request.Theme, out theme, changed, proposed)) return UiPresentationTheme.UnknownTheme;
                    if (ReferenceEquals(theme, hud.Theme)) continue;
                }

                var validation = UiPresentationTheme.StylesValid(hud.PresentationContent, theme);
                if (!validation.Succeeded) return validation;
                var stage = StageEnvelope(hud, theme, hud.PresentationContent, preparingHost, generations, ref position, out var envelope);
                if (!stage.Succeeded) return stage;
                var payload = hud.StageRetained(envelope, theme);
                if (payload == null && (Hosting || preparingHost)) return Limit<bool>();
                var present = surfaces.Contains(hud);
                if (present) { bytes -= hud.Bytes; count--; }
                if (payload != null)
                {
                    bytes += payload.Value.Bytes.Length + 4;
                    count++;
                }

                (updates ??= new List<HudThemeUpdate>()).Add(new HudThemeUpdate(hud, theme, payload));
            }

            if (bytes <= MaximumBytes && count <= MaximumSurfaces) return UiPresentationTheme.Allowed;
            if (Hosting || preparingHost) return Limit<bool>();

            // Solo keeps the local composition. Reopening must qualify the complete K1 set before admitting anyone.
            if (updates != null)
                for (var index = 0; index < updates.Count; index++)
                {
                    var change = updates[index];
                    updates[index] = new HudThemeUpdate(change.Hud, change.Theme, null);
                }
            return UiPresentationTheme.Allowed;
        }

        internal void CommitThemes(List<HudThemeUpdate>? updates)
        {
            if (updates == null) return;
            foreach (var change in updates)
            {
                var hud = change.Hud;
                var current = surfaces.IndexOf(hud);
                if (current >= 0) surfaceBytes -= hud.Bytes;
                hud.Theme = change.Theme;
                hud.Encoded = change.Payload;
                if (change.Payload == null)
                {
                    if (current >= 0) surfaces.RemoveAt(current);
                }
                else
                {
                    if (current < 0) surfaces.Add(hud);
                    surfaceBytes += hud.Bytes;
                    RememberGeneration(change.Payload);
                }
            }

            version++;
        }

        internal readonly struct HudThemeUpdate
        {
            internal HudThemeUpdate(MirroredHudPresenter hud, UiThemeDescriptor? theme, MirroredHudPresenter.EncodedHud? payload)
            { Hud = hud; Theme = theme; Payload = payload; }
            internal MirroredHudPresenter Hud { get; }
            internal UiThemeDescriptor? Theme { get; }
            internal MirroredHudPresenter.EncodedHud? Payload { get; }
        }
    }
}
