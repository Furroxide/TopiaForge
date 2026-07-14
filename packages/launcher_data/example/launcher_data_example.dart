import 'package:launcher_data/launcher_data.dart';

Future<void> main() async {
  final repository = LocalLauncherRepository();
  try {
    print('launcher data root: ${repository.dataRoot}');
  } finally {
    await repository.dispose();
  }
}
