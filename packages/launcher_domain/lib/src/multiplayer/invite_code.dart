part of '../multiplayer.dart';

/// A 10-character invite code in Crockford base32 (50 random bits), as the
/// control plane issues them.
///
/// Reading is forgiving: ASCII case, hyphens and spaces are ignored, O reads
/// as 0, and I and L read as 1. The canonical spelling is upper case without
/// separators, and people see it as `XXXXX-XXXXX`. Only ASCII letters change
/// case, so no other script's letter can stand in for one of the alphabet's.
abstract final class InviteCode {
  static const length = 10;
  static const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

  /// The longest input [tryNormalize] reads, separators included.
  static const maximumInputLength = 32;

  /// The canonical code [input] spells, or null when it spells none.
  static String? tryNormalize(String? input) {
    if (input == null || input.length > maximumInputLength) return null;
    final units = <int>[];
    for (var index = 0; index < input.length; index++) {
      var unit = input.codeUnitAt(index);
      if (unit == 0x2D || unit == 0x20) continue;
      if (unit >= 0x61 && unit <= 0x7A) unit -= 0x20;
      if (unit == 0x4F) {
        unit = 0x30;
      } else if (unit == 0x49 || unit == 0x4C) {
        unit = 0x31;
      }
      if (units.length == length ||
          unit > 0x7F ||
          !alphabet.contains(String.fromCharCode(unit))) {
        return null;
      }
      units.add(unit);
    }
    return units.length == length ? String.fromCharCodes(units) : null;
  }

  /// Whether [code] is already canonical.
  static bool isCanonical(String? code) =>
      code != null && tryNormalize(code) == code;

  /// Formats a canonical code for people: two groups of five.
  static String display(String code) {
    if (!isCanonical(code)) {
      throw ArgumentError('A canonical invite code is required.', 'code');
    }
    return '${code.substring(0, 5)}-${code.substring(5)}';
  }
}
