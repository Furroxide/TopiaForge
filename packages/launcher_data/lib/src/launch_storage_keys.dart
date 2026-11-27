import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';

/// Portable storage names bind exact ordinal wire identities.
abstract final class LaunchStorageKeys {
  static String request(String requestId) {
    final units = requestId.codeUnits;
    bool alphaNumeric(int c) =>
        c >= 65 && c <= 90 || c >= 97 && c <= 122 || c >= 48 && c <= 57;
    if (units.isEmpty ||
        units.length > 128 ||
        !alphaNumeric(units.first) ||
        units.any((c) => !alphaNumeric(c) && c != 46 && c != 45 && c != 95)) {
      throw const FormatException('Invalid launch request identifier.');
    }
    return sha256.convert(utf8.encode(requestId)).toString();
  }

  static String observation(LaunchObservationEnvelope value) => sha256
      .convert(
        utf8.encode(
          jsonEncode([
            value.profileId,
            value.profileRevision,
            value.producer.id,
            value.producer.version,
            value.packageSetDigest,
          ]),
        ),
      )
      .toString();
}
