part of 'launch_staging_store.dart';

/// A private acceptance sidecar; this never adds fields to profile wire V4.
final class AcceptanceIsolationRequest {
  AcceptanceIsolationRequest(
    this.context,
    this.profile,
    this.document,
    this.sha256Digest,
  );
  final AcceptanceIsolationContext context;
  final ProfileLaunchConfigurationV4 profile;
  final Map<String, Object?> document;
  final String sha256Digest;
}

final class AcceptanceIsolationAcknowledgement {
  AcceptanceIsolationAcknowledgement(this.document, this.sha256Digest);
  final Map<String, Object?> document;
  final String sha256Digest;
}

extension AcceptanceIsolationStaging on LaunchStagingStore {
  Future<AcceptanceIsolationRequest> writeAcceptanceRequest({
    required AcceptanceIsolationContext context,
    required ProfileLaunchConfigurationV4 profile,
    required String challenge,
    required DateTime now,
  }) async {
    context.verify();
    if (!sameAcceptancePath(trustedGameRoot, context.gameRoot) ||
        !RegExp(r'^[0-9a-f]{64}$').hasMatch(challenge)) {
      throw const FormatException(
        'Acceptance staging context is inconsistent.',
      );
    }
    final profileFile = _channel('launch-profile', profile.requestId);
    _directories();
    _ordinaryFile(profileFile);
    final bytes = await profileFile.readAsBytes();
    if (bytes.length > LaunchStagingStore.documentLimit ||
        jsonEncode(
              ProfileLaunchConfigurationV4.fromJson(
                jsonDecode(utf8.decode(bytes)) as Map<String, Object?>,
              ).toJson(),
            ) !=
            jsonEncode(profile.toJson())) {
      throw const FormatException(
        'Acceptance must bind the exact staged launch profile.',
      );
    }
    final document = <String, Object?>{
      'schemaVersion': 1,
      'requestId': profile.requestId,
      'challenge': challenge,
      'profileRequestSha256': sha256.convert(bytes).toString(),
      'isolationEvidenceSha256': context.recordSha256,
      'issuedAtUtc': now.toUtc().toIso8601String(),
      'expiresAtUtc': now
          .toUtc()
          .add(const Duration(minutes: 15))
          .toIso8601String(),
      'expectedOsIdentity': context.identity.toJson(),
      'expectedRoots': context.runtimeRoots(),
    };
    final encoded = utf8.encode(jsonEncode(document));
    await _writePrivateAcceptance(
      'acceptance-isolation-request',
      profile.requestId,
      encoded,
    );
    return AcceptanceIsolationRequest(
      context,
      profile,
      Map.unmodifiable(document),
      sha256.convert(encoded).toString(),
    );
  }

  Future<AcceptanceIsolationAcknowledgement?> readAcceptanceAcknowledgement(
    AcceptanceIsolationRequest expected,
    LaunchProcessIdentity process,
  ) async {
    final file = _channel(
      'acceptance-isolation-ack',
      expected.profile.requestId,
    );
    _directories();
    _ordinaryFile(file, allowMissing: true);
    if (!file.existsSync()) return null;
    final bytes = readAcceptanceRecord(file);
    final raw = decodeAcceptanceObject(bytes);
    requireAcceptanceKeys(raw, const {
      'schemaVersion',
      'requestId',
      'challenge',
      'requestSha256',
      'status',
      'process',
      'observedOsIdentity',
      'observedRoots',
      'reasons',
    });
    if (raw['schemaVersion'] is! int ||
        raw['schemaVersion'] != 1 ||
        raw['requestId'] != expected.profile.requestId ||
        raw['challenge'] != expected.document['challenge'] ||
        raw['requestSha256'] != expected.sha256Digest) {
      throw const FormatException(
        'Acceptance acknowledgement correlation mismatch.',
      );
    }
    if (raw['status'] != 'admitted' ||
        raw['reasons'] is! List ||
        (raw['reasons']! as List).isNotEmpty) {
      throw StateError('Runtime refused acceptance isolation.');
    }
    final native = raw['process'];
    if (native is! Map<String, Object?>) {
      throw const FormatException('Missing native acceptance identity.');
    }
    requireAcceptanceKeys(native, const {
      'pid',
      'nativeStartToken',
      'executablePath',
    });
    if (native['pid'] is! int ||
        native['pid'] != process.pid ||
        native['nativeStartToken'] != process.nativeStartToken ||
        native['executablePath'] is! String ||
        !_sameValidatedAcceptancePath(
          native['executablePath']! as String,
          process.executablePath,
        ) ||
        !_acceptanceMatches(
          raw['observedOsIdentity'],
          expected.context.identity.toJson(),
          const {'userProfile', 'localAppDataLow'},
        ) ||
        !_acceptanceMatches(
          raw['observedRoots'],
          expected.context.runtimeRoots(),
          expected.context.runtimeRoots().keys.toSet(),
        )) {
      throw StateError(
        'Runtime did not acknowledge the owned process and exact isolated roots.',
      );
    }
    return AcceptanceIsolationAcknowledgement(
      Map.unmodifiable(raw),
      sha256.convert(bytes).toString(),
    );
  }

  Future<void> _writePrivateAcceptance(
    String channel,
    String requestId,
    List<int> bytes,
  ) async {
    if (bytes.isEmpty || bytes.length > acceptanceDocumentLimit) {
      throw const FormatException('Acceptance sidecar exceeds its byte limit.');
    }
    _directories(create: true);
    final file = _channel(channel, requestId);
    _ordinaryFile(file, allowMissing: true);
    if (file.existsSync()) {
      throw StateError('Acceptance request identity already exists.');
    }
    final temporary = File(
      '${file.path}.tmp-${DateTime.now().microsecondsSinceEpoch}-${Random.secure().nextInt(1 << 32)}',
    );
    try {
      temporary.createSync(exclusive: true);
      final output = await temporary.open(mode: FileMode.write);
      try {
        await output.writeFrom(bytes);
        await output.flush();
      } finally {
        await output.close();
      }
      _directories();
      _ordinaryFile(temporary);
      _ordinaryFile(file, allowMissing: true);
      if (file.existsSync()) {
        throw StateError('Acceptance request was claimed during publication.');
      }
      temporary.renameSync(file.path);
    } finally {
      _directories();
      _ordinaryFile(temporary, allowMissing: true);
      if (temporary.existsSync()) temporary.deleteSync();
    }
  }
}

bool _acceptanceMatches(
  Object? actual,
  Map<String, Object?> expected,
  Set<String> pathKeys,
) {
  if (actual is! Map<String, Object?>) return false;
  requireAcceptanceKeys(actual, expected.keys.toSet());
  for (final key in expected.keys) {
    final value = actual[key];
    if (pathKeys.contains(key)) {
      if (value is! String ||
          !_sameValidatedAcceptancePath(value, expected[key]! as String)) {
        return false;
      }
    } else if (value.runtimeType != expected[key].runtimeType ||
        value != expected[key]) {
      return false;
    }
  }
  return true;
}

bool _sameValidatedAcceptancePath(String actual, String expected) {
  // Compare only after raw admission, matching C# LocalPath: normalization must
  // not erase a relative path, dot segment or an actual filesystem alias.
  requireAcceptanceUnlinkedPath(actual);
  requireAcceptanceUnlinkedPath(expected);
  return sameAcceptancePath(actual, expected);
}
