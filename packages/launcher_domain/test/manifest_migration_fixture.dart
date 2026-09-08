Map<String, Object?> migrationFixture(int version) => {
  'schemaVersion': version,
  'name': 'example.mod',
  'displayName': 'Example',
  'version': '0.1.0',
  'author': {'name': 'Example Author'},
  'entryAssembly': 'Example.dll',
  'entryType': 'Example.Mod',
  'supportedGameVersionRange': '>=0.0.2409',
  'supportedLoaderVersionRange': '*',
  'supportedSdkVersionRange': '*',
};
