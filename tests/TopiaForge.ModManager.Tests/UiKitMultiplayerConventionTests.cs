using System;
using System.IO;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Source-level guards for the multiplayer kit widgets that the offline tests cannot execute
    /// because they need Unity's text renderer.
    /// </summary>
    internal static class UiKitMultiplayerConventionTests
    {
        public static void Run()
        {
            PlayerTextRendersAsPlainText();
            Console.WriteLine("UiKitMultiplayerConventionTests passed.");
        }

        /// <summary>
        /// Player names and chat messages are untrusted. TextMeshPro interprets rich-text tags and
        /// escape sequences by default, so a name such as "&lt;size=400&gt;" could restyle a scoreboard
        /// and a message could forge extra chat lines. Every widget that shows player text must bound
        /// and sanitize it through TopiaForgePlainText and render it with both features off.
        /// </summary>
        private static void PlayerTextRendersAsPlainText()
        {
            var widgets = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi", "Widgets");

            Require(widgets, "TopiaForgeLabel.cs", "text.richText = false;", "PlainText() must disable rich text.");
            Require(widgets, "TopiaForgeLabel.cs", "text.parseCtrlCharacters = false;", "PlainText() must disable escape parsing.");

            Require(widgets, "TopiaForgeChatLog.Chrome.cs", "text.richText = false;", "Chat lines must disable rich text.");
            Require(widgets, "TopiaForgeChatLog.Chrome.cs", "text.parseCtrlCharacters = false;", "Chat lines must disable escape parsing.");
            Require(widgets, "TopiaForgeChatLog.cs", "TopiaForgePlainText.Sanitize(author, MaxAuthorChars)", "Chat authors must be sanitized and bounded.");

            Require(widgets, "TopiaForgeNametag.cs", "Name = head.Label(string.Empty, TopiaForgeTextStyle.Label).NoWrap().PlainText();", "Name tags must render names as plain text.");
            Require(widgets, "TopiaForgeNametag.cs", "secondary = content.Label(TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).NoWrap().PlainText();", "Name tag secondary lines must render as plain text.");
            Require(widgets, "TopiaForgeNametag.cs", "TopiaForgePlainText.Sanitize(value, MaxNameChars)", "Name tag names must be sanitized and bounded.");
            Require(widgets, "TopiaForgeNametag.cs", "TopiaForgePlainText.Sanitize(value, MaxSecondaryChars)", "Name tag secondary lines must be sanitized and bounded.");
            Require(widgets, "TopiaForgeNametag.cs", "TopiaForgePlainText.Sanitize(value, MaxBadgeChars)", "Name tag badges must be sanitized and bounded.");

            Require(widgets, "TopiaForgeTableColumn.cs", "cell.Label(string.Empty, TopiaForgeTextStyle.Body).Ellipsis().PlainText()", "Text table cells must render as plain text.");
            Require(widgets, "TopiaForgeTableColumn.cs", "TopiaForgePlainText.Sanitize(text(item), MaxCellChars)", "Text table cells must be sanitized and bounded.");
        }

        private static void Require(string directory, string file, string expected, string message)
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(file + " was not found under " + directory + "; update UiKitMultiplayerConventionTests if it moved.");
            }

            if (!File.ReadAllText(path).Contains(expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(message + " Expected to find `" + expected + "` in " + file + ".");
            }
        }
    }
}
