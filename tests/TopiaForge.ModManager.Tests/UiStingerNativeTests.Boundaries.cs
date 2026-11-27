namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiStingerNativeTests
    {
        private const string Boundaries = """
            using System;
            using TMPro;
            using TopiaForge.ModManager.Core;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.EventSystems;

            namespace TopiaForge.Mods.UnityUi
            {
                internal static class TopiaForgeFonts
                {
                    internal static readonly TMP_FontAsset Kit = new();
                    internal static TMP_FontAsset ForThemeRole(string name, TopiaForgeTextStyle role) => Kit;
                }

                // Stingers never take Back. The raw overlay input is exercised by UiOverlayNativeTests.
                internal static class TopiaForgeInput
                {
                    internal static bool BackHeld() => false;
                    internal static bool BackPressedThisFrame() => false;
                }

                internal static class TopiaForgeInputOwnership
                {
                    internal static bool IsLiveRoot(object root) => root is GameObject view && view.activeInHierarchy;
                    internal static bool HoldsSelection(object root) => root is GameObject view &&
                        EventSystem.current?.currentSelectedGameObject is { } selected &&
                        selected.transform.IsChildOf(view.transform);
                }
            }

            namespace TopiaForge.ModManager.Core
            {
                internal sealed class AdmittedUiCatalogue
                {
                    internal AdmittedUiCatalogue(UiThemeAcquisitions assets) => Assets = assets;
                    internal UiThemeAcquisitions Assets { get; }
                }
            }

            namespace TopiaForge.ModManager
            {
                internal static class UnityMainThreadGuard { internal static void AssertCurrent() { } }
                internal sealed class UiThemeRegistration
                {
                    private readonly TMP_FontAsset? banner;
                    private readonly TMP_FontAsset? subtitle;
                    internal UiThemeRegistration(TMP_FontAsset? banner, TMP_FontAsset? subtitle = null)
                    { this.banner = banner; this.subtitle = subtitle; }
                    internal T? Font<T>(UiTextStyle style) where T : class =>
                        (style == UiTextStyle.Banner ? banner : subtitle) as T;
                }
                internal sealed class UiThemeDemand : IDisposable
                {
                    private Action? release;
                    private readonly Func<UiThemeRegistration?> registration;
                    internal UiThemeDemand(Func<UiThemeRegistration?> registration, Action release)
                    { this.registration = registration; this.release = release; }
                    internal UiThemeRegistration? Registration => registration();
                    public void Dispose() { var callback = release; release = null; callback?.Invoke(); }
                }
                internal sealed class UiThemeScope
                {
                    private readonly UiThemeDescriptor descriptor;
                    internal int Leases;
                    internal UiThemeRegistration? Ready;
                    internal bool RecordedFailure;
                    internal int FailedRetries;
                    internal UiThemeScope(UiThemeDescriptor descriptor) => this.descriptor = descriptor;
                    internal bool TryDescribe(UiThemeRef? selected, out UiThemeDescriptor? result)
                    { result = descriptor; return true; }
                    internal UiThemeDemand DemandSurface(UiThemeDescriptor theme, Action<OperationResult<bool>> settled, bool retryFailed = true)
                    {
                        if (RecordedFailure && retryFailed) FailedRetries++;
                        Leases++;
                        return new UiThemeDemand(() => Ready, () => Leases--);
                    }
                }
                internal sealed class UiThemeAcquisitions
                {
                    internal static bool HasNativeResources(UiThemeDescriptor descriptor) =>
                        descriptor.Fonts.Count != 0 || descriptor.Images.Count != 0;
                    internal UiThemeRegistration? Ready;
                    internal UiThemeDemand Demand(UiThemeDescriptor descriptor, bool retryFailed = true) => new(() => Ready, () => { });
                }
                internal sealed class UnityUiThemePackages
                {
                    internal UiThemeAcquisitions ForPackage(IModLifetime lifetime, AdmittedUiCatalogue catalogue) => catalogue.Assets;
                }
                internal sealed class TestLog : IModLogger
                {
                    public void Debug(string message) { }
                    public void Info(string message) { }
                    public void Warn(string message) { }
                    public void Error(string message) { }
                    public void Error(Exception error, string message) { }
                }
                internal sealed partial class OwnerUiService
                {
                    private readonly UiHost host;
                    private readonly IModLifetime lifetime;
                    private readonly IModLogger logger = new TestLog();
                    private readonly UiThemeScope? themeScope;
                    private readonly UnityUiThemePackages themePackages = new UnityUiThemePackages();
                    internal OwnerUiService(UiHost host, IModLifetime lifetime, UiThemeScope? theme = null)
                    { this.host = host; this.lifetime = lifetime; themeScope = theme; }
                }
            }
            """;
    }
}
