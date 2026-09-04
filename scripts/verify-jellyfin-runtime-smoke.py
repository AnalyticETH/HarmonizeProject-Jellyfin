#!/usr/bin/env python3
"""Bounded disposable Jellyfin runtime smoke verification for release artifacts."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
from pathlib import Path

EXPECTED_ARCHIVE_ENTRIES = (
    "BouncyCastle.Cryptography.dll",
    "Jellyfin.Plugin.Hue.dll",
    "meta.json",
)
EXPECTED_ASSEMBLIES = ("BouncyCastle.Cryptography.dll", "Jellyfin.Plugin.Hue.dll")
EXPECTED_PLUGIN_GUID = "4e078f02-ec43-473e-85f1-98e86eb9a761"
EXPECTED_PLUGIN_NAME = "Philips Hue Sync"
PLUGIN_DIRECTORY_PREFIX = "HueSync_"
PLUGIN_VERSION_PATTERN = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$")
REQUIRED_LOG_MARKERS = (
    "Loaded plugin: Philips Hue Sync",
)
FORBIDDEN_LOG_MARKERS = (
    "Plugin /config/plugins/HueSync has been disabled",
    "Plugin /config/plugins/HueSync_",
    "Error creating Jellyfin.Plugin.Hue.Plugin",
    "UnauthorizedAccessException",
    "Cannot serialize member Jellyfin.Plugin.Hue.Configuration.PluginConfiguration.HueBridgeCertificatePins",
)
EXPECTED_ENTRY_MODE = 0o644 << 16
RUNTIME_DIRECTORY_MODE = 0o700
# Jellyfin persists its normalized plugin manifest during activation. The ZIP
# itself remains non-executable 0644, but the disposable extracted manifest
# must be writable by the invoking rootless container UID during the smoke run.
RUNTIME_MANIFEST_MODE = 0o600
# Official Jellyfin 10.10.7 linux/amd64 image manifest digest.
DEFAULT_IMAGE = "jellyfin/jellyfin@sha256:3b38dae4c3ddd6ebc7378538fba4d3f314070ebefbdb3d688166b7c8658fb123"
DEFAULT_STARTUP_TIMEOUT_SECONDS = 150
DEFAULT_HTTP_TIMEOUT_SECONDS = 3
DEFAULT_POLL_INTERVAL_SECONDS = 2


class VerificationError(RuntimeError):
    """Raised when the runtime smoke gate fails."""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Verify that the canonical Jellyfin plugin ZIP boots successfully inside a disposable pinned Jellyfin runtime."
    )
    parser.add_argument("--archive", type=Path, help="Path to jellyfin-plugin-hue-release.zip")
    parser.add_argument("--image", default=DEFAULT_IMAGE, help="Pinned Jellyfin image reference")
    parser.add_argument("--container-name-prefix", default="jellyfin-hue-runtime-smoke")
    parser.add_argument("--startup-timeout-seconds", type=int, default=DEFAULT_STARTUP_TIMEOUT_SECONDS)
    parser.add_argument("--http-timeout-seconds", type=int, default=DEFAULT_HTTP_TIMEOUT_SECONDS)
    parser.add_argument("--poll-interval-seconds", type=int, default=DEFAULT_POLL_INTERVAL_SECONDS)
    parser.add_argument("--plugin-directory-name", default=None)
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise VerificationError(message)


def run_command(command: list[str], *, capture_output: bool = True, check: bool = True) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        command,
        check=False,
        capture_output=capture_output,
        text=True,
    )
    if check and result.returncode != 0:
        detail = (result.stdout or "") + (result.stderr or "")
        raise VerificationError(
            f"Command failed ({result.returncode}): {' '.join(command)}\n{detail.strip()}"
        )
    return result


def validate_archive_path(path: Path) -> Path:
    resolved = path.resolve()
    require(resolved.is_file(), f"Archive does not exist: {resolved}")
    return resolved


def validate_image_reference(image: str) -> str:
    require("@sha256:" in image, "Jellyfin runtime image must be pinned by digest.")
    return image


def resolve_runtime_temp_dir() -> Path:
    """Choose a host-visible temporary directory for Docker bind mounts."""
    runner_temp = os.environ.get("RUNNER_TEMP", "").strip()
    if runner_temp:
        candidate = Path(runner_temp)
        if candidate.is_absolute() and candidate.is_dir() and os.access(candidate, os.W_OK | os.X_OK):
            return candidate
    return Path(tempfile.gettempdir())


def current_runtime_user() -> str:
    uid = os.getuid()
    gid = os.getgid()
    require(uid >= 0 and gid >= 0, "Current process UID/GID must be non-negative.")
    return f"{uid}:{gid}"


def parse_security_options(raw_value: str) -> list[str]:
    options = json.loads(raw_value)
    require(isinstance(options, list), "docker info security options must be a JSON array")
    normalized: list[str] = []
    for option in options:
        require(isinstance(option, str) and option.strip(), "docker info security options must contain non-empty strings")
        normalized.append(option.strip())
    return normalized


def resolve_container_user() -> str:
    security_options_result = run_command([
        "docker", "info", "--format", "{{json .SecurityOptions}}"
    ])
    security_options = parse_security_options((security_options_result.stdout or "").strip())
    if any(option == "name=rootless" or option == "rootless" for option in security_options):
        return "0:0"
    return current_runtime_user()


def parse_manifest(archive_entries: dict[str, bytes]) -> dict[str, object]:
    try:
        manifest = json.loads(archive_entries["meta.json"].decode("utf8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise VerificationError(f"meta.json must be valid UTF-8 JSON: {error}") from error
    require(isinstance(manifest, dict), "meta.json must contain a JSON object")
    version = manifest.get("version")
    require(
        isinstance(version, str) and PLUGIN_VERSION_PATTERN.fullmatch(version) is not None,
        "meta.json version must use a four-part numeric version",
    )
    require(manifest.get("guid") == EXPECTED_PLUGIN_GUID, "meta.json guid must match the plugin assembly identity")
    require(manifest.get("name") == EXPECTED_PLUGIN_NAME, "meta.json name must match the plugin display name")
    return manifest


def validate_archive_entries(archive_path: Path) -> dict[str, bytes]:
    extracted: dict[str, bytes] = {}
    with zipfile.ZipFile(archive_path) as archive:
        entries = sorted(item.filename for item in archive.infolist())
        require(entries == list(EXPECTED_ARCHIVE_ENTRIES),
                f"Archive entries must be exactly {EXPECTED_ARCHIVE_ENTRIES}; found {tuple(entries)}")
        for info in archive.infolist():
            require(info.create_system == 0, f"{info.filename} create_system must stay 0 for canonical ZIP output")
            require(info.external_attr == EXPECTED_ENTRY_MODE,
                    f"{info.filename} must use canonical non-executable 0644 ZIP attributes")
            extracted[info.filename] = archive.read(info.filename)
    meta = parse_manifest(extracted)
    assemblies = meta.get("assemblies")
    require(
        isinstance(assemblies, list)
        and tuple(assemblies) == EXPECTED_ASSEMBLIES
        and all(isinstance(assembly, str) and assembly.strip() for assembly in assemblies),
        "meta.json assemblies must be the Jellyfin-compatible string filename list",
    )
    return extracted


def set_owner_mode(path: Path, mode: int) -> None:
    current_mode = path.stat().st_mode & 0o777
    if current_mode != mode:
        path.chmod(mode)


def resolve_plugin_directory_name(archive_entries: dict[str, bytes], requested_name: str | None) -> str:
    version = parse_manifest(archive_entries)["version"]
    assert isinstance(version, str)
    expected_name = f"{PLUGIN_DIRECTORY_PREFIX}{version}"
    if requested_name is not None:
        require(
            requested_name == expected_name,
            f"plugin-directory-name must remain {expected_name} for Jellyfin discovery parity",
        )
    return expected_name


def prepare_plugin_directory(root: Path, plugin_directory_name: str, archive_entries: dict[str, bytes]) -> Path:
    config_dir = root / "config"
    cache_dir = root / "cache"
    plugin_dir = config_dir / "plugins" / plugin_directory_name
    plugin_dir.mkdir(parents=True, exist_ok=True)
    cache_dir.mkdir(parents=True, exist_ok=True)
    set_owner_mode(config_dir, RUNTIME_DIRECTORY_MODE)
    set_owner_mode(cache_dir, RUNTIME_DIRECTORY_MODE)
    set_owner_mode(config_dir / "plugins", RUNTIME_DIRECTORY_MODE)
    set_owner_mode(plugin_dir, RUNTIME_DIRECTORY_MODE)
    for name, payload in archive_entries.items():
        output = plugin_dir / name
        output.write_bytes(payload)
        set_owner_mode(output, RUNTIME_MANIFEST_MODE if name == "meta.json" else 0o644)
    return plugin_dir


def get_logs(container_name: str) -> str:
    result = run_command(["docker", "logs", container_name], capture_output=True, check=False)
    return ((result.stdout or "") + (result.stderr or "")).strip()


def ensure_image_present(image: str) -> None:
    run_command(["docker", "pull", image])


def start_container(container_name: str, image: str, config_dir: Path, cache_dir: Path) -> str:
    runtime_user = resolve_container_user()
    run_command(["docker", "rm", "-f", container_name], check=False)
    result = run_command([
        "docker", "run", "-d",
        "--name", container_name,
        "--read-only",
        "--cap-drop=ALL",
        "--security-opt", "no-new-privileges",
        "--pids-limit", "512",
        "--memory", "2g",
        "--cpus", "2",
        "--user", runtime_user,
        "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=64m",
        "--tmpfs", "/run:rw,noexec,nosuid,nodev,size=16m",
        "-v", f"{config_dir}:/config",
        "-v", f"{cache_dir}:/cache",
        image,
    ])
    container_id = (result.stdout or "").strip()
    require(container_id, "docker run did not return a container ID")
    return container_id


def inspect_container_state(container_name: str) -> tuple[str, int]:
    result = run_command([
        "docker", "inspect", container_name,
        "--format", "{{.State.Status}} {{.State.ExitCode}}"
    ])
    parts = (result.stdout or "").strip().split()
    require(len(parts) == 2, f"Unexpected docker inspect state output: {(result.stdout or '').strip()}")
    return parts[0], int(parts[1])


def health_probe_command(container_name: str, timeout_seconds: int) -> list[str]:
    bounded_timeout = max(1, timeout_seconds)
    return [
        "docker",
        "exec",
        container_name,
        "/usr/bin/curl",
        "--fail",
        "--silent",
        "--show-error",
        "--connect-timeout",
        str(bounded_timeout),
        "--max-time",
        str(bounded_timeout),
        "http://127.0.0.1:8096/health",
    ]


def fetch_container_health(container_name: str, timeout_seconds: int) -> bool:
    """Probe the runtime from its own network namespace for remote Docker daemons."""
    command = health_probe_command(container_name, timeout_seconds)
    try:
        result = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            timeout=max(3, bounded_timeout + 2),
        )
    except (OSError, subprocess.TimeoutExpired):
        return False
    return result.returncode == 0


def wait_for_runtime(container_name: str, timeout_seconds: int, http_timeout_seconds: int, poll_interval_seconds: int) -> str:
    deadline = time.monotonic() + timeout_seconds
    last_logs = ""
    health_passed = False
    while time.monotonic() < deadline:
        status, exit_code = inspect_container_state(container_name)
        last_logs = get_logs(container_name)
        forbidden = [marker for marker in FORBIDDEN_LOG_MARKERS if marker in last_logs]
        if forbidden:
            raise VerificationError(
                "Jellyfin runtime smoke failed due to forbidden plugin/runtime marker(s): "
                + ", ".join(forbidden)
                + "\n"
                + last_logs
            )
        if status == "exited":
            raise VerificationError(
                f"Jellyfin container exited early with code {exit_code}.\n{last_logs}"
            )
        if not health_passed:
            # Probe through the runtime container itself. This avoids relying on
            # host port forwarding, which is not stable across Docker namespaces
            # and may be rejected by Windows port-reservation policy.
            health_passed = fetch_container_health(container_name, http_timeout_seconds)
        has_required_logs = all(marker in last_logs for marker in REQUIRED_LOG_MARKERS)
        if health_passed and has_required_logs:
            return last_logs
        time.sleep(poll_interval_seconds)
    raise VerificationError(
        "Timed out waiting for Jellyfin health and plugin-load markers.\n"
        + last_logs
    )


def cleanup_container(container_name: str) -> None:
    run_command(["docker", "rm", "-f", container_name], check=False)


def verify_runtime_smoke(
    archive_path: Path,
    image: str,
    container_name_prefix: str,
    startup_timeout_seconds: int,
    http_timeout_seconds: int,
    poll_interval_seconds: int,
    plugin_directory_name: str,
) -> None:
    archive_entries = validate_archive_entries(archive_path)
    require(startup_timeout_seconds > 0, "startup-timeout-seconds must be positive")
    require(http_timeout_seconds > 0, "http-timeout-seconds must be positive")
    require(poll_interval_seconds > 0, "poll-interval-seconds must be positive")
    plugin_directory_name = resolve_plugin_directory_name(archive_entries, plugin_directory_name)

    image = validate_image_reference(image)
    ensure_image_present(image)
    # The runtime runner service has PrivateTmp enabled, while its rootless
    # Docker daemon is a separate user service. Use RUNNER_TEMP (under the
    # shared runner work tree) when present so bind mounts are visible to both
    # namespaces; local invocations fall back to the normal system temp path.
    temp_root = Path(tempfile.mkdtemp(prefix="jellyfin-runtime-smoke-", dir=resolve_runtime_temp_dir()))
    container_name = f"{container_name_prefix}-{int(time.time())}"
    try:
        prepare_plugin_directory(temp_root, plugin_directory_name, archive_entries)
        start_container(container_name, image, temp_root / "config", temp_root / "cache")
        logs = wait_for_runtime(
            container_name,
            startup_timeout_seconds,
            http_timeout_seconds,
            poll_interval_seconds,
        )
        print(
            f"Jellyfin runtime smoke passed for {archive_path.name} using {image} "
            f"with marker '{REQUIRED_LOG_MARKERS[0]}'."
        )
        if logs:
            print("Verified runtime log markers and HTTP /health readiness.")
    finally:
        cleanup_container(container_name)
        shutil.rmtree(temp_root, ignore_errors=True)


def run_self_test() -> None:
    require(validate_image_reference(DEFAULT_IMAGE) == DEFAULT_IMAGE, "self-test image pin validation failed")
    require(current_runtime_user() == f"{os.getuid()}:{os.getgid()}", "self-test runtime user resolution failed")
    require(parse_security_options('["name=seccomp,profile=builtin","name=rootless","name=cgroupns"]') == [
        "name=seccomp,profile=builtin",
        "name=rootless",
        "name=cgroupns",
    ], "self-test security option parsing failed")
    with tempfile.TemporaryDirectory(prefix="jellyfin-runtime-smoke-self-test-") as temp_dir:
        archive_path = Path(temp_dir) / "jellyfin-plugin-hue-release.zip"
        with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name, payload in {
                "BouncyCastle.Cryptography.dll": b"bc",
                "Jellyfin.Plugin.Hue.dll": b"dll",
                "meta.json": b'{"guid":"4e078f02-ec43-473e-85f1-98e86eb9a761","name":"Philips Hue Sync","version":"1.5.456.0","assemblies":["BouncyCastle.Cryptography.dll","Jellyfin.Plugin.Hue.dll"]}\n',
            }.items():
                info = zipfile.ZipInfo(filename=name, date_time=(1980, 1, 1, 0, 0, 0))
                info.create_system = 0
                info.external_attr = EXPECTED_ENTRY_MODE
                archive.writestr(info, payload, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
        entries = validate_archive_entries(archive_path)
        require(tuple(sorted(entries)) == EXPECTED_ARCHIVE_ENTRIES, "self-test archive parsing failed")
        plugin_directory_name = resolve_plugin_directory_name(entries, None)
        require(plugin_directory_name == "HueSync_1.5.456.0",
                "self-test Jellyfin plugin directory naming failed")
        try:
            resolve_plugin_directory_name(entries, "HueSync")
        except VerificationError:
            pass
        else:
            raise VerificationError("self-test accepted an undiscoverable plugin directory name")
        plugin_dir = Path(temp_dir) / "config" / "plugins" / plugin_directory_name
        prepare_plugin_directory(Path(temp_dir), plugin_directory_name, entries)
        require((plugin_dir).stat().st_mode & 0o777 == RUNTIME_DIRECTORY_MODE,
                "self-test plugin directory mode failed")
        require((plugin_dir / "meta.json").stat().st_mode & 0o777 == RUNTIME_MANIFEST_MODE,
                "self-test runtime manifest mode failed")
        require((plugin_dir / "Jellyfin.Plugin.Hue.dll").stat().st_mode & 0o777 == 0o644,
                "self-test assembly mode failed")
        good_logs = "...\nLoaded plugin: Philips Hue Sync 1.5.456.0\n..."
        require(all(marker in good_logs for marker in REQUIRED_LOG_MARKERS), "self-test required marker detection failed")
        require(not any(marker in good_logs for marker in FORBIDDEN_LOG_MARKERS), "self-test forbidden marker detection failed")
        bad_logs = f"Plugin /config/plugins/{plugin_directory_name} has been disabled"
        require(any(marker in bad_logs for marker in FORBIDDEN_LOG_MARKERS), "self-test forbidden marker matching failed")
        require(
            health_probe_command("smoke-container", 3) == [
                "docker",
                "exec",
                "smoke-container",
                "/usr/bin/curl",
                "--fail",
                "--silent",
                "--show-error",
                "--connect-timeout",
                "3",
                "--max-time",
                "3",
                "http://127.0.0.1:8096/health",
            ],
            "self-test container health probe command failed",
        )
    print("Jellyfin runtime smoke verifier self-test passed")


def main() -> int:
    args = parse_args()
    try:
        if args.self_test:
            run_self_test()
            return 0
        require(args.archive is not None, "--archive is required unless --self-test is used")
        verify_runtime_smoke(
            archive_path=validate_archive_path(args.archive),
            image=args.image,
            container_name_prefix=args.container_name_prefix,
            startup_timeout_seconds=args.startup_timeout_seconds,
            http_timeout_seconds=args.http_timeout_seconds,
            poll_interval_seconds=args.poll_interval_seconds,
            plugin_directory_name=args.plugin_directory_name,
        )
        return 0
    except VerificationError as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
