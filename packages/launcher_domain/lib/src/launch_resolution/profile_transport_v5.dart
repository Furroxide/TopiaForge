part of '../launch_resolution.dart';

/// One-shot launch request V5: everything V4 carries, for a host-session or
/// join-session launch, and the [session] to host or join.
///
/// Only those two launches write V5; every other launch stages
/// [ProfileLaunchConfigurationV4] exactly as before ([StagedLaunchRequest]).
///
/// - Safe mode never hosts or joins, so V5 has no `safeMode`.
/// - A host may start a launch target first, carried as [plan].
/// - A join never carries a plan, since the host's session decides what
///   loads, and never inherits the manager's mod state, since it runs
///   exactly the packages it planned.
/// - It is written as sorted JSON (see `session_values.dart`).
final class ProfileLaunchConfigurationV5 {
  static const environmentVariable =
      ProfileLaunchConfigurationV4.environmentVariable;
  static const schemaVersion = 5;

  ProfileLaunchConfigurationV5({
    required String profileId,
    required int profileRevision,
    required String requestId,
    required bool inheritManagerModState,
    required Iterable<String> enabledMods,
    required Map<String, String> selectedVersions,
    required Iterable<PackageIdentity> packages,
    String? digest,
    LaunchPlanDescriptor? plan,
    required this.session,
  }) : executable = ProfileLaunchConfigurationV4(
         profileId: profileId,
         profileRevision: profileRevision,
         requestId: requestId,
         command: plan == null ? 'main-menu' : 'launch-target',
         safeMode: false,
         inheritManagerModState: inheritManagerModState,
         enabledMods: enabledMods,
         selectedVersions: selectedVersions,
         packages: packages,
         digest: digest,
         plan: plan,
       ) {
    if (session.role == LaunchSessionRole.join &&
        (plan != null || inheritManagerModState)) {
      throw const FormatException(
        'A join runs exactly its planned packages and carries no plan.',
      );
    }
  }

  /// The session launch of [profile]: the same profile, packages and plan,
  /// with [session]. Safe mode never hosts or joins.
  factory ProfileLaunchConfigurationV5.forProfile(
    ProfileLaunchConfigurationV4 profile,
    LaunchSessionRequest session,
  ) {
    if (profile.safeMode) {
      throw const FormatException('Safe mode never hosts or joins.');
    }
    return ProfileLaunchConfigurationV5(
      profileId: profile.profileId,
      profileRevision: profile.profileRevision,
      requestId: profile.requestId,
      inheritManagerModState: profile.inheritManagerModState,
      enabledMods: profile.enabledMods,
      selectedVersions: profile.selectedVersions,
      packages: profile.packages,
      digest: profile.digest,
      plan: profile.plan,
      session: session,
    );
  }

  factory ProfileLaunchConfigurationV5.fromJson(Object? value) {
    const required = {
      'command',
      'digest',
      'enabledMods',
      'inheritManagerModState',
      'packages',
      'profileId',
      'profileRevision',
      'requestId',
      'schemaVersion',
      'selectedVersions',
      'session',
    };
    final json = _object(value, {...required, 'plan'}, required);
    if (_integer(json['schemaVersion']) != schemaVersion) {
      throw const FormatException('Unsupported launch profile version.');
    }
    final versions = json['selectedVersions'];
    if (versions is! Map<String, Object?>) {
      throw const FormatException('Invalid selected versions.');
    }
    final command = _choice(json['command'], sessionLaunchCommands);
    final result = ProfileLaunchConfigurationV5(
      profileId: _token(json['profileId']),
      profileRevision: _integer(json['profileRevision']),
      requestId: _token(json['requestId']),
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
      session: LaunchSessionRequest.fromJson(json['session']),
    );
    if (command != result.command) {
      throw const FormatException('The command disagrees with the session.');
    }
    return result;
  }

  /// What the runtime executes before the session starts: the host's launch
  /// target, or the main menu. The game runs this V4 view through its
  /// existing launch path and hands [session] to the multiplayer provider.
  final ProfileLaunchConfigurationV4 executable;
  final LaunchSessionRequest session;

  String get command => session.command;
  String get profileId => executable.profileId;
  int get profileRevision => executable.profileRevision;
  String get requestId => executable.requestId;
  bool get inheritManagerModState => executable.inheritManagerModState;
  List<String> get enabledMods => executable.enabledMods;
  Map<String, String> get selectedVersions => executable.selectedVersions;
  List<PackageIdentity> get packages => executable.packages;
  String get digest => executable.digest;
  LaunchPlanDescriptor? get plan => executable.plan;

  Map<String, Object?> toJson() =>
      _sorted({
            'command': command,
            'digest': digest,
            'enabledMods': enabledMods,
            'inheritManagerModState': inheritManagerModState,
            'packages': [for (final item in packages) item.toJson()],
            if (plan != null) 'plan': plan!.toJson(),
            'profileId': profileId,
            'profileRevision': profileRevision,
            'requestId': requestId,
            'schemaVersion': schemaVersion,
            'selectedVersions': selectedVersions,
            'session': session.toJson(),
          })!
          as Map<String, Object?>;

  @override
  String toString() => 'launch request V5 $requestId ($command)';
}

/// The one-shot request a launch stages, and the only place that chooses its
/// version: V5 for a host-session or join-session launch, V4 for every other
/// launch, written byte for byte as it was before V5 existed.
sealed class StagedLaunchRequest {
  const StagedLaunchRequest._();

  /// The request for launching [profile]: V4 unless [session] asks to host or
  /// join.
  factory StagedLaunchRequest.forLaunch(
    ProfileLaunchConfigurationV4 profile, {
    LaunchSessionRequest? session,
  }) => session == null
      ? StagedV4LaunchRequest(profile)
      : StagedV5LaunchRequest(
          ProfileLaunchConfigurationV5.forProfile(profile, session),
        );

  /// Reads a staged request of either version.
  factory StagedLaunchRequest.decode(String json) {
    final value = _decodeContract(json, LaunchTransport.maxDocumentBytes);
    return value is Map<String, Object?> &&
            value['schemaVersion'] == ProfileLaunchConfigurationV5.schemaVersion
        ? StagedV5LaunchRequest(ProfileLaunchConfigurationV5.fromJson(value))
        : StagedV4LaunchRequest(ProfileLaunchConfigurationV4.fromJson(value));
  }

  int get schemaVersion;

  /// The profile the runtime executes before any session starts.
  ProfileLaunchConfigurationV4 get executable;

  /// The session to host or join, for a V5 request.
  LaunchSessionRequest? get session;

  /// `main-menu` or `launch-target` for V4; `host-session` or `join-session`
  /// for V5.
  String get command;

  String get requestId => executable.requestId;
  String get digest => executable.digest;

  /// The exact text staged for the game.
  String encode();
}

final class StagedV4LaunchRequest extends StagedLaunchRequest {
  const StagedV4LaunchRequest(this.profile) : super._();

  final ProfileLaunchConfigurationV4 profile;

  @override
  int get schemaVersion => 4;

  @override
  ProfileLaunchConfigurationV4 get executable => profile;

  @override
  LaunchSessionRequest? get session => null;

  @override
  String get command => profile.command;

  @override
  String encode() => LaunchTransport.writeProfile(profile);
}

final class StagedV5LaunchRequest extends StagedLaunchRequest {
  const StagedV5LaunchRequest(this.request) : super._();

  final ProfileLaunchConfigurationV5 request;

  @override
  int get schemaVersion => ProfileLaunchConfigurationV5.schemaVersion;

  @override
  ProfileLaunchConfigurationV4 get executable => request.executable;

  @override
  LaunchSessionRequest get session => request.session;

  @override
  String get command => request.command;

  @override
  String encode() => LaunchTransport.writeProfileV5(request);
}
