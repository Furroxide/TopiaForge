#!/usr/bin/env python3
"""Exercise the verifier's repository URL checks without Git or network access."""

import json
import re
import subprocess
from pathlib import Path


def main():
    source = Path(__file__).with_name("verify-release-candidate.sh").read_text(
        encoding="utf-8"
    )
    match = re.search(r"repository_identity_jq='\n(.*?)\n'", source, re.DOTALL)
    if match is None:
        raise SystemExit("Repository identity helper was not found.")
    helper = match.group(1)
    # jq 1.8.1 returns an empty string for rtrimstr(""). Shadow only that
    # primitive so both variants exercise the actual verifier helper above.
    legacy_trim = """
def rtrimstr($suffix):
  if $suffix == "" then ""
  elif endswith($suffix) then .[0:(length - ($suffix | length))]
  else . end;
"""
    prefix = "https://api.github.com/repos/"
    repository = "Furroxide/TopiaForge"
    cases = [
        (repository, "", True),
        ("furroxide/topiaforge", "", True),
        (repository + "/pulls/7", "/pulls/7", True),
        ("FURROXIDE/TOPIAFORGE/pulls/7", "/pulls/7", True),
        (repository + "/pulls/8", "/pulls/7", False),
        (repository + "/pulls/7/extra", "/pulls/7", False),
        (repository + "/pulls/7", "", False),
        (repository + "/", "", False),
        ("other/TopiaForge", "", False),
        (repository + "extra", "", False),
        (repository + "?query=1", "", False),
        (repository + "/PULLS/7", "/pulls/7", False),
        (repository + "/pulls/07", "/pulls/7", False),
        ("", "", False),
    ]
    cases = [(prefix + value, suffix, expected) for value, suffix, expected in cases]
    cases.extend([
        ("http://api.github.com/repos/" + repository, "", False),
        ("https://example.invalid/repos/" + repository, "", False),
        (None, "", False),
        (42, "", False),
        ([], "", False),
        ({}, "", False),
    ])
    for label, prelude in (("current", ""), ("legacy-empty-trim", legacy_trim)):
        for index, (value, suffix, expected) in enumerate(cases, 1):
            result = subprocess.run(
                ["jq", "-c", "--arg", "repository", repository,
                 "--arg", "prefix", prefix, "--arg", "suffix", suffix,
                 prelude + helper + "\nrepository_url($prefix; $suffix)"],
                input=json.dumps(value), text=True, capture_output=True, check=False,
            )
            if result.returncode != 0 or result.stdout.strip() != json.dumps(expected):
                raise SystemExit(f"Repository URL case {index} failed ({label}).")
    print(f"Repository URL checks passed: {len(cases) * 2} cases.")


if __name__ == "__main__":
    main()
