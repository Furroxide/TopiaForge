part of 'robotopia.dart';

/// Top-level command names, mirroring the dispatch switch in `_RobotopiaCli.run`.
/// Used for did-you-mean suggestions on unknown commands.
const _commands = [
  'help',
  'new',
  'mod',
  'check',
  'add',
  'remove',
  'list',
  'resolve',
  'restore',
  'pack',
  'install',
  'launch',
  'restart',
  'dev-install',
  'doctor',
  'compat',
  'setup',
  'migrate',
  'ugc',
  'world',
  'projects',
  'unity',
  'updates',
  'registry',
  'release',
];

/// Shared remediation message for commands that need a detected game install.
const _noInstallRemedy =
    'Robotopia install was not detected. Set ROBOTOPIA_GAME_DIR (or select '
    'the game folder in the launcher), or run `robotopia doctor` to diagnose.';

/// Two-row dynamic-programming Levenshtein distance, for did-you-mean.
int _editDistance(String a, String b) {
  if (a == b) return 0;
  if (a.isEmpty) return b.length;
  if (b.isEmpty) return a.length;
  var previous = List<int>.generate(b.length + 1, (i) => i);
  final current = List<int>.filled(b.length + 1, 0);
  for (var i = 0; i < a.length; i++) {
    current[0] = i + 1;
    for (var j = 0; j < b.length; j++) {
      final substitution = previous[j] + (a[i] == b[j] ? 0 : 1);
      current[j + 1] = [
        previous[j + 1] + 1,
        current[j] + 1,
        substitution,
      ].reduce((x, y) => x < y ? x : y);
    }
    previous = List<int>.of(current);
  }
  return previous[b.length];
}

/// Closest known command for [input], or null when nothing is plausibly near.
String? _suggestCommand(String input) {
  final lower = input.toLowerCase();
  String? best;
  var bestDistance = 3; // suggest only when distance <= 2
  for (final command in _commands) {
    if (command.startsWith(lower)) return command;
    final distance = _editDistance(lower, command);
    if (distance < bestDistance) {
      bestDistance = distance;
      best = command;
    }
  }
  return best;
}

/// The `robotopia help` text (split out so robotopia.dart stays under the file-size cap).
extension _HelpCommand on _RobotopiaCli {
  void _printHelp() {
    stdout.writeln(
      'QuantumWorks CLI — build, package, and run Robotopia mods.',
    );
    stdout.writeln('');
    stdout.writeln('Getting started:');
    stdout.writeln(
      '  robotopia setup                       Audit the toolchain and apply safe fixes.',
    );
    stdout.writeln(
      '  robotopia doctor [--strict]           Read-only toolchain, project, and game-compat audit.',
    );
    stdout.writeln(
      '  robotopia new mod <id> [--template t] Scaffold a mod project (see `robotopia list templates`).',
    );
    stdout.writeln(
      '  robotopia list templates              List the available mod templates.',
    );
    stdout.writeln('');
    stdout.writeln('Project & manifest:');
    stdout.writeln(
      '  robotopia mod show                    Print the current robotopia.mod.json.',
    );
    stdout.writeln(
      '  robotopia mod set <field> <value>     Update a manifest field (validated on write).',
    );
    stdout.writeln(
      '  robotopia mod add|remove <kind> <v>   Add/remove tag, permission, dependency, conflict, gamemode, ...',
    );
    stdout.writeln(
      '  robotopia mod bump [major|minor|patch]  Increment the manifest version.',
    );
    stdout.writeln(
      '  robotopia check project [path]        Validate a developer project.',
    );
    stdout.writeln(
      '  robotopia check package <path>        Validate a mod folder or .robotopiamod (--sha256, --entry, --resolve).',
    );
    stdout.writeln(
      '  robotopia migrate legacy <game> <out> Convert legacy Robotopia\\Mods entries into packages.',
    );
    stdout.writeln('');
    stdout.writeln('Packages & sources:');
    stdout.writeln(
      '  robotopia add source <id> <url>       Register a package source.',
    );
    stdout.writeln(
      '  robotopia add package <id[@range]>    Add a dependency to the project manifest.',
    );
    stdout.writeln(
      '  robotopia remove package <id>         Remove a dependency.',
    );
    stdout.writeln(
      '  robotopia list sources                List registered package sources.',
    );
    stdout.writeln(
      '  robotopia resolve [--prerelease]      Compute the dependency plan and write robotopia.lock.json.',
    );
    stdout.writeln(
      '  robotopia restore [--prerelease]      Resolve, download packages, and write lock + dev props.',
    );
    stdout.writeln('');
    stdout.writeln('Build & run:');
    stdout.writeln(
      '  robotopia pack [--output dir]         Build and package the current mod project.',
    );
    stdout.writeln(
      '  robotopia pack --all                  Pack every first-party mod under mods/.',
    );
    stdout.writeln(
      '  robotopia install [package]           Install a .robotopiamod into the detected game.',
    );
    stdout.writeln(
      '  robotopia dev-install [--game-dir p]  Install the loader + dev mods into the game.',
    );
    stdout.writeln('  robotopia launch                      Launch Robotopia.');
    stdout.writeln(
      '  robotopia restart                     Restart Robotopia.',
    );
    stdout.writeln(
      '  robotopia compat [--json]             Resolve declared game bindings against the install.',
    );
    stdout.writeln('');
    stdout.writeln('UGC live-sync:');
    stdout.writeln(
      '  robotopia ugc setup                   Configure live-sync (transport, watch folder).',
    );
    stdout.writeln(
      '  robotopia ugc dev [--project p]       One-command UGC dev loop (watch + deploy).',
    );
    stdout.writeln(
      '  robotopia ugc publish --file <p>      Publish a UGC project.',
    );
    stdout.writeln(
      '  robotopia ugc watch <folder>          Watch a folder and sync changes into the game.',
    );
    stdout.writeln(
      '  robotopia ugc status                  Show live-sync status.',
    );
    stdout.writeln(
      '  robotopia ugc go-live                 Promote the current UGC session.',
    );
    stdout.writeln('');
    stdout.writeln('Unity & worlds:');
    stdout.writeln(
      '  robotopia new unity-world <name>      Scaffold a Unity world project paired with a mod.',
    );
    stdout.writeln(
      '  robotopia world link --project <p> --mod <m>  Pair a Unity project with a world mod.',
    );
    stdout.writeln(
      '  robotopia world build [--project p]   Build the world asset bundle via Unity.',
    );
    stdout.writeln(
      '  robotopia world play [--project p]    Build, install, and launch the world mod.',
    );
    stdout.writeln(
      '  robotopia unity <subcommand>          Unity package management (new-package, resolve, add, remove,',
    );
    stdout.writeln(
      '                                        list, repos, add-repo, pack-packages).',
    );
    stdout.writeln(
      '  robotopia unity build-ui-bundle       Rebuild the embedded QuantumWorks brand bundle (repo maintainers).',
    );
    stdout.writeln(
      '  robotopia projects list|add|remove|open  Manage registered Unity projects.',
    );
    stdout.writeln('');
    stdout.writeln('Publish & registry:');
    stdout.writeln(
      '  robotopia registry add-entry <pkg> --url <url>  Create your registry/<id>.json entry for a PR.',
    );
    stdout.writeln(
      '  robotopia registry index ...          Build a registry index.json (official or self-hosted).',
    );
    stdout.writeln(
      '  robotopia registry validate           Check registry entries the way the official CI does.',
    );
    stdout.writeln('');
    stdout.writeln('Maintenance:');
    stdout.writeln(
      '  robotopia updates index --repository owner/name --output path  Build a launcher update index.',
    );
    stdout.writeln(
      '  robotopia release build-package ...   Build a release zip (CI maintainers).',
    );
    stdout.writeln(
      '  robotopia release test-package ...    Smoke-test a release zip (CI maintainers).',
    );
    stdout.writeln('');
    stdout.writeln(
      'Run a command with wrong or missing arguments to see its full usage.',
    );
    stdout.writeln('Exit codes: 0 ok, 1 failure, 2 usage error.');
    stdout.writeln(
      'Docs: docs/YourFirstMod.md (walkthrough), docs/Modding.md (reference), '
      'docs/PublishingYourMod.md (publish).',
    );
  }
}
