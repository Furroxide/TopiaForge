using System;
using TopiaForge.ModManager.Sessions;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class InSessionChatTests
    {
        private static void NormalizedChatTextStaysPrivate()
        {
            foreach (var text in new[] { "No route to 192.0.\u202e2.8", "No route to host.t\u202eest", "retry\0host.test",
                "a.\u202eco " + new string('x', HostText.LineChars - 21) + " 192.0.2.800" })
            {
                void Private(string value) => That(!value.Contains("192.0.2.8", StringComparison.Ordinal) &&
                    !value.Contains("host.test", StringComparison.Ordinal) && value.Length <= 256,
                    "normalizing chat text never reconstructs a visible address after redaction");

                using var host = new ChatRig(true);
                host.Receive("p1", ChatWire.Write(text));
                That(host.Chat.TryTakeLine(out var line), "the host receives the bounded message");
                Private(line.Text);
                That(ChatWire.TryRead(host.Channel.Sent[0], true, out _, out var relayed), "the host sends a valid relay");
                Private(relayed);

                using var guest = new ChatRig(false);
                guest.Receive("p0", ChatWire.Write(text, "p0"));
                That(guest.Chat.TryTakeLine(out line), "a hostile host's relay reaches the display boundary");
                Private(line.Text);
                That(guest.Chat.Send(text).Length == 0, "local chat accepts the sanitized text");
                That(ChatWire.TryRead(guest.Channel.Sent[0], false, out _, out var sent), "the sender emits a valid request");
                Private(sent);
                guest.Chat.Feedback(text, true);
                That(guest.Chat.TryTakeLine(out line) && line.Error, "local feedback uses the same boundary");
                Private(line.Text);
            }
            That(HostText.ForDisplay("a.\u202eco", 4).Length <= 4, "redaction after normalization still respects a short label's bound");
            foreach (var bound in new[] { 20, HostText.NameChars, HostText.LineChars })
            {
                var text = bound == 20 ? "aaaaa.\u202eco 192.0.2.800" :
                    "a.\u202eco " + new string('x', bound - 21) + " 192.0.2.800";
                var shown = HostText.ForDisplay(text, bound);
                That(shown.Length <= bound && MultiplayerDiagnosticRedactor.RedactAddresses(shown) == shown,
                    "replacement expansion cannot reconstruct an address at the final truncation boundary");
            }
            That(HostText.ForDisplay("  \u202eType /help  for rules \n", HostText.LineChars) == "Type /help for rules",
                "normalized display text keeps ordinary slash commands through every privacy pass");
            That(HostText.ForDisplay("<b>Try again</b>") == "<b>Try again</b>", "markup remains literal text for the plain renderer");
        }

        private static void HostRefusalsSpendNoAllowance()
        {
            using var rig = new ChatRig(true);
            rig.Channel.Refuse = true;
            for (var index = 0; index < 3; index++)
                That(rig.Chat.Send("window full").StartsWith("The message could not be sent", StringComparison.Ordinal), "a host channel refusal is reported");
            That(!rig.Chat.TryTakeLine(out _), "a refused host message has no success echo");
            rig.Channel.Refuse = false;
            for (var index = 0; index < 3; index++)
                That(rig.Chat.Send("recovered").Length == 0, "host refusals preserve the complete burst");
            That(rig.Channel.Sent.Count == 3 && rig.Chat.Send("fourth").Contains("Wait two seconds", StringComparison.Ordinal), "successful host messages commit their allowance");
            using var presenter = new PresenterRig(true);
            presenter.Screen.Open();
            presenter.Channel.Refuse = true;
            That(!presenter.Screen.Submit("retry") && presenter.Screen.IsOpen, "a refused host send keeps the composer open for its draft");
            presenter.Channel.Refuse = false;
            That(presenter.Screen.Submit("retry") && !presenter.Screen.IsOpen, "the retained draft can be sent after recovery");
        }
    }
}
