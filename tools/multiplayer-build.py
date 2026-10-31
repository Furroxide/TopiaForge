#!/usr/bin/env python3
"""Build multiplayer clients with a public service origin, never credentials."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import urllib.request

ORIGINS = {
    "staging": "https://mp-staging.topiaforge.dev",
    "production": "https://mp.topiaforge.dev",
    "offline": "",
}


def check_service(origin):
    # An explicit offline build is useful for hermetic testing.
    if not origin:
        return
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs):
            return None
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect)
    request = urllib.request.Request(origin + "/v1/status",
                                     headers={"User-Agent": "TopiaForge-build-check/1"})
    with opener.open(request, timeout=20) as response:
        status = json.loads(response.read(65537))
    if (status.get("schema") != "topiaforge.control-plane-status/1"
            or status.get("api") != 1 or status.get("killSwitch") is not False
            or any(status.get("features", {}).get(key) is not True
                   for key in ("hosting", "invites", "relay", "signaling"))):
        raise ValueError("The multiplayer service is not ready for Internet play.")


def build_command(kind, executable, arguments, origin):
    if any("TOPIAFORGE_CONTROL_PLANE_ORIGIN" in arg or "define-from-file" in arg
           for arg in arguments):
        raise ValueError("Choose the service with --environment, not another define.")
    define = "TOPIAFORGE_CONTROL_PLANE_ORIGIN=" + origin
    if kind == "flutter" and arguments and arguments[0] in ("build", "run"):
        return [executable, *arguments, "--dart-define=" + define]
    if kind == "dart" and arguments[:2] == ["compile", "exe"]:
        return [executable, "compile", "exe", "-D" + define, *arguments[2:]]
    raise ValueError("Use flutter build/run or dart compile exe.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--environment", choices=ORIGINS, default="staging")
    parser.add_argument("--kind", choices=("flutter", "dart"), default="flutter")
    parser.add_argument("--executable")
    parser.add_argument("--check-only", action="store_true")
    parser.add_argument("arguments", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    origin = ORIGINS[args.environment]
    try:
        check_service(origin)
        print("Multiplayer service: " + args.environment, flush=True)
        if args.check_only:
            return 0
        arguments = args.arguments
        if arguments[:1] == ["--"]:
            arguments = arguments[1:]
        executable = args.executable or args.kind + (".bat" if os.name == "nt" else "")
        command = build_command(args.kind, executable, arguments, origin)
        root = Path(__file__).resolve().parents[1]
        project = "topiaforge_launcher_flutter" if args.kind == "flutter" else "topiaforge_cli"
        return subprocess.call(command, cwd=root / "apps" / project)
    except (OSError, ValueError) as error:
        # Avoid remote response bodies or local credential/proxy details in logs.
        print("Multiplayer build refused: service unavailable or invalid build arguments ("
              + type(error).__name__ + ").")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
