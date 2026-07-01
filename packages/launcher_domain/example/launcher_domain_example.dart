import 'package:launcher_domain/launcher_domain.dart';

void main() {
  final manifest = ModManifest(
    schemaVersion: 1,
    id: 'example.mod',
    name: 'Example Mod',
    version: '1.0.0',
    entryAssembly: 'Example.dll',
    entryType: 'Example.Entry',
  );
  print(manifest.toJson());
}
