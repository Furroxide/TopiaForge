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
