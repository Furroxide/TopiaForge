"""Prepare the pinned analyzer for CI using a verified archive cache."""

import argparse
import hashlib
from pathlib import Path
import re
import stat
import subprocess
import tempfile
import zipfile

VERSION = "1.25.0"
ARCHIVE_NAME = f"PSScriptAnalyzer.{VERSION}.nupkg"
ARCHIVE_SIZE = 14_658_674
SHA256 = "14e634c828eb98efb9f40b2918ba90f139ed5eccdf663a2a747736d996995d60"
URL = f"https://github.com/PowerShell/PSScriptAnalyzer/releases/download/{VERSION}/{ARCHIVE_NAME}"
MANIFEST = "PSScriptAnalyzer.psd1"


def download(destination):
    version_output = subprocess.check_output(["curl", "--version"], text=True, timeout=5)
    version = re.match(r"curl (\d+)\.(\d+)", version_output)
    if not version or tuple(map(int, version.groups())) < (8, 4):
        raise ValueError("PSScriptAnalyzer acquisition requires curl 8.4 or newer for the streamed size limit.")
    subprocess.run(
        [
            "curl", "--disable", "--silent", "--show-error", "--fail", "--location",
            "--proto", "=https", "--proto-redir", "=https", "--max-redirs", "3",
            "--connect-timeout", "10", "--max-time", "60", "--max-filesize",
            str(ARCHIVE_SIZE), "--output", str(destination), URL,
        ],
        check=True,
        timeout=65,
    )


def verify(archive, *, restored):
    if archive.stat().st_size == ARCHIVE_SIZE:
        with archive.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() == SHA256:
                return
    if restored:
        raise ValueError(f"Cached PSScriptAnalyzer archive failed size or SHA-256 verification. Delete the psscriptanalyzer-{SHA256} cache entry for this ref and retry.")
    raise ValueError("Downloaded PSScriptAnalyzer archive failed size or SHA-256 verification.")


def extract(archive, destination):
    with zipfile.ZipFile(archive) as package:
        names = set()
        for entry in package.infolist():
            name = entry.orig_filename
            parts = name.removesuffix("/").split("/")
            mode = stat.S_IFMT(entry.external_attr >> 16)
            if (
                any(part in ("", ".", "..") for part in parts)
                or any(character in name for character in "\\:\0")
                or mode not in (0, stat.S_IFREG, stat.S_IFDIR)
                or name in names
            ):
                raise ValueError(f"Unsafe PSScriptAnalyzer archive entry {name!r}.")
            names.add(name)
        if MANIFEST not in names:
            raise ValueError(f"PSScriptAnalyzer archive is missing {MANIFEST} at its root.")
        package.extractall(destination)


def prepare(archive, temp_root, *, expect_cache=False):
    archive.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="analyzer-download-", dir=archive.parent) as temporary:
        restored = archive.exists()
        if expect_cache and not restored:
            raise ValueError(f"Cache hit for psscriptanalyzer-{SHA256} did not restore {ARCHIVE_NAME}. Delete that cache entry for this ref and retry.")
        candidate = archive if restored else Path(temporary) / ARCHIVE_NAME
        if not restored:
            download(candidate)
        verify(candidate, restored=restored)
        with tempfile.TemporaryDirectory(prefix="analyzer-module-", dir=temp_root, delete=False) as module_dir:
            destination = Path(module_dir)
            extract(candidate, destination)
        if not restored:
            candidate.replace(archive)
        print(f"Verified PSScriptAnalyzer {VERSION} archive ({'cache hit' if restored else 'downloaded'}).")
        return destination / MANIFEST


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runner-temp", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--cache-info", action="store_true")
    parser.add_argument("--cache-hit", choices=("", "true", "false"), default="")
    args = parser.parse_args()
    archive = args.runner_temp / f"psscriptanalyzer-{SHA256}" / ARCHIVE_NAME
    try:
        if args.cache_info:
            outputs = {"archive": archive, "key": f"psscriptanalyzer-{SHA256}"}
        else:
            manifest = prepare(archive, args.runner_temp, expect_cache=args.cache_hit == "true")
            outputs = {"manifest": manifest, "version": VERSION}
        with args.output.open("a", encoding="utf-8") as stream:
            for name, value in outputs.items():
                stream.write(f"{name}={value}\n")
    except (OSError, ValueError, subprocess.SubprocessError, zipfile.BadZipFile) as error:
        parser.exit(1, f"PSScriptAnalyzer preparation failed: {error}\n")


if __name__ == "__main__":
    main()
