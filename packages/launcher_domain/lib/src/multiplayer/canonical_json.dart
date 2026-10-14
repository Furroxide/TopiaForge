part of '../multiplayer.dart';

/// Refusal of a document that is not canonical JSON, or of a value that
/// canonical JSON cannot represent.
final class CanonicalJsonException extends FormatException {
  const CanonicalJsonException(super.message);
}

/// Canonical JSON for signed and shared multiplayer documents: the subset of
/// RFC 8785 that the game, the launcher and the control plane all produce
/// byte for byte (`docs/internal/MultiplayerLiveSessions.md`, section 2).
///
/// - Values are objects, arrays, strings, `true`, `false`, `null` and
///   integers within plus or minus 2^53 - 1. There are no fractions,
///   exponents or `-0`, and Dart doubles are refused even when integral.
/// - Keys are 1 to 64 ASCII letters and digits, written in ordinal order.
/// - There is no white space.
/// - Strings escape only the quotation mark, the backslash and U+0000 to
///   U+001F (`\b \t \n \f \r` where they exist, otherwise lowercase
///   `\u00xx`). Everything else, U+007F and non-ASCII included, is UTF-8.
///   Unpaired surrogates are refused.
///
/// The writer is written out here rather than built on `jsonEncode`, which
/// keeps insertion order, writes doubles and escapes lone surrogates instead
/// of refusing them. [decode] accepts a document only if writing what it read
/// reproduces the same bytes, so a signature never covers an ambiguous
/// document.
abstract final class CanonicalJson {
  static const maximumSafeInteger = 9007199254740991;
  static const maximumDepth = 16;
  static const maximumKeyLength = 64;

  /// Whether [key] is 1 to 64 ASCII letters and digits.
  static bool isKey(String key) {
    if (key.isEmpty || key.length > maximumKeyLength) return false;
    for (var index = 0; index < key.length; index++) {
      final unit = key.codeUnitAt(index);
      final letter =
          (unit >= 0x61 && unit <= 0x7A) || (unit >= 0x41 && unit <= 0x5A);
      final digit = unit >= 0x30 && unit <= 0x39;
      if (!letter && !digit) return false;
    }
    return true;
  }

  /// Writes [value] as canonical UTF-8 bytes. Maps need [String] keys that
  /// pass [isKey]; values may be maps, lists, strings, [int]s, [bool]s or
  /// null.
  static Uint8List encode(Object? value) =>
      Uint8List.fromList(utf8.encode(encodeToString(value)));

  /// Writes [value] as canonical text, before UTF-8 encoding.
  static String encodeToString(Object? value) {
    final buffer = StringBuffer();
    _write(buffer, value, 0);
    return buffer.toString();
  }

  /// Reads [bytes] as one canonical document of at most [maximumBytes] bytes,
  /// returning unmodifiable maps and lists. Throws [CanonicalJsonException]
  /// for anything else: malformed UTF-8, a byte-order mark, white space,
  /// unsorted or repeated keys, fractions, out-of-range integers, needless
  /// escapes, unpaired surrogates, nesting past [maximumDepth], or trailing
  /// content.
  static Object? decode(List<int> bytes, {required int maximumBytes}) {
    if (bytes.isEmpty || bytes.length > maximumBytes) {
      throw CanonicalJsonException(
        'The document is empty or larger than $maximumBytes bytes.',
      );
    }
    if (bytes.length >= 3 &&
        bytes[0] == 0xEF &&
        bytes[1] == 0xBB &&
        bytes[2] == 0xBF) {
      throw const CanonicalJsonException(
        'The document starts with a byte-order mark.',
      );
    }
    final String text;
    try {
      text = utf8.decode(bytes);
    } on FormatException {
      throw const CanonicalJsonException('The document is not valid UTF-8.');
    }
    final reader = _CanonicalJsonReader(text);
    final value = reader.readValue(0);
    if (!reader.atEnd) {
      throw const CanonicalJsonException(
        'Unexpected content after the document.',
      );
    }
    if (!_sameBytes(encode(value), bytes)) {
      throw const CanonicalJsonException(
        'The document is not in canonical form.',
      );
    }
    return value;
  }

  static void _write(StringBuffer buffer, Object? value, int depth) {
    if (depth > maximumDepth) {
      throw const CanonicalJsonException('The document nests too deeply.');
    }
    switch (value) {
      case null:
        buffer.write('null');
      case bool():
        buffer.write(value ? 'true' : 'false');
      case int():
        if (value > maximumSafeInteger || value < -maximumSafeInteger) {
          throw CanonicalJsonException(
            'The integer $value is outside the exactly representable range.',
          );
        }
        buffer.write(value);
      case String():
        _writeString(buffer, value);
      case List<Object?>():
        buffer.write('[');
        for (var index = 0; index < value.length; index++) {
          if (index > 0) buffer.write(',');
          _write(buffer, value[index], depth + 1);
        }
        buffer.write(']');
      case Map<Object?, Object?>():
        final keys = <String>[];
        for (final key in value.keys) {
          if (key is! String || !isKey(key)) {
            throw CanonicalJsonException(
              'The key $key is not 1 to 64 ASCII letters and digits.',
            );
          }
          keys.add(key);
        }
        keys.sort();
        buffer.write('{');
        for (var index = 0; index < keys.length; index++) {
          if (index > 0) buffer.write(',');
          _writeString(buffer, keys[index]);
          buffer.write(':');
          _write(buffer, value[keys[index]], depth + 1);
        }
        buffer.write('}');
      default:
        throw CanonicalJsonException(
          'A ${value.runtimeType} cannot be written as canonical JSON.',
        );
    }
  }

  static void _writeString(StringBuffer buffer, String value) {
    buffer.write('"');
    for (var index = 0; index < value.length; index++) {
      final unit = value.codeUnitAt(index);
      switch (unit) {
        case 0x22:
          buffer.write(r'\"');
        case 0x5C:
          buffer.write(r'\\');
        case 0x08:
          buffer.write(r'\b');
        case 0x0C:
          buffer.write(r'\f');
        case 0x0A:
          buffer.write(r'\n');
        case 0x0D:
          buffer.write(r'\r');
        case 0x09:
          buffer.write(r'\t');
        default:
          if (unit < 0x20) {
            buffer
              ..write(r'\u00')
              ..write(unit.toRadixString(16).padLeft(2, '0'));
          } else if (_isHighSurrogate(unit) &&
              index + 1 < value.length &&
              _isLowSurrogate(value.codeUnitAt(index + 1))) {
            buffer.write(value.substring(index, index + 2));
            index++;
          } else if (_isSurrogate(unit)) {
            throw const CanonicalJsonException(
              'A string holds an unpaired surrogate.',
            );
          } else {
            buffer.writeCharCode(unit);
          }
      }
    }
    buffer.write('"');
  }
}
