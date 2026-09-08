#!/usr/bin/env python3
"""Create and test the canonical Jellyfin Hue release archive.

The release archive is deliberately built here instead of through the host's
zip command or PowerShell's Compress-Archive so Linux and Windows use the same
entry order, timestamps, metadata, and compression settings.
"""

from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import tempfile
from typing import Iterable
import zipfile


EXPECTED_FILES = (
    "BouncyCastle.Cryptography.dll",
    "Jellyfin.Plugin.Hue.dll",
    "LICENSE",
    "NOTICE",
    "meta.json",
)
FIXED_TIMESTAMP = (1980, 1, 1, 0, 0, 0)
COMPRESSION_LEVEL = 9
# Python's ZipFile otherwise fills this with the process's default file mode.
# Keep one fixed, non-executable, service-readable mode across host platforms.
# Jellyfin commonly runs as a different service account than the operator who
# extracts the archive, so 0600 would make plugin discovery fail on install.
FIXED_EXTERNAL_ATTR = 0o644 << 16
# mkstemp creates the archive with a private mode. Make the completed release
# artifact readable by the operator even when the helper runs as a container
# root over a mounted checkout.
FIXED_OUTPUT_MODE = 0o644


def _canonical_info(name: str) -> zipfile.ZipInfo:
    """Return ZIP metadata that does not depend on the source filesystem."""

    info = zipfile.ZipInfo(name, date_time=FIXED_TIMESTAMP)
    info.compress_type = zipfile.ZIP_DEFLATED
    # Use the DOS creator and clear all optional attributes so Unix and
    # Windows package creation do not encode host-specific metadata.
    info.create_system = 0
    info.create_version = 20
    info.extract_version = 20
    info.flag_bits = 0
    info.volume = 0
    info.internal_attr = 0
    info.external_attr = FIXED_EXTERNAL_ATTR
    info.extra = b""
    info.comment = b""
    return info


def _source_files(input_dir: Path) -> Iterable[tuple[str, Path]]:
    if not input_dir.is_dir():
        raise ValueError(f"Release package directory does not exist: {input_dir}")

    children = sorted(path.name for path in input_dir.iterdir())
    if children != list(EXPECTED_FILES):
        raise ValueError(
            "Release package must contain exactly the canonical files: "
            f"expected {list(EXPECTED_FILES)}, found {children}"
        )

    for name in EXPECTED_FILES:
        path = input_dir / name
        if not path.is_file() or path.is_symlink():
            raise ValueError(f"Release package entry is not a regular file: {path}")
        yield name, path


def inspect_archive(archive_path: Path) -> tuple[zipfile.ZipInfo, ...]:
    """Validate the canonical archive contract and return its entries."""

    with zipfile.ZipFile(archive_path, mode="r") as archive:
        if archive.comment != b"":
            raise ValueError("Release archive must not contain a comment")
        entries = tuple(archive.infolist())
        names = tuple(entry.filename for entry in entries)
        if names != EXPECTED_FILES:
            raise ValueError(
                "Release archive entries are not the canonical sorted set: "
                f"expected {EXPECTED_FILES}, found {names}"
            )
        for entry in entries:
            if entry.date_time != FIXED_TIMESTAMP:
                raise ValueError(
                    f"Release archive entry {entry.filename} has non-fixed timestamp "
                    f"{entry.date_time}"
                )
            if entry.compress_type != zipfile.ZIP_DEFLATED:
                raise ValueError(
                    f"Release archive entry {entry.filename} does not use DEFLATE"
                )
            if entry.create_system != 0 or entry.create_version != 20:
                raise ValueError(
                    f"Release archive entry {entry.filename} has non-canonical creator metadata"
                )
            if entry.flag_bits != 0 or entry.volume != 0:
                raise ValueError(
                    f"Release archive entry {entry.filename} has non-canonical flags"
                )
            if entry.internal_attr != 0 or entry.external_attr != FIXED_EXTERNAL_ATTR:
                raise ValueError(
                    f"Release archive entry {entry.filename} has non-canonical attributes"
                )
            if entry.extra != b"" or entry.comment != b"":
                raise ValueError(
                    f"Release archive entry {entry.filename} has optional metadata"
                )
    return entries


def create_archive(input_dir: Path, output_path: Path) -> None:
    """Create a canonical archive from exactly the required release files."""

    source_files = tuple(_source_files(input_dir))
    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path: Path | None = None
    try:
        descriptor, temporary_name = tempfile.mkstemp(
            prefix=f".{output_path.name}.", suffix=".tmp", dir=output_path.parent
        )
        os.close(descriptor)
        temporary_path = Path(temporary_name)
        with zipfile.ZipFile(
            temporary_path,
            mode="w",
            compression=zipfile.ZIP_DEFLATED,
            compresslevel=COMPRESSION_LEVEL,
            strict_timestamps=True,
        ) as archive:
            archive.comment = b""
            for name, source_path in source_files:
                archive.writestr(
                    _canonical_info(name), source_path.read_bytes(), compresslevel=COMPRESSION_LEVEL
                )

        inspect_archive(temporary_path)
        os.replace(temporary_path, output_path)
        os.chmod(output_path, FIXED_OUTPUT_MODE)
        temporary_path = None
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def run_self_test() -> None:
    """Prove source mtimes/order do not affect archive bytes or metadata."""

    from unittest.mock import call, patch

    with tempfile.TemporaryDirectory(prefix="hue-release-package-test-") as temporary:
        root = Path(temporary)
        first_source = root / "first"
        second_source = root / "second"
        first_source.mkdir()
        second_source.mkdir()
        contents = {
            "meta.json": b'{"version":"test"}\n',
            "NOTICE": b"Runtime dependency notices\n",
            "Jellyfin.Plugin.Hue.dll": b"plugin-bytes\n",
            "LICENSE": b"GNU GENERAL PUBLIC LICENSE\n",
            "BouncyCastle.Cryptography.dll": b"dependency-bytes\n",
        }
        # Deliberately create the files in different orders and assign
        # different mtimes; neither may appear in the resulting archive.
        for name in contents:
            (first_source / name).write_bytes(contents[name])
        for name in reversed(EXPECTED_FILES):
            (second_source / name).write_bytes(contents[name])
        os.utime(first_source / "meta.json", (946684800, 946684800))
        os.utime(second_source / "meta.json", (1893456000, 1893456000))

        first_archive = root / "first.zip"
        second_archive = root / "second.zip"
        with patch.object(zipfile, "_get_compressor", wraps=zipfile._get_compressor) as compressor:
            create_archive(first_source, first_archive)
            create_archive(second_source, second_archive)
        expected_compressors = [call(zipfile.ZIP_DEFLATED, COMPRESSION_LEVEL)] * (2 * len(EXPECTED_FILES))
        if compressor.call_args_list != expected_compressors:
            raise AssertionError("Every release entry must use the declared DEFLATE compression level")
        first_hash = _sha256(first_archive)
        second_hash = _sha256(second_archive)
        if first_hash != second_hash:
            raise AssertionError(
                "Canonical release archives differ for identical file bytes: "
                f"{first_hash} != {second_hash}"
            )
        entries = inspect_archive(first_archive)
        if any(entry.date_time != FIXED_TIMESTAMP for entry in entries):
            raise AssertionError("Canonical release archive timestamp check failed")
        if any(entry.external_attr != (0o644 << 16) for entry in entries):
            raise AssertionError(
                "Canonical release archive entries must be readable by the Jellyfin service account"
            )
        with zipfile.ZipFile(first_archive) as archive:
            for name, payload in contents.items():
                if archive.read(name) != payload:
                    raise AssertionError(f"Release archive did not preserve {name}")
            for missing_name in ("LICENSE", "NOTICE"):
                incomplete_archive = root / f"missing-{missing_name}.zip"
                with zipfile.ZipFile(incomplete_archive, "w") as incomplete:
                    for entry in archive.infolist():
                        if entry.filename != missing_name:
                            incomplete.writestr(_canonical_info(entry.filename), archive.read(entry.filename))
                try:
                    inspect_archive(incomplete_archive)
                except ValueError as error:
                    if "canonical sorted set" not in str(error):
                        raise
                else:
                    raise AssertionError(f"Release archive without {missing_name} was accepted")

        for missing_name in ("LICENSE", "NOTICE"):
            (first_source / missing_name).unlink()
            try:
                create_archive(first_source, root / "incomplete.zip")
            except ValueError as error:
                if "canonical files" not in str(error):
                    raise
            else:
                raise AssertionError(f"Release package without {missing_name} was accepted")
            finally:
                (first_source / missing_name).write_bytes(contents[missing_name])
        print(
            "Deterministic release package self-test passed "
            f"(sha256={first_hash}, entries={','.join(EXPECTED_FILES)})"
        )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input-dir", type=Path, help="Directory containing the required release files")
    parser.add_argument("--output", type=Path, help="Output ZIP archive path")
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="Build two archives from identical bytes with different source metadata and verify them",
    )
    args = parser.parse_args()
    if args.self_test:
        if args.input_dir is not None or args.output is not None:
            parser.error("--self-test cannot be combined with --input-dir or --output")
        run_self_test()
        return 0
    if args.input_dir is None or args.output is None:
        parser.error("--input-dir and --output are required unless --self-test is used")
    create_archive(args.input_dir, args.output)
    print(f"Created deterministic release archive: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
