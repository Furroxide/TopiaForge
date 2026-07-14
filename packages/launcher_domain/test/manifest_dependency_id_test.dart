import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('ModManifest dependency identifiers', () {
    test('rejects unsafe dependency, conflict, and load-order ids', () {
      final manifest = _manifest(
        'main.mod',
        dependencies: const [ModDependency(id: '../required')],
        optionalDependencies: const [ModDependency(id: r'..\optional')],
        conflicts: const [ModConflict(id: '/conflict')],
        loadAfter: const ['safe.mod', '../load-after'],
      );

      final blocking = manifest
          .validate()
          .where((issue) => issue.isBlocking)
          .toList();

      expect(blocking.map((issue) => issue.subjectId), contains('../required'));
      expect(
        blocking.map((issue) => issue.subjectId),
        contains(r'..\optional'),
      );
      expect(blocking.map((issue) => issue.subjectId), contains('/conflict'));
      expect(
        blocking.map((issue) => issue.subjectId),
        contains('../load-after'),
      );
    });

    test('invalid registry manifests cannot satisfy dependencies', () {
      final candidate = _manifest(
        'main.mod',
        dependencies: const [ModDependency(id: 'feature.mod')],
      );
      final invalidFeature = _manifest(
        'feature.mod',
        dependencies: const [ModDependency(id: '../outside')],
      );

      final plan = const DependencyPlanner().previewInstall(
        candidate,
        const [],
        availableMods: [_registry(invalidFeature)],
      );

      expect(plan.hasBlockingIssues, isTrue);
      expect(
        plan.installActions.where((action) => action.modId == 'feature.mod'),
        isEmpty,
      );
    });
  });
}

ModManifest _manifest(
  String id, {
  List<ModDependency> dependencies = const [],
  List<ModDependency> optionalDependencies = const [],
  List<ModConflict> conflicts = const [],
  List<String> loadAfter = const [],
}) => ModManifest(
  schemaVersion: 2,
  id: id,
  name: id,
  version: '1.0.0',
  author: const ModAuthor(name: 'QuantumWorks'),
  entryAssembly: '$id.dll',
  entryType: '$id.Entry',
  dependencies: dependencies,
  optionalDependencies: optionalDependencies,
  conflicts: conflicts,
  loadAfter: loadAfter,
);

RegistryMod _registry(ModManifest manifest) => RegistryMod(
  manifest: manifest,
  downloadUrl: 'file:///${manifest.id}.robotopiamod',
  packageSha256: List.filled(64, 'a').join(),
);
