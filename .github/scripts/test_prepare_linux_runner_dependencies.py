"""Exercise package acquisition without sudo, APT, network access or package installs."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / ".github/scripts/prepare-linux-runner-dependencies.sh"

# Shell stubs deliberately call no real package-management commands. Each test has
# an isolated PATH and records arguments before reporting its chosen acquisition result.
STUB = r'''#!/usr/bin/env bash
set -euo pipefail
name=${0##*/}
printf '%s\0' "$name" "$@" >>"$FIXTURE/events"
printf '\n' >>"$FIXTURE/events"
case "$name" in
  lsb_release)
    if [[ "$1" == -is ]]; then echo Ubuntu; else echo "${CODENAME:-noble}"; fi ;;
  c++) [[ "${NO_COMPILER:-0}" == 0 ]] ;;
  pkg-config) [[ -f "$FIXTURE/installed" ]] ;;
  sudo) exec "$@" ;;
  timeout)
    [[ "$1" == --kill-after=5s && "$2" == 120s ]]
    shift 2
    exec "$@" ;;
  apt-get)
    route=default
    action=
    for argument in "$@"; do
      case "$argument" in
        Dir::Etc::sourcelist=*)
          route=mirror
          path=${argument#*=}
          cat "$path" >"$FIXTURE/source-copy"
          printf '%s' "$path" >"$FIXTURE/source-path" ;;
        update|install) action=$argument ;;
      esac
    done
    [[ -n "$action" ]]
    if [[ "${FAIL_AT:-}" == "$route-$action" || "${FAIL_AT:-}" == both ||
          ( "$route" == mirror && "${MIRROR_FAIL_AT:-}" == "$action" ) ]]; then
      exit 100
    fi
    if [[ "$action" == install && "${MISSING_AFTER_INSTALL:-0}" == 0 ]]; then
      touch "$FIXTURE/installed"
    fi ;;
  *) exit 99 ;;
esac
'''


class LinuxRunnerDependenciesTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bash = shutil.which("bash")
        if os.name == "nt":
            git = Path(shutil.which("git"))
            cls.bash = next((str(path) for path in (git.parent / "bash.exe",
                git.parent.parent / "bin/bash.exe") if path.is_file()), None)
        if not cls.bash:
            raise RuntimeError("Bash is required for these hermetic tests.")

    def exercise(self, *, installed=False, arguments=(), **settings):
        with tempfile.TemporaryDirectory(prefix="linux-deps-") as directory:
            fixture = Path(directory)
            commands = fixture / "bin"
            commands.mkdir()
            for name in ("lsb_release", "c++", "pkg-config", "sudo", "timeout", "apt-get"):
                command = commands / name
                command.write_text(STUB, encoding="utf-8", newline="\n")
                command.chmod(0o755)
            if installed:
                (fixture / "installed").touch()
            # Bash supplies only its bundled core utilities after the fake commands.
            # Windows PATH uses semicolons, so set the shell PATH after conversion.
            environment = dict(os.environ)
            environment.update(FIXTURE=fixture.as_posix(), RUNNER_TEMP=fixture.as_posix(), **settings)
            environment.pop("BASH_ENV", None)
            environment.pop("ENV", None)
            shell = ('FIXTURE=$(cd "$FIXTURE" && pwd -P); export FIXTURE RUNNER_TEMP="$FIXTURE"; '
                     'export PATH="$FIXTURE/bin:/usr/bin:/bin"; exec bash "$@"')
            completed = subprocess.run([self.bash, "--noprofile", "--norc", "-c", shell,
                "linux-deps-test", SCRIPT.as_posix(), *arguments], env=environment,
                capture_output=True, text=True, timeout=15, check=False)
            events = (fixture / "events").read_bytes() if (fixture / "events").exists() else b""
            calls = [row.decode().split("\0")[:-1] for row in events.splitlines()]
            source = (fixture / "source-copy").read_text() if (fixture / "source-copy").exists() else None
            if (fixture / "source-path").exists():
                # The path is POSIX under Git Bash; check the matching basename locally.
                source_name = (fixture / "source-path").read_text().rsplit("/", 1)[-1]
                self.assertFalse((fixture / source_name).exists(), "Fallback source was not removed")
            self.assertFalse(list(fixture.glob("topiaforge-linux-deps-*.sources")))
            apt = [call[1:] for call in calls if call[0] == "apt-get"]
            for call in apt:
                for option in ("Acquire::Retries=0", "Acquire::http::Timeout=20",
                               "Acquire::https::Timeout=20", "APT::Update::Error-Mode=any"):
                    self.assertIn(option, call)
                self.assertFalse(any("unauthenticated" in value or "insecure" in value
                                     or "trusted=" in value for value in call))
            return completed, apt, source

    def test_existing_prerequisites_need_no_acquisition(self):
        result, apt, source = self.exercise(installed=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(apt, [])
        self.assertIsNone(source)

    def test_default_route_installs_both_packages(self):
        result, apt, source = self.exercise()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(apt), 2)
        self.assertEqual(apt[0][-1], "update")
        self.assertEqual(apt[1][-4:], ["install", "-y", "pkg-config", "libglib2.0-dev"])
        self.assertIsNone(source)

    def test_each_default_failure_uses_one_authenticated_mirror_route(self):
        for phase in ("update", "install"):
            with self.subTest(phase=phase):
                result, apt, source = self.exercise(FAIL_AT="default-" + phase)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(len(apt), 3 if phase == "update" else 4)
                self.assertEqual(source, "Types: deb\nURIs: https://mirrors.mit.edu/ubuntu/\n"
                    "Suites: noble noble-updates noble-security\nComponents: main\n"
                    "Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n")
                for call in apt[-2:]:
                    self.assertIn("Dir::Etc::sourceparts=-", call)
                    self.assertEqual(sum(value.startswith("Dir::Etc::sourcelist=") for value in call), 1)
                self.assertEqual(apt[-2][-1], "update")
                self.assertEqual(apt[-1][-4:], ["install", "-y", "pkg-config", "libglib2.0-dev"])

    def test_failed_mirror_is_fatal_and_not_retried(self):
        for phase in ("update", "install"):
            with self.subTest(phase=phase):
                result, apt, source = self.exercise(FAIL_AT="default-update", MIRROR_FAIL_AT=phase)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len(apt), 2 if phase == "update" else 3)
                self.assertIsNotNone(source)

    def test_successful_install_without_headers_is_fatal(self):
        result, apt, _ = self.exercise(MISSING_AFTER_INSTALL="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(apt), 2)

    def test_unsupported_image_missing_compiler_and_arguments_fail_before_apt(self):
        cases = ({"CODENAME": "resolute"}, {"NO_COMPILER": "1"}, {"arguments": ("--mirror",)})
        for settings in cases:
            with self.subTest(settings=settings):
                result, apt, _ = self.exercise(**settings)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(apt, [])


if __name__ == "__main__":
    unittest.main()
