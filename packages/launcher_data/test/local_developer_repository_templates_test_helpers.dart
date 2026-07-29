import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void expectServiceTemplateContract({
  required String projectRoot,
  required String assembly,
  required String mainProjectText,
  required String testProjectText,
  required List<String> apiAssemblies,
}) {
  final apiAssembly = '$assembly.Api';
  expect(apiAssemblies, ['$apiAssembly.dll']);
  expect(
    mainProjectText,
    allOf(
      contains(
        '<ProjectReference Include="contracts\\$apiAssembly\\$apiAssembly.csproj" />',
      ),
      contains('<Compile Remove="contracts\\**\\*.cs" />'),
    ),
  );

  final contractProject = File(
    p.join(projectRoot, 'contracts', apiAssembly, '$apiAssembly.csproj'),
  );
  expect(contractProject.existsSync(), isTrue);
  expect(
    contractProject.readAsStringSync(),
    allOf(
      contains('<AssemblyName>$apiAssembly</AssemblyName>'),
      contains('<TargetFramework>netstandard2.1</TargetFramework>'),
      contains('<GenerateDocumentationFile>true</GenerateDocumentationFile>'),
    ),
  );
  final contractSources = contractProject.parent
      .listSync()
      .whereType<File>()
      .where(
        (file) =>
            p.basename(file.path).startsWith('I') &&
            p.basename(file.path).endsWith('Service.cs'),
      );
  expect(contractSources, hasLength(1));
  expect(
    testProjectText,
    contains(
      '<ProjectReference Include="..\\..\\contracts\\$apiAssembly\\$apiAssembly.csproj" />',
    ),
  );
}
