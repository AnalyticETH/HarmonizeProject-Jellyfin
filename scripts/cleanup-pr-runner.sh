#!/bin/sh
set -eu

# This hook is intentionally scoped to the disposable PR runner's own work
# tree. It runs after every job and before service start so canceled jobs cannot
# leave repository code, credentials, or generated package state for the next
# pull request. Keep the runner installation and diagnostics outside this tree.
runner_root=${RUNNER_ROOT:?RUNNER_ROOT must identify the PR runner installation}
work_root="$runner_root/_work"

if [ -d "$work_root" ]; then
    find "$work_root" -depth -mindepth 1 -delete
fi
