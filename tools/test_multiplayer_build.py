import importlib.util
from pathlib import Path
import unittest
from unittest.mock import Mock, patch
import io
import json
import os
import sys
import tempfile

spec = importlib.util.spec_from_file_location('multiplayer_build', Path(__file__).with_name('multiplayer-build.py'))
build = importlib.util.module_from_spec(spec)
spec.loader.exec_module(build)


class MultiplayerBuildTests(unittest.TestCase):
    def test_default_executable_uses_path_and_platform_extensions(self):
        suffixes = ('.exe', '.bat') if os.name == 'nt' else ('',)
        for kind, arguments in (('dart', ['compile', 'exe', 'bin/topiaforge.dart']),
                                ('flutter', ['build', 'windows'])):
            for suffix in suffixes:
                with self.subTest(kind=kind, suffix=suffix), tempfile.TemporaryDirectory() as folder:
                    executable = Path(folder) / (kind + suffix)
                    executable.touch(mode=0o755)
                    with patch.dict(os.environ, PATH=folder, PATHEXT='.EXE;.BAT'), \
                         patch.object(sys, 'argv', ['multiplayer-build.py', '--environment', 'offline',
                                                   '--kind', kind, '--', *arguments]), \
                         patch.object(build.subprocess, 'call', return_value=0) as call:
                        self.assertEqual(build.main(), 0)
                        self.assertEqual(Path(call.call_args.args[0][0]), executable)

    def test_explicit_executable_is_preserved(self):
        with patch.object(sys, 'argv', ['multiplayer-build.py', '--environment', 'offline',
                                       '--kind', 'dart', '--executable', '/pinned/dart',
                                       '--', 'compile', 'exe', 'bin/topiaforge.dart']), \
             patch.object(build.shutil, 'which') as which, \
             patch.object(build.subprocess, 'call', return_value=7) as call:
            self.assertEqual(build.main(), 7)
            self.assertEqual(call.call_args.args[0][0], '/pinned/dart')
            which.assert_not_called()

    def test_missing_executable_refuses_without_starting_build(self):
        with patch.object(sys, 'argv', ['multiplayer-build.py', '--environment', 'offline',
                                       '--kind', 'dart', '--', 'compile', 'exe', 'bin/topiaforge.dart']), \
             patch.object(build.shutil, 'which', return_value=None), \
             patch.object(build.subprocess, 'call') as call:
            self.assertEqual(build.main(), 1)
            call.assert_not_called()

    def test_both_clients_receive_same_origin(self):
        for environment, origin in build.ORIGINS.items():
            with self.subTest(environment=environment):
                flutter = build.build_command('flutter', 'flutter', ['build', 'windows'], origin)
                dart = build.build_command('dart', 'dart', ['compile', 'exe', 'bin/topiaforge.dart'], origin)
                self.assertIn('--dart-define=TOPIAFORGE_CONTROL_PLANE_ORIGIN=' + origin, flutter)
                self.assertIn('-DTOPIAFORGE_CONTROL_PLANE_ORIGIN=' + origin, dart)

    def test_conflicting_defines_cannot_override_selected_environment(self):
        for option in ('--dart-define=TOPIAFORGE_CONTROL_PLANE_ORIGIN=', '--dart-define-from-file=other.json'):
            with self.assertRaises(ValueError):
                build.build_command('flutter', 'flutter', ['build', 'windows', option], build.ORIGINS['staging'])

    def test_status_must_confirm_all_features_and_no_kill_switch(self):
        healthy = dict(schema='topiaforge.control-plane-status/1', api=1, killSwitch=False,
                       features=dict(hosting=True, invites=True, relay=True, signaling=True))
        for field in ('healthy', 'schema', 'api', 'killSwitch', 'hosting', 'invites', 'relay', 'signaling'):
            status = json.loads(json.dumps(healthy))
            if field in status['features']:
                status['features'][field] = False
            elif field != 'healthy':
                status[field] = None
            opener = Mock()
            opener.open.return_value = io.BytesIO(json.dumps(status).encode())
            with patch.object(build.urllib.request, 'build_opener', return_value=opener):
                if field == 'healthy':
                    build.check_service(build.ORIGINS['staging'])
                else:
                    with self.assertRaises(ValueError):
                        build.check_service(build.ORIGINS['staging'])

    def test_offline_never_contacts_service(self):
        with patch.object(build.urllib.request, 'build_opener') as opener:
            build.check_service('')
            opener.assert_not_called()


if __name__ == '__main__':
    unittest.main()
