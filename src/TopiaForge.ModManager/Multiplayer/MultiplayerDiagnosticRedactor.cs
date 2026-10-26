using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Removes anything shaped like a network address from multiplayer text before it reaches a log, the diagnostics
    /// bundle or a crash report. The provider already keeps addresses inside its network layer; this is the runtime's
    /// second line, and it errs towards removing too much: a diagnostic that loses a file name or a mod id is better than
    /// one that keeps a host name. The multiplayer provider compiles this file too, so what it logs on its own, before a
    /// runtime attaches or when the runtime fails, is redacted by the same rules. <see cref="Redact"/> also removes local
    /// paths, by the manager log's rules; <see cref="RedactAddresses"/>, for labels, removes addresses only.
    /// </summary>
    /// <remarks>
    /// The text is split into tokens at white space, quotes, brackets of every kind, commas, semicolons and bars. A token
    /// is replaced whole when it, or its percent-decoded form, holds:
    /// <list type="bullet">
    /// <item>a URL, <c>scheme://authority</c>;</item>
    /// <item>an IPv4 address of four parts, even inside a word or a dotted name, so a four-part version such as 1.2.3.4
    /// goes too; or one in a shorter, hexadecimal or octal form the classic parsers accept, such as <c>10.20.7720</c> or
    /// <c>0x0A.0x14.0x1E.0x28</c>. Fewer than four dotted decimal parts that each fit a byte stay, since they read as a
    /// version;</item>
    /// <item>an IPv6 address the platform's parser accepts once a zone and a port are stripped, mapped and scoped forms
    /// included, even glued to a label or a word;</item>
    /// <item>a <c>label:port</c> form whose label holds a letter, such as <c>myhost:27250</c> or <c>File.cs:42</c>;</item>
    /// <item>a host name in any part between <c>:</c>, <c>/</c>, <c>\</c>, <c>@</c>, <c>=</c>, <c>?</c>, <c>&amp;</c> and
    /// <c>#</c>: dotted labels ending in a top-level domain, which is any word of two letters or more in one case, any two
    /// letters, a punycode label, or a common domain in any case. File names and mod ids such as
    /// <c>io.example.sandbox</c> read the same, and go too.</item>
    /// </list>
    /// Times such as 12:30, versions such as 1.0.0 and code names whose last part mixes cases, such as
    /// <c>System.Net.Sockets.SocketException</c>, stay.
    /// </remarks>
    internal static class MultiplayerDiagnosticRedactor
    {
        internal const string Replacement = "[address]";
        private const string Delimiters = "\"'`()<>{}[],;|";

        private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);
        private static readonly char[] PartSeparators = { ':', '/', '\\', '@', '=', '?', '&', '#' };
        private static readonly char[] SegmentSeparators = { '/', '\\', '?', '&', '#' };

        private static readonly Regex IPv4 = new Regex(
            @"(?<![0-9])([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})(?![0-9])", RegexOptions.CultureInvariant, Budget);

        private static readonly Regex HostName = new Regex(
            @"^(?:[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?\.)+([A-Za-z]{2,63}|[Xx][Nn]--[A-Za-z0-9-]{1,59})$", RegexOptions.CultureInvariant, Budget);

        /// <summary>Top-level domains a host, or a dynamic name for one, is likely to end in, matched in any case.</summary>
        private static readonly HashSet<string> TopLevelDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "com", "net", "org", "edu", "gov", "mil", "int", "io", "gg", "dev", "app", "me", "co", "info", "biz", "xyz", "online",
            "site", "tech", "cloud", "host", "server", "games", "game", "live", "tv", "pro", "name", "mobi", "zone", "world", "club",
            "local", "lan", "home", "internal", "localdomain", "arpa", "onion", "test", "example", "invalid", "localhost"
        };

        /// <summary>
        /// Describes <paramref name="diagnostic"/> in one redacted line: its code, message and participant, and its failure's
        /// type, message and, for an error, stack. Only the failure's own message is kept, never an inner one. The runtime
        /// logs and keeps exactly this line, and the provider's own log takes the same, so every path redacts alike.
        /// </summary>
        /// <param name="diagnostic">The diagnostic, whatever text it holds.</param>
        /// <param name="code">The redacted code, as the diagnostics bundle keeps it.</param>
        internal static string Describe(InternalMultiplayerDiagnostic diagnostic, out string code)
        {
            if (diagnostic == null) throw new ArgumentNullException(nameof(diagnostic));
            code = Redact(diagnostic.Code);
            var text = "Multiplayer [" + code + "]: " + Redact(diagnostic.Message);
            if (diagnostic.Participant.HasValue) text += " (participant " + Redact(diagnostic.Participant.Value.Value) + ")";
            var failure = diagnostic.Exception;
            if (failure == null) return text;
            text += " " + failure.GetType().Name + ": " + Redact(failure.Message);
            if (diagnostic.Severity == InternalDiagnosticSeverity.Error && failure.StackTrace != null) text += Environment.NewLine + Redact(failure.StackTrace);
            return text;
        }

        /// <summary>
        /// Returns <paramref name="text"/> with every local path and every address-shaped token replaced, as a log, the
        /// diagnostics bundle or a launch record keeps it; a text that defeats the patterns is withheld. Paths and tokens are
        /// both found in the text as it came, and one placeholder covers a path and a token that overlap, so removing a path
        /// never cuts a token down to an address the token rule no longer sees, as in <c>myhost:27250=file:///x</c>.
        /// </summary>
        internal static string Redact(string? text) => Replace(text, paths: true);

        /// <summary>
        /// Returns <paramref name="text"/> with every address-shaped token replaced and its slashes kept, for words another
        /// player wrote and a label shows, such as a server name or a kick message: there "EU / NA" or "/help" is
        /// punctuation, not a path. A text that defeats the patterns is withheld.
        /// </summary>
        internal static string RedactAddresses(string? text) => Replace(text, paths: false);

        private static string Replace(string? text, bool paths)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var value = text!;
            try
            {
                List<Span>? spans = null;
                var index = 0;
                var quote = '\0';
                while (paths && DiagnosticPathRedactor.TryFindPath(value, ref index, ref quote, out var pathStart, out var pathEnd))
                    (spans ??= new List<Span>()).Add(new Span(pathStart, pathEnd, true));

                index = 0;
                while (index < value.Length)
                {
                    if (IsDelimiter(value[index]))
                    {
                        index++;
                        continue;
                    }

                    var start = index;
                    while (index < value.Length && !IsDelimiter(value[index])) index++;
                    if (HoldsAddress(value.Substring(start, index - start))) (spans ??= new List<Span>()).Add(new Span(start, index, false));
                }

                return spans == null ? value : Merge(value, spans);
            }
            catch (RegexMatchTimeoutException)
            {
                return "[withheld: the text could not be checked for addresses]";
            }
        }

        /// <summary>Replaces each run of overlapping spans with one placeholder: a path's, when a path is among them.</summary>
        private static string Merge(string text, List<Span> spans)
        {
            spans.Sort((left, right) => left.Start.CompareTo(right.Start));
            var builder = new StringBuilder(text.Length);
            var copied = 0;
            for (var index = 0; index < spans.Count;)
            {
                var start = spans[index].Start;
                var end = spans[index].End;
                var path = spans[index].Path;
                for (index++; index < spans.Count && spans[index].Start < end; index++)
                {
                    if (spans[index].End > end) end = spans[index].End;
                    path |= spans[index].Path;
                }

                builder.Append(text, copied, start - copied).Append(path ? DiagnosticPathRedactor.Placeholder : Replacement);
                copied = end;
            }

            return builder.Append(text, copied, text.Length - copied).ToString();
        }

        private readonly struct Span
        {
            internal Span(int start, int end, bool path)
            {
                Start = start;
                End = end;
                Path = path;
            }

            internal int Start { get; }

            internal int End { get; }

            internal bool Path { get; }
        }

        private static bool IsDelimiter(char character) => char.IsWhiteSpace(character) || Delimiters.IndexOf(character) >= 0;

        private static bool HoldsAddress(string token)
        {
            if (Shaped(token)) return true;

            // A percent-encoded address, as a URL carries one, is decoded until nothing more changes; what it decodes to is
            // split at the same delimiters, so an encoded bracket or space hides nothing.
            var decoded = token;
            for (var round = 0; round < 3 && decoded.IndexOf('%') >= 0; round++)
            {
                var next = PercentDecode(decoded);
                if (string.Equals(next, decoded, StringComparison.Ordinal)) break;
                decoded = next;
                if (AnyShaped(decoded)) return true;
            }

            return false;
        }

        private static bool AnyShaped(string text)
        {
            var index = 0;
            while (index < text.Length)
            {
                if (IsDelimiter(text[index]))
                {
                    index++;
                    continue;
                }

                var start = index;
                while (index < text.Length && !IsDelimiter(text[index])) index++;
                if (Shaped(text.Substring(start, index - start))) return true;
            }

            return false;
        }

        private static bool Shaped(string token)
        {
            if (HasUrl(token) || HasIPv4(token) || HasIPv6(token)) return true;
            foreach (var segment in token.Split(SegmentSeparators))
            {
                if (IsLabelWithPort(segment)) return true;
            }

            foreach (var part in token.Split(PartSeparators))
            {
                var trimmed = part.TrimEnd('.', '!', '?');
                if (IsHostName(trimmed) || IsUnusualIPv4(trimmed)) return true;
            }

            return false;
        }

        /// <summary>A scheme of letters, digits, plus, dot or dash, starting with a letter, then <c>://</c>.</summary>
        private static bool HasUrl(string token)
        {
            var mark = token.IndexOf("://", StringComparison.Ordinal);
            if (mark <= 0) return false;
            var start = mark;
            while (start > 0 && (IsAsciiLetterOrDigit(token[start - 1]) || token[start - 1] == '+' || token[start - 1] == '.' || token[start - 1] == '-')) start--;
            while (start < mark && !IsAsciiLetter(token[start])) start++;
            return start < mark;
        }

        private static bool HasIPv4(string token)
        {
            foreach (Match match in IPv4.Matches(token))
            {
                var valid = true;
                for (var group = 1; group <= 4 && valid; group++)
                {
                    valid = int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture) <= 255;
                }

                if (valid) return true;
            }

            return false;
        }

        /// <summary>
        /// Reads <paramref name="part"/> the way the classic parsers read an IPv4 address: one to four parts, each decimal,
        /// octal with a leading zero, or hexadecimal with <c>0x</c>, the last filling the bytes the others leave. It is an
        /// unusual one when a part is hexadecimal or octal, or when fewer than four parts end in one past a byte, as in
        /// <c>10.20.7720</c>; a single part counts only when hexadecimal, since a plain number is not an address.
        /// </summary>
        private static bool IsUnusualIPv4(string part)
        {
            if (part.Length == 0 || part.Length > 48) return false;
            var pieces = part.Split('.');
            if (pieces.Length > 4) return false;
            var unusual = false;
            for (var index = 0; index < pieces.Length; index++)
            {
                if (!TryReadPiece(pieces[index], out var value, out var numberBase)) return false;
                var last = index == pieces.Length - 1;
                var bits = last ? 8 * (5 - pieces.Length) : 8;
                if (bits < 64 && value >> bits != 0) return false;
                if (pieces.Length == 1) return numberBase == 16;
                unusual |= numberBase != 10 || (last && pieces.Length < 4 && value > 255);
            }

            return unusual;
        }

        private static bool TryReadPiece(string piece, out ulong value, out int numberBase)
        {
            value = 0;
            numberBase = 10;
            if (piece.Length == 0 || piece.Length > 18) return false;
            var start = 0;
            if (piece.Length > 2 && piece[0] == '0' && (piece[1] == 'x' || piece[1] == 'X'))
            {
                numberBase = 16;
                start = 2;
            }
            else if (piece.Length > 1 && piece[0] == '0')
            {
                numberBase = 8;
                start = 1;
            }

            for (var index = start; index < piece.Length; index++)
            {
                var digit = Hex(piece[index]);
                if (digit < 0 || digit >= numberBase) return false;
                value = (value * (ulong)numberBase) + (ulong)digit;
            }

            return true;
        }

        /// <summary>
        /// Finds each run of hexadecimal digits, colons and dots with two colons or more, and the zone after it, and asks
        /// the platform's parser whether it is an IPv6 address once the zone and any port are stripped. A run glued to a
        /// word needs two groups or more, so <c>std::vector</c> stays.
        /// </summary>
        private static bool HasIPv6(string token)
        {
            var index = 0;
            while (index < token.Length)
            {
                if (!IsIPv6Character(token[index]))
                {
                    index++;
                    continue;
                }

                var start = index;
                var colons = 0;
                while (index < token.Length && IsIPv6Character(token[index]))
                {
                    if (token[index] == ':') colons++;
                    index++;
                }

                var end = index;
                if (index < token.Length && token[index] == '%')
                {
                    index++;
                    while (index < token.Length && (IsAsciiLetterOrDigit(token[index]) || token[index] == '_' || token[index] == '-')) index++;
                }

                if (colons < 2) continue;
                var glued = (start > 0 && IsAsciiLetterOrDigit(token[start - 1])) || (index < token.Length && IsAsciiLetterOrDigit(token[index]));
                if (IsIPv6(token.Substring(start, end - start), glued)) return true;
            }

            return false;
        }

        private static bool IsIPv6(string run, bool glued)
        {
            // A label's colon in front, as in endpoint:2001:db8::1, is not part of the address.
            if (run.StartsWith(":", StringComparison.Ordinal) && !run.StartsWith("::", StringComparison.Ordinal)) run = run.Substring(1);
            run = run.TrimEnd('.');
            foreach (var candidate in Candidates(run))
            {
                if (!IPAddress.TryParse(candidate, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetworkV6) continue;
                if (!glued || Groups(candidate) >= 2) return true;
            }

            return false;
        }

        /// <summary>The run as it is, without a trailing port, and without characters glued to its first group.</summary>
        private static IEnumerable<string> Candidates(string run)
        {
            yield return run;
            var unported = WithoutPort(run);
            if (unported != null) yield return unported;
            var firstColon = run.IndexOf(':');
            if (firstColon <= 4) yield break;
            var trimmed = run.Substring(firstColon - 4);
            yield return trimmed;
            var trimmedUnported = WithoutPort(trimmed);
            if (trimmedUnported != null) yield return trimmedUnported;
        }

        private static string? WithoutPort(string run)
        {
            var colon = run.LastIndexOf(':');
            return colon > 0 && run[colon - 1] != ':' && IsPort(run, colon + 1) ? run.Substring(0, colon) : null;
        }

        private static int Groups(string candidate)
        {
            var groups = 0;
            foreach (var part in candidate.Split(':'))
            {
                if (part.Length > 0) groups++;
            }

            return groups;
        }

        /// <summary>A label with a letter, then a colon and a port: <c>myhost:27250</c>, <c>play.example.org:27250:</c>, <c>File.cs:42</c>.</summary>
        private static bool IsLabelWithPort(string segment)
        {
            var trimmed = segment.TrimEnd('.', '!', '?', ':');
            var colon = trimmed.LastIndexOf(':');
            if (colon <= 0 || !IsPort(trimmed, colon + 1)) return false;
            var start = colon;
            while (start > 0 && IsLabelCharacter(trimmed[start - 1])) start--;
            for (var index = start; index < colon; index++)
            {
                if (IsAsciiLetter(trimmed[index])) return true;
            }

            return false;
        }

        /// <summary>Dotted labels ending in a top-level domain: <c>play.example.org</c>, <c>MyServer.duckdns.org</c>, <c>lab.example.edu</c>.</summary>
        private static bool IsHostName(string part)
        {
            if (part.IndexOf('.') < 0) return false;
            var match = HostName.Match(part);
            return match.Success && IsTopLevelDomain(match.Groups[1].Value);
        }

        /// <summary>
        /// Any punycode label, any two letters, a common domain in any case, or any word written in one case: names are
        /// typed and printed in one case, while code names such as <c>SocketException</c> mix them.
        /// </summary>
        private static bool IsTopLevelDomain(string label)
        {
            if (label.Length == 2 || TopLevelDomains.Contains(label) || label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)) return true;
            var lower = true;
            var upper = true;
            foreach (var character in label)
            {
                if (char.IsUpper(character)) lower = false;
                else upper = false;
            }

            return lower || upper;
        }

        private static bool IsPort(string text, int start)
        {
            var digits = text.Length - start;
            if (digits < 1 || digits > 5) return false;
            for (var index = start; index < text.Length; index++)
            {
                if (text[index] < '0' || text[index] > '9') return false;
            }

            return true;
        }

        private static bool IsIPv6Character(char character) =>
            (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F') ||
            character == ':' || character == '.';

        private static bool IsLabelCharacter(char character) => IsAsciiLetterOrDigit(character) || character == '-' || character == '_' || character == '.';

        private static bool IsAsciiLetter(char character) => (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z');

        private static bool IsAsciiLetterOrDigit(char character) => IsAsciiLetter(character) || (character >= '0' && character <= '9');

        /// <summary>Decodes each run of <c>%XX</c> sequences as UTF-8 and leaves everything else as it was.</summary>
        private static string PercentDecode(string token)
        {
            StringBuilder? decoded = null;
            List<byte>? pending = null;
            for (var index = 0; index < token.Length; index++)
            {
                if (token[index] == '%' && index + 2 < token.Length && Hex(token[index + 1]) >= 0 && Hex(token[index + 2]) >= 0)
                {
                    decoded ??= new StringBuilder(token.Length).Append(token, 0, index);
                    (pending ??= new List<byte>()).Add((byte)((Hex(token[index + 1]) << 4) | Hex(token[index + 2])));
                    index += 2;
                    continue;
                }

                if (decoded == null) continue;
                FlushBytes(decoded, pending);
                decoded.Append(token[index]);
            }

            if (decoded == null) return token;
            FlushBytes(decoded, pending);
            return decoded.ToString();
        }

        private static void FlushBytes(StringBuilder decoded, List<byte>? pending)
        {
            if (pending == null || pending.Count == 0) return;
            decoded.Append(Encoding.UTF8.GetString(pending.ToArray()));
            pending.Clear();
        }

        private static int Hex(char character)
        {
            if (character >= '0' && character <= '9') return character - '0';
            if (character >= 'a' && character <= 'f') return character - 'a' + 10;
            if (character >= 'A' && character <= 'F') return character - 'A' + 10;
            return -1;
        }
    }
}
