using System.IO;
using Robotopia.Mods.UnityUi;

namespace Robotopia.ModManager
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

        public void Build(QwContainer content, ManagerTabContext context)
        {
            content.Label("PACKAGE INBOX", QwTextStyle.Display).FixedHeight(34f);
            content.Label("Install trusted local .robotopiamod packages only.", QwTextStyle.Caption).Tone(QwTone.Muted).FixedHeight(22f);
            content.Label("A package can contain executable C# code. Treat unknown packages like native binaries.", QwTextStyle.Body).Tone(QwTone.Warning).FixedHeight(26f);

            var input = content.Input("Full path to .robotopiamod", uiState.PackagePath, value => uiState.PackagePath = value);
            input.OnSubmit(_ => context.RunAction(() => context.Plugin.InstallPackage(uiState.PackagePath)));

            var actions = content.Row(QwGap.Sm);
            actions.FixedHeight(QwTokens.ControlHeight);
            actions.Button("INSTALL PATH", () => context.RunAction(() => context.Plugin.InstallPackage(uiState.PackagePath)));
            actions.Button("INSTALL INBOX", () => context.RunAction(() => context.Plugin.InstallInboxPackages()), QwButtonStyle.Outline);
            actions.Button("OPEN INBOX", () => context.Plugin.OpenFolder(context.Plugin.Paths.PackageInbox), QwButtonStyle.Ghost);

            var inbox = context.Plugin.GetInboxPackages();
            var header = content.Row(QwGap.Sm);
            header.FixedHeight(26f);
            header.Label("INBOX PACKAGES", QwTextStyle.Heading);
            header.Badge(inbox.Count.ToString(), QwTone.Accent);

            if (inbox.Count == 0)
            {
                content.Label("The inbox is empty. Drop .robotopiamod files into the package-inbox folder or install by path.", QwTextStyle.Caption).Tone(QwTone.Muted);
                return;
            }

            var scroll = content.Scroll(QwGap.Xs);
            foreach (var file in inbox)
            {
                var captured = file;
                var row = scroll.Content.Row(QwGap.Sm, QwGap.Xs, expandChildWidth: false);
                row.FixedHeight(QwTokens.ListRowHeight);
                var name = row.Label(Path.GetFileName(captured), QwTextStyle.Body);
                name.Flex(1f, 0f);
                row.Button("INSTALL", () =>
                {
                    uiState.PackagePath = captured;
                    context.RunAction(() => context.Plugin.InstallPackage(captured));
                }, QwButtonStyle.Outline).Fixed(110f, 30f);
            }
        }
    }
}
