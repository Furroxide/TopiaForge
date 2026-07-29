import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('publisher command keeps paths and URLs as literal arguments', () {
    final command = UgcPublisherCommand.forSettings(
      sidecarPath: r'C:\TopiaForge Tools\sidecar\index.mjs',
      sessionFilePath: r'C:\Users\Test User\ugc-session.json',
      settings: const UgcLiveSyncSettings(
        watchFolder: r'C:\UGC Worlds\watch & review',
        syncServerUrl: 'https://sync.example.test/?room=a&mode=write',
        documentUrl: 'automerge:abc?scene=main&mode=edit',
        sceneId: 'main scene',
      ),
    );

    expect(command.executable, 'node');
    expect(command.arguments, [
      r'C:\TopiaForge Tools\sidecar\index.mjs',
      '--watch',
      r'C:\UGC Worlds\watch & review',
      '--sync',
      'https://sync.example.test/?room=a&mode=write',
      '--session-file',
      r'C:\Users\Test User\ugc-session.json',
      '--doc',
      'automerge:abc?scene=main&mode=edit',
      '--scene',
      'main scene',
    ]);
  });
}
