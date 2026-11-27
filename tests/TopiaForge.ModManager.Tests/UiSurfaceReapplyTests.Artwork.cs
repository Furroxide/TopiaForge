namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiSurfaceReapplyTests
    {
        private const string ArtworkScenarios = """"
            using System;
            using System.Collections.Generic;
            using System.Diagnostics;
            using System.Linq;
            using System.Text;
            using System.Threading;
            using System.Threading.Tasks;
            using TopiaForge.ModManager.Core;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;

            namespace TopiaForge.ModManager
            {
                internal sealed partial class OwnerUiService
                {
                    private static readonly UiImageRef ProbeImage = new UiImageRef("harbour", "sail");

                    private static void ProbeArtworkAdmission()
                    {
                        using var dispatcher = new HostDispatcher();
                        using var package = new OwnerModLifetime();
                        var assets = new UiThemeAcquisitions(dispatcher, package, new ArtworkProbeLoader());
                        var (catalogue, _) = ArtworkCatalogue();
                        using var scope = new UiThemeScope(package, catalogue, acquisitions: assets);
                        var (surface, parent) = OpenArtworkProbe(package);
                        using (surface)
                        {
                            surface.ConfigureArtwork(scope, catalogue, assets, remote: false);
                            var original = new UiText("Keep this panel");
                            Require(surface.SetContent(original).Succeeded, "The production art probe opened its initial panel");
                            var retained = parent.Children.Single();
                            CheckImage(new UiImage(new UiImageRef("unknown", "sail"), "Sail"), ModErrorCode.NotFound);
                            CheckImage(new UiImage(new UiImageRef("harbour", "unknown"), "Sail"), ModErrorCode.NotFound);
                            CheckImage(new UiImage(ProbeImage, "Sail", tint: UiTone.Primary), ModErrorCode.InvalidArgument);

                            void CheckImage(UiImage image, ModErrorCode expected)
                            {
                                var clock = new UiCountdown(20, "New deadline");
                                var result = surface.SetContent(new UiColumn(clock, image));
                                Require(result.ErrorCode == expected && ReferenceEquals(retained, parent.Children.Single()) &&
                                    clock.ClockEpoch == null && surface.SetContent(original).Succeeded,
                                    "The production surface classifies missing art and malformed metadata without replacing its prior tree");
                            }
                        }
                    }

                    private static void ProbeArtworkRetries(bool remote, bool retryWhileDraining)
                    {
                        using var dispatcher = new HostDispatcher();
                        using var package = new OwnerModLifetime();
                        var loader = new ArtworkProbeLoader();
                        var assets = new UiThemeAcquisitions(dispatcher, package, loader);
                        var (catalogue, descriptor) = ArtworkCatalogue();
                        using var scope = new UiThemeScope(package, catalogue, acquisitions: assets);
                        var (surface, _) = OpenArtworkProbe(package);
                        using (surface)
                        {
                            surface.ConfigureArtwork(scope, catalogue, assets, remote);
                            try
                            {
                                Snapshot(0);
                                dispatcher.Drain();
                                Require(loader.Attempts == 1 && loader.Retirement != null,
                                    "First automatic artwork use started the initial load");
                                for (var index = 1; index <= 4; index++) Snapshot(index);
                                dispatcher.Drain();
                                Require(assets.Outcome(descriptor)?.ErrorCode == ModErrorCode.External && loader.Attempts == 1,
                                    "Changed artwork snapshots do not queue a retry while the failed load is retiring");

                                var theme = new UiThemeRef(descriptor.Id);
                                var retry = retryWhileDraining ? scope.PreloadAsync(theme) : null;
                                if (retryWhileDraining)
                                    Require(retry is { IsCompleted: false } && loader.Attempts == 1,
                                        "A deliberate preload waits for failed artwork retirement before retrying");
                                loader.Retirement!.Complete();
                                if (retry == null)
                                {
                                    var drained = package.DrainNativeWorkAsync();
                                    PumpArtwork(dispatcher, () => drained.IsCompleted || loader.Attempts > 1);
                                    Require(loader.Attempts == 1 && drained.IsCompletedSuccessfully,
                                        "Failed artwork retired without creating a new attempt");
                                    for (var index = 5; index <= 8; index++) Snapshot(index);
                                    dispatcher.Drain();
                                    Require(loader.Attempts == 1 && assets.Outcome(descriptor)?.ErrorCode == ModErrorCode.External,
                                        "Changed artwork snapshots keep the recorded failure after retirement");
                                    retry = scope.PreloadAsync(theme);
                                }

                                PumpArtwork(dispatcher, () => retry.IsCompleted);
                                Require(retry.Result.Succeeded && loader.Attempts == 2 &&
                                    assets.Registration(descriptor)?.Image<Sprite>("sail") != null,
                                    "A deliberate preload retries exactly once and makes the sprite available to existing artwork");
                            }
                            finally
                            {
                                loader.Retirement?.Complete();
                                surface.Dispose();
                                scope.Dispose();
                                var drained = package.DrainNativeWorkAsync();
                                PumpArtwork(dispatcher, () => drained.IsCompleted);
                                drained.GetAwaiter().GetResult();
                            }

                            void Snapshot(int number)
                            {
                                var art = new UiImage(ProbeImage, "Sail");
                                if (remote) art = art.WithWireMetadata(48f, 24f, descriptor.Digest.Span);
                                Require(surface.SetContent(new UiColumn(art, new UiText("Current snapshot " + number))).Succeeded,
                                    "The production surface admits each new snapshot with its retained artwork reference");
                            }
                        }
                    }

                    private static (UnityUiSurface Surface, TopiaForgeContainer Parent) OpenArtworkProbe(IModLifetime lifetime)
                    {
                        var host = new UiHost();
                        var parent = new TopiaForgeContainer(host);
                        var surface = UnityUiSurface.ForWindow("artwork-probe", new TopiaForgeWindow(host),
                            new TopiaForgeLabel(), parent, lifetime, new BoundaryLogger(), _ => { });
                        surface.SetClock(() => 0d, () => "artwork-epoch");
                        surface.Present();
                        return (surface, parent);
                    }

                    private static (AdmittedUiCatalogue Catalogue, UiThemeDescriptor Descriptor) ArtworkCatalogue()
                    {
                        const string json = """
                            {"schema":1,"id":"harbour","images":[{"id":"sail","bundlePath":"ui/ship.bundle",
                            "assetName":"Sail","licensePath":"LICENSE","pixelWidth":48,"pixelHeight":24,"isMask":false}]}
                            """;
                        var descriptor = UiThemePackParser.Parse(Encoding.UTF8.GetBytes(json)).Value ??
                            throw new InvalidOperationException("Artwork descriptor fixture did not parse");
                        return (new AdmittedUiCatalogue(null, new Dictionary<string, UiThemeDescriptor> { ["harbour"] = descriptor }), descriptor);
                    }

                    private static void PumpArtwork(HostDispatcher dispatcher, Func<bool> settled)
                    {
                        var timer = Stopwatch.StartNew();
                        while (!settled())
                        {
                            dispatcher.Drain();
                            if (timer.Elapsed > TimeSpan.FromSeconds(5))
                                throw new TimeoutException("The artwork probe is waiting for the acquisition state");
                            if (!settled()) Thread.Sleep(1);
                        }
                    }

                    private sealed class ArtworkProbeLoader : IUiThemeNativeLoader
                    {
                        internal int Attempts { get; private set; }
                        internal AssetNativeWorkTicket? Retirement { get; private set; }

                        public Task<OperationResult<UiThemeRegistration>> LoadAsync(UiThemeDescriptor descriptor, OwnerModLifetime owner)
                        {
                            if (++Attempts == 1)
                            {
                                Retirement = owner.RegisterNativeWork("observe failed sprite retirement");
                                return Task.FromResult(OperationResult<UiThemeRegistration>.Failure(ModErrorCode.External, "Sprite load failed"));
                            }
                            return Task.FromResult(OperationResult<UiThemeRegistration>.Success(new UiThemeRegistration(
                                new Dictionary<string, object> { ["sail"] = new Sprite() },
                                new Dictionary<UiTextStyle, object>(), new Dictionary<int, long>())));
                        }
                    }
                }
            }
            """";
    }
}
