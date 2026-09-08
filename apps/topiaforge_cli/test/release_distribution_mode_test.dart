import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge/src/release_policy.dart';
import 'package:test/test.dart';

/// Covers the recorded Windows distribution mode.
///
/// Kept apart from the wider release-metadata suite because it needs no
/// candidate assets, only the policy file and its schema.
void main() {
  late Directory temp;
  late String root;
  late TopiaForgeReleaseCatalogEntry release;

  setUp(() {
    temp = Directory.systemTemp.createTempSync('topiaforge-distribution-test-');
    root = _repositoryRoot();
    release = TopiaForgeReleaseCatalog.load(root).release('0.1.0-rc.1');
  });

  tearDown(() {
    if (temp.existsSync()) temp.deleteSync(recursive: true);
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

      // Silence remains signed even after the reviewed RC1 policy opts out.
      final silent = loadWith('silent', {});
      expect(silent.windowsDistribution, 'signed');
      expect(silent.distributesWindowsUnsigned, isFalse);
      expect(silent.requiresWindowsSigningIdentity, isTrue);
      expect(silent.hasConfiguredWindowsSigningIdentity, isFalse);

      final reviewed = TopiaForgeReleasePolicy.load(root);
      expect(schema.validate(policyJson).isValid, isTrue);
      expect(reviewed.productVersion, '0.1.0-rc.1');
      expect(reviewed.windowsDistribution, 'unsigned');
      expect(reviewed.windowsCertificateSha256, isEmpty);

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

      // Preserve the validator's other real inputs without mutating root policy.
      final inputs = Process.runSync(
        'git',
        [
          'ls-files',
          '--',
          'global.json',
          'Directory.Build.props',
          'tools/unity-ui-bundle/ProjectSettings/ProjectVersion.txt',
          '.github/robotopia-game-build.json',
          'baselines/gamecode.surface.baseline.json',
          'src/TopiaForge.ModManager.Core/TopiaForgeVersions.cs',
          '*.csproj',
          '*/pubspec.yaml',
          'templates/**/package.json',
          'mods/**/topiaforge.mod.json',
          'LICENSE',
          release.notesFile,
          ...silent.provenanceFiles,
        ],
        workingDirectory: root,
        stdoutEncoding: utf8,
      );
      expect(inputs.exitCode, 0);
      for (final relative in (inputs.stdout as String).trim().split('\n')) {
        final path = relative.trim();
        final copy = File(p.join(silent.repositoryRoot, path));
        copy.parent.createSync(recursive: true);
        File(p.join(root, path)).copySync(copy.path);
      }
      // The silent fixture still fails closed on missing signing identity.
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
}

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
