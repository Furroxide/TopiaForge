import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_contract_pack_fixture.dart';

// Opt in with an already installed, pinned .NET host. The regression performs
// genuine offline compilation; it must not bootstrap or download a toolchain.
void main() {
  final dotnet = Platform.environment['TOPIAFORGE_TEST_DOTNET'];
  for (var attempt = 1; attempt <= 3; attempt++) {
    test(
      'clean pack $attempt uses one node and a private compiler',
      () async {
        final fixture = MultiplayerContractPackFixture.create();
        addTearDown(fixture.dispose);
        fixture.writeManifest();
        const sources =
            '<configuration><packageSources><clear /></packageSources></configuration>';
        for (final directory in [fixture.repoRoot, fixture.projectDir]) {
          File(
            p.join(directory.path, 'NuGet.Config'),
          ).writeAsStringSync(sources);
        }
        File(
          p.join(fixture.projectDir.path, 'Sample.csproj'),
        ).writeAsStringSync(r'''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <Target Name="RecordWorkerPolicy" AfterTargets="Build">
    <WriteLinesToFile File="$(MSBuildProjectDirectory)/worker-policy.txt"
      Lines="$(MSBuildNodeCount);$(UseSharedCompilation)" Overwrite="true" />
  </Target>
</Project>
''');
        File(p.join(fixture.projectDir.path, 'Entry.cs')).writeAsStringSync(
          'namespace Sample { public sealed class Entry { } }',
        );
        fixture.repository = LocalDeveloperRepository(
          dataRoot: p.join(fixture.root.path, 'data'),
          repositoryRoot: fixture.repoRoot.path,
          dotnetSdkResolver: (_) async => DotnetSdkSelection(
            executable: dotnet!,
            version: '10.0.301',
            requiredVersion: '10.0.301',
          ),
        );
        final archive = fixture.readPackage(
          await fixture.repository.packModDirectory(fixture.projectDir.path),
        );
        expect(
          archive.files.where((file) => file.name == 'Sample.dll'),
          hasLength(1),
        );
        // Observe effective MSBuild values, rather than a spelling of CLI flags.
        expect(
          File(p.join(fixture.projectDir.path, 'worker-policy.txt'))
              .readAsLinesSync()
              .map((line) => line.trim())
              .where((line) => line.isNotEmpty),
          ['1', 'false'],
        );
      },
      skip: dotnet == null
          ? 'Needs an explicitly selected existing .NET 10.0.301 host.'
          : false,
    );
  }
}
