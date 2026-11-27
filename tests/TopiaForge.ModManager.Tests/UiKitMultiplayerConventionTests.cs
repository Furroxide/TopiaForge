using System;
using System.IO;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Source-level guards for the multiplayer kit widgets that the offline tests cannot execute
    /// because they need Unity's text renderer or frame loop.
    /// </summary>
    internal static class UiKitMultiplayerConventionTests
    {
        public static void Run()
        {
            var widgets = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi", "Widgets");
            PlayerTextRendersAsPlainText(widgets);
            ChatVisibilityFollowsAccessibilityChanges(widgets);
            RemoteUiIsPlainText();
            Console.WriteLine("UiKitMultiplayerConventionTests passed.");
        }

        internal static void RemoteUiIsPlainText()
        {
            var root = Program.FindRepoRoot();
            var kit = Path.Combine(root, "src", "TopiaForge.Mods.UnityUi");
            Require(kit, "UiHost.cs", "AddComponent<TopiaForgePlainTextRoot>()", "Received UI must mark the whole canvas, including pooled content.");
            Require(Path.Combine(kit, "Text"), "TopiaForgeTmp.cs", "GetComponentInParent<TopiaForgePlainTextRoot>(true)", "Labels must inherit the remote canvas's literal text policy.");
            Require(Path.Combine(kit, "Text"), "TopiaForgeTmp.cs", "tmp.richText = false;", "Host markup must not restyle remote controls.");
            Require(Path.Combine(kit, "Text"), "TopiaForgeTmp.cs", "tmp.parseCtrlCharacters = false;", "Host escape sequences must remain literal.");
            Require(Path.Combine(root, "src", "TopiaForge.ModManager"), "UnityUiService.Remote.cs", "TopiaForgeToasts.ShowPlain(message", "Remote toasts must use the kit's plain renderer.");
        }

        /// <summary>
        /// Player names and chat messages are untrusted. TextMeshPro interprets rich-text tags and
        /// escape sequences by default, so a name such as "&lt;size=400&gt;" could restyle a scoreboard
        /// or a name tag and a message could forge extra chat lines. Every widget that shows player
        /// text must bound and sanitize it through TopiaForgePlainText and render it with both off.
        /// </summary>
        private static void PlayerTextRendersAsPlainText(string widgets)
        {
            Require(widgets, "TopiaForgeLabel.cs", "text.richText = false;", "Label PlainText() must disable rich text.");
            Require(widgets, "TopiaForgeLabel.cs", "text.parseCtrlCharacters = false;", "Label PlainText() must disable escape parsing.");
            Require(widgets, "TopiaForgeBadge.cs", "label.richText = false;", "Badge PlainText() must disable rich text.");
            Require(widgets, "TopiaForgeBadge.cs", "label.parseCtrlCharacters = false;", "Badge PlainText() must disable escape parsing.");

            Require(widgets, "TopiaForgeChatLog.Chrome.cs", "text.richText = false;", "Chat lines must disable rich text.");
            Require(widgets, "TopiaForgeChatLog.Chrome.cs", "text.parseCtrlCharacters = false;", "Chat lines must disable escape parsing.");
            Require(widgets, "TopiaForgeChatLog.cs", "TopiaForgePlainText.Sanitize(author, MaxAuthorChars)", "Chat authors must be sanitized and bounded.");

            Require(widgets, "TopiaForgeNametag.cs", "Name = head.Label(string.Empty, TopiaForgeTextStyle.Label).NoWrap().PlainText();", "Name tags must render names as plain text.");
            Require(widgets, "TopiaForgeNametag.cs", "secondary = content.Label(TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).NoWrap().PlainText();", "Name tag secondary lines must render as plain text.");
            Require(widgets, "TopiaForgeNametag.cs", "badge = head.Badge(string.Empty, badgeTone).PlainText();", "Name tag badges must render as plain text.");
            Require(widgets, "TopiaForgeNametag.cs", "TopiaForgePlainText.Sanitize(value, MaxNameChars)", "Name tag names must be sanitized and bounded.");
            Require(widgets, "TopiaForgeNametag.cs", "TopiaForgePlainText.Sanitize(value, MaxSecondaryChars)", "Name tag secondary lines must be sanitized and bounded.");
            Require(widgets, "TopiaForgeNametag.cs", "TopiaForgePlainText.Sanitize(value, MaxBadgeChars)", "Name tag badges must be sanitized and bounded.");

            Require(widgets, "TopiaForgeTableColumn.cs", "cell.Label(string.Empty, TopiaForgeTextStyle.Body).Ellipsis().PlainText()", "Text table cells must render as plain text.");
            Require(widgets, "TopiaForgeTableColumn.cs", "TopiaForgePlainText.Sanitize(text(item), MaxCellChars)", "Text table cells must be sanitized and bounded.");
        }

        /// <summary>
        /// Accessibility changes reach widgets only through ApplyTheme. When reduced motion hides a
        /// fading chat line, the fade driver stops; turning reduced motion off again must re-run the
        /// visibility pass there, or the line stays hidden for the rest of its fade.
        /// </summary>
        private static void ChatVisibilityFollowsAccessibilityChanges(string widgets)
        {
            var body = MethodBody(widgets, "TopiaForgeChatLog.cs", "public void ApplyTheme(TopiaForgeResolvedTheme theme)");
            if (!body.Contains("Refresh();", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "TopiaForgeChatLog.ApplyTheme must call Refresh() so lines hidden by reduced motion come back when it is turned off.");
            }
        }

        private static void Require(string directory, string file, string expected, string message)
        {
            if (!Read(directory, file).Contains(expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(message + " Expected to find `" + expected + "` in " + file + ".");
            }
        }

        private static string MethodBody(string directory, string file, string signature)
        {
            var source = Read(directory, file);
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            var open = start < 0 ? -1 : source.IndexOf('{', start);
            if (open < 0)
            {
                throw new InvalidOperationException("`" + signature + "` was not found in " + file + "; update UiKitMultiplayerConventionTests if it changed.");
            }

            var depth = 0;
            for (var index = open; index < source.Length; index++)
            {
                if (source[index] == '{')
                {
                    depth++;
                }
                else if (source[index] == '}' && --depth == 0)
                {
                    return source.Substring(open, index - open + 1);
                }
            }

            throw new InvalidOperationException("The body of `" + signature + "` in " + file + " is not closed.");
        }

        private static string Read(string directory, string file)
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(file + " was not found under " + directory + "; update UiKitMultiplayerConventionTests if it moved.");
            }

            return File.ReadAllText(path);
        }
    }
}
