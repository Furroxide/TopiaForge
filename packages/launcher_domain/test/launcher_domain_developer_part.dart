part of 'launcher_domain_test.dart';

void _developerModelTests() {
  group('developer models', () {
    test('UnityCompanionSettings round-trips through toJson', () {
      const companion = UnityCompanionSettings(
        enabled: true,
        projectPath: r'C:\proj\my-world',
        unityVersion: '6000.0.23f1',
        assetBundleOutputPath: r'C:\proj\my-world\out',
      );

      final restored = UnityCompanionSettings.fromJson(companion.toJson());

      expect(restored.enabled, isTrue);
      expect(restored.projectPath, r'C:\proj\my-world');
      expect(restored.unityVersion, '6000.0.23f1');
      expect(restored.assetBundleOutputPath, r'C:\proj\my-world\out');
    });

    test('UnityCompanionSettings omits empty optional paths', () {
      const companion = UnityCompanionSettings(enabled: false);

      expect(companion.toJson(), equals({'enabled': false}));
      expect(UnityCompanionSettings.fromJson(const {}).enabled, isFalse);
    });

    test('RegisteredProject + UnityEditor round-trip and kind parsing', () {
      const project = RegisteredProject(
        path: r'C:\proj\my-world',
        name: 'My World',
        kind: ProjectKind.unityWorld,
        unityVersion: '6000.0.23f1',
        lastOpenedUtc: '2026-06-30T12:00:00Z',
      );
      final restored = RegisteredProject.fromJson(project.toJson());
      expect(restored.path, project.path);
      expect(restored.kind, ProjectKind.unityWorld);
      expect(restored.isUnity, isTrue);
      expect(restored.unityVersion, '6000.0.23f1');
      expect(restored.lastOpenedUtc, '2026-06-30T12:00:00Z');

      expect(projectKindFromString('modCSharp'), ProjectKind.modCSharp);
      expect(projectKindFromString('unityPackage'), ProjectKind.unityPackage);
      expect(projectKindFromString('bogus'), ProjectKind.unknown);
      expect(const RegisteredProject(path: 'p', name: 'n').isUnity, isFalse);

      const editor = UnityEditor(version: '6000.0.23f1', path: r'C:\unity.exe');
      final editorBack = UnityEditor.fromJson(editor.toJson());
      expect(editorBack.version, '6000.0.23f1');
      expect(editorBack.path, r'C:\unity.exe');
    });
  });
}
