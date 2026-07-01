import 'package:launcher_data/launcher_data.dart';

void main() {
  final repository = LocalLauncherRepository();
  print('launcher data root: ${repository.dataRoot}');
}
