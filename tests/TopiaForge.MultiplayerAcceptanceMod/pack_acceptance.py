"""Stage the non-distributable multiplayer acceptance project for the normal packer."""

import argparse
from hashlib import sha256
from pathlib import Path
import shutil
import subprocess
from tempfile import TemporaryDirectory


def pack(cli: str, output: Path) -> None:
    package = Path(__file__).resolve().parent
    tests = package.parent
    repo = tests.parent
    canonical = repo / "mods" / "TopiaForge.UiGallery" / "ui" / "themes" / "survival.json"
    canonical_bytes = canonical.read_bytes()

    # A sibling project preserves the SDK and source links used by the ordinary build.
    with TemporaryDirectory(prefix=".multiplayer-acceptance-", dir=tests) as scratch:
        stage = Path(scratch).resolve()
        if stage.parent != tests:
            raise ValueError("Acceptance staging must be inside the repository tests directory.")
        for path in sorted(package.glob("*.cs")):
            shutil.copy2(path, stage / path.name)
        for name in (
            "TopiaForge.MultiplayerAcceptanceMod.csproj",
            "LICENSE",
            "topiaforge.mod.json",
            "topiaforge.multiplayer.lock.json",
        ):
            shutil.copy2(package / name, stage / name)
        shutil.copytree(package / "ui", stage / "ui")

        # R4-11 exception. Only the disposable input of this non-distributable probe receives a copy.
        staged_theme = stage / "ui" / "themes" / "survival.json"
        with staged_theme.open("xb") as destination:
            destination.write(canonical_bytes)
        if staged_theme.read_bytes() != canonical_bytes:
            raise ValueError("The staged Survival definition differs from the Gallery source.")

        subprocess.run(
            [cli, "pack", "--project", str(stage), "--output", str(output.resolve())],
            cwd=repo,
            check=True,
        )
        print("Acceptance Survival definition SHA-256 " + sha256(canonical_bytes).hexdigest())


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cli", default="topiaforge", help="Path to the current TopiaForge CLI executable.")
    args = parser.parse_args()
    pack(args.cli, args.output)


if __name__ == "__main__":
    main()
