using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TopiaForge.Mods
{
    internal sealed class UiThemeDataException : Exception
    {
        private UiThemeDataException(ModErrorCode code, string path, string message)
            : base(path + ": " + message) => Code = code;

        internal ModErrorCode Code { get; }

        internal static UiThemeDataException Invalid(string path, string message) =>
            new UiThemeDataException(ModErrorCode.InvalidArgument, path, message);

        internal static UiThemeDataException Limit(string path, string message) =>
            new UiThemeDataException(ModErrorCode.RateLimited, path, message);

        internal static UiThemeDataException Conflict(string path, string message) =>
            new UiThemeDataException(ModErrorCode.Conflict, path, message);

        internal static UiThemeDataException Io(string path, string message) =>
            new UiThemeDataException(ModErrorCode.Io, path, message);
    }

    internal readonly struct UiThemeJsonObject
    {
        private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();
        private readonly IReadOnlyDictionary<string, object?> fields;

        private UiThemeJsonObject(IReadOnlyDictionary<string, object?> fields, string path)
        {
            this.fields = fields;
            Path = path;
        }

        internal string Path { get; }
        internal string At(string name) => Path + "." + name;

        internal static UiThemeJsonObject From(object? value, string path) => value is Dictionary<string, object?> table
            ? new UiThemeJsonObject(table, path)
            : throw UiThemeDataException.Invalid(path, "Expected an object.");

        internal void Allow(params string[] names)
        {
            foreach (var field in fields.Keys)
            {
                if (System.Array.IndexOf(names, field) < 0)
                {
                    // The logical parent is bounded and never echoes a submitted property name.
                    throw UiThemeDataException.Invalid(Path, "Unknown field.");
                }
            }
        }

        internal UiThemeJsonObject Object(string name) => fields.TryGetValue(name, out var value)
            ? From(value, At(name))
            : new UiThemeJsonObject(Empty, At(name));

        internal IReadOnlyList<object?> Array(string name, int maximum, bool required = false)
        {
            if (!fields.TryGetValue(name, out var value) && !required)
            {
                return System.Array.Empty<object?>();
            }

            if (value is not List<object?> items)
            {
                throw UiThemeDataException.Invalid(At(name), "Expected an array.");
            }

            return items.Count <= maximum
                ? items
                : throw UiThemeDataException.Limit(At(name), "Too many entries.");
        }

        internal bool Has(string name) => fields.ContainsKey(name);

        internal string RequiredString(string name) => OptionalString(name)
            ?? throw UiThemeDataException.Invalid(At(name), "Expected a string.");

        internal string? OptionalString(string name, bool nullable = false)
        {
            if (!fields.TryGetValue(name, out var value) || (nullable && value == null))
            {
                return null;
            }

            return value as string ?? throw UiThemeDataException.Invalid(At(name), "Expected a string.");
        }

        internal int RequiredInteger(string name, int minimum, int maximum) => Integer(name, minimum, maximum)
            ?? throw UiThemeDataException.Invalid(At(name), "Expected an integer.");

        internal int? Integer(string name, int minimum, int maximum)
        {
            if (!fields.TryGetValue(name, out var value))
            {
                return null;
            }

            return value is UiThemeJsonNumber { IsInteger: true } number
                && number.Integer >= minimum && number.Integer <= maximum
                ? number.Integer
                : throw UiThemeDataException.Invalid(At(name), "Integer is outside the permitted range.");
        }

        internal float? Number(string name, double minimum, double maximum)
        {
            if (!fields.TryGetValue(name, out var value))
            {
                return null;
            }

            return value is UiThemeJsonNumber number && number.Value >= minimum && number.Value <= maximum
                ? (float)number.Value
                : throw UiThemeDataException.Invalid(At(name), "Expected a finite number in the permitted range.");
        }

        internal bool? Boolean(string name)
        {
            if (!fields.TryGetValue(name, out var value))
            {
                return null;
            }

            return value is bool flag ? flag : throw UiThemeDataException.Invalid(At(name), "Expected a boolean.");
        }
    }

    internal static class UiThemeData
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly char[] InvalidPathCharacters = { '\\', ':', '<', '>', '"', '|', '?', '*' };

        internal static int HexDigit(char value) => value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1,
        };

        internal static string Hex(ReadOnlySpan<byte> bytes)
        {
            const string digits = "0123456789ABCDEF";
            var text = new char[bytes.Length * 2];
            for (var index = 0; index < bytes.Length; index++)
            {
                text[2 * index] = digits[bytes[index] >> 4];
                text[(2 * index) + 1] = digits[bytes[index] & 15];
            }

            return new string(text);
        }

        internal static string Sha256(string value, string path)
        {
            if (value.Length != 64)
            {
                throw UiThemeDataException.Invalid(path, "Expected a SHA-256 digest in hexadecimal.");
            }

            foreach (var digit in value)
            {
                if (HexDigit(digit) < 0)
                {
                    throw UiThemeDataException.Invalid(path, "Expected a SHA-256 digest in hexadecimal.");
                }
            }

            return value.ToUpperInvariant();
        }

        internal static string Id(string value, string path, bool allowReserved = false)
        {
            if (value.Length < 1 || value.Length > 64 || !IdLetterOrDigit(value[0]))
            {
                throw UiThemeDataException.Invalid(path, "Expected a lowercase theme or resource identifier.");
            }

            foreach (var character in value)
            {
                if (!IdLetterOrDigit(character) && character != '.' && character != '_' && character != '-')
                {
                    throw UiThemeDataException.Invalid(path, "Expected a lowercase theme or resource identifier.");
                }
            }

            if (!allowReserved && value.StartsWith("topiaforge.", StringComparison.Ordinal))
            {
                throw UiThemeDataException.Invalid(path, "The identifier uses a namespace reserved for the kit.");
            }

            return value;
        }

        private static bool IdLetterOrDigit(char character) =>
            character is (>= 'a' and <= 'z') or (>= '0' and <= '9');

        internal static string Path(string value, string path)
        {
            var length = Utf8.GetByteCount(value);
            if (length < 1 || length > 256 || value.IndexOfAny(InvalidPathCharacters) >= 0)
            {
                throw UiThemeDataException.Invalid(path, "Expected a normalized package-relative path.");
            }

            foreach (var character in value)
            {
                if (char.IsControl(character))
                {
                    throw UiThemeDataException.Invalid(path, "Expected a normalized package-relative path.");
                }
            }

            foreach (var segment in value.Split('/'))
            {
                if (segment.Length == 0 || segment.EndsWith(".", StringComparison.Ordinal)
                    || segment.EndsWith(" ", StringComparison.Ordinal) || IsDeviceName(segment))
                {
                    throw UiThemeDataException.Invalid(path, "Expected a normalized package-relative path.");
                }
            }

            return value;
        }

        private static bool IsDeviceName(string segment)
        {
            var dot = segment.IndexOf('.');
            var stem = (dot < 0 ? segment : segment.Substring(0, dot)).TrimEnd(' ').ToUpperInvariant();
            return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && stem[3] is (>= '1' and <= '9') or '\u00b9' or '\u00b2' or '\u00b3');
        }

        internal static string ListedName(string value, string path, string[] names, out int index)
        {
            index = Array.IndexOf(names, value);
            return index >= 0 ? value : throw UiThemeDataException.Invalid(path, "Unknown named value.");
        }

        internal static string Entry(string path, int index) =>
            path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
    }
}
