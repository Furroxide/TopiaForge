import 'dart:convert';
import 'dart:io';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'package:path/path.dart' as p;

final class IsolationFixture {
  IsolationFixture() {
    source.createSync();
    game.createSync();
    record.writeAsStringSync(jsonEncode(document));
  }
  final root = Directory(
    Directory.systemTemp
        .createTempSync('tf-isolation-')
        .resolveSymbolicLinksSync(),
  );
  Directory get source => Directory(p.join(root.path, 'source'));
  Directory get game => Directory(p.join(root.path, 'game'));
  String get output => p.join(root.path, 'output');
  String get launcher => p.join(root.path, 'launcher');
  File get record => File(p.join(root.path, 'isolation.json'));
  WindowsAcceptanceIdentity get identity => WindowsAcceptanceIdentity(
    userSid: 'S-1-5-21-42',
    logonId: '0000000000000042',
    sessionId: 2,
    userProfile: p.join(root.path, 'qa'),
    localAppDataLow: p.join(root.path, 'qa', 'LocalLow'),
  );
  Map<String, Object?> get document => {
    'schemaVersion': 1,
    'kind': 'windows-user',
    'sourceGameRoot': source.path,
    'gameRoot': game.path,
    'launcherRoot': launcher,
    'outputRoot': output,
    'persistentDataRoot': p.join(identity.localAppDataLow, 'Vendor', 'Game'),
    'userSid': identity.userSid,
    'userProfile': identity.userProfile,
    'localAppDataLow': identity.localAppDataLow,
    'normalUserSid': 'S-1-5-21-1',
    'normalUserProfile': p.join(root.path, 'normal'),
    'reviewerEvidence': 'Synthetic fixture only; no OS user or game created.',
  };
  void change(void Function(Map<String, Object?>) mutate) {
    final raw = document;
    mutate(raw);
    record.writeAsStringSync(jsonEncode(raw));
  }

  AcceptanceIsolationContext admit() => AcceptanceIsolationContext.admit(
    recordPath: record.path,
    sourceGameRoot: source.path,
    outputRoot: output,
    identityReader: () => identity,
  );
  ProfileLaunchConfigurationV4 get profile => ProfileLaunchConfigurationV4(
    profileId: 'qa',
    profileRevision: 1,
    requestId: 'owned-acceptance',
    command: 'main-menu',
    safeMode: false,
    inheritManagerModState: false,
    enabledMods: const [],
    selectedVersions: const {},
    packages: const [],
  );
  LaunchProcessIdentity get process => LaunchProcessIdentity(
    pid: 4242,
    startTimeUtc: DateTime.utc(2026),
    executablePath: p.join(game.path, 'Game.exe'),
    nativeStartToken: 'windows:1234',
  );
  File channel(String name) => File(
    p.join(
      game.path,
      'BepInEx',
      'TopiaForge',
      'staging',
      '$name-${LaunchStorageKeys.request(profile.requestId)}.json',
    ),
  );
  Map<String, Object?> ack(AcceptanceIsolationRequest request) => {
    'schemaVersion': 1,
    'requestId': profile.requestId,
    'challenge': request.document['challenge'],
    'requestSha256': request.sha256Digest,
    'status': 'admitted',
    'process': {
      'pid': process.pid,
      'nativeStartToken': process.nativeStartToken,
      'executablePath': process.executablePath,
    },
    'observedOsIdentity': identity.toJson(),
    'observedRoots': request.context.runtimeRoots(),
    'reasons': <Object?>[],
  };
  void dispose() => root.deleteSync(recursive: true);
}
