import 'package:launcher_data/launcher_data.dart';
import 'package:test/test.dart';

void main() {
  test('link handling starts on, as the plan defaults it', () async {
    final setting = TransientSessionLinkSetting();
    addTearDown(setting.close);

    expect(await setting.read(), isTrue);
  });

  test('a write is read back and reported once per change', () async {
    final setting = TransientSessionLinkSetting();
    addTearDown(setting.close);
    final changes = <bool>[];
    final subscription = setting.changes.listen(changes.add);
    addTearDown(subscription.cancel);

    await setting.write(false);
    await setting.write(false);
    await setting.write(true);
    await pumpEventQueue();

    expect(await setting.read(), isTrue);
    expect(changes, [false, true]);
  });

  test('it lives only as long as the launcher runs', () async {
    final first = TransientSessionLinkSetting();
    await first.write(false);
    await first.close();

    expect(await TransientSessionLinkSetting().read(), isTrue);
    await first.write(true);
    expect(await first.read(), isTrue);
  });
}
