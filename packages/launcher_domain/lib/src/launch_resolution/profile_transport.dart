part of '../launch_resolution.dart';

/// Wire v4 is deliberately inactive until all production surfaces switch together.
final class ProfileLaunchConfigurationV4 {
  ProfileLaunchConfigurationV4({
    required String profileId,
    required int profileRevision,
    required String requestId,
    required String command,
    required bool safeMode,
    required this.inheritManagerModState,
    required Iterable<String> enabledMods,
    required Map<String, String> selectedVersions,
    required Iterable<PackageIdentity> packages,
    String? digest,
    this.plan,
  }) : profileId = _token(profileId),
       profileRevision = _integer(profileRevision),
       requestId = _token(requestId),
       command = _choice(command, const ['main-menu', 'launch-target']),
       safeMode = safeMode,
       enabledMods = _ids(enabledMods.toList(), packages: true),
       selectedVersions = _selectedVersions(selectedVersions),
       packages = _identities(packages) {
    this.digest = packageSetDigest(this.packages);
    final actual = this.packages.map((item) => item.id.toLowerCase()).toSet();
    final enabled = this.enabledMods.map((id) => id.toLowerCase()).toSet();
    if ((!inheritManagerModState &&
            !safeMode &&
            (actual.length != enabled.length ||
                !actual.containsAll(enabled))) ||
        this.packages.any(
          (item) => this.selectedVersions.entries.any(
            (pin) => _idEquals(pin.key, item.id) && pin.value != item.version,
          ),
        )) {
      throw const FormatException(
        'Profile selections disagree with package identities.',
      );
    }
    if ((digest != null && _digest(digest) != this.digest) ||
        (this.command == 'main-menu' && plan != null) ||
        (this.command == 'launch-target' && plan == null) ||
        (safeMode && this.command != 'main-menu') ||
        (plan != null &&
            (!_samePackages(this.packages, plan!.packages) ||
                plan!.digest != this.digest))) {
      throw const FormatException('Inconsistent profile launch command.');
    }
  }
  factory ProfileLaunchConfigurationV4.fromJson(Object? value) {
    const required = {
      'schemaVersion',
      'profileId',
      'profileRevision',
      'requestId',
      'command',
      'safeMode',
      'inheritManagerModState',
      'enabledMods',
      'selectedVersions',
      'packages',
      'digest',
    };
    final json = _object(value, {...required, 'plan'}, required);
    if (_integer(json['schemaVersion']) != 4) {
      throw const FormatException('Unsupported launch profile version.');
    }
    final versions = json['selectedVersions'];
    if (versions is! Map<String, Object?>) {
      throw const FormatException('Invalid selected versions.');
    }
    return ProfileLaunchConfigurationV4(
      profileId: _token(json['profileId']),
      profileRevision: _integer(json['profileRevision']),
      requestId: _token(json['requestId']),
      command: _text(json['command']),
      safeMode: _boolean(json['safeMode']),
      inheritManagerModState: _boolean(json['inheritManagerModState']),
      enabledMods: _ids(json['enabledMods'], packages: true),
      selectedVersions: versions.map(
        (key, value) => MapEntry(key, _version(value)),
      ),
      packages: _list(json['packages'], PackageIdentity.fromJson),
      digest: _digest(json['digest']),
      plan: json.containsKey('plan')
          ? LaunchPlanDescriptor.fromJson(json['plan'])
          : null,
    );
  }
  final String profileId;
  final int profileRevision;
  final String requestId;
  final String command;
  final bool safeMode;
  final bool inheritManagerModState;
  final List<String> enabledMods;
  final Map<String, String> selectedVersions;
  final List<PackageIdentity> packages;
  late final String digest;
  final LaunchPlanDescriptor? plan;
  Map<String, Object?> toJson() => {
    'schemaVersion': 4,
    'profileId': profileId,
    'profileRevision': profileRevision,
    'requestId': requestId,
    'command': command,
    'safeMode': safeMode,
    'inheritManagerModState': inheritManagerModState,
    'enabledMods': enabledMods,
    'selectedVersions': selectedVersions,
    'packages': packages.map((item) => item.toJson()).toList(),
    'digest': digest,
    if (plan != null) 'plan': plan!.toJson(),
  };
}

Map<String, String> _selectedVersions(Map<String, String> source) {
  if (source.length > 4096) {
    throw const FormatException('Excessive selected versions.');
  }
  final seen = <String>{};
  final keys = source.keys.toList()..sort();
  final result = <String, String>{};
  for (final key in keys) {
    if (!seen.add(_packageId(key).toLowerCase())) {
      throw const FormatException('Duplicate selected package id.');
    }
    result[key] = _version(source[key]);
  }
  return Map.unmodifiable(result);
}
