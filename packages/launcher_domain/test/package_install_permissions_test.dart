import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('install plan aggregates root and dependency capabilities', () {
    final dependency = ModManifest(
      schemaVersion: 4,
      id: 'dependency.mod',
      name: 'Dependency',
      version: '1.0.0',
      author: const ModAuthor(name: 'Author'),
      description: 'Dependency',
      entryAssembly: 'Dependency.dll',
      entryType: 'Dependency.Mod',
      capabilities: const ['filesystem', 'network'],
    );
    final root = ModManifest(
      schemaVersion: 4,
      id: 'root.mod',
      name: 'Root',
      version: '1.0.0',
      author: const ModAuthor(name: 'Author'),
      description: 'Root',
      entryAssembly: 'Root.dll',
      entryType: 'Root.Mod',
      capabilities: const ['input', 'network'],
      dependencies: const [ModDependency(id: 'dependency.mod')],
    );

    final plan = const DependencyPlanner().previewInstall(
      root,
      const [],
      availableMods: [
        RegistryMod(
          manifest: dependency,
          downloadUrl: 'file:///dependency.zip',
        ),
      ],
    );

    expect(plan.requiredPermissions, ['filesystem', 'input', 'network']);
  });
}
