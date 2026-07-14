using System.IO;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>Package install: trust warning, path input, inbox list with per-file installs.</summary>
    internal sealed class PackagesTab : IManagerTab
    {
        private readonly ManagerUiState uiState;

        public PackagesTab(ManagerUiState uiState)
        {
            this.uiState = uiState;
        }

        public string Title => "PACKAGES";

        public void Build(TopiaForgeContainer content, ManagerTabContext context)
        {
            content.Label("PACKAGE INBOX", TopiaForgeTextStyle.Display).FixedHeight(34f);
            content.Label("Install trusted local .topiaforgemod packages only.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).FixedHeight(22f);
            content.Label("A package can contain executable C# code. Treat unknown packages like native binaries.", TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Warning).FixedHeight(26f);

            var input = content.Input("Full path to .topiaforgemod", uiState.PackagePath, value => uiState.PackagePath = value);
            input.OnSubmit(_ => context.RunAction(() => context.Plugin.InstallPackage(uiState.PackagePath)));

            var actions = content.Row(TopiaForgeGap.Sm);
            actions.FixedHeight(TopiaForgeTokens.ControlHeight);
            actions.Button("INSTALL PATH", () => context.RunAction(() => context.Plugin.InstallPackage(uiState.PackagePath)));
            actions.Button("INSTALL INBOX", () => context.RunAction(() => context.Plugin.InstallInboxPackages()), TopiaForgeButtonStyle.Outline);
            actions.Button("OPEN INBOX", () => context.Plugin.OpenFolder(context.Plugin.Paths.PackageInbox), TopiaForgeButtonStyle.Ghost);

            var inbox = context.Plugin.GetInboxPackages();
            var header = content.Row(TopiaForgeGap.Sm);
            header.FixedHeight(26f);
            header.Label("INBOX PACKAGES", TopiaForgeTextStyle.Heading);
            header.Badge(inbox.Count.ToString(), TopiaForgeTone.Accent);

            if (inbox.Count == 0)
            {
                content.Label("The inbox is empty. Dropped .topiaforgemod files install automatically at launch, or install by path.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
                return;
            }

            var scroll = content.Scroll(TopiaForgeGap.Xs);
            foreach (var file in inbox)
            {
                var captured = file;
                var row = scroll.Content.Row(TopiaForgeGap.Sm, TopiaForgeGap.Xs, expandChildWidth: false);
                row.FixedHeight(TopiaForgeTokens.ListRowHeight);
                var name = row.Label(Path.GetFileName(captured), TopiaForgeTextStyle.Body);
                name.Flex(1f, 0f);
                row.Button("INSTALL", () =>
                {
                    uiState.PackagePath = captured;
                    context.RunAction(() => context.Plugin.InstallPackage(captured));
                }, TopiaForgeButtonStyle.Outline).Fixed(110f, 30f);
            }
        }
    }
}
