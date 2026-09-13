import 'native_screen_oracle.dart';
import 'native_audio_oracle.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'native_annex.dart';
import 'native_annex_verifier.dart';
import 'native_io.dart';
import 'native_options.dart';
import 'native_transcript_oracles.dart';
import 'sandbox_json.dart';
import 'sandbox_specification.dart';

/// Replays private files on the admitted workstation. It never launches or
/// treats the historical identity reader below as live OS admission.
Future<SandboxNativeVerification> verifySandboxNativeFiles(
  SandboxNativePaths paths,
) async {
  final snapshots = <String, (String, int)>{};
  List<int> read(String path, {int maximum = nativeInputLimit}) {
    final bytes = readNativeFile(path, maximum: maximum);
    final digest = nativeHash(bytes);
    final prior = snapshots[path];
    if (prior != null && prior.$1 != digest) {
      throw StateError('Private input changed between reads.');
    }
    snapshots[path] = (digest, maximum);
    return bytes;
  }

  final annex = SandboxNativeAnnex.parse(read(paths.annexPath));
  final recordBytes = read(paths.isolationRecordPath);
  final record = sandboxDocument(recordBytes, 'private provisioning');
  final ack = sandboxObject(annex.isolation['acknowledgement'], 'ack');
  final identity = sandboxObject(
    ack['observedOsIdentity'],
    'recorded identity',
  );
  final historic = WindowsAcceptanceIdentity(
    userSid: identity['userSid']! as String,
    logonId: identity['logonId']! as String,
    sessionId: identity['sessionId']! as int,
    userProfile: identity['userProfile']! as String,
    localAppDataLow: identity['localAppDataLow']! as String,
  );
  final context = AcceptanceIsolationContext.admit(
    recordPath: paths.isolationRecordPath,
    sourceGameRoot: sandboxText(record['sourceGameRoot'], 'source game'),
    outputRoot: sandboxText(record['outputRoot'], 'private output'),
    identityReader: () => historic,
  );
  final root = nativeChild(context.outputRoot, 'sandbox/${annex.runId}');
  if (!sameAcceptancePath(
    paths.annexPath,
    nativeChild(root, 'sandbox-annex.json'),
  )) {
    throw StateError('Native annex is outside its exact admitted run root.');
  }
  final packages = SandboxNativePackageInventory.read(paths.packagesPath);
  if (packages.sourceRevision != annex.sourceRevision) {
    throw StateError('Native package inventory revision differs.');
  }
  final key = LaunchStorageKeys.request(ack['requestId']! as String);
  final stage = p.join(context.managerRoot, 'staging');
  final requestBytes = read(
    p.join(stage, 'acceptance-isolation-request-$key.json'),
  );
  final acknowledgementBytes = read(
    p.join(stage, 'acceptance-isolation-ack-$key.json'),
  );
  final profileBytes = read(
    p.join(stage, 'launch-profile-$key.json'),
    maximum: 4 * 1024 * 1024,
  );
  final transcriptBytes = read(
    nativeChild(
      root,
      sandboxObject(annex.document['transcript'], 'transcript')['path']!
          as String,
    ),
    maximum: nativeTranscriptLimit,
  );
  final sourceBytes = read(
    nativeChild(root, 'source-workspace.json'),
    maximum: nativeSourceLimit,
  );
  final source = decodeNativeSourceWorkspace(sourceBytes, annex.sourceRevision);
  if (source['sourceRevision'] != annex.sourceRevision) {
    throw StateError('Native source workspace revision differs.');
  }
  final artifacts = <String, SandboxNativeArtifactFact>{};
  final audioMeasurements = <String, NativeAudioMeasurement>{};
  final screenMeasurements = <String, NativeScreenMeasurement>{};
  for (final artifact in annex.artifacts) {
    final path = artifact['path']! as String;
    final bytes = read(nativeChild(root, path), maximum: 128 * 1024 * 1024);
    if (path.endsWith('.bmp')) {
      screenMeasurements[path] = measureNativeScreen(bytes);
    }
    if (path.endsWith('.wav')) {
      audioMeasurements[path] = measureNativeAudio(bytes);
    }
    artifacts[path] = SandboxNativeArtifactFact(
      sha256Digest: nativeHash(bytes),
      length: bytes.length,
    );
  }
  for (final required in ['source-workspace.json', 'runtime-files.json']) {
    if (!artifacts.containsKey(required)) {
      throw StateError('Required native input artifact is absent.');
    }
  }
  final runtime = sandboxDocument(
    read(nativeChild(root, 'runtime-files.json')),
    'runtime receipt',
  );
  sandboxFields(runtime, {'schemaVersion', 'kind', 'files'}, 'runtime receipt');
  if (runtime['schemaVersion'] is! int ||
      runtime['schemaVersion'] != 1 ||
      runtime['kind'] != 'sandbox-runtime-files-v1') {
    throw StateError('Runtime receipt identity differs.');
  }
  final runtimeFiles = nativeRows(runtime['files'], 128, allowEmpty: false);
  final expectedRuntime = {
    for (final name in topiaForgeRuntimeLoaderDlls)
      'BepInEx/plugins/TopiaForge.ModManager/$name',
  };
  if (runtimeFiles.length != expectedRuntime.length) {
    throw StateError('Runtime receipt inventory differs.');
  }
  for (final file in runtimeFiles) {
    nativeFile(file, length: true);
    final relative = file['path']! as String;
    if (!expectedRuntime.remove(relative)) {
      throw StateError('Unexpected or repeated runtime file.');
    }
    final bytes = read(
      nativeChild(context.gameRoot, relative),
      maximum: 128 * 1024 * 1024,
    );
    if (bytes.length != file['length'] || nativeHash(bytes) != file['sha256']) {
      throw StateError('Actual QA runtime differs from its recorded bytes.');
    }
  }
  final driverBytes = read(paths.driverManifestPath, maximum: 4 * 1024 * 1024);
  final brokerResult = sandboxDocument(
    read(nativeChild(root, 'broker-result.json')),
    'broker result',
  );
  sandboxFields(brokerResult, {
    'schemaVersion',
    'kind',
    'runId',
    'processId',
    'transcript',
    'artifacts',
    'inputReleased',
    'fixtureReleased',
    'failures',
    'qualifiesRelease',
  }, 'broker result');
  if (brokerResult['schemaVersion'] is! int ||
      brokerResult['schemaVersion'] != 1 ||
      brokerResult['kind'] != 'sandbox-broker-result-v1' ||
      brokerResult['runId'] != annex.runId ||
      brokerResult['qualifiesRelease'] != false ||
      brokerResult['processId'] !=
          sandboxObject(annex.document['broker'], 'broker')['processId'] ||
      !nativeSame(brokerResult['transcript'], annex.document['transcript'])) {
    throw StateError('Native broker result does not match the annex.');
  }
  final bindings = SandboxNativeBindings(
    sourceRevision: packages.sourceRevision,
    challenge: annex.challenge,
    sourceWorkspaceBytes: sourceBytes,
    driverManifestBytes: driverBytes,
    deviceProfileBytes: read(paths.deviceProfilePath),
    provisioningRecordBytes: recordBytes,
    acknowledgementBytes: acknowledgementBytes,
    requestBytes: requestBytes,
    profileRequestBytes: profileBytes,
    transcriptBytes: transcriptBytes,
    brokerBinarySha256: nativeHash(
      read(paths.brokerPath, maximum: 256 * 1024 * 1024),
    ),
    brokerProcessId:
        sandboxObject(annex.document['broker'], 'broker')['processId']! as int,
    packages: packages.receipts,
    artifactDigests: artifacts,
    originalProcessIdentity: sandboxObject(
      ack['process'],
      'original game process',
    ),
  );
  final spec = SandboxSpecification.parse(read(paths.specPath));
  final oracles = evaluateSandboxNativeTranscript(
    annex: annex,
    transcriptBytes: transcriptBytes,
    driverManifestBytes: driverBytes,
    audioMeasurements: audioMeasurements,
    screenMeasurements: screenMeasurements,
    audioEndpointId:
        sandboxObject(
              sandboxDocument(read(paths.deviceProfilePath), 'device')['audio'],
              'audio',
            )['endpointId']!
            as String,
  );
  final result = SandboxNativeAnnexVerifier().verify(
    annex,
    spec,
    bindings,
    oracles,
  );
  // Preserve comparison with actual private acknowledgement via the production
  // staging verifier, including native identity/root/path checks.
  final request = sandboxDocument(requestBytes, 'private request');
  final profile = ProfileLaunchConfigurationV4.fromJson(
    sandboxDocument(profileBytes, 'launch profile'),
  );
  final actual = await LaunchStagingStore(context.gameRoot)
      .readAcceptanceAcknowledgement(
        AcceptanceIsolationRequest(
          context,
          profile,
          request,
          nativeHash(requestBytes),
        ),
        LaunchProcessIdentity(
          pid: sandboxObject(ack['process'], 'process')['pid']! as int,
          startTimeUtc: DateTime.utc(1970),
          executablePath:
              sandboxObject(ack['process'], 'process')['executablePath']!
                  as String,
          nativeStartToken:
              sandboxObject(ack['process'], 'process')['nativeStartToken']!
                  as String,
        ),
      );
  if (actual == null ||
      actual.sha256Digest != annex.isolation['acknowledgementSha256']) {
    throw StateError('Actual private acknowledgement did not replay.');
  }
  context.verify();
  for (final entry in snapshots.entries) {
    if (nativeHash(readNativeFile(entry.key, maximum: entry.value.$2)) !=
        entry.value.$1) {
      throw StateError('Private native input changed during verification.');
    }
  }
  if (SandboxNativePackageInventory.read(paths.packagesPath).sha256Digest !=
      packages.sha256Digest) {
    throw StateError('Native package inventory changed during verification.');
  }
  return result;
}
