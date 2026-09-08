#!/usr/bin/env python3
"""Offline portability, deadline, and cleanup contracts for the runtime gate."""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import math
import os
import subprocess
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("runtime_smoke", Path(__file__).with_name("verify-jellyfin-runtime-smoke.py"))
assert spec is not None and spec.loader is not None
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)


def archive_entries() -> dict[str, bytes]:
    return {
        "BouncyCastle.Cryptography.dll": b"dependency",
        "Jellyfin.Plugin.Hue.dll": b"plugin",
        "LICENSE": b"license",
        "NOTICE": b"notice",
        "meta.json": json.dumps({
            "guid": runtime.EXPECTED_PLUGIN_GUID,
            "name": runtime.EXPECTED_PLUGIN_NAME,
            "version": "1.5.458.0",
            "assemblies": list(runtime.EXPECTED_ASSEMBLIES),
        }).encode("utf8"),
    }


class Clock:
    def __init__(self) -> None:
        self.value = 100.0
        self.sleeps: list[float] = []

    def monotonic(self) -> float:
        return self.value

    def sleep(self, duration: float) -> None:
        self.sleeps.append(duration)
        self.value += duration


class DockerStub:
    def __init__(self, clock: Clock, *, delays=None, failures=None, output=None) -> None:
        self.clock = clock
        self.delays = delays or {}
        self.failures = failures or {}
        self.output = output or {}
        self.calls: list[tuple[list[str], float]] = []

    def __call__(self, command, *, timeout, check, capture_output, text):
        assert command[0] == "docker", "Only stubbed Docker commands are permitted"
        assert timeout > 0 and math.isfinite(timeout), "Every Docker command must have a finite positive timeout"
        assert check is False and capture_output is True and text is True
        self.calls.append((list(command), timeout))
        operation = command[1]
        failure = self.failures.get(operation)
        if failure == "timeout" or self.delays.get(operation, 0) > timeout:
            self.clock.value += timeout
            raise subprocess.TimeoutExpired(command, timeout)
        if failure == "oserror":
            raise OSError("stubbed Docker unavailable")
        self.clock.value += self.delays.get(operation, 0)
        if failure == "exit":
            return subprocess.CompletedProcess(command, 1, "", "stubbed daemon error")
        if failure == "missing":
            assert operation == "rm", "Missing-container fixtures apply only to cleanup"
            return subprocess.CompletedProcess(command, 1, "", f"Error response from daemon: No such container: {command[-1]}")
        stdout = self.output.get(operation, {
            "pull": "image ready",
            "info": '["name=seccomp,profile=builtin"]',
            "run": "disposable-container-id",
            "inspect": "running 0",
            "logs": "Loaded plugin: Philips Hue Sync",
            "exec": "Healthy",
            "rm": "disposable-container-id",
        }[operation])
        return subprocess.CompletedProcess(command, 0, stdout, "")


class RuntimeSmokeContracts(unittest.TestCase):
    def invoke_runtime(self, root: Path, docker: DockerStub, *, startup_timeout=10) -> str:
        output = io.StringIO()
        self.last_output = output
        with contextlib.ExitStack() as stack:
            stack.enter_context(patch.object(runtime.subprocess, "run", docker))
            stack.enter_context(patch.object(runtime.time, "monotonic", docker.clock.monotonic))
            stack.enter_context(patch.object(runtime.time, "sleep", docker.clock.sleep))
            stack.enter_context(patch.object(runtime, "validate_archive_entries", return_value=archive_entries()))
            stack.enter_context(patch.object(runtime, "resolve_runtime_temp_dir", return_value=root))
            stack.enter_context(patch.object(runtime, "supports_runtime_permissions", return_value=True))
            stack.enter_context(patch.object(runtime, "current_runtime_user", return_value="123:456"))
            stack.enter_context(contextlib.redirect_stdout(output))
            runtime.verify_runtime_smoke(root / "fixture.zip", runtime.DEFAULT_IMAGE, "offline-smoke", startup_timeout, 3, 2, None)
        return output.getvalue()

    def test_portable_self_test_does_not_require_posix_apis_or_modes(self):
        with patch.object(runtime, "os", types.SimpleNamespace(name="nt")), \
                patch.object(runtime, "run_runtime_permission_self_test", side_effect=AssertionError("POSIX-only checks invoked")), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            runtime.run_self_test()
        self.assertIn("portable archive and command checks remain enabled", output.getvalue())
        self.assertIn("self-test passed", output.getvalue())

    def test_unsupported_live_runtime_fails_before_docker_or_uid_lookup(self):
        with patch.object(runtime, "os", types.SimpleNamespace(name="nt")), \
                patch.object(runtime, "validate_archive_entries", return_value=archive_entries()), \
                patch.object(runtime.subprocess, "run", side_effect=AssertionError("Docker must not run")):
            with self.assertRaisesRegex(runtime.VerificationError, "POSIX host"):
                runtime.current_runtime_user()
            with self.assertRaisesRegex(runtime.VerificationError, "POSIX host"):
                runtime.verify_runtime_smoke(Path("fixture.zip"), runtime.DEFAULT_IMAGE, "smoke", 10, 3, 2, None)

    @unittest.skipUnless(runtime.supports_runtime_permissions(), "POSIX owner-mode checks require a POSIX host")
    def test_posix_runtime_permissions_remain_enforced(self):
        with tempfile.TemporaryDirectory(prefix="runtime-permission-contract-") as directory:
            root = Path(directory)
            runtime.run_runtime_permission_self_test(root, "HueSync_1.5.458.0", archive_entries())
            self.assertEqual(runtime.current_runtime_user(), f"{os.getuid()}:{os.getgid()}")
            for relative in ["config", "cache", "config/plugins", "config/plugins/HueSync_1.5.458.0"]:
                self.assertEqual((root / relative).stat().st_mode & 0o777, 0o700)
            for name in runtime.EXPECTED_ARCHIVE_ENTRIES:
                self.assertEqual((root / "config/plugins/HueSync_1.5.458.0" / name).stat().st_mode & 0o777,
                                 0o600 if name == "meta.json" else 0o644)

    def test_rootless_and_rootful_container_identity_are_preserved(self):
        for options, expected in [('[]', "123:456"), ('["name=rootless"]', "0:0")]:
            docker = DockerStub(Clock(), output={"info": options})
            with self.subTest(options=options), patch.object(runtime.subprocess, "run", docker), \
                    patch.object(runtime, "current_runtime_user", return_value="123:456"):
                self.assertEqual(runtime.resolve_container_user(), expected)
                self.assertEqual(docker.calls[0][1], runtime.DEFAULT_COMMAND_TIMEOUT_SECONDS)

    def test_all_docker_operations_are_bounded_on_stalls_and_launch_errors(self):
        for operation in ["pull", "info", "run", "inspect", "logs", "exec", "rm"]:
            for failure in ["timeout", "oserror"]:
                with self.subTest(operation=operation, failure=failure):
                    clock = Clock()
                    docker = DockerStub(clock, failures={operation: failure})
                    actions = {
                        "pull": lambda: runtime.ensure_image_present(runtime.DEFAULT_IMAGE, deadline=120),
                        "info": lambda: runtime.resolve_container_user(deadline=120),
                        "run": lambda: runtime.start_container("smoke", runtime.DEFAULT_IMAGE, Path("config"), Path("cache"), "123:456", deadline=120),
                        "inspect": lambda: runtime.inspect_container_state("smoke", deadline=120),
                        "logs": lambda: runtime.get_logs("smoke", deadline=120),
                        "exec": lambda: runtime.fetch_container_health("smoke", 3, deadline=120),
                        "rm": lambda: runtime.cleanup_container("smoke"),
                    }
                    with patch.object(runtime.subprocess, "run", docker), \
                            patch.object(runtime.time, "monotonic", clock.monotonic), \
                            contextlib.redirect_stderr(io.StringIO()):
                        if operation in ["exec", "rm"]:
                            self.assertFalse(actions[operation]())
                        else:
                            with self.assertRaises(runtime.VerificationError):
                                actions[operation]()
                    self.assertEqual(len(docker.calls), 1)
                    self.assertLessEqual(docker.calls[0][1], 20)

    def test_expired_deadline_prevents_subprocess_creation(self):
        with patch.object(runtime.time, "monotonic", return_value=100), \
                patch.object(runtime.subprocess, "run", side_effect=AssertionError("Expired command started")):
            with self.assertRaisesRegex(runtime.VerificationError, "deadline expired"):
                runtime.run_command(["docker", "inspect", "smoke"], deadline=99)

    def test_nonzero_command_results_fail_closed_or_remain_explicitly_best_effort(self):
        for operation in ["pull", "info", "run", "inspect", "logs", "exec", "rm"]:
            with self.subTest(operation=operation):
                clock = Clock()
                docker = DockerStub(clock, failures={operation: "exit"})
                actions = {
                    "pull": lambda: runtime.ensure_image_present(runtime.DEFAULT_IMAGE),
                    "info": lambda: runtime.resolve_container_user(),
                    "run": lambda: runtime.start_container("smoke", runtime.DEFAULT_IMAGE, Path("config"), Path("cache"), "123:456"),
                    "inspect": lambda: runtime.inspect_container_state("smoke"),
                    "logs": lambda: runtime.get_logs("smoke"),
                    "exec": lambda: runtime.fetch_container_health("smoke", 3),
                    "rm": lambda: runtime.cleanup_container("smoke"),
                }
                with patch.object(runtime.subprocess, "run", docker), contextlib.redirect_stderr(io.StringIO()):
                    if operation in ["exec", "rm"]:
                        self.assertFalse(actions[operation]())
                    elif operation == "logs":
                        self.assertEqual(actions[operation](), "stubbed daemon error")
                    else:
                        with self.assertRaisesRegex(runtime.VerificationError, "Command failed"):
                            actions[operation]()
                self.assertEqual(len(docker.calls), 1)

    def test_one_budget_covers_pull_through_readiness_with_fresh_cleanup_budget(self):
        docker = DockerStub(Clock(), delays={"pull": 4, "info": 1, "run": 1, "inspect": 1, "logs": 1, "exec": 1})
        with tempfile.TemporaryDirectory(prefix="runtime-budget-contract-") as directory:
            root = Path(directory)
            output = self.invoke_runtime(root, docker)
            self.assertEqual(list(root.iterdir()), [])
        self.assertIn("runtime smoke passed", output)
        self.assertEqual([call[0][1] for call in docker.calls], ["pull", "info", "run", "inspect", "logs", "exec", "rm"])
        self.assertEqual([call[1] for call in docker.calls], [10, 6, 5, 4, 3, 2, runtime.CLEANUP_TIMEOUT_SECONDS])
        run = docker.calls[2][0]
        self.assertIn("--read-only", run)
        self.assertIn("--cap-drop=ALL", run)
        self.assertIn("no-new-privileges", run)
        self.assertEqual(run[run.index("--user") + 1], "123:456")
        self.assertEqual(run[-1], runtime.DEFAULT_IMAGE)
        self.assertEqual(docker.calls[-1][0][-1], run[run.index("--name") + 1])

    def test_long_pulls_use_the_supplied_startup_budget(self):
        for pull_delay, startup_timeout in [(45, runtime.DEFAULT_STARTUP_TIMEOUT_SECONDS), (151, 300)]:
            with self.subTest(pull_delay=pull_delay, startup_timeout=startup_timeout), \
                    tempfile.TemporaryDirectory(prefix="runtime-long-pull-") as directory:
                docker = DockerStub(Clock(), delays={"pull": pull_delay})
                root = Path(directory)
                output = self.invoke_runtime(root, docker, startup_timeout=startup_timeout)
                self.assertIn("runtime smoke passed", output)
                self.assertEqual(list(root.iterdir()), [])
                self.assertEqual(docker.calls[0][0][1], "pull")
                self.assertEqual(docker.calls[0][1], startup_timeout)

    def test_standalone_pull_retains_a_finite_default_timeout(self):
        clock = Clock()
        docker = DockerStub(clock, delays={"pull": runtime.DEFAULT_STARTUP_TIMEOUT_SECONDS + 1})
        with patch.object(runtime.subprocess, "run", docker), \
                patch.object(runtime.time, "monotonic", clock.monotonic):
            with self.assertRaisesRegex(runtime.VerificationError, "Command timed out after 150s: docker pull"):
                runtime.ensure_image_present(runtime.DEFAULT_IMAGE)
        self.assertEqual(len(docker.calls), 1)
        self.assertEqual(docker.calls[0][1], runtime.DEFAULT_STARTUP_TIMEOUT_SECONDS)

    def test_stalls_before_container_creation_do_not_attempt_removal(self):
        for operation in ["pull", "info"]:
            for failure in ["timeout", "exit"]:
                with self.subTest(operation=operation, failure=failure), tempfile.TemporaryDirectory(prefix="runtime-early-failure-") as directory:
                    docker = DockerStub(Clock(), failures={operation: failure})
                    root = Path(directory)
                    with self.assertRaisesRegex(runtime.VerificationError, operation):
                        self.invoke_runtime(root, docker)
                    self.assertEqual(list(root.iterdir()), [])
                    self.assertNotIn("rm", [call[0][1] for call in docker.calls])

    def test_start_and_readiness_failures_still_attempt_bounded_cleanup(self):
        for operation in ["run", "inspect", "logs"]:
            with self.subTest(operation=operation), tempfile.TemporaryDirectory(prefix="runtime-late-failure-") as directory:
                docker = DockerStub(Clock(), failures={operation: "timeout"})
                root = Path(directory)
                with self.assertRaisesRegex(runtime.VerificationError, operation):
                    self.invoke_runtime(root, docker)
                self.assertEqual(list(root.iterdir()), [])
                self.assertEqual(docker.calls[-1][0][1], "rm")
                self.assertEqual(docker.calls[-1][1], runtime.CLEANUP_TIMEOUT_SECONDS)

    def test_missing_container_after_run_error_or_timeout_retains_files_and_original_failure(self):
        for failure in ["exit", "timeout"]:
            with self.subTest(failure=failure), tempfile.TemporaryDirectory(prefix="runtime-missing-container-") as directory, \
                    contextlib.redirect_stderr(io.StringIO()) as errors:
                docker = DockerStub(Clock(), failures={"run": failure, "rm": "missing"})
                root = Path(directory)
                with self.assertRaisesRegex(runtime.VerificationError, "docker run") as raised:
                    self.invoke_runtime(root, docker, startup_timeout=runtime.DEFAULT_STARTUP_TIMEOUT_SECONDS)
                self.assertTrue(str(raised.exception).startswith("Command failed" if failure == "exit" else "Command timed out"))
                self.assertNotIn("runtime smoke passed", self.last_output.getvalue())
                retained = list(root.iterdir())
                self.assertEqual(len(retained), 1)
                self.assertTrue((retained[0] / "config/plugins/HueSync_1.5.458.0/meta.json").is_file())
                self.assertIn("No such container", errors.getvalue())
                self.assertIn(str(retained[0]), errors.getvalue())
                self.assertIn(docker.calls[-1][0][-1], errors.getvalue())
                self.assertEqual([command[1] for command, _ in docker.calls], ["pull", "info", "run", "rm"])
                self.assertEqual(docker.calls[-1][1], runtime.CLEANUP_TIMEOUT_SECONDS)

    def test_poll_sleep_and_health_subprocess_share_remaining_budget(self):
        docker = DockerStub(Clock(), failures={"exec": "timeout"})
        with tempfile.TemporaryDirectory(prefix="runtime-poll-failure-") as directory:
            with self.assertRaisesRegex(runtime.VerificationError, "Timed out waiting"):
                self.invoke_runtime(Path(directory), docker, startup_timeout=8)
        self.assertEqual([timeout for command, timeout in docker.calls if command[1] == "exec"], [5, 1])
        self.assertEqual(docker.clock.sleeps, [2])
        self.assertEqual(docker.clock.value, 108)

    def test_final_poll_sleep_never_exceeds_remaining_budget(self):
        docker = DockerStub(Clock(), output={"logs": "still starting"})
        with tempfile.TemporaryDirectory(prefix="runtime-final-poll-") as directory:
            with self.assertRaisesRegex(runtime.VerificationError, "Timed out waiting"):
                self.invoke_runtime(Path(directory), docker, startup_timeout=1)
        self.assertEqual(docker.clock.sleeps, [1])
        self.assertEqual(docker.clock.value, 101)

    def test_cleanup_failures_fail_successful_smoke_and_preserve_runtime_directory(self):
        for failure in ["timeout", "exit", "oserror"]:
            with self.subTest(failure=failure), tempfile.TemporaryDirectory(prefix="runtime-cleanup-failure-") as directory, \
                    contextlib.redirect_stderr(io.StringIO()) as errors:
                docker = DockerStub(Clock(), failures={"rm": failure})
                root = Path(directory)
                with self.assertRaisesRegex(runtime.VerificationError, "Runtime cleanup failed"):
                    self.invoke_runtime(root, docker)
                self.assertNotIn("runtime smoke passed", self.last_output.getvalue())
                retained = list(root.iterdir())
                self.assertEqual(len(retained), 1)
                self.assertTrue((retained[0] / "config/plugins/HueSync_1.5.458.0/meta.json").is_file())
                self.assertIn(str(retained[0]), errors.getvalue())
                self.assertIn(docker.calls[-1][0][-1], errors.getvalue())
                self.assertEqual(docker.calls[-1][1], runtime.CLEANUP_TIMEOUT_SECONDS)

    def test_cleanup_failure_does_not_replace_original_runtime_failure(self):
        docker = DockerStub(Clock(), failures={"rm": "timeout"}, output={"logs": "UnauthorizedAccessException"})
        with tempfile.TemporaryDirectory(prefix="runtime-double-failure-") as directory, contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaisesRegex(runtime.VerificationError, "forbidden plugin/runtime marker"):
                self.invoke_runtime(Path(directory), docker)
            self.assertEqual(len(list(Path(directory).iterdir())), 1)

    def test_concurrent_name_prefixes_never_remove_a_previous_container(self):
        docker = DockerStub(Clock())
        with tempfile.TemporaryDirectory(prefix="runtime-name-contract-") as directory:
            self.invoke_runtime(Path(directory), docker)
            self.invoke_runtime(Path(directory), docker)
        names = [command[command.index("--name") + 1] for command, _ in docker.calls if command[1] == "run"]
        removed = [command[-1] for command, _ in docker.calls if command[1] == "rm"]
        self.assertEqual(len(set(names)), 2)
        self.assertEqual(removed, names)


if __name__ == "__main__":
    unittest.main(testRunner=unittest.TextTestRunner(stream=sys.stdout))
