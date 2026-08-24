import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:json_schema/json_schema.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge/src/release_handoff_models.dart';
import 'package:topiaforge/src/release_metadata.dart';
import 'package:topiaforge/src/release_policy.dart';
import 'package:test/test.dart';

void main() {
  late Directory temp;
  late String root;
  late TopiaForgeReleaseCatalogEntry release;
  const targetSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';

  setUp(() {
    temp = Directory.systemTemp.createTempSync('topiaforge-metadata-test-');
    root = _repositoryRoot();
    release = TopiaForgeReleaseCatalog.load(root).release('0.1.0-rc.1');
    _writeCandidateAssets(temp, release);
  });

  tearDown(() {
    if (temp.existsSync()) temp.deleteSync(recursive: true);
  });

  test('release catalog schema and parser retain prerelease state', () {
    final catalogFile = File(p.join(root, 'release', 'catalog.json'));
    final schemaFile = File(
      p.join(root, 'schemas', 'topiaforge.release-catalog.schema.json'),
    );
    final catalogJson =
        jsonDecode(catalogFile.readAsStringSync()) as Map<String, Object?>;
    final schemaJson =
        jsonDecode(schemaFile.readAsStringSync()) as Map<String, Object?>;
    final schema = JsonSchema.create(schemaJson);
    final result = schema.validate(catalogJson);

    expect(result.isValid, isTrue, reason: result.errors.join('\n'));
    expect(catalogJson['schemaVersion'], 3);
    expect(release.version, '0.1.0-rc.1');
    expect(release.tag, 'v0.1.0-rc.1');
    expect(release.prerelease, isTrue);

    final entryWithoutFlag = Map<String, Object?>.from(
      (catalogJson['releases'] as List).single as Map,
    )..remove('prerelease');
    final invalidCatalog = <String, Object?>{
      ...catalogJson,
      'releases': [entryWithoutFlag],
    };
    expect(schema.validate(invalidCatalog).isValid, isFalse);
    expect(
      () => TopiaForgeReleaseCatalogEntry.fromJson(entryWithoutFlag),
      throwsStateError,
    );

    final legacyCatalog = <String, Object?>{...catalogJson, 'schemaVersion': 2};
    expect(schema.validate(legacyCatalog).isValid, isFalse);
    final legacyRoot = Directory(p.join(temp.path, 'legacy-catalog-root'));
    File(p.join(legacyRoot.path, 'release', 'catalog.json'))
      ..createSync(recursive: true)
      ..writeAsStringSync(jsonEncode(legacyCatalog));
    expect(
      () => TopiaForgeReleaseCatalog.load(legacyRoot.path),
      throwsA(
        isA<StateError>().having(
          (error) => error.toString(),
          'message',
          contains('schemaVersion 3'),
        ),
      ),
    );
  });

  test('release policy forbids RC1 signing exceptions and requires a pin', () {
    final policyFile = File(p.join(root, 'release', 'release-policy.json'));
    final schemaFile = File(
      p.join(root, 'schemas', 'topiaforge.release-policy.schema.json'),
    );
    final policyJson =
        jsonDecode(policyFile.readAsStringSync()) as Map<String, Object?>;
    final schema = JsonSchema.create(
      jsonDecode(schemaFile.readAsStringSync()) as Map<String, Object?>,
    );
    expect(
      schema.validate(policyJson).isValid,
      isTrue,
      reason: schema.validate(policyJson).errors.join('\n'),
    );

    final policy = TopiaForgeReleasePolicy.load(root);
    expect(policy.targetPlatforms, ['windows-x64']);
    expect(policy.windowsCertificateSha256, isEmpty);
    expect(policy.requiresWindowsSigningIdentity, isTrue);
    expect(policy.hasConfiguredWindowsSigningIdentity, isFalse);

    final configuredJson = jsonDecode(jsonEncode(policyJson)) as Map;
    configuredJson['signingIdentities'] = {
      'windowsCertificateSha256': List.filled(64, 'a').join(),
    };
    final configuredRoot = Directory(p.join(temp.path, 'configured-policy'));
    File(p.join(configuredRoot.path, 'release', 'release-policy.json'))
      ..createSync(recursive: true)
      ..writeAsStringSync(jsonEncode(configuredJson));
    final configuredPolicy = TopiaForgeReleasePolicy.load(configuredRoot.path);
    expect(configuredPolicy.hasConfiguredWindowsSigningIdentity, isTrue);
    expect(schema.validate(configuredJson).isValid, isTrue);

    final zeroPinJson = jsonDecode(jsonEncode(configuredJson)) as Map;
    (zeroPinJson['signingIdentities'] as Map)['windowsCertificateSha256'] =
        List.filled(64, '0').join();
    expect(schema.validate(zeroPinJson).isValid, isFalse);

    final exceptionJson = jsonDecode(jsonEncode(policyJson)) as Map;
    (exceptionJson['publication'] as Map)['codeSigningException'] = {
      'legacy': 'forbidden',
    };
    expect(schema.validate(exceptionJson).isValid, isFalse);
  });

  test(
    'an unsigned Windows distribution must be recorded, not inferred',
    () async {
      final schema = JsonSchema.create(
        jsonDecode(
              File(
                p.join(
                  root,
                  'schemas',
                  'topiaforge.release-policy.schema.json',
                ),
              ).readAsStringSync(),
            )
            as Map<String, Object?>,
      );
      final policyJson =
          jsonDecode(
                File(
                  p.join(root, 'release', 'release-policy.json'),
                ).readAsStringSync(),
              )
              as Map<String, Object?>;

      // Silence means signed. A certificate that simply went missing must never
      // read as a decision to ship without one.
      final silent = TopiaForgeReleasePolicy.load(root);
      expect(silent.windowsDistribution, 'signed');
      expect(silent.distributesWindowsUnsigned, isFalse);
      expect(silent.requiresWindowsSigningIdentity, isTrue);
      expect(silent.hasConfiguredWindowsSigningIdentity, isFalse);

      TopiaForgeReleasePolicy loadWith(
        String label,
        Map<String, Object?> signingIdentities, {
        bool expectSchemaValid = true,
      }) {
        final json = jsonDecode(jsonEncode(policyJson)) as Map<String, Object?>;
        json['signingIdentities'] = signingIdentities;
        expect(schema.validate(json).isValid, expectSchemaValid, reason: label);
        final candidate = Directory(p.join(temp.path, 'distribution-$label'));
        File(p.join(candidate.path, 'release', 'release-policy.json'))
          ..createSync(recursive: true)
          ..writeAsStringSync(jsonEncode(json));
        return TopiaForgeReleasePolicy.load(candidate.path);
      }

      // Recorded unsigned: the signing identity stops being required, and the
      // decision is visible in the policy rather than inferred from an absence.
      final unsigned = loadWith('unsigned', {
        'windowsDistribution': 'unsigned',
      });
      expect(unsigned.distributesWindowsUnsigned, isTrue);
      expect(unsigned.requiresWindowsSigningIdentity, isFalse);

      // Explicitly signed behaves exactly like silence.
      final signed = loadWith('signed', {'windowsDistribution': 'signed'});
      expect(signed.distributesWindowsUnsigned, isFalse);
      expect(signed.requiresWindowsSigningIdentity, isTrue);

      // An unknown mode is rejected by the schema rather than falling back.
      loadWith('unknown', {
        'windowsDistribution': 'maybe',
      }, expectSchemaValid: false);

      // Carrying both an unsigned mode and a certificate pin is a contradiction
      // the validator reports rather than silently resolving.
      final contradictory = loadWith('both', {
        'windowsDistribution': 'unsigned',
        'windowsCertificateSha256': List.filled(64, 'a').join(),
      });
      expect(contradictory.distributesWindowsUnsigned, isTrue);
      expect(contradictory.hasConfiguredWindowsSigningIdentity, isTrue);

      // And the default repository policy still fails closed on signing.
      final issues = await const ReleasePolicyValidator().validate(
        policy: silent,
        release: release,
        verifyArchiveHashes: false,
      );
      expect(
        issues,
        contains(
          'A configured Windows signing identity is required for this release.',
        ),
      );
    },
  );

  test('release policy rejects a mismatched prerelease flag', () async {
    final mismatched = TopiaForgeReleaseCatalogEntry(
      version: release.version,
      tag: release.tag,
      prerelease: false,
      status: release.status,
      notesFile: release.notesFile,
      components: release.components,
      vpmPackages: release.vpmPackages,
      mods: release.mods,
      excludedDeveloperMods: release.excludedDeveloperMods,
      artifacts: release.artifacts,
    );
    final issues = await const ReleasePolicyValidator().validate(
      policy: TopiaForgeReleasePolicy.load(root),
      release: mismatched,
      allowUnresolvedPolicy: true,
      verifyArchiveHashes: false,
    );

    expect(
      issues,
      contains('Catalog prerelease false does not match version 0.1.0-rc.1.'),
    );
  });

  test(
    'RC1 fails closed until its reviewed signer pin is configured',
    () async {
      final policy = TopiaForgeReleasePolicy.load(root);
      final issues = await const ReleasePolicyValidator().validate(
        policy: policy,
        release: release,
        verifyArchiveHashes: false,
      );
      expect(
        issues,
        contains(
          'A configured Windows signing identity is required for this release.',
        ),
      );
    },
  );

  test(
    'unresolved release metadata is complete but non-distributable',
    () async {
      final builder = const TopiaForgeReleaseMetadataBuilder();
      await builder.build(
        repositoryRoot: root,
        version: release.version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
        outputDirectory: temp.path,
        allowUnresolvedPolicy: true,
      );
      final bom = _json(File(p.join(temp.path, 'release-bom.json')));
      final sbom = _json(File(p.join(temp.path, 'release-sbom.spdx.json')));

      expect(bom['distributable'], isFalse);
      expect(
        bom['blockingReasons'] as List,
        contains('Unresolved-policy mode is non-distributable.'),
      );
      expect((bom['readiness'] as Map)['status'], 'unavailable');
      expect(((bom['codeSigning'] as Map)['platforms'] as Map)['windows-x64'], {
        'status': 'trusted',
        'exceptionApplied': false,
      });
      expect((bom['gameArchives'] as Map).keys, ['windows']);
      expect(
        (bom['expectedArtifactSet'] as List).toSet(),
        release.artifacts.toSet(),
      );
      expect(
        ((bom['ecosystem'] as Map)['platformCopies'] as List),
        hasLength(1),
      );
      expect(
        (bom['provenance'] as Map).keys,
        containsAll(['unity', 'bepInEx']),
      );
      expect((bom['legalInventory'] as List), isNotEmpty);
      expect(sbom['spdxVersion'], 'SPDX-2.3');
      await builder.verify(
        repositoryRoot: root,
        version: release.version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
        metadataDirectory: temp.path,
        allowUnresolvedPolicy: true,
      );
    },
  );

  test(
    'metadata verification accepts only a complete checksummed handoff set',
    () async {
      final builder = const TopiaForgeReleaseMetadataBuilder();
      await builder.build(
        repositoryRoot: root,
        version: release.version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
        outputDirectory: temp.path,
        allowUnresolvedPolicy: true,
      );
      final handoffNames = <String>[
        releaseHandoffFileName,
        for (final platform in TopiaForgeReleasePolicy.load(
          root,
        ).targetPlatforms)
          releasePlatformBundleFileName(platform),
      ];
      for (final name in handoffNames) {
        _writeJson(File(p.join(temp.path, name)), {'fixture': name});
        _appendChecksum(temp, name);
      }
      await builder.verify(
        repositoryRoot: root,
        version: release.version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
        metadataDirectory: temp.path,
        allowUnresolvedPolicy: true,
      );

      File(p.join(temp.path, handoffNames.last)).deleteSync();
      await expectLater(
        builder.verify(
          repositoryRoot: root,
          version: release.version,
          targetSha: targetSha,
          assetsDirectory: temp.path,
          metadataDirectory: temp.path,
          allowUnresolvedPolicy: true,
        ),
        throwsStateError,
      );
    },
  );

  test(
    'nested payload, provenance, and legal inventory tampering fails',
    () async {
      final builder = const TopiaForgeReleaseMetadataBuilder();
      await builder.build(
        repositoryRoot: root,
        version: release.version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
        outputDirectory: temp.path,
        allowUnresolvedPolicy: true,
      );

      final bomFile = File(p.join(temp.path, 'release-bom.json'));
      for (final section in const ['provenance', 'legalInventory']) {
        final bom = _json(bomFile);
        if (section == 'provenance') {
          ((bom[section] as Map)['unity'] as Map)['bundleSha256'] = List.filled(
            64,
            '0',
          ).join();
        } else {
          ((bom[section] as List).first as Map)['sha256'] = List.filled(
            64,
            '0',
          ).join();
        }
        _writeJson(bomFile, bom);
        _refreshChecksum(temp, 'release-bom.json');
        await expectLater(
          builder.verify(
            repositoryRoot: root,
            version: release.version,
            targetSha: targetSha,
            assetsDirectory: temp.path,
            metadataDirectory: temp.path,
            allowUnresolvedPolicy: true,
          ),
          throwsStateError,
        );
        await builder.build(
          repositoryRoot: root,
          version: release.version,
          targetSha: targetSha,
          assetsDirectory: temp.path,
          outputDirectory: temp.path,
          allowUnresolvedPolicy: true,
        );
      }

      _writePlatformArchive(
        File(p.join(temp.path, 'TopiaForge-windows-x64.zip')),
        release,
        prefix: 'TopiaForge/',
        changedPath: 'dist/vpm/index.json',
      );
      _refreshChecksum(temp, 'TopiaForge-windows-x64.zip');
      await expectLater(
        builder.verify(
          repositoryRoot: root,
          version: release.version,
          targetSha: targetSha,
          assetsDirectory: temp.path,
          metadataDirectory: temp.path,
          allowUnresolvedPolicy: true,
        ),
        throwsStateError,
      );
    },
  );

  test('safe contract assembly identity survives patch releases', () async {
    final next = TopiaForgeReleaseCatalogEntry(
      version: release.version,
      tag: release.tag,
      prerelease: release.prerelease,
      status: release.status,
      notesFile: release.notesFile,
      components: {...release.components, 'sdk': '0.1.1', 'unityUi': '0.1.1'},
      vpmPackages: release.vpmPackages,
      mods: release.mods,
      excludedDeveloperMods: release.excludedDeveloperMods,
      artifacts: release.artifacts,
    );
    final issues = await const ReleasePolicyValidator().validate(
      policy: TopiaForgeReleasePolicy.load(root),
      release: next,
      allowUnresolvedPolicy: true,
      verifyArchiveHashes: false,
    );

    const abstractions =
        'src/TopiaForge.Mods.Abstractions/TopiaForge.Mods.Abstractions.csproj';
    const interop =
        'src/TopiaForge.Mods.Interop.Unity/TopiaForge.Mods.Interop.Unity.csproj';
    const unityUi =
        'src/TopiaForge.Mods.UnityUi/TopiaForge.Mods.UnityUi.csproj';
    bool hasAssemblyIssue(String project) => issues.any(
      (issue) =>
          issue.startsWith('$project AssemblyVersion ') &&
          issue.contains('does not match'),
    );

    expect(
      issues,
      contains('$abstractions Version 0.1.0-rc.1 does not match 0.1.1.'),
    );
    expect(hasAssemblyIssue(abstractions), isFalse);
    expect(hasAssemblyIssue(unityUi), isFalse);
    expect(
      hasAssemblyIssue(interop),
      isTrue,
      reason: 'the explicitly unstable interop package is not frozen',
    );
  });
}

void _writeCandidateAssets(
  Directory output,
  TopiaForgeReleaseCatalogEntry release,
) {
  for (final entry in release.mods.entries) {
    File(p.join(output.path, '${entry.key}-${entry.value}.topiaforgemod'))
      ..parent.createSync(recursive: true)
      ..writeAsStringSync('package:${entry.key}:${entry.value}\n');
  }
  _writePlatformArchive(
    File(p.join(output.path, 'TopiaForge-windows-x64.zip')),
    release,
    prefix: 'TopiaForge/',
  );
  File(
    p.join(output.path, 'topiaforge-update-v1.json'),
  ).writeAsStringSync('{"fixture":true}\n');
  File(
    p.join(output.path, 'topiaforge-update-v1.json.sig'),
  ).writeAsStringSync('{"fixture":true}\n');
  _writeJson(
    File(
      p.join(
        output.path,
        TopiaForgeReleaseMetadataBuilder.trustEvidenceFileName,
      ),
    ),
    {
      'windows-x64': {'status': 'trusted', 'exceptionApplied': false},
    },
  );
}

void _writePlatformArchive(
  File file,
  TopiaForgeReleaseCatalogEntry release, {
  required String prefix,
  String? changedPath,
}) {
  final archive = Archive();
  for (final entry in release.mods.entries) {
    final relative = 'dist/${entry.key}-${entry.value}.topiaforgemod';
    final bytes = File(
      p.join(file.parent.path, p.basename(relative)),
    ).readAsBytesSync();
    archive.addFile(ArchiveFile.bytes('$prefix$relative', bytes));
  }
  final indexBytes = utf8.encode(
    changedPath == 'dist/vpm/index.json'
        ? '{"changed":true}\n'
        : '{"packages":{}}\n',
  );
  archive.addFile(
    ArchiveFile.bytes('${prefix}dist/vpm/index.json', indexBytes),
  );
  for (final entry in release.vpmPackages.entries) {
    final relative = 'dist/vpm/${entry.key}-${entry.value}.zip';
    archive.addFile(
      ArchiveFile.string(
        '$prefix$relative',
        changedPath == relative
            ? 'changed\n'
            : 'vpm:${entry.key}:${entry.value}\n',
      ),
    );
  }
  file.writeAsBytesSync(ZipEncoder().encode(archive));
}

void _refreshChecksum(Directory directory, String name) {
  final sums = File(p.join(directory.path, 'SHA256SUMS'));
  final file = File(p.join(directory.path, name));
  final hash = sha256.convert(file.readAsBytesSync()).toString();
  final lines = sums.readAsLinesSync();
  final replacement = '$hash  $name';
  sums.writeAsStringSync(
    '${lines.map((line) => line.endsWith('  $name') ? replacement : line).join('\n')}\n',
  );
}

void _appendChecksum(Directory directory, String name) {
  final file = File(p.join(directory.path, name));
  final hash = sha256.convert(file.readAsBytesSync()).toString();
  File(
    p.join(directory.path, 'SHA256SUMS'),
  ).writeAsStringSync('$hash  $name\n', mode: FileMode.append, flush: true);
}

Map<String, Object?> _json(File file) =>
    (jsonDecode(file.readAsStringSync()) as Map).cast<String, Object?>();

void _writeJson(File file, Object value) => file.writeAsStringSync(
  '${const JsonEncoder.withIndent('  ').convert(value)}\n',
);

String _repositoryRoot() {
  var directory = Directory.current.absolute;
  while (!File(p.join(directory.path, 'TopiaForge.slnx')).existsSync()) {
    if (directory.parent.path == directory.path) {
      throw StateError('Repository root not found.');
    }
    directory = directory.parent;
  }
  return directory.path;
}
