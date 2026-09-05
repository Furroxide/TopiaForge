part of '../launch_resolution.dart';

final class LaunchRequest {
  LaunchRequest({
    required String targetId,
    String? worldOverride,
    String? transitionOverride,
  }) : targetId = _declaration(targetId),
       worldOverride = worldOverride == null
           ? null
           : _declaration(worldOverride),
       transitionOverride = transitionOverride == null
           ? null
           : _choice(transitionOverride, ModTransitions.byPrecedence);
  factory LaunchRequest.fromJson(Object? value) {
    final json = _object(
      value,
      {'targetId', 'worldOverride', 'transitionOverride'},
      {'targetId'},
    );
    return LaunchRequest(
      targetId: _declaration(json['targetId']),
      worldOverride: json.containsKey('worldOverride')
          ? _declaration(json['worldOverride'])
          : null,
      transitionOverride: json.containsKey('transitionOverride')
          ? _choice(json['transitionOverride'], ModTransitions.byPrecedence)
          : null,
    );
  }
  final String targetId;
  final String? worldOverride;
  final String? transitionOverride;
  Map<String, Object?> toJson() => {
    'targetId': targetId,
    if (worldOverride != null) 'worldOverride': worldOverride,
    if (transitionOverride != null) 'transitionOverride': transitionOverride,
  };
}

/// Inactive transport descriptor; every launch is re-resolved before execution.
final class LaunchPlanDescriptor {
  LaunchPlanDescriptor({
    required String targetId,
    required String gamemodeId,
    required String worldId,
    String? worldFamilyId,
    required String transition,
    required LaunchRequest request,
    required Iterable<PackageIdentity> packages,
    String? digest,
  }) : targetId = _declaration(targetId),
       gamemodeId = _declaration(gamemodeId),
       worldId = _declaration(worldId),
       worldFamilyId = worldFamilyId == null
           ? null
           : _declaration(worldFamilyId),
       transition = _choice(transition, ModTransitions.byPrecedence),
       request = LaunchRequest.fromJson(request.toJson()),
       packages = _identities(packages) {
    if (!_idEquals(this.targetId, this.request.targetId) ||
        (this.worldFamilyId != null &&
            (!this.worldId.toLowerCase().startsWith(
                  '${this.worldFamilyId!.toLowerCase()}.',
                ) ||
                this.worldId.length <= this.worldFamilyId!.length + 1))) {
      throw const FormatException('Inconsistent launch identities.');
    }
    this.digest = packageSetDigest(this.packages);
    if (digest != null && _digest(digest) != this.digest) {
      throw const FormatException('Package digest does not match identities.');
    }
  }
  factory LaunchPlanDescriptor.fromJson(Object? value) {
    final json = _object(
      value,
      {
        'targetId',
        'gamemodeId',
        'worldId',
        'worldFamilyId',
        'transition',
        'request',
        'packages',
        'digest',
      },
      {
        'targetId',
        'gamemodeId',
        'worldId',
        'transition',
        'request',
        'packages',
        'digest',
      },
    );
    return LaunchPlanDescriptor(
      targetId: _declaration(json['targetId']),
      gamemodeId: _declaration(json['gamemodeId']),
      worldId: _declaration(json['worldId']),
      worldFamilyId: json.containsKey('worldFamilyId')
          ? _declaration(json['worldFamilyId'])
          : null,
      transition: _choice(json['transition'], ModTransitions.byPrecedence),
      request: LaunchRequest.fromJson(json['request']),
      packages: _list(json['packages'], PackageIdentity.fromJson),
      digest: _digest(json['digest']),
    );
  }
  final String targetId;
  final String gamemodeId;
  final String worldId;
  final String? worldFamilyId;
  final String transition;
  final LaunchRequest request;
  final List<PackageIdentity> packages;
  late final String digest;
  String get launchTargetId => targetId;
  List<PackageIdentity> get resolvedPackages => packages;
  Map<String, Object?> toJson() => {
    'targetId': targetId,
    'gamemodeId': gamemodeId,
    'worldId': worldId,
    if (worldFamilyId != null) 'worldFamilyId': worldFamilyId,
    'transition': transition,
    'request': request.toJson(),
    'packages': packages.map((item) => item.toJson()).toList(),
    'digest': digest,
  };
}

/// Only the resolver creates authority, with copied selected declarations.
final class LaunchPlan extends LaunchPlanDescriptor {
  LaunchPlan._({
    required super.targetId,
    required super.gamemodeId,
    required super.worldId,
    super.worldFamilyId,
    required super.transition,
    required super.request,
    required super.packages,
    required ModLaunchTargetDeclaration target,
    required ModGamemodeDeclaration gamemode,
    required ModWorldDeclaration world,
  }) : _targetJson = jsonEncode(target.toJson()),
       _gamemodeJson = jsonEncode(gamemode.toJson()),
       _worldJson = jsonEncode(world.toJson());
  final String _targetJson;
  final String _gamemodeJson;
  final String _worldJson;
  ModLaunchTargetDeclaration get target =>
      ModLaunchTargetDeclaration.fromJson(jsonDecode(_targetJson));
  ModGamemodeDeclaration get gamemode =>
      ModGamemodeDeclaration.fromJson(jsonDecode(_gamemodeJson));
  ModWorldDeclaration get world =>
      ModWorldDeclaration.fromJson(jsonDecode(_worldJson));
  LaunchPlanDescriptor get descriptor => LaunchPlanDescriptor(
    targetId: targetId,
    gamemodeId: gamemodeId,
    worldId: worldId,
    worldFamilyId: worldFamilyId,
    transition: transition,
    request: request,
    packages: packages,
    digest: digest,
  );
}

/// The established FNV-1a digest over sorted id@version UTF-16LE code units.
/// Entries are separated by one LF byte. This is a consistency check only.
String packageSetDigest(Iterable<PackageIdentity> packages) {
  final entries = packages.map((item) => '${item.id}@${item.version}').toList()
    ..sort();
  return packageSetDigestOfCanonical(entries);
}

String packageSetDigestOfCanonical(List<String> entries) {
  var hash = BigInt.parse('14695981039346656037');
  final prime = BigInt.parse('1099511628211');
  final mask = (BigInt.one << 64) - BigInt.one;
  void mix(int byte) {
    hash = ((hash ^ BigInt.from(byte)) * prime) & mask;
  }

  for (var i = 0; i < entries.length; i++) {
    if (i > 0) mix(10);
    for (final unit in entries[i].codeUnits) {
      mix(unit & 255);
      mix((unit >> 8) & 255);
    }
  }
  return hash.toRadixString(16).padLeft(16, '0');
}
