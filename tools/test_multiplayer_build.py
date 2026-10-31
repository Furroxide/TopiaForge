import importlib.util
from pathlib import Path
import unittest
from unittest.mock import Mock, patch
import io
import json

spec = importlib.util.spec_from_file_location('multiplayer_build', Path(__file__).with_name('multiplayer-build.py'))
build = importlib.util.module_from_spec(spec)
spec.loader.exec_module(build)


class MultiplayerBuildTests(unittest.TestCase):
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
