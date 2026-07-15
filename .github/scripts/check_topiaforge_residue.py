#!/usr/bin/env python3
"""Reject retired ecosystem names while preserving explicit Robotopia game facts."""

from __future__ import annotations

import argparse
import io
import os
import re
import subprocess
import sys
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SELF_PATH = ".github/scripts/check_topiaforge_residue.py"

# These paths describe the game build/reference input, not the modding ecosystem.
ROBOTOPIA_PATH_ALLOWLIST = {
    ".github/robotopia-game-build.json",
    "tools/restore-robotopia-managed-refs.ps1",
    "tools/test-restore-robotopia-managed-refs.ps1",
}

FORBIDDEN_PATH = re.compile(
    r"quantum(?:works|-works)|qwui|robotopia(?:modmanager|launcher)|"
    r"(?:^|/)(?:apps|mods|schemas|src|templates|tests)/robotopia(?:[._/-]|$)|"
    r"\.robotopiamod$",
    re.IGNORECASE,
)

BYTE_RULES = (
    ("retired QuantumWorks brand", re.compile(rb"quantum(?:works|-works)", re.IGNORECASE)),
    ("retired QwUi abbreviation", re.compile(rb"qwui", re.IGNORECASE)),
    (
        "retired Qw-prefixed identifier",
        # Require a PascalCase stem of at least three alphabetic characters.
        # Runtime-table collisions such as QwYw and QwYw6 are not identifiers;
        # historical short symbols such as QwGap remain covered. QwUi and its
        # longer forms are covered by the dedicated rule above.
        re.compile(rb"(?<![A-Za-z0-9_])I?Qw[A-Z][a-z]{2}[A-Za-z0-9_]*"),
    ),
    ("retired package extension", re.compile(rb"\.robotopiamod\b", re.IGNORECASE)),
    ("retired manifest filename", re.compile(rb"\brobotopia\.mod\.json\b", re.IGNORECASE)),
    ("retired reverse-DNS root", re.compile(rb"\bcom\.robotopia\b", re.IGNORECASE)),
    ("retired SDK interface", re.compile(rb"\bIRobotopiaMod\b")),
    ("retired manager name", re.compile(rb"\bRobotopia(?:ModManager|Launcher)\b")),
    (
        "retired C#/Unity namespace",
        re.compile(
            rb"\bRobotopia\.(?:GameCompat|ModManager|Mods|UgcCompanion|"
            rb"WorldCompanion|UnityPackageTemplate|UnityWorldTemplate)\b"
        ),
    ),
    (
        "retired first-party identifier",
        re.compile(
            rb"\brobotopia\.(?:assets|chronos|first_party|gravitygun|level|local|"
            rb"modmanager|no-feedback-url|official|performance|perffixes|prompts|"
            rb"repos\.local|robotkit|sandbox|ugc\.livesync|uigallery|vpm|worlds|zombies)\b",
            re.IGNORECASE,
        ),
    ),
    ("retired UGC protocol prefix", re.compile(rb"\bROBOTOPIA_UGC_[A-Z0-9_]+\b")),
    (
        "retired ecosystem path",
        re.compile(
            rb"\b(?:apps|mods|schemas|src|templates|tests)[/\\]Robotopia(?:[._/\\-]|$)",
            re.IGNORECASE,
        ),
    ),
    (
        "retired Robotopia ecosystem phrase",
        re.compile(
            rb"\bRobotopia (?:Agent Guide|CLI|Creator Companion|Developer Docs|"
            rb"ecosystem|launcher UI|loader|Mod Manager|mod SDK|modding ecosystem|"
            rb"mods?|project|repository|runtime loader|UI Kit|Unity packages?)\b",
            re.IGNORECASE,
        ),
    ),
    (
        "retired CLI invocation",
        re.compile(
            rb"(?<![A-Za-z0-9_.@-])robotopia\s+(?:add|check|dev-install|doctor|"
            rb"install|launch|list|migrate|mod|new|pack|projects|registry|release|"
            rb"restart|restore|setup|ugc|unity|world)\b"
        ),
    ),
)

# Two-character strings in compressed images routinely collide with this token,
# so enforce the bare prose abbreviation only in decoded text. Public Qw-prefixed
# identifiers remain covered in binary `strings` output by BYTE_RULES above.
TEXT_ONLY_BYTE_RULES = (
    ("retired Qw abbreviation", re.compile(rb"(?<![A-Za-z0-9_])Qw(?![A-Za-z0-9_])")),
)

# Every lowercase use is deliberately narrow. The title-cased word Robotopia is
# the game name; these additional forms are game files, game compatibility
# identifiers, managed-reference plumbing, verified in-game asset ids, or tags.
LOWERCASE_ROBOTOPIA_ALLOWLIST = (
    re.compile(r"robotopia\.gg", re.IGNORECASE),
    re.compile(r"@robotopia(?:-parts)?/", re.IGNORECASE),
    re.compile(r"(?:test-)?restore-robotopia-managed-refs", re.IGNORECASE),
    re.compile(r"robotopia-(?:bundled-refs|game-build|managed-refs|public-refs)", re.IGNORECASE),
    re.compile(r"-robotopiaManagedDir\b"),
)

BINARY_SUFFIXES = {
    ".7z",
    ".a",
    ".bin",
    ".bundle",
    ".dll",
    ".dylib",
    ".exe",
    ".node",
    ".pdb",
    ".png",
    ".so",
    ".ttf",
    ".wasm",
    ".webp",
    ".woff",
    ".woff2",
    ".zip",
}

ARCHIVE_SUFFIXES = {".topiaforgemod", ".zip"}
MAX_ARCHIVE_DEPTH = 8
MAX_ARCHIVE_UNCOMPRESSED_BYTES = 8 * 1024 * 1024 * 1024

LOWERCASE_LITERAL_ALLOWLIST = {
    "packages/launcher_data/lib/src/local_launcher_repository/game_layout.dart",
    "packages/launcher_data/test/game_layout_test.dart",
    "packages/launcher_data/test/mac_layout_repository_test.dart",
}


def repository_files() -> list[str]:
    result = subprocess.run(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
        cwd=ROOT,
        check=True,
        stdout=subprocess.PIPE,
    )
    return [item.decode("utf-8") for item in result.stdout.split(b"\0") if item]


class AuditToolError(RuntimeError):
    """A local prerequisite failed, so the audit could not be completed."""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Scan repository files and explicitly included generated payloads "
            "for retired ecosystem identities."
        )
    )
    parser.add_argument(
        "--include",
        action="append",
        default=[],
        metavar="PATH",
        help=(
            "also scan a generated file or directory recursively; may be repeated. "
            "Relative paths are resolved from the repository root"
        ),
    )
    return parser.parse_args()


def allowed_lowercase_span(path: str, text: str, start: int, end: int) -> bool:
    for pattern in LOWERCASE_ROBOTOPIA_ALLOWLIST:
        if any(match.start() <= start and end <= match.end() for match in pattern.finditer(text)):
            return True

    if path in LOWERCASE_LITERAL_ALLOWLIST:
        for match in re.finditer(r"(['\"])robotopia\1", text):
            if match.start() <= start and end <= match.end():
                return True

    # Unity package discovery keywords intentionally include the target game.
    # Keep this exception scoped to the exact JSON keyword array in source
    # package manifests and their generated VPM index representation.
    normalized_path = path.replace("\\", "/")
    if normalized_path.endswith("package.json") or normalized_path.endswith(
        "/vpm/index.json"
    ):
        for array_match in re.finditer(
            r'"keywords"\s*:\s*\[(.*?)\]', text, flags=re.DOTALL
        ):
            content_start = array_match.start(1)
            for literal in re.finditer(
                r'(["\'])robotopia\1', array_match.group(1), flags=re.IGNORECASE
            ):
                literal_start = content_start + literal.start()
                literal_end = content_start + literal.end()
                if literal_start <= start and end <= literal_end:
                    return True

    return False


def archive_suffix(path: str) -> str:
    return Path(path.replace("\\", "/")).suffix.lower()


def check_path(display: str, policy_path: str, failures: list[str]) -> None:
    normalized = policy_path.replace("\\", "/")
    allowed_game_path = False
    for allowed in ROBOTOPIA_PATH_ALLOWLIST:
        suffix = f"/{allowed}"
        if normalized == allowed:
            allowed_game_path = True
            break
        if normalized.endswith(suffix):
            package_prefix = normalized[: -len(suffix)]
            if "robotopia" not in package_prefix.lower():
                allowed_game_path = True
                break
    if "robotopia" in normalized.lower() and not allowed_game_path:
        failures.append(f"{display}: retired Robotopia ecosystem name in path")
    if FORBIDDEN_PATH.search(normalized):
        failures.append(f"{display}: retired ecosystem name in path")


def extract_binary_strings(data: bytes, display: str) -> bytes:
    try:
        result = subprocess.run(
            ["strings", "-a"],
            check=True,
            input=data,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    except FileNotFoundError as error:
        raise AuditToolError(
            "The rename residue audit requires the `strings` executable "
            f"to inspect binary payloads (while scanning {display})."
        ) from error
    except subprocess.CalledProcessError as error:
        detail = error.stderr.decode("utf-8", errors="replace").strip()
        raise AuditToolError(
            f"strings failed while scanning {display}: {detail or f'exit {error.returncode}'}"
        ) from error
    return result.stdout


def scan_content(
    display: str,
    policy_path: str,
    data: bytes,
    failures: list[str],
    *,
    force_text: bool = False,
) -> None:
    binary = not force_text and (
        archive_suffix(policy_path) in BINARY_SUFFIXES or b"\0" in data
    )
    scan_data = extract_binary_strings(data, display) if binary else data
    location_suffix = ":strings" if binary else ""

    rules = BYTE_RULES if binary else BYTE_RULES + TEXT_ONLY_BYTE_RULES
    for label, pattern in rules:
        match = pattern.search(scan_data)
        if match:
            line = scan_data.count(b"\n", 0, match.start()) + 1
            failures.append(f"{display}{location_suffix}:{line}: {label}")

    if binary:
        return

    text = data.decode("utf-8", errors="replace")
    for match in re.finditer(r"robotopia", text):
        token = match.group(0)
        if token == "Robotopia":
            continue
        if allowed_lowercase_span(policy_path, text, match.start(), match.end()):
            continue
        line = text.count("\n", 0, match.start()) + 1
        failures.append(
            f"{display}:{line}: lowercase/unallowlisted Robotopia token; "
            "use TopiaForge unless this is an approved game fact"
        )


def scan_archive(
    display: str,
    data: bytes,
    failures: list[str],
    *,
    depth: int,
) -> None:
    if depth > MAX_ARCHIVE_DEPTH:
        failures.append(
            f"{display}: nested archive depth exceeds the audit limit "
            f"of {MAX_ARCHIVE_DEPTH}"
        )
        return

    try:
        archive = zipfile.ZipFile(io.BytesIO(data))
    except (OSError, zipfile.BadZipFile) as error:
        failures.append(f"{display}: invalid ZIP-compatible archive: {error}")
        return

    with archive:
        members = archive.infolist()
        unpacked_size = sum(member.file_size for member in members)
        if unpacked_size > MAX_ARCHIVE_UNCOMPRESSED_BYTES:
            failures.append(
                f"{display}: archive expands to {unpacked_size} bytes, exceeding the "
                f"audit limit of {MAX_ARCHIVE_UNCOMPRESSED_BYTES} bytes"
            )
            return

        if archive.comment:
            scan_content(
                f"{display}!<archive-comment>",
                "<archive-comment>",
                archive.comment,
                failures,
                force_text=True,
            )

        for member in members:
            member_path = member.filename.replace("\\", "/")
            member_display = f"{display}!{member_path}"
            check_path(member_display, member_path, failures)
            if member.is_dir():
                continue

            try:
                member_data = archive.read(member)
            except (OSError, RuntimeError, NotImplementedError, zipfile.BadZipFile) as error:
                failures.append(f"{member_display}: could not read archive member: {error}")
                continue

            if archive_suffix(member_path) in ARCHIVE_SUFFIXES:
                scan_archive(member_display, member_data, failures, depth=depth + 1)
            else:
                scan_content(member_display, member_path, member_data, failures)


def scan_file(
    path: Path,
    display: str,
    policy_path: str,
    failures: list[str],
) -> None:
    if not path.exists() and not path.is_symlink():
        # Working-tree renames leave the old tracked path absent until the
        # change is staged. Audit the present destination through --others.
        return
    check_path(display, policy_path, failures)
    try:
        if path.is_symlink():
            scan_content(
                display,
                policy_path,
                os.readlink(path).encode("utf-8", errors="surrogateescape"),
                failures,
                force_text=True,
            )
            return
        data = path.read_bytes()
    except FileNotFoundError:
        # A staged rename can briefly expose the old index path in a local
        # worktree. CI always checks a complete checkout.
        return
    except OSError as error:
        failures.append(f"{display}: could not read file: {error}")
        return

    if archive_suffix(policy_path) in ARCHIVE_SUFFIXES:
        scan_archive(display, data, failures, depth=1)
    else:
        scan_content(display, policy_path, data, failures)


def repository_relative(path: Path) -> str | None:
    try:
        return path.relative_to(ROOT).as_posix()
    except ValueError:
        return None


def scan_include(
    include: Path,
    failures: list[str],
    scanned_files: set[Path],
) -> None:
    resolved = include.resolve(strict=False)
    repo_relative = repository_relative(resolved)
    root_policy = repo_relative if repo_relative is not None else include.name
    root_display = repo_relative if repo_relative is not None else str(resolved)

    if not include.exists() and not include.is_symlink():
        raise AuditToolError(f"requested --include path does not exist: {include}")

    if include.is_file() or include.is_symlink():
        if resolved not in scanned_files:
            scan_file(include, root_display, root_policy, failures)
            scanned_files.add(resolved)
        return

    if not include.is_dir():
        raise AuditToolError(f"requested --include path is not a file or directory: {include}")

    check_path(root_display, root_policy, failures)
    for current, directory_names, file_names in os.walk(include, followlinks=False):
        directory_names.sort()
        file_names.sort()
        current_path = Path(current)

        for directory_name in tuple(directory_names):
            directory_path = current_path / directory_name
            relative = directory_path.relative_to(include).as_posix()
            policy_path = f"{root_policy}/{relative}" if root_policy else relative
            display = f"{root_display}/{relative}" if root_display else relative
            check_path(display, policy_path, failures)
            if directory_path.is_symlink():
                scan_file(directory_path, display, policy_path, failures)
                scanned_files.add(directory_path.resolve(strict=False))
                directory_names.remove(directory_name)

        for file_name in file_names:
            file_path = current_path / file_name
            resolved_file = file_path.resolve(strict=False)
            if resolved_file in scanned_files:
                continue
            relative = file_path.relative_to(include).as_posix()
            policy_path = f"{root_policy}/{relative}" if root_policy else relative
            display = f"{root_display}/{relative}" if root_display else relative
            scan_file(file_path, display, policy_path, failures)
            scanned_files.add(resolved_file)


def main() -> int:
    args = parse_args()
    failures: list[str] = []
    scanned_files: set[Path] = set()

    for relative in repository_files():
        if relative == SELF_PATH:
            continue
        path = ROOT / relative
        scan_file(path, relative, relative, failures)
        scanned_files.add(path.resolve(strict=False))

    try:
        for raw_include in args.include:
            include = Path(raw_include)
            if not include.is_absolute():
                include = ROOT / include
            scan_include(include, failures, scanned_files)
    except AuditToolError as error:
        print(f"TopiaForge rename residue audit could not run: {error}", file=sys.stderr)
        return 2

    if failures:
        print("TopiaForge rename residue audit failed:", file=sys.stderr)
        for failure in sorted(set(failures)):
            print(f"  - {failure}", file=sys.stderr)
        return 1

    print("TopiaForge rename residue audit passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
