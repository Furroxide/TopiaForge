using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TopiaForge.Mods
{
    // A bounded reader for the data-only theme format. It also locates ui in a manifest without
    // interpreting or retaining the values of any other manifest fields.
    internal sealed class UiThemeJson
    {
        internal const int MaximumBytes = 64 * 1024;
        private const int MaximumThemeDepth = 16;
        private const int MaximumItems = 64;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly string source;
        private int position;

        private UiThemeJson(ReadOnlySpan<byte> utf8, int maximumBytes, string path)
        {
            if (utf8.Length > maximumBytes)
            {
                throw UiThemeDataException.Limit(path, "JSON exceeds its UTF-8 size limit.");
            }

            try
            {
                source = Utf8.GetString(utf8);
            }
            catch (DecoderFallbackException)
            {
                throw UiThemeDataException.Invalid(path, "Expected strict UTF-8.");
            }
        }

        internal static UiThemeJsonObject Read(ReadOnlySpan<byte> utf8, string path)
        {
            var reader = new UiThemeJson(utf8, MaximumBytes, path);
            var value = reader.Value(0, MaximumThemeDepth, true, path);
            reader.End(path);
            return UiThemeJsonObject.From(value, path);
        }

        internal static UiThemeJsonObject? ReadManifestUi(ReadOnlySpan<byte> utf8)
        {
            var reader = new UiThemeJson(utf8, 1024 * 1024, "$");
            if (reader.Current == '\uFEFF')
            {
                reader.position++;
            }

            reader.Require('{', "$");
            UiThemeJsonObject? ui = null;
            if (!reader.Consume('}'))
            {
                var fields = new HashSet<string>(StringComparer.Ordinal);
                do
                {
                    var field = reader.String("$");
                    if (!fields.Add(field))
                    {
                        throw UiThemeDataException.Invalid(field == "ui" ? "$.ui" : "$", "Duplicate field.");
                    }

                    if (fields.Count > MaximumItems)
                    {
                        throw UiThemeDataException.Limit("$", "Manifest exceeds its field limit.");
                    }

                    reader.Require(':', "$");
                    if (field == "ui")
                    {
                        var start = reader.position;
                        ui = UiThemeJsonObject.From(reader.Value(0, MaximumThemeDepth, true, "$.ui"), "$.ui");
                        if (Utf8.GetByteCount(reader.source.AsSpan(start, reader.position - start)) > MaximumBytes)
                        {
                            throw UiThemeDataException.Limit("$.ui", "JSON exceeds its UTF-8 size limit.");
                        }
                    }
                    else
                    {
                        reader.Value(1, 128, false, "$");
                    }
                } while (reader.Consume(','));

                reader.Require('}', "$");
            }

            reader.End("$");
            return ui;
        }

        private object? Value(int depth, int limit, bool retain, string path)
        {
            Whitespace();
            var token = Current;
            if ((token == '{' || token == '[') && depth >= limit)
            {
                throw UiThemeDataException.Limit(path, "JSON exceeds its nesting limit.");
            }

            return token switch
            {
                '{' => Object(depth + 1, limit, retain, path),
                '[' => Array(depth + 1, limit, retain, path),
                '"' => String(path),
                't' => Literal("true", true, path),
                'f' => Literal("false", false, path),
                'n' => Literal("null", null, path),
                _ => Number(path),
            };
        }

        private Dictionary<string, object?>? Object(int depth, int limit, bool retain, string path)
        {
            position++;
            var values = retain ? new Dictionary<string, object?>(StringComparer.Ordinal) : null;
            if (!Consume('}'))
            {
                do
                {
                    if (values?.Count == MaximumItems)
                    {
                        throw UiThemeDataException.Limit(path, "JSON object exceeds its field limit.");
                    }

                    var field = String(path);
                    if (values != null && values.ContainsKey(field))
                    {
                        throw UiThemeDataException.Invalid(path, "Duplicate field.");
                    }

                    Require(':', path);
                    var value = Value(depth, limit, retain, path);
                    values?.Add(field, value);
                } while (Consume(','));

                Require('}', path);
            }

            return values;
        }

        private List<object?>? Array(int depth, int limit, bool retain, string path)
        {
            position++;
            var values = retain ? new List<object?>() : null;
            if (!Consume(']'))
            {
                do
                {
                    if (values?.Count == MaximumItems)
                    {
                        throw UiThemeDataException.Limit(path, "JSON array exceeds its entry limit.");
                    }

                    var value = Value(depth, limit, retain, path);
                    values?.Add(value);
                } while (Consume(','));

                Require(']', path);
            }

            return values;
        }

        private string String(string path)
        {
            Require('"', path);
            var start = position;
            StringBuilder? decoded = null;
            while (position < source.Length)
            {
                var character = source[position++];
                if (character == '"')
                {
                    if (decoded == null)
                    {
                        return source.Substring(start, position - start - 1);
                    }

                    decoded.Append(source, start, position - start - 1);
                    var text = decoded.ToString();
                    try
                    {
                        Utf8.GetByteCount(text); // Reject isolated UTF-16 surrogates in JSON escapes.
                        return text;
                    }
                    catch (EncoderFallbackException)
                    {
                        throw UiThemeDataException.Invalid(path, "Invalid Unicode escape.");
                    }
                }

                if (character < ' ')
                {
                    throw UiThemeDataException.Invalid(path, "Invalid JSON string.");
                }

                if (character == '\\')
                {
                    decoded ??= new StringBuilder();
                    decoded.Append(source, start, position - start - 1).Append(Escape(path));
                    start = position;
                }
            }

            throw UiThemeDataException.Invalid(path, "Unterminated JSON string.");
        }

        private char Escape(string path)
        {
            if (position < source.Length)
            {
                var escaped = source[position++];
                switch (escaped)
                {
                    case '"': case '\\': case '/': return escaped;
                    case 'b': return '\b';
                    case 'f': return '\f';
                    case 'n': return '\n';
                    case 'r': return '\r';
                    case 't': return '\t';
                    case 'u' when position <= source.Length - 4:
                        var value = 0;
                        for (var digit = 0; digit < 4; digit++)
                        {
                            var hex = UiThemeData.HexDigit(source[position++]);
                            if (hex < 0)
                            {
                                throw UiThemeDataException.Invalid(path, "Invalid Unicode escape.");
                            }

                            value = (value * 16) + hex;
                        }

                        return (char)value;
                }
            }

            throw UiThemeDataException.Invalid(path, "Invalid JSON escape.");
        }

        private object? Literal(string text, object? value, string path)
        {
            if (!source.AsSpan(position).StartsWith(text.AsSpan(), StringComparison.Ordinal))
            {
                throw UiThemeDataException.Invalid(path, "Invalid JSON literal.");
            }

            position += text.Length;
            return value;
        }

        private UiThemeJsonNumber Number(string path)
        {
            var start = position;
            if (Current == '-')
            {
                position++;
            }

            if (Current == '0')
            {
                position++;
            }
            else
            {
                Digits(path);
            }

            if (Current == '.')
            {
                position++;
                Digits(path);
            }

            if (Current == 'e' || Current == 'E')
            {
                position++;
                if (Current == '+' || Current == '-')
                {
                    position++;
                }

                Digits(path);
            }

            // Keep the token's syntax separate from strings, including numbers outside the finite range.
            var token = source.AsSpan(start, position - start);
            var parsed = double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
            return new UiThemeJsonNumber(parsed ? number : double.NaN, token);
        }

        private void Digits(string path)
        {
            if (Current < '0' || Current > '9')
            {
                throw UiThemeDataException.Invalid(path, "Expected a JSON value.");
            }

            do
            {
                position++;
            } while (Current >= '0' && Current <= '9');
        }

        private char Current => position < source.Length ? source[position] : '\0';

        private void Whitespace()
        {
            while (Current == ' ' || Current == '\t' || Current == '\r' || Current == '\n')
            {
                position++;
            }
        }

        private bool Consume(char token)
        {
            Whitespace();
            if (Current != token)
            {
                return false;
            }

            position++;
            return true;
        }

        private void Require(char token, string path)
        {
            if (!Consume(token))
            {
                throw UiThemeDataException.Invalid(path, "Invalid JSON structure.");
            }
        }

        private void End(string path)
        {
            Whitespace();
            if (position != source.Length)
            {
                throw UiThemeDataException.Invalid(path, "Unexpected data after the JSON value.");
            }
        }
    }

    internal readonly struct UiThemeJsonNumber
    {
        internal UiThemeJsonNumber(double value, ReadOnlySpan<char> token)
        {
            Value = value;
            IsInteger = int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer);
            Integer = integer;
        }

        internal double Value { get; }
        internal bool IsInteger { get; }
        internal int Integer { get; }
    }
}
