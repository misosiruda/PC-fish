"""Package a clean committed source tree and its freshly built mod, without game files."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import tomllib
import xml.etree.ElementTree as ET
import zipfile


def run(*args: str, cwd: Path) -> str:
    return subprocess.check_output(args, cwd=cwd, text=True, encoding="utf-8").strip()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-root", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    if run("git", "status", "--porcelain", "--untracked-files=all", cwd=root):
        raise SystemExit("Commit all release inputs before packaging; the worktree must be clean.")
    commit = run("git", "rev-parse", "HEAD", cwd=root)
    version = tomllib.loads((root / "pyproject.toml").read_text(encoding="utf-8"))["project"]["version"]
    cs_version = ET.parse(root / "mod/PCFish.Automation/PCFish.Automation.csproj").findtext(".//Version")
    plugin = (root / "mod/PCFish.Automation/Plugin.cs").read_text(encoding="utf-8")
    declared = re.search(r'BepInPlugin\([^\n]*"([0-9]+\.[0-9]+\.[0-9]+)"\)', plugin)
    runtime = re.search(r'\bversion\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"', plugin)
    if not declared or not runtime or {version, cs_version, declared[1], runtime[1]} != {version}:
        raise SystemExit("Python, DLL metadata, plugin and runtime versions must agree.")
    output = root / "dist" / f"release-v{version}"
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"PCFish-Automation-v{version}-win-x64.zip"
    checksum = output / "SHA256SUMS.txt"
    if archive.exists() or checksum.exists():
        raise SystemExit(f"Release artifacts already exist: {output}. Preserve them before rebuilding.")
    subprocess.run(["pwsh", "-NoProfile", "-File", str(root / "scripts/Build-Mod.ps1"),
                    "-GameRoot", args.game_root], cwd=root, check=True)
    if run("git", "status", "--porcelain", "--untracked-files=all", cwd=root) or run("git", "rev-parse", "HEAD", cwd=root) != commit:
        raise SystemExit("Release inputs changed during the build.")
    dll_path = "mod/PCFish.Automation/bin/Release/net6.0/PCFish.Automation.dll"
    dll = (root / dll_path).read_bytes()
    source = subprocess.check_output(["git", "archive", "--format=zip", commit], cwd=root)
    with zipfile.ZipFile(io.BytesIO(source)) as source_zip:
        files = {name: source_zip.read(name) for name in source_zip.namelist() if not name.endswith("/")}
    forbidden_roots = {"work", "var", "tools", ".venv", "build", "dist", ".git"}
    for name in files:
        path = Path(name)
        if path.parts[0] in forbidden_roots or path.suffix.lower() in {".dll", ".exe", ".pdb", ".es3", ".log", ".key", ".pem"} or path.name.startswith(".env"):
            raise SystemExit(f"Unexpected private or binary source input: {name}")
    files[dll_path] = dll
    manifest = {
        "version": version, "tag": f"v{version}", "source_commit": commit,
        "platform": "windows-x64", "game_version": "1.6.2", "steam_build": "26315872",
        "game_assembly_sha256": "F5F98F82FE2FED5F177B440DF07C9084C814EB8C5C9A141661A67FF184149F93",
        "loader": "BepInEx 6.0.0-be.793+dbc82f0 Unity.IL2CPP-win-x64",
        "files": {name: {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()} for name, data in sorted(files.items())},
    }
    files["release-manifest.json"] = (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as packaged:
        for name, data in sorted(files.items()):
            packaged.writestr(name, data)
    checksum.write_text(f"{hashlib.sha256(archive.read_bytes()).hexdigest()}  {archive.name}\n", encoding="ascii")
    print(json.dumps({"version": version, "commit": commit, "archive": str(archive),
                      "bytes": archive.stat().st_size, "sha256": hashlib.sha256(archive.read_bytes()).hexdigest()}, indent=2))


if __name__ == "__main__":
    main()
