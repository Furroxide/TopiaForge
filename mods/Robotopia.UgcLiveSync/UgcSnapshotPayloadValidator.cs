using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Robotopia.UgcLiveSync
{
    /// <summary>
    /// Strict, bounded UGC snapshot preflight. Gzip is expanded through a fixed-size buffer under the configured
    /// cap; UTF-8 must be well formed; and the decoded payload must be one complete JSON object with no trailing
    /// tokens. This deliberately validates structure without binding to the game's evolving project schema.
    /// </summary>
    internal static class UgcSnapshotPayloadValidator
    {
        private const int MaxJsonDepth = 128;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool TryValidate(byte[]? payload, long maximumExpandedBytes, out string error)
        {
            error = string.Empty;
            if (payload == null || payload.Length == 0)
            {
                error = "snapshot is empty";
                return false;
            }

            if (maximumExpandedBytes <= 0 || maximumExpandedBytes > int.MaxValue)
            {
                error = "snapshot validation limit is invalid";
                return false;
            }

            try
            {
                if (IsGzip(payload))
                {
                    return TryValidateGzip(payload, (int)maximumExpandedBytes, out error);
                }

                if (payload.LongLength > maximumExpandedBytes)
                {
                    error = "snapshot exceeds the " + maximumExpandedBytes + " byte limit";
                    return false;
                }

                return TryValidateUtf8Json(payload, 0, payload.Length, out error);
            }
            catch (DecoderFallbackException ex)
            {
                error = "snapshot is not strict UTF-8: " + ex.Message;
                return false;
            }
            catch (InvalidDataException ex)
            {
                error = "invalid gzip content: " + ex.Message;
                return false;
            }
            catch (IOException ex)
            {
                error = "could not expand gzip content: " + ex.Message;
                return false;
            }
            catch (OutOfMemoryException)
            {
                error = "snapshot validation buffer allocation failed";
                return false;
            }
        }

        private static bool IsGzip(byte[] payload)
        {
            return payload.Length >= 2 && payload[0] == 0x1f && payload[1] == 0x8b;
        }

        private static bool TryValidateGzip(byte[] payload, int maximumExpandedBytes, out string error)
        {
            error = string.Empty;
            // RFC 1952: ten-byte base header, eight-byte trailer, deflate compression method.
            if (payload.Length < 18 || payload[2] != 0x08)
            {
                error = "invalid gzip content: truncated header/trailer or unsupported compression method";
                return false;
            }

            using var input = new MemoryStream(payload, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);
            using var expanded = new MemoryStream(Math.Min(maximumExpandedBytes, 64 * 1024));
            var buffer = new byte[64 * 1024];
            var total = 0;
            while (true)
            {
                var count = gzip.Read(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    break;
                }

                if (total > maximumExpandedBytes - count)
                {
                    error = "expanded gzip exceeds the " + maximumExpandedBytes + " byte limit";
                    return false;
                }

                expanded.Write(buffer, 0, count);
                total += count;
            }

            if (total == 0)
            {
                error = "expanded gzip snapshot is empty";
                return false;
            }

            return TryValidateUtf8Json(expanded.GetBuffer(), 0, total, out error);
        }

        private static bool TryValidateUtf8Json(byte[] bytes, int offset, int count, out string error)
        {
            var text = StrictUtf8.GetString(bytes, offset, count);
            var parser = new JsonStructureParser(text);
            return parser.TryParseRootObject(out error);
        }

        private sealed class JsonStructureParser
        {
            private readonly string text;
            private int position;

            public JsonStructureParser(string text)
            {
                this.text = text;
            }

            public bool TryParseRootObject(out string error)
            {
                if (position < text.Length && text[position] == '\uFEFF')
                {
                    position++;
                }

                SkipWhitespace();
                if (position >= text.Length || text[position] != '{')
                {
                    error = "snapshot JSON root must be an object";
                    return false;
                }

                if (!ParseObject(1, out error))
                {
                    return false;
                }

                SkipWhitespace();
                if (position != text.Length)
                {
                    error = At("snapshot JSON has trailing content");
                    return false;
                }

                error = string.Empty;
                return true;
            }

            private bool ParseValue(int depth, out string error)
            {
                SkipWhitespace();
                if (position >= text.Length)
                {
                    error = At("unexpected end of JSON");
                    return false;
                }

                switch (text[position])
                {
                    case '{':
                        return ParseObject(depth + 1, out error);
                    case '[':
                        return ParseArray(depth + 1, out error);
                    case '"':
                        return ParseString(out error);
                    case 't':
                        return ParseLiteral("true", out error);
                    case 'f':
                        return ParseLiteral("false", out error);
                    case 'n':
                        return ParseLiteral("null", out error);
                    default:
                        return ParseNumber(out error);
                }
            }

            private bool ParseObject(int depth, out string error)
            {
                if (!CheckDepth(depth, out error))
                {
                    return false;
                }

                position++;
                SkipWhitespace();
                if (Consume('}'))
                {
                    return Success(out error);
                }

                while (true)
                {
                    if (!ParseString(out error))
                    {
                        return false;
                    }

                    SkipWhitespace();
                    if (!Consume(':'))
                    {
                        error = At("expected ':' after object property name");
                        return false;
                    }

                    if (!ParseValue(depth, out error))
                    {
                        return false;
                    }

                    SkipWhitespace();
                    if (Consume('}'))
                    {
                        return Success(out error);
                    }

                    if (!Consume(','))
                    {
                        error = At("expected ',' or '}' in object");
                        return false;
                    }

                    SkipWhitespace();
                }
            }

            private bool ParseArray(int depth, out string error)
            {
                if (!CheckDepth(depth, out error))
                {
                    return false;
                }

                position++;
                SkipWhitespace();
                if (Consume(']'))
                {
                    return Success(out error);
                }

                while (true)
                {
                    if (!ParseValue(depth, out error))
                    {
                        return false;
                    }

                    SkipWhitespace();
                    if (Consume(']'))
                    {
                        return Success(out error);
                    }

                    if (!Consume(','))
                    {
                        error = At("expected ',' or ']' in array");
                        return false;
                    }
                }
            }

            private bool ParseString(out string error)
            {
                if (!Consume('"'))
                {
                    error = At("expected JSON string");
                    return false;
                }

                while (position < text.Length)
                {
                    var character = text[position++];
                    if (character == '"')
                    {
                        return Success(out error);
                    }

                    if (character < 0x20)
                    {
                        error = At("JSON string contains an unescaped control character");
                        return false;
                    }

                    if (character == '\\')
                    {
                        if (position >= text.Length)
                        {
                            error = At("unterminated JSON escape");
                            return false;
                        }

                        var escape = text[position++];
                        if (escape == 'u')
                        {
                            if (!ParseEscapedCodeUnit(out var codeUnit, out error))
                            {
                                return false;
                            }

                            if (char.IsHighSurrogate(codeUnit))
                            {
                                if (position + 1 >= text.Length || text[position] != '\\' || text[position + 1] != 'u')
                                {
                                    error = At("high-surrogate escape is not followed by a low surrogate");
                                    return false;
                                }

                                position += 2;
                                if (!ParseEscapedCodeUnit(out var low, out error) || !char.IsLowSurrogate(low))
                                {
                                    error = At("high-surrogate escape is not followed by a low surrogate");
                                    return false;
                                }
                            }
                            else if (char.IsLowSurrogate(codeUnit))
                            {
                                error = At("unpaired low-surrogate escape");
                                return false;
                            }
                        }
                        else if (escape != '"' && escape != '\\' && escape != '/' && escape != 'b' &&
                                 escape != 'f' && escape != 'n' && escape != 'r' && escape != 't')
                        {
                            error = At("invalid JSON escape");
                            return false;
                        }

                        continue;
                    }

                    if (char.IsHighSurrogate(character))
                    {
                        if (position >= text.Length || !char.IsLowSurrogate(text[position]))
                        {
                            error = At("unpaired high surrogate in JSON string");
                            return false;
                        }

                        position++;
                    }
                    else if (char.IsLowSurrogate(character))
                    {
                        error = At("unpaired low surrogate in JSON string");
                        return false;
                    }
                }

                error = At("unterminated JSON string");
                return false;
            }

            private bool ParseEscapedCodeUnit(out char value, out string error)
            {
                value = '\0';
                if (position > text.Length - 4)
                {
                    error = At("truncated Unicode escape");
                    return false;
                }

                var number = 0;
                for (var index = 0; index < 4; index++)
                {
                    var digit = HexValue(text[position++]);
                    if (digit < 0)
                    {
                        error = At("invalid Unicode escape");
                        return false;
                    }

                    number = (number << 4) | digit;
                }

                value = (char)number;
                return Success(out error);
            }

            private bool ParseNumber(out string error)
            {
                var start = position;
                Consume('-');
                if (Consume('0'))
                {
                    if (position < text.Length && IsDigit(text[position]))
                    {
                        error = At("JSON number has a leading zero");
                        return false;
                    }
                }
                else if (position < text.Length && text[position] >= '1' && text[position] <= '9')
                {
                    while (position < text.Length && IsDigit(text[position]))
                    {
                        position++;
                    }
                }
                else
                {
                    error = At("invalid JSON value");
                    return false;
                }

                if (Consume('.'))
                {
                    if (position >= text.Length || !IsDigit(text[position]))
                    {
                        error = At("JSON fraction requires a digit");
                        return false;
                    }

                    while (position < text.Length && IsDigit(text[position]))
                    {
                        position++;
                    }
                }

                if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
                {
                    position++;
                    if (position < text.Length && (text[position] == '+' || text[position] == '-'))
                    {
                        position++;
                    }

                    if (position >= text.Length || !IsDigit(text[position]))
                    {
                        error = At("JSON exponent requires a digit");
                        return false;
                    }

                    while (position < text.Length && IsDigit(text[position]))
                    {
                        position++;
                    }
                }

                if (position == start)
                {
                    error = At("invalid JSON number");
                    return false;
                }

                return Success(out error);
            }

            private bool ParseLiteral(string literal, out string error)
            {
                if (position > text.Length - literal.Length ||
                    !string.Equals(text.Substring(position, literal.Length), literal, StringComparison.Ordinal))
                {
                    error = At("invalid JSON literal");
                    return false;
                }

                position += literal.Length;
                return Success(out error);
            }

            private bool CheckDepth(int depth, out string error)
            {
                if (depth > MaxJsonDepth)
                {
                    error = At("snapshot JSON nesting exceeds " + MaxJsonDepth);
                    return false;
                }

                return Success(out error);
            }

            private void SkipWhitespace()
            {
                while (position < text.Length &&
                       (text[position] == ' ' || text[position] == '\t' ||
                        text[position] == '\r' || text[position] == '\n'))
                {
                    position++;
                }
            }

            private bool Consume(char expected)
            {
                if (position >= text.Length || text[position] != expected)
                {
                    return false;
                }

                position++;
                return true;
            }

            private string At(string message)
            {
                return message + " at character " + position + ".";
            }

            private static int HexValue(char value)
            {
                if (value >= '0' && value <= '9')
                {
                    return value - '0';
                }

                if (value >= 'a' && value <= 'f')
                {
                    return value - 'a' + 10;
                }

                return value >= 'A' && value <= 'F' ? value - 'A' + 10 : -1;
            }

            private static bool IsDigit(char value) => value >= '0' && value <= '9';

            private static bool Success(out string error)
            {
                error = string.Empty;
                return true;
            }
        }
    }
}
