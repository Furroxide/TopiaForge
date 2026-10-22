import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_contract_pack_fixture.dart';

void main() {
  late MultiplayerContractPackFixture fixture;

  setUp(() => fixture = MultiplayerContractPackFixture.create());
  tearDown(() => fixture.dispose());

  test('packs the contract lock as synchronized session content', () async {
    fixture.writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
        'synchronizedFiles': ['Content/gameplay-rules.json'],
      },
    });
    File(
      p.join(fixture.projectDir.path, 'Sample.dll'),
    ).writeAsStringSync('payload');
    File(p.join(fixture.projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    await fixture.useFakeMultiplayerBuild(emitContract: false);
    final rules = File(
      p.join(fixture.projectDir.path, 'Content', 'gameplay-rules.json'),
    )..createSync(recursive: true);
    rules.writeAsStringSync('{"difficulty":"hard"}');
    fixture.writeEmptyContractLock();

    final archive = fixture.readPackage(
      await fixture.repository.packModDirectory(fixture.projectDir.path),
    );
    final packed = fixture.packedManifest(archive);
    final archivedLock = archive.files.singleWhere(
      (file) => file.name == 'topiaforge.multiplayer.lock.json',
    );
    final multiplayer = packed['multiplayer'] as Map<String, Object?>;
    final hashes = packed['hashes'] as Map;

    expect(multiplayer['synchronizedFiles'], [
      'Content/gameplay-rules.json',
      'topiaforge.multiplayer.lock.json',
    ]);
    expect(
      hashes['Content/gameplay-rules.json'],
      sha256.convert(rules.readAsBytesSync()).toString(),
    );
    expect(
      hashes['topiaforge.multiplayer.lock.json'],
      sha256.convert(archivedLock.content as List<int>).toString(),
    );
    final source =
        jsonDecode(
              File(
                p.join(fixture.projectDir.path, 'topiaforge.mod.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(source, isNot(contains('hashes')));
    expect((source['multiplayer'] as Map)['synchronizedFiles'], [
      'Content/gameplay-rules.json',
    ]);
  });

  test(
    'canonicalizes an already declared lock without duplicating it',
    () async {
      fixture.writeManifest({
        'multiplayer': {
          'mode': 'session',
          'presence': 'required',
          'protocol': {'version': '1.0.0'},
          'synchronizedFiles': ['TopiaForge.Multiplayer.Lock.Json'],
        },
      });
      File(
        p.join(fixture.projectDir.path, 'Sample.dll'),
      ).writeAsStringSync('payload');
      File(p.join(fixture.projectDir.path, 'Sample.csproj')).writeAsStringSync(
        '''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''',
      );
      await fixture.useFakeMultiplayerBuild(emitContract: false);
      fixture.writeEmptyContractLock();

      final archive = fixture.readPackage(
        await fixture.repository.packModDirectory(fixture.projectDir.path),
      );
      final packed = fixture.packedManifest(archive);
      final multiplayer = packed['multiplayer'] as Map<String, Object?>;

      expect(multiplayer['synchronizedFiles'], [
        'topiaforge.multiplayer.lock.json',
      ]);
      expect((packed['hashes'] as Map), hasLength(1));
    },
  );

  test('reserves one synchronized-file slot for the generated lock', () async {
    fixture.writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
        'synchronizedFiles': [
          for (var index = 0; index < 256; index++) 'Content/$index.json',
        ],
      },
    });
    File(
      p.join(fixture.projectDir.path, 'Sample.dll'),
    ).writeAsStringSync('payload');
    fixture.writeEmptyContractLock();

    await expectLater(
      fixture.repository.packModDirectory(fixture.projectDir.path),
      throwsA(
        predicate(
          (error) =>
              error.toString().contains('at most 255 synchronized files') &&
              error.toString().contains('synchronized automatically'),
        ),
      ),
    );
  });

  test('does not add admission metadata to standalone packages', () async {
    fixture.writeManifest();
    File(
      p.join(fixture.projectDir.path, 'Sample.dll'),
    ).writeAsStringSync('payload');

    final archive = fixture.readPackage(
      await fixture.repository.packModDirectory(fixture.projectDir.path),
    );
    final packed = fixture.packedManifest(archive);

    expect(packed, isNot(contains('multiplayer')));
    expect(packed, isNot(contains('hashes')));
    expect(
      archive.files.map((file) => file.name),
      isNot(contains('topiaforge.multiplayer.lock.json')),
    );
  });

  test('does not synchronize the lock for non-session modes', () async {
    fixture.writeManifest({
      'multiplayer': {'mode': 'client-local'},
    });
    File(
      p.join(fixture.projectDir.path, 'Sample.dll'),
    ).writeAsStringSync('payload');
    File(p.join(fixture.projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    await fixture.useFakeMultiplayerBuild(emitContract: false);
    fixture.writeEmptyContractLock();

    final archive = fixture.readPackage(
      await fixture.repository.packModDirectory(fixture.projectDir.path),
    );
    final packed = fixture.packedManifest(archive);

    expect(packed['multiplayer'], {'mode': 'client-local'});
    expect(packed, isNot(contains('hashes')));
  });

  test('packs non-session modes that carry no contract lock', () async {
    // Admission compares only a session mod's lock, so no other mode needs
    // one: first-party Performance Fixes is client-local and carries none.
    for (final mode in ['client-local', 'server-only', 'standalone']) {
      fixture.writeManifest({
        'multiplayer': {'mode': mode},
      });
      File(
        p.join(fixture.projectDir.path, 'Sample.dll'),
      ).writeAsStringSync('payload');

      final archive = fixture.readPackage(
        await fixture.repository.packModDirectory(fixture.projectDir.path),
      );
      final packed = fixture.packedManifest(archive);

      expect(packed['multiplayer'], {'mode': mode}, reason: mode);
      expect(packed, isNot(contains('hashes')), reason: mode);
      expect(
        archive.files.map((file) => file.name),
        isNot(contains('topiaforge.multiplayer.lock.json')),
        reason: mode,
      );
    }
  });

  test('still verifies a lock that a non-session mod carries', () async {
    fixture.writeManifest({
      'multiplayer': {'mode': 'client-local'},
    });
    File(p.join(fixture.projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    await fixture.useFakeMultiplayerBuild();
    fixture.writeEmptyContractLock();

    await expectLater(
      fixture.repository.packModDirectory(fixture.projectDir.path),
      throwsA(
        predicate((error) => error.toString().contains('stale or tampered')),
      ),
    );
  });

  test('sync generates and pack verifies the checked-in lock', () async {
    fixture.writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
      },
    });
    File(p.join(fixture.projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    await fixture.useFakeMultiplayerBuild();

    final lockPath = await fixture.repository
        .synchronizeMultiplayerContractLock(fixture.projectDir.path);
    final lock = jsonDecode(File(lockPath).readAsStringSync()) as Map;
    expect(lock['schemaVersion'], multiplayerContractLockSchemaVersion);
    // The lock is a synchronized file, so it must not hash the protocol
    // version that peerVersionRange negotiates.
    expect(lock, isNot(contains('protocolVersion')));
    expect((lock['contracts'] as List).single, {
      'id': 'sample.contract',
      'wireFormatRevision': 1,
      'schemaSha256':
          '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef',
      'stateIds': ['sample.contract/state'],
      'commandIds': ['sample.contract/command'],
      'objectTypeIds': ['sample.contract/object'],
      'eventIds': ['sample.contract/event'],
    });

    final archive = fixture.readPackage(
      await fixture.repository.packModDirectory(fixture.projectDir.path),
    );
    final archivedLock = archive.files.singleWhere(
      (file) => file.name == 'topiaforge.multiplayer.lock.json',
    );
    expect(jsonDecode(utf8.decode(archivedLock.content as List<int>)), lock);
  });

  test('rejects generated wire revisions outside positive Int32', () async {
    fixture.writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
      },
    });
    File(p.join(fixture.projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    for (final revision in ['0', '2147483648']) {
      await fixture.useFakeMultiplayerBuild(wireFormatRevision: revision);
      await expectLater(
        fixture.repository.synchronizeMultiplayerContractLock(
          fixture.projectDir.path,
        ),
        throwsA(
          predicate(
            (error) => error.toString().contains(
              'generator emitted invalid contract metadata',
            ),
          ),
        ),
      );
    }
  });

  test('pack rejects stale, missing, and arbitrary contract locks', () async {
    fixture.writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
      },
    });
    File(p.join(fixture.projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    await fixture.useFakeMultiplayerBuild();

    await expectLater(
      fixture.repository.packModDirectory(fixture.projectDir.path),
      throwsA(predicate((error) => error.toString().contains('is missing'))),
    );
    fixture.writeEmptyContractLock();
    await expectLater(
      fixture.repository.packModDirectory(fixture.projectDir.path),
      throwsA(
        predicate(
          (error) =>
              error.toString().contains('stale or tampered') &&
              error.toString().contains('mod sync multiplayer'),
        ),
      ),
    );
    File(
      p.join(fixture.projectDir.path, 'topiaforge.multiplayer.lock.json'),
    ).writeAsStringSync('{"schemaVersion":3,"contracts":[{"id":"guess"}]}');
    await expectLater(
      fixture.repository.packModDirectory(fixture.projectDir.path),
      throwsA(
        predicate((error) => error.toString().contains('stale or tampered')),
      ),
    );

    // A lock written before schemaVersion 3 gets a specific refresh path.
    File(
      p.join(fixture.projectDir.path, 'topiaforge.multiplayer.lock.json'),
    ).writeAsStringSync(
      '{"schemaVersion":2,"protocolVersion":"1.0.0","contracts":[]}',
    );
    await expectLater(
      fixture.repository.packModDirectory(fixture.projectDir.path),
      throwsA(
        predicate(
          (error) =>
              error.toString().contains('lock schemaVersion 2') &&
              error.toString().contains('mod sync multiplayer'),
        ),
      ),
    );
  });

  test('rejects a synchronized file absent from the package', () async {
    fixture.writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
        'synchronizedFiles': ['Content/missing.json'],
      },
    });
    File(
      p.join(fixture.projectDir.path, 'Sample.dll'),
    ).writeAsStringSync('payload');
    File(p.join(fixture.projectDir.path, 'Sample.csproj')).writeAsStringSync('''
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
''');
    await fixture.useFakeMultiplayerBuild(emitContract: false);
    fixture.writeEmptyContractLock();

    await expectLater(
      fixture.repository.packModDirectory(fixture.projectDir.path),
      throwsA(
        predicate(
          (error) =>
              error.toString().contains('was not included in the package'),
        ),
      ),
    );
  });

  test('rejects source-less multiplayer synchronization and packing', () async {
    fixture.writeManifest({
      'multiplayer': {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0'},
      },
    });
    File(
      p.join(fixture.projectDir.path, 'Sample.dll'),
    ).writeAsStringSync('payload');
    fixture.writeEmptyContractLock();

    for (final operation in <Future<Object?> Function()>[
      () => fixture.repository.synchronizeMultiplayerContractLock(
        fixture.projectDir.path,
      ),
      () => fixture.repository.packModDirectory(fixture.projectDir.path),
    ]) {
      await expectLater(
        operation(),
        throwsA(
          predicate(
            (error) =>
                error.toString().contains('one root C# project') &&
                error.toString().contains('precompiled-only'),
          ),
        ),
      );
    }
  });
}
