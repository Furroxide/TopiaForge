part of 'sdk_reference_pack.dart';

SdkNuGetPackage _writePackage(
  Directory destination,
  String id,
  String version,
  List<int> bytes,
  String kind,
  List<String> dependencies,
  List<String> runtimeAssemblyIds,
) {
  final relative = '${SdkReferencePack.feedPath}/$id.$version.nupkg';
  final file = File(p.join(destination.path, p.fromUri(relative)));
  file.parent.createSync(recursive: true);
  file.writeAsBytesSync(bytes, flush: true);
  return SdkNuGetPackage(
    id: id,
    version: version,
    path: relative,
    sha256: sha256.convert(bytes).toString(),
    kind: kind,
    dependencies: List.unmodifiable(dependencies),
    runtimeAssemblyIds: List.unmodifiable(runtimeAssemblyIds),
  );
}

List<int> _contractNuGetPackage({
  required String packageId,
  required String version,
  required File reference,
  required File documentation,
  required File? runtime,
  required List<File> runtimeSupport,
  required List<String> dependencies,
  required File? customProps,
  required File? customTargets,
}) {
  _requireRegularFile(reference, maxBytes: SdkReferencePack.maxFileBytes);
  _requireRegularFile(documentation, maxBytes: SdkReferencePack.maxFileBytes);
  if (runtime != null) {
    _requireRegularFile(runtime, maxBytes: SdkReferencePack.maxFileBytes);
  }
  for (final support in runtimeSupport) {
    _requireRegularFile(support, maxBytes: SdkReferencePack.maxFileBytes);
  }
  if (customProps != null) {
    _requireRegularFile(customProps, maxBytes: SdkReferencePack.maxFileBytes);
  }
  if (customTargets != null) {
    _requireRegularFile(customTargets, maxBytes: SdkReferencePack.maxFileBytes);
  }
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        '$packageId.nuspec',
        _nuspec(packageId, version, dependencies, developmentDependency: false),
      ),
    )
    ..addFile(
      ArchiveFile.bytes(
        'ref/netstandard2.1/$packageId.dll',
        reference.readAsBytesSync(),
      ),
    )
    ..addFile(
      ArchiveFile.bytes(
        'ref/netstandard2.1/$packageId.xml',
        documentation.readAsBytesSync(),
      ),
    )
    ..addFile(
      ArchiveFile.string('build/$packageId.props', _packageProps(version)),
    )
    ..addFile(ArchiveFile.string('build/$packageId.targets', _packageTargets()))
    ..addFile(
      ArchiveFile.string(
        'buildTransitive/$packageId.props',
        customProps?.readAsStringSync() ?? _packageProps(version),
      ),
    )
    ..addFile(
      ArchiveFile.string(
        'buildTransitive/$packageId.targets',
        customTargets?.readAsStringSync() ?? _packageTargets(),
      ),
    );
  if (runtime != null) {
    archive.addFile(
      ArchiveFile.bytes(
        'lib/netstandard2.1/$packageId.dll',
        runtime.readAsBytesSync(),
      ),
    );
  }
  for (final support in runtimeSupport) {
    archive.addFile(
      ArchiveFile.bytes(
        'lib/netstandard2.1/${p.basename(support.path)}',
        support.readAsBytesSync(),
      ),
    );
  }
  return ZipEncoder().encode(archive, modified: _nugetTimestamp);
}

List<int> _analyzerNuGetPackage({
  required String packageId,
  required String version,
  required File analyzer,
  required File? customProps,
  required File? customTargets,
}) {
  _requireRegularFile(analyzer, maxBytes: SdkReferencePack.maxFileBytes);
  if (customProps != null) {
    _requireRegularFile(customProps, maxBytes: SdkReferencePack.maxFileBytes);
  }
  if (customTargets != null) {
    _requireRegularFile(customTargets, maxBytes: SdkReferencePack.maxFileBytes);
  }
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        '$packageId.nuspec',
        _nuspec(packageId, version, const [], developmentDependency: true),
      ),
    )
    ..addFile(
      ArchiveFile.bytes(
        'analyzers/dotnet/cs/$packageId.dll',
        analyzer.readAsBytesSync(),
      ),
    )
    ..addFile(
      ArchiveFile.string(
        'buildTransitive/$packageId.props',
        customProps?.readAsStringSync() ?? _analyzerProps(),
      ),
    )
    ..addFile(
      ArchiveFile.string(
        'buildTransitive/$packageId.targets',
        customTargets?.readAsStringSync() ?? _analyzerTargets(),
      ),
    );
  return ZipEncoder().encode(archive, modified: _nugetTimestamp);
}

String _nuspec(
  String id,
  String version,
  List<String> dependencies, {
  required bool developmentDependency,
}) {
  final dependencyXml = dependencies.isEmpty
      ? ''
      : '''
      <dependencies>
        <group targetFramework=".NETStandard2.1">
${dependencies.map((dependency) => '          <dependency id="${_xml(dependency)}" version="[$version]" />').join('\n')}
        </group>
      </dependencies>''';
  return '''<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>${_xml(id)}</id>
    <version>${_xml(version)}</version>
    <authors>TopiaForge</authors>
    <description>TopiaForge V1 mod SDK package.</description>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <developmentDependency>${developmentDependency ? 'true' : 'false'}</developmentDependency>$dependencyXml
  </metadata>
</package>
''';
}

String _packageProps(String version) =>
    '''<Project>
  <PropertyGroup>
    <TopiaForgeSdkVersion Condition="'\$(TopiaForgeSdkVersion)' == ''">$version</TopiaForgeSdkVersion>
  </PropertyGroup>
</Project>
''';

String _packageTargets() => '''<Project>
  <Target Name="TopiaForgeValidateTargetFramework" BeforeTargets="CoreCompile"
          Condition="'\$(TopiaForgeSafeProject)' != 'false' and '\$(IsTestProject)' != 'true' and '\$(TargetFramework)' != 'netstandard2.1'">
    <Error Code="TF0004" Text="TopiaForge V1 safe mod projects must target netstandard2.1." />
  </Target>
</Project>
''';

String _analyzerProps() => '''<Project>
  <PropertyGroup>
    <TopiaForgeSafeProject Condition="'\$(TopiaForgeSafeProject)' == ''">true</TopiaForgeSafeProject>
  </PropertyGroup>
</Project>
''';

String _analyzerTargets() => '''<Project>
  <Target Name="TopiaForgeRejectCopiedSdkAssemblies" BeforeTargets="BeforeBuild"
          Condition="Exists('\$(MSBuildProjectDirectory)/TopiaForge.Mods.Abstractions.dll')">
    <Error Code="TF0005" Text="Do not copy SDK runtime assemblies into a mod project. Use exact PackageReference items instead." />
  </Target>
</Project>
''';

void _validateNuGetPackage(SdkNuGetPackage package, List<int> bytes) {
  final archive = SafeZipArchive.decode(
    bytes,
    policy: const SafeArchivePolicy(
      maxArchiveBytes: SdkReferencePack.maxFileBytes,
      maxEntries: 64,
      maxEntryBytes: SdkReferencePack.maxFileBytes,
      maxExpandedBytes: 256 * 1024 * 1024,
    ),
    label: 'SDK NuGet package ${package.id}',
  );
  final names = <String>{};
  for (final entry in archive.entries) {
    if (!entry.isFile) continue;
    final name = _safeRelativePath(entry.name);
    if (!names.add(name)) {
      throw StateError('NuGet package ${package.id} has duplicate $name.');
    }
  }
  final nuspecName = '${package.id}.nuspec';
  if (!names.contains(nuspecName)) {
    throw StateError('NuGet package ${package.id} has no nuspec.');
  }
  final nuspec = archive.entryNamed(nuspecName);
  if (nuspec == null) {
    throw StateError('NuGet package ${package.id} has no nuspec.');
  }
  final nuspecText = utf8.decode(
    nuspec.readBytes(maxBytes: 1024 * 1024, label: '${package.id} nuspec'),
  );
  if (!nuspecText.contains('<id>${package.id}</id>') ||
      !nuspecText.contains('<version>${package.version}</version>')) {
    throw StateError('NuGet package ${package.id} identity is inconsistent.');
  }
  if (package.isContract) {
    for (final required in [
      'ref/netstandard2.1/${package.id}.dll',
      'ref/netstandard2.1/${package.id}.xml',
      'build/${package.id}.props',
      'build/${package.id}.targets',
      'buildTransitive/${package.id}.props',
      'buildTransitive/${package.id}.targets',
    ]) {
      if (!names.contains(required)) {
        throw StateError('NuGet package ${package.id} is missing $required.');
      }
    }
    final libPrefix = 'lib/netstandard2.1/';
    final runtimeFiles = names
        .where((name) => name.startsWith(libPrefix))
        .toSet();
    final expectedRuntimeFiles = package.runtimeAssemblyIds
        .map((id) => '$libPrefix$id.dll')
        .toSet();
    if (package.hasRuntimeAssembly) {
      if (runtimeFiles.length != expectedRuntimeFiles.length ||
          !runtimeFiles.containsAll(expectedRuntimeFiles)) {
        throw StateError(
          'Testing package ${package.id} has an inconsistent runtime inventory.',
        );
      }
    } else if (runtimeFiles.isNotEmpty) {
      throw StateError(
        'Contract package ${package.id} must remain reference-only.',
      );
    }
  } else {
    if (!names.contains('analyzers/dotnet/cs/${package.id}.dll') ||
        !names.contains('buildTransitive/${package.id}.props') ||
        !names.contains('buildTransitive/${package.id}.targets')) {
      throw StateError('Analyzer package ${package.id} is incomplete.');
    }
  }
}

void _requireRegularFile(File file, {required int maxBytes}) {
  final type = FileSystemEntity.typeSync(file.path, followLinks: false);
  if (type != FileSystemEntityType.file) {
    throw StateError('Expected a regular SDK file: ${file.path}');
  }
  if (file.lengthSync() > maxBytes) {
    throw StateError('SDK file exceeds the size limit: ${file.path}');
  }
}

String _safeRelativePath(String input) {
  final normalized = input.replaceAll('\\', '/');
  if (normalized.isEmpty ||
      normalized.startsWith('/') ||
      normalized
          .split('/')
          .any((part) => part.isEmpty || part == '.' || part == '..')) {
    throw StateError('Unsafe SDK package path: $input');
  }
  return normalized;
}

String _safePackageId(String input) {
  if (!_packageId.hasMatch(input)) {
    throw StateError('Invalid SDK package id: $input');
  }
  return input;
}

String _xml(String input) => input
    .replaceAll('&', '&amp;')
    .replaceAll('"', '&quot;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');

final _packageId = RegExp(r'^[A-Za-z0-9]+(?:[._-][A-Za-z0-9]+)*$');
final _semanticVersion = RegExp(
  r'^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$',
);
final _sha256Pattern = RegExp(r'^[0-9a-f]{64}$');
final _nugetTimestamp = DateTime(1980, 1, 1);
