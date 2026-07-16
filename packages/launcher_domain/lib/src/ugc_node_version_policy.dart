import 'versioning.dart';

/// The supported Node.js runtime floor for the networked Automerge sidecar.
abstract final class UgcNodeVersionPolicy {
  static const minimumVersion = '24.16.0';
  static const requirement = 'Node.js 24.16.0 or newer';

  static final SemanticVersion _minimum = SemanticVersion.parse(minimumVersion);

  /// Parses ordinary `node --version` output and enforces the supported floor.
  static bool supports(String output) {
    final match = RegExp(
      r'^v?([0-9]+\.[0-9]+\.[0-9]+)$',
    ).firstMatch(output.trim());
    final version = SemanticVersion.tryParse(match?.group(1));
    return version != null && version.compareTo(_minimum) >= 0;
  }
}
