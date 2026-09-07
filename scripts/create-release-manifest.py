#!/usr/bin/env python3
"""Create a deterministic release manifest for the Jellyfin Hue plugin.

The manifest is intentionally a small, dependency-free JSON document that can
be verified independently of the release workflow.  It records the exact
published package files, their SHA-256 digests, the deterministic archive
digest, and the hash-locked NuGet graph used to produce the plugin.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import tempfile
import zipfile
from pathlib import Path
from typing import Any


SCHEMA_VERSION = 2
VERSION_PATTERN = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$")
COMMIT_PATTERN = re.compile(r"^[0-9a-f]{40}$")
REQUIRED_PACKAGE_FILES = (
    "BouncyCastle.Cryptography.dll",
    "Jellyfin.Plugin.Hue.dll",
    "LICENSE",
    "NOTICE",
    "meta.json",
)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_version(meta_path: Path) -> str:
    try:
        document = json.loads(meta_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"could not read package metadata {meta_path}: {error}") from error
    version = document.get("version")
    if not isinstance(version, str) or VERSION_PATTERN.fullmatch(version) is None:
        raise ValueError(f"{meta_path} version must be a four-part numeric version")
    return version


def load_locked_packages(lock_path: Path) -> tuple[str, list[dict[str, Any]]]:
    try:
        lock_document = json.loads(lock_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"could not read dependency lock {lock_path}: {error}") from error

    dependencies = lock_document.get("dependencies")
    if not isinstance(dependencies, dict) or not dependencies:
        raise ValueError(f"{lock_path} must contain at least one dependency framework")

    packages: list[dict[str, Any]] = []
    for framework, framework_packages in sorted(dependencies.items()):
        if not isinstance(framework, str) or not isinstance(framework_packages, dict):
            raise ValueError(f"{lock_path} contains a malformed dependency framework")
        for name, package in sorted(framework_packages.items()):
            if not isinstance(name, str) or not isinstance(package, dict):
                raise ValueError(f"{lock_path} contains a malformed package entry")
            resolved = package.get("resolved")
            content_hash = package.get("contentHash")
            package_type = package.get("type")
            if not isinstance(resolved, str) or not resolved:
                raise ValueError(f"{lock_path} package {name} has no resolved version")
            if not isinstance(content_hash, str) or not content_hash:
                raise ValueError(f"{lock_path} package {name} has no content hash")
            if not isinstance(package_type, str) or not package_type:
                raise ValueError(f"{lock_path} package {name} has no package type")
            packages.append(
                {
                    "framework": framework,
                    "name": name,
                    "type": package_type,
                    "version": resolved,
                    "contentHash": content_hash,
                }
            )
    return str(lock_document.get("version", "")), packages


def build_manifest(
    package_dir: Path,
    lock_path: Path,
    archive_path: Path,
    expected_version: str | None = None,
    source_commit: str | None = None,
) -> dict[str, Any]:
    if not package_dir.is_dir():
        raise ValueError(f"package directory does not exist: {package_dir}")
    for file_name in REQUIRED_PACKAGE_FILES:
        path = package_dir / file_name
        if not path.is_file():
            raise ValueError(f"release package is missing required file: {file_name}")

    package_files = []
    for path in sorted(package_dir.iterdir(), key=lambda candidate: candidate.name):
        if not path.is_file():
            raise ValueError(f"release package contains a non-file entry: {path.name}")
        package_files.append(
            {
                "name": path.name,
                "size": path.stat().st_size,
                "sha256": sha256_file(path),
            }
        )
    if tuple(entry["name"] for entry in package_files) != REQUIRED_PACKAGE_FILES:
        raise ValueError(
            "release package must contain exactly the required files: "
            + ", ".join(REQUIRED_PACKAGE_FILES)
        )

    version = load_version(package_dir / "meta.json")
    if expected_version is not None and version != expected_version:
        raise ValueError(f"package version {version} does not match expected {expected_version}")
    if not isinstance(source_commit, str):
        raise ValueError("source commit is required for release provenance")
    source_commit = source_commit.strip().lower()
    if COMMIT_PATTERN.fullmatch(source_commit) is None:
        raise ValueError("source commit must be a 40-character hexadecimal Git SHA")
    lock_version, packages = load_locked_packages(lock_path)
    if lock_version != "1":
        raise ValueError(f"{lock_path} lock schema must be version 1, found {lock_version!r}")
    if not archive_path.is_file():
        raise ValueError(f"release archive does not exist: {archive_path}")

    try:
        with zipfile.ZipFile(archive_path) as archive:
            archive_entries = sorted(archive.namelist())
            if tuple(archive_entries) != REQUIRED_PACKAGE_FILES:
                raise ValueError(
                    "release archive must contain exactly the required files: "
                    + ", ".join(REQUIRED_PACKAGE_FILES)
                )
            package_by_name = {entry["name"]: entry for entry in package_files}
            for file_name in REQUIRED_PACKAGE_FILES:
                archive_bytes = archive.read(file_name)
                package_file = package_dir / file_name
                if archive_bytes != package_file.read_bytes():
                    raise ValueError(f"release archive entry does not match package file: {file_name}")
                if len(archive_bytes) != package_by_name[file_name]["size"]:
                    raise ValueError(f"release archive entry size does not match package file: {file_name}")
    except zipfile.BadZipFile as error:
        raise ValueError(f"release archive is not a valid ZIP: {archive_path}") from error

    return {
        "schemaVersion": SCHEMA_VERSION,
        "pluginVersion": version,
        "sourceCommit": source_commit,
        "archive": {
            "size": archive_path.stat().st_size,
            "sha256": sha256_file(archive_path),
        },
        "packageFiles": package_files,
        "nuget": {
            "lockFile": lock_path.as_posix(),
            "lockFileSha256": sha256_file(lock_path),
            "lockSchemaVersion": int(lock_version),
            "packages": packages,
        },
    }


def serialize_manifest(manifest: dict[str, Any]) -> str:
    return json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n"


def run_self_test() -> None:
    with tempfile.TemporaryDirectory(prefix="hue-release-manifest-") as temporary:
        root = Path(temporary)
        package_dir = root / "release-package"
        package_dir.mkdir()
        (package_dir / "BouncyCastle.Cryptography.dll").write_bytes(b"bouncy")
        (package_dir / "Jellyfin.Plugin.Hue.dll").write_bytes(b"plugin")
        (package_dir / "LICENSE").write_bytes(b"GNU GENERAL PUBLIC LICENSE\n")
        (package_dir / "NOTICE").write_bytes(b"Runtime dependency notices\n")
        (package_dir / "meta.json").write_text(
            json.dumps({"version": "1.2.3.4"}) + "\n", encoding="utf-8"
        )
        lock_path = root / "packages.lock.json"
        lock_path.write_text(
            json.dumps(
                {
                    "version": 1,
                    "dependencies": {
                        "net8.0": {
                            "Example": {
                                "type": "Direct",
                                "resolved": "1.0.0",
                                "contentHash": "hash",
                            }
                        }
                    },
                }
            )
            + "\n",
            encoding="utf-8",
        )
        archive_path = root / "release.zip"
        with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for file_name in REQUIRED_PACKAGE_FILES:
                archive.write(package_dir / file_name, arcname=file_name)
        source_commit = "0123456789abcdef0123456789abcdef01234567"
        first = serialize_manifest(
            build_manifest(package_dir, lock_path, archive_path, "1.2.3.4", source_commit)
        )
        second = serialize_manifest(
            build_manifest(package_dir, lock_path, archive_path, "1.2.3.4", source_commit)
        )
        if first != second:
            raise AssertionError("release manifest serialization is not deterministic")
        parsed = json.loads(first)
        if parsed["schemaVersion"] != SCHEMA_VERSION:
            raise AssertionError("release manifest schema marker is incorrect")
        if parsed["sourceCommit"] != source_commit:
            raise AssertionError("release manifest source commit marker is incorrect")
        if [entry["name"] for entry in parsed["packageFiles"]] != list(REQUIRED_PACKAGE_FILES):
            raise AssertionError("release manifest package order is incorrect")
        if len(parsed["nuget"]["packages"]) != 1:
            raise AssertionError("release manifest dependency inventory is incomplete")
        tampered_archive = root / "tampered.zip"
        for tampered_name in ("Jellyfin.Plugin.Hue.dll", "LICENSE", "NOTICE"):
            with zipfile.ZipFile(tampered_archive, "w", compression=zipfile.ZIP_DEFLATED) as archive:
                for file_name in REQUIRED_PACKAGE_FILES:
                    payload = (
                        b"tampered"
                        if file_name == tampered_name
                        else (package_dir / file_name).read_bytes()
                    )
                    archive.writestr(file_name, payload)
            try:
                build_manifest(package_dir, lock_path, tampered_archive, "1.2.3.4", source_commit)
            except ValueError as error:
                if f"does not match package file: {tampered_name}" not in str(error):
                    raise AssertionError("tampered archive failure did not identify the mismatched file") from error
            else:
                raise AssertionError(f"tampered {tampered_name} was accepted")

        for missing_name in ("LICENSE", "NOTICE"):
            missing_path = package_dir / missing_name
            payload = missing_path.read_bytes()
            missing_path.unlink()
            try:
                build_manifest(package_dir, lock_path, archive_path, "1.2.3.4", source_commit)
            except ValueError as error:
                if f"missing required file: {missing_name}" not in str(error):
                    raise
            else:
                raise AssertionError(f"release manifest without {missing_name} was accepted")
            finally:
                missing_path.write_bytes(payload)

        for invalid_source_commit in (None, "", "not-a-sha", "a" * 39, "g" * 40):
            try:
                build_manifest(
                    package_dir,
                    lock_path,
                    archive_path,
                    "1.2.3.4",
                    invalid_source_commit,
                )
            except ValueError as error:
                if "source commit" not in str(error):
                    raise AssertionError("invalid source commit failure was not identified") from error
            else:
                raise AssertionError("invalid source commit was accepted")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-dir", type=Path)
    parser.add_argument("--lock-file", type=Path)
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--source-commit")
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.self_test:
        run_self_test()
        print("Deterministic release manifest self-test passed")
        return 0
    required = {
        "--package-dir": args.package_dir,
        "--lock-file": args.lock_file,
        "--archive": args.archive,
        "--output": args.output,
        "--source-commit": args.source_commit,
    }
    missing = [name for name, value in required.items() if value is None]
    if missing:
        raise ValueError(f"missing required arguments: {', '.join(missing)}")
    manifest = build_manifest(
        args.package_dir,
        args.lock_file,
        args.archive,
        args.version,
        args.source_commit,
    )
    args.output.write_text(serialize_manifest(manifest), encoding="utf-8", newline="\n")
    print(f"Release manifest written to {args.output}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, OSError, TypeError, ValueError, zipfile.BadZipFile, json.JSONDecodeError) as error:
        print(f"create-release-manifest.py: {error}", file=sys.stderr)
        raise SystemExit(1) from error
