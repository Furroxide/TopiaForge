using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TopiaForge.ModManager.Sessions;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The in-game privacy wording against its approval: every string in <see cref="MultiplayerPrivacyText"/> must equal the
    /// approved text in docs/internal/MultiplayerPrivacyText.md, read from the document itself, so neither side can drift.
    /// The new interface's sources may not spell any of it again outside the constants file.
    /// </summary>
    internal static class MultiplayerPrivacyTextTests
    {
        private const string DisclosureSection = "First time a player hosts or joins";
        private const string DirectSection = "When a session needs a direct connection";
        private const string VoiceSection = "First time a player turns on voice chat";

        internal static void Run()
        {
            var document = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "docs", "internal", "MultiplayerPrivacyText.md"));
            ConstantsMatchTheApprovedDocument(document);
            TheParserRejectsAChangedWord(document);
            ThePromptsCarryTheApprovedWords();
            NoOtherSourceSpellsTheWording();
            Console.WriteLine("MultiplayerPrivacyTextTests passed.");
        }

        private static void ConstantsMatchTheApprovedDocument(string document)
        {
            var disclosure = Section(document, DisclosureSection);
            Same(disclosure.Title, MultiplayerPrivacyText.DisclosureTitle, "the disclosure's title");
            That(disclosure.Paragraphs.Count == 3, "the disclosure has three approved paragraphs (" + disclosure.Paragraphs.Count + ")");
            Same(disclosure.Paragraphs[0], MultiplayerPrivacyText.DisclosureExposure, "the disclosure's first paragraph");
            Same(disclosure.Paragraphs[1], MultiplayerPrivacyText.DisclosureHidden, "the disclosure's second paragraph");
            Same(disclosure.Paragraphs[2], MultiplayerPrivacyText.DisclosureChoice, "the disclosure's third paragraph");
            That(disclosure.Buttons.Count == 1, "the disclosure has one button");
            Same(disclosure.Buttons[0], MultiplayerPrivacyText.DisclosureButton, "the disclosure's button");

            var direct = Section(document, DirectSection);
            Same(direct.Title, MultiplayerPrivacyText.DirectTitle, "the direct prompt's title");
            That(direct.Paragraphs.Count == 1, "the direct prompt has one approved paragraph");
            Same(direct.Paragraphs[0], MultiplayerPrivacyText.DirectBody, "the direct prompt's body");
            That(direct.Buttons.Count == 2, "the direct prompt has two buttons");
            Same(direct.Buttons[0], MultiplayerPrivacyText.DirectAllowButton, "the direct prompt's allow button");
            Same(direct.Buttons[1], MultiplayerPrivacyText.DirectCancelButton, "the direct prompt's cancel button");

            var voice = Section(document, VoiceSection);
            Same(voice.Title, MultiplayerPrivacyText.VoiceTitle, "the voice consent's title");
            That(voice.Paragraphs.Count == 1, "the voice consent has one approved paragraph");
            Same(voice.Paragraphs[0], MultiplayerPrivacyText.VoiceBody, "the voice consent's body");
            Same(voice.Buttons[0], MultiplayerPrivacyText.VoiceAcceptButton, "the voice consent's accept button");
            Same(voice.Buttons[1], MultiplayerPrivacyText.VoiceDeclineButton, "the voice consent's decline button");

            // The setting is named as the approved text names it.
            That(MultiplayerPrivacyText.DisclosureHidden.StartsWith(MultiplayerPrivacyText.HideMyIp + " is on", StringComparison.Ordinal),
                "the setting's name is the one the approved disclosure uses");
            That(MultiplayerPrivacyText.DisclosureVersion == 1 && MultiplayerPrivacyText.VoiceConsentVersion == 1,
                "the versions match the launcher's, which counts the same approvals");
        }

        /// <summary>The parser itself must see a changed word, or the comparison above proves nothing.</summary>
        private static void TheParserRejectsAChangedWord(string document)
        {
            var changed = document.Replace("TopiaForge never turns it on for you.", "TopiaForge never turns it on.");
            That(Section(changed, DisclosureSection).Paragraphs[2] != MultiplayerPrivacyText.DisclosureChoice, "a changed sentence in the document is caught");
            var rewrapped = document.Replace("they can see your IP address and you can see theirs. An IP address shows\n> roughly",
                "they can see your IP address and you can see theirs.\n> An IP address shows roughly");
            if (!ReferenceEquals(rewrapped, document))
            {
                Same(Section(rewrapped, DisclosureSection).Paragraphs[0], MultiplayerPrivacyText.DisclosureExposure, "rewrapping a paragraph changes none of its words");
            }
        }

        private static void ThePromptsCarryTheApprovedWords()
        {
            var disclosure = PrivacyPrompts.Disclosure(_ => { });
            That(disclosure.Kind == ScreenPromptKind.Disclosure && disclosure.Title == MultiplayerPrivacyText.DisclosureTitle &&
                 disclosure.Paragraphs.Count == 3 && disclosure.Paragraphs[2] == MultiplayerPrivacyText.DisclosureChoice &&
                 disclosure.ConfirmLabel == MultiplayerPrivacyText.DisclosureButton && disclosure.CancelLabel.Length == 0,
                "the disclosure prompt shows the approved words with its one button");
            var direct = PrivacyPrompts.Direct(_ => { });
            That(direct.Kind == ScreenPromptKind.DirectConnection && direct.Title == MultiplayerPrivacyText.DirectTitle &&
                 direct.Paragraphs.Count == 1 && direct.Paragraphs[0] == MultiplayerPrivacyText.DirectBody &&
                 direct.ConfirmLabel == MultiplayerPrivacyText.DirectAllowButton && direct.CancelLabel == MultiplayerPrivacyText.DirectCancelButton,
                "the direct prompt shows the approved words with its two buttons");
        }

        /// <summary>Every string lives in one constants file: no other source of the new interface spells the wording.</summary>
        private static void NoOtherSourceSpellsTheWording()
        {
            var root = Program.FindRepoRoot();
            var fragments = new[] { "IP address", "Hide my IP", "direct connection", "Allow for this session", "I understand", "Your address and" };
            var literal = new Regex("\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.CultureInvariant);
            foreach (var folder in MultiplayerUiArchitectureTests.SourceFolders(root))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly))
                {
                    if (string.Equals(Path.GetFileName(file), "MultiplayerPrivacyText.cs", StringComparison.Ordinal)) continue;
                    foreach (Match match in literal.Matches(File.ReadAllText(file)))
                    {
                        foreach (var fragment in fragments)
                        {
                            That(match.Value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0,
                                Path.GetFileName(file) + " spells approved privacy wording (" + fragment + ") outside MultiplayerPrivacyText.cs");
                        }
                    }
                }
            }
        }

        private static void Same(string actual, string expected, string what)
        {
            if (string.Equals(actual, expected, StringComparison.Ordinal)) return;
            var at = 0;
            while (at < actual.Length && at < expected.Length && actual[at] == expected[at]) at++;
            throw new InvalidOperationException("Assertion failed: " + what + " differs from the approved document at character " + at +
                ": the document reads \"" + actual + "\" and the constant \"" + expected + "\".");
        }

        /// <summary>
        /// Reads one prompt from the document: the <c>**Title:**</c> line, the blockquote's paragraphs with their wrapped lines
        /// joined by single spaces, and the <c>**Button:**</c> or <c>**Buttons:**</c> line split at its middle dots.
        /// </summary>
        private static Prompt Section(string document, string heading)
        {
            var lines = document.Replace("\r\n", "\n").Split('\n');
            var start = Array.IndexOf(lines, "## " + heading);
            That(start >= 0, "the approved document has the section \"" + heading + "\"");
            var prompt = new Prompt();
            var paragraph = new StringBuilder();
            for (var index = start + 1; index < lines.Length && !lines[index].StartsWith("## ", StringComparison.Ordinal); index++)
            {
                var line = lines[index];
                if (line.StartsWith("**Title:** ", StringComparison.Ordinal))
                {
                    prompt.Title = line.Substring("**Title:** ".Length).Trim();
                }
                else if (line.StartsWith("**Button:** ", StringComparison.Ordinal) || line.StartsWith("**Buttons:** ", StringComparison.Ordinal))
                {
                    var text = line.Substring(line.IndexOf("** ", StringComparison.Ordinal) + 3);
                    foreach (var button in text.Split('·')) prompt.Buttons.Add(button.Trim());
                }
                else if (line.StartsWith(">", StringComparison.Ordinal))
                {
                    var content = line.Substring(1).Trim();
                    if (content.Length == 0)
                    {
                        Flush(prompt, paragraph);
                    }
                    else
                    {
                        if (paragraph.Length > 0) paragraph.Append(' ');
                        paragraph.Append(content);
                    }
                }
                else
                {
                    Flush(prompt, paragraph);
                }
            }

            Flush(prompt, paragraph);
            That(prompt.Title.Length > 0 && prompt.Paragraphs.Count > 0 && prompt.Buttons.Count > 0, "the section \"" + heading + "\" has a title, text and buttons");
            return prompt;
        }

        private static void Flush(Prompt prompt, StringBuilder paragraph)
        {
            if (paragraph.Length == 0) return;
            prompt.Paragraphs.Add(paragraph.ToString());
            paragraph.Clear();
        }

        private sealed class Prompt
        {
            internal string Title { get; set; } = string.Empty;

            internal List<string> Paragraphs { get; } = new List<string>();

            internal List<string> Buttons { get; } = new List<string>();
        }
    }
}
