part 'release_handoff_qa_helpers.dart';

const releasePlatformQaLinuxSchema = 'release-platform-qa-linux-v1';
const releasePlatformQaWindowsSchema = 'release-platform-qa-windows-v1';
const releaseWindowsQaSummarySchema = 'release-windows-qa-summary-v1';
const releaseProtonEvidenceSchema = 'release-proton-evidence-v1';

Map<String, Object?> buildReleasePlatformQa({
  required String platform,
  required Map<String, Object?> source,
  required String sourceDescriptorSha256,
}) => switch (platform) {
  'linux-x64' => _normalizeLinuxQa(
    source,
    sourceDescriptorSha256: sourceDescriptorSha256,
    embedded: false,
  ),
  'windows-x64' => _normalizeWindowsQa(
    source,
    sourceDescriptorSha256: sourceDescriptorSha256,
    embedded: false,
  ),
  _ => throw StateError('QA is not defined for release platform $platform.'),
};

Map<String, Object?> parseReleasePlatformQa(
  Object? value, {
  required String platform,
}) {
  final json = _qaObject(value, '$platform qa');
  return switch (platform) {
    'linux-x64' => _normalizeLinuxQa(json, embedded: true),
    'windows-x64' => _normalizeWindowsQa(json, embedded: true),
    _ => throw StateError('QA is not defined for release platform $platform.'),
  };
}

Map<String, Object?> _normalizeLinuxQa(
  Map<String, Object?> json, {
  String? sourceDescriptorSha256,
  required bool embedded,
}) {
  const sourceFields = {
    'schema',
    'version',
    'targetSha',
    'platform',
    'archiveSha256',
    'archiveSize',
    'canonicalEcosystemSha256',
    'gameBuildId',
    'gameArchiveSha256',
    'gameFilesManifestSha256',
    'gameFilesVerified',
    'result',
    'suite',
    'protonVersion',
    'protonAppId',
    'protonDepotId',
    'protonManifestId',
    'protonBuildId',
    'protonSourceCommit',
    'protonRuntimeSha256',
    'executionEnvironment',
    'runtime',
    'winDllOverrides',
    'independentQa',
    'caseInventorySha256',
    'requiredCases',
    'requiredCasesSha256',
    'passedCases',
    'passedCasesSha256',
    'failures',
    'releaseJourney',
    'acceptanceResultSha256',
    'gameExecutableSha256',
    'runtimeConfigurationSha256',
    'wineCommandSha256',
    'evidenceSha256',
    'evidenceSize',
  };
  const embeddedFields = {
    ...sourceFields,
    'sourceSchema',
    'sourceDescriptorSha256',
  };
  if (embedded) {
    _qaExactKeys(json, embeddedFields, 'linux qa');
    if (json['schema'] != releasePlatformQaLinuxSchema ||
        json['sourceSchema'] != releaseProtonEvidenceSchema) {
      throw StateError('linux qa schemas are invalid.');
    }
    sourceDescriptorSha256 = _qaString(
      json,
      'sourceDescriptorSha256',
      'linux qa',
    );
  } else {
    _qaExactKeys(json, sourceFields, 'Proton QA descriptor');
    if (json['schema'] != releaseProtonEvidenceSchema) {
      throw StateError(
        'Linux QA input schema must be $releaseProtonEvidenceSchema.',
      );
    }
  }
  final journey = _qaReleaseJourney(json['releaseJourney'], 'linux qa');
  return {
    'schema': releasePlatformQaLinuxSchema,
    'sourceSchema': releaseProtonEvidenceSchema,
    'sourceDescriptorSha256': _qaRequiredString(
      sourceDescriptorSha256,
      'linux qa.sourceDescriptorSha256',
    ),
    'version': _qaString(json, 'version', 'linux qa'),
    'targetSha': _qaString(json, 'targetSha', 'linux qa'),
    'platform': _qaString(json, 'platform', 'linux qa'),
    'archiveSha256': _qaString(json, 'archiveSha256', 'linux qa'),
    'archiveSize': _qaPositiveInt(json, 'archiveSize', 'linux qa'),
    'canonicalEcosystemSha256': _qaString(
      json,
      'canonicalEcosystemSha256',
      'linux qa',
    ),
    'gameBuildId': _qaPositiveInt(json, 'gameBuildId', 'linux qa'),
    'gameArchiveSha256': _qaString(json, 'gameArchiveSha256', 'linux qa'),
    'gameFilesManifestSha256': _qaString(
      json,
      'gameFilesManifestSha256',
      'linux qa',
    ),
    'gameFilesVerified': _qaPositiveInt(json, 'gameFilesVerified', 'linux qa'),
    'result': _qaString(json, 'result', 'linux qa'),
    'suite': _qaString(json, 'suite', 'linux qa'),
    'protonVersion': _qaString(json, 'protonVersion', 'linux qa'),
    'protonAppId': _qaPositiveInt(json, 'protonAppId', 'linux qa'),
    'protonDepotId': _qaPositiveInt(json, 'protonDepotId', 'linux qa'),
    'protonManifestId': _qaString(json, 'protonManifestId', 'linux qa'),
    'protonBuildId': _qaPositiveInt(json, 'protonBuildId', 'linux qa'),
    'protonSourceCommit': _qaString(json, 'protonSourceCommit', 'linux qa'),
    'protonRuntimeSha256': _qaString(json, 'protonRuntimeSha256', 'linux qa'),
    'executionEnvironment': _qaString(json, 'executionEnvironment', 'linux qa'),
    'runtime': _qaString(json, 'runtime', 'linux qa'),
    'winDllOverrides': _qaString(json, 'winDllOverrides', 'linux qa'),
    'independentQa': _qaBool(json, 'independentQa', 'linux qa'),
    'caseInventorySha256': _qaString(json, 'caseInventorySha256', 'linux qa'),
    'requiredCases': _qaStringList(
      json['requiredCases'],
      'linux qa.requiredCases',
    ),
    'requiredCasesSha256': _qaString(json, 'requiredCasesSha256', 'linux qa'),
    'passedCases': _qaStringList(json['passedCases'], 'linux qa.passedCases'),
    'passedCasesSha256': _qaString(json, 'passedCasesSha256', 'linux qa'),
    'failures': _qaStringList(
      json['failures'],
      'linux qa.failures',
      allowEmpty: true,
    ),
    'releaseJourney': journey,
    'acceptanceResultSha256': _qaString(
      json,
      'acceptanceResultSha256',
      'linux qa',
    ),
    'gameExecutableSha256': _qaString(json, 'gameExecutableSha256', 'linux qa'),
    'runtimeConfigurationSha256': _qaString(
      json,
      'runtimeConfigurationSha256',
      'linux qa',
    ),
    'wineCommandSha256': _qaString(json, 'wineCommandSha256', 'linux qa'),
    'evidenceSha256': _qaString(json, 'evidenceSha256', 'linux qa'),
    'evidenceSize': _qaPositiveInt(json, 'evidenceSize', 'linux qa'),
  };
}

Map<String, Object?> _normalizeWindowsQa(
  Map<String, Object?> json, {
  String? sourceDescriptorSha256,
  required bool embedded,
}) {
  const sourceFields = {
    'schema',
    'version',
    'targetSha',
    'platform',
    'archiveSha256',
    'archiveSize',
    'canonicalEcosystemSha256',
    'signingState',
    'toolchains',
    'gameBuildId',
    'validationDescriptorSha256',
    'unity',
    'robotopia',
  };
  const embeddedFields = {
    ...sourceFields,
    'sourceSchema',
    'sourceDescriptorSha256',
  };
  if (embedded) {
    _qaExactKeys(json, embeddedFields, 'Windows qa');
    if (json['schema'] != releasePlatformQaWindowsSchema ||
        json['sourceSchema'] != releaseWindowsQaSummarySchema) {
      throw StateError('Windows qa schemas are invalid.');
    }
    sourceDescriptorSha256 = _qaString(
      json,
      'sourceDescriptorSha256',
      'Windows qa',
    );
  } else {
    _qaExactKeys(json, sourceFields, 'Windows QA summary');
    if (json['schema'] != releaseWindowsQaSummarySchema) {
      throw StateError(
        'Windows QA input schema must be $releaseWindowsQaSummarySchema.',
      );
    }
  }
  return {
    'schema': releasePlatformQaWindowsSchema,
    'sourceSchema': releaseWindowsQaSummarySchema,
    'sourceDescriptorSha256': _qaRequiredString(
      sourceDescriptorSha256,
      'Windows qa.sourceDescriptorSha256',
    ),
    'version': _qaString(json, 'version', 'Windows qa'),
    'targetSha': _qaString(json, 'targetSha', 'Windows qa'),
    'platform': _qaString(json, 'platform', 'Windows qa'),
    'archiveSha256': _qaString(json, 'archiveSha256', 'Windows qa'),
    'archiveSize': _qaPositiveInt(json, 'archiveSize', 'Windows qa'),
    'canonicalEcosystemSha256': _qaString(
      json,
      'canonicalEcosystemSha256',
      'Windows qa',
    ),
    'signingState': _qaString(json, 'signingState', 'Windows qa'),
    'toolchains': _qaWindowsToolchains(json['toolchains']),
    'gameBuildId': _qaPositiveInt(json, 'gameBuildId', 'Windows qa'),
    'validationDescriptorSha256': _qaString(
      json,
      'validationDescriptorSha256',
      'Windows qa',
    ),
    'unity': _qaUnity(json['unity']),
    'robotopia': _qaRobotopia(json['robotopia']),
  };
}
