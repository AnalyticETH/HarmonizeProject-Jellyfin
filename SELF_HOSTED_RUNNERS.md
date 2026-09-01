# Self-hosted runner operations

This repository uses repository-scoped Linux x64 runners on the Jellyfin host. The
installations follow GitHub's self-hosted-runner and service guidance and deliberately
separate build state from release authority.

| Purpose | Runner label | Service account | Writable state |
| --- | --- | --- | --- |
| Build, tests, formatting, package audit, Gitleaks, Semgrep, packaging, Linux release-helper validation | `harmonizeproject-jellyfin` | `harmonize-runner` | `/var/lib/harmonize-runner/actions-runner/_work`, `_diag`, `_temp`, dedicated home/cache |
| Disposable Jellyfin runtime smoke verification for the canonical ZIP (provisioned and online on the Jellyfin host) | `harmonizeproject-jellyfin-runtime` | `harmonize-runtime-runner` | `/var/lib/harmonize-runtime-runner/actions-runner/_work`, `_diag`, `_temp`, dedicated home/cache/rootless-Docker state |
| GitHub Release publication only | `harmonizeproject-jellyfin-release` | `harmonize-release-runner` | `/var/lib/harmonize-release-runner/actions-runner/_work`, `_diag`, `_temp`, dedicated home/cache |
| Dependabot update jobs | `dependabot` (with GitHub's default `self-hosted`, `Linux`, and `X64` labels) | `harmonize-dependabot-runner` | `/var/lib/harmonize-dependabot-runner/actions-runner/_work`, `_diag`, `_temp`, dedicated home/cache/rootless-Docker state |

All four identities are locked system users with `nologin`, no sudo, no privileged Docker
access, no LXD access, and no supplementary groups, and no access to the interactive user's home or
GitHub CLI credentials. The build and release identities have no Docker daemon or socket
access. The runtime-smoke and Dependabot identities use separate rootless Docker sockets
with dedicated writable state; neither socket is exposed to the build or release runners.
Their root-owned runner installations are read-only inside systemd;
credentials are `0440` and work/home/cache directories are `0700`. The services use
`ProtectSystem=strict`, `ProtectHome`, private devices and temporary directories,
namespace and SUID/SGID restrictions, an empty capability set, and bounded resources.

The runtime-smoke identity is intentionally separate because the trusted workflow now
boots the published `jellyfin-plugin-hue-release.zip` inside an official pinned Jellyfin
container before publication. Provision it only with the minimum rootless-Docker access
required to pull `jellyfin/jellyfin@sha256:3b38dae4c3ddd6ebc7378538fba4d3f314070ebefbdb3d688166b7c8658fb123`
and run a bounded disposable container on localhost. Keep its runner installation
root-owned and read-only, bind only its own socket into the service, and keep it isolated
from the release runner's GitHub credentials.

The Dependabot identity is intentionally separate because Dependabot update jobs execute
untrusted package-manager and build code. Its runner installation is also root-owned and
read-only, while its Docker daemon runs rootless under the same locked service account at
`unix:///run/user/980/docker.sock`. The rootless user manager is kept alive with
`loginctl enable-linger harmonize-dependabot-runner`; the daemon is bounded to 8 GiB of
memory, 8 GiB of swap, 2,048 tasks, and 400% CPU. The runner service is bounded by the same
8 GiB memory/swap ceiling, 2,048 tasks, and 400% CPU, and exposes only the dedicated socket
through a read-only `/run/user/980` bind. It is never used by trusted build or release jobs.

After provisioning the runner, enable **Dependabot on self-hosted runners** in the
repository's GitHub Settings → Advanced Security page. GitHub will keep Dependabot jobs on
the `dependabot` label; do not enable that setting until an online runner with that label is
available, or jobs will remain queued indefinitely. The setting is owner-controlled and is
not represented in `.github/dependabot.yml`.

Persistent runners accept only repository-controlled trusted `main` pushes, the main-only
operator recovery dispatch, and scheduled default-branch security scans. The trusted main
and scheduled security workflows expose no pull-request or non-main push triggers, and every self-hosted
job has a `github.ref == 'refs/heads/main'` guard as defense in depth. Pull-request and non-main code
must not be routed to any persistent identity. The release label is reserved for the single
`contents:write` job. Every job also has a bounded `timeout-minutes` budget (20 minutes for
build/test, runtime smoke, and the Linux release-helper validation; 15 minutes for quality,
security, and packaging; and 10 minutes for release publication) so a stalled network operation or tool cannot
hold a persistent runner forever.

The blocking security workflow uses an event-scoped concurrency key:
`security-scan-${{ github.workflow }}-${{ github.ref }}-${{ github.event_name }}` with
`cancel-in-progress: true`. Consequently, the weekly default-branch `schedule` scan cannot
cancel or be canceled by the `workflow_call` security gate invoked by a trusted `main` push.
Cancellation remains intentional for an older scan in the same event/ref lane; preserve this
event component whenever the workflow or its triggers are changed.

## Stale queued-run recovery

Persistent-runner outages can leave a GitHub Actions run displayed as `queued` even though it
has no assigned job. Treat a queued run older than one hour as an operator incident rather than
re-running the workflow repeatedly:

```bash
gh run list --repo AnalyticETH/HarmonizeProject-Jellyfin --status queued --limit 50 \
  --json databaseId,createdAt,headBranch,headSha,event,status,workflowName
run_id=REPLACE_WITH_THE_DATABASE_ID
gh api "repos/AnalyticETH/HarmonizeProject-Jellyfin/actions/runs/$run_id" \
  --jq '{id,status,conclusion,event,head_branch,head_sha,created_at,updated_at}'
gh api "repos/AnalyticETH/HarmonizeProject-Jellyfin/actions/runs/$run_id/jobs" \
  --jq '{total_count,jobs: [.jobs[] | {id,status,conclusion,runner_name,labels: [.labels[].name]}]}'
```

Only cancel a confirmed stale run after checking that its ref is the trusted `main` ref and
that no job is actively using a runner. Try the supported cancellation endpoint and record the
HTTP result:

```bash
gh api --method POST \
  "repos/AnalyticETH/HarmonizeProject-Jellyfin/actions/runs/$run_id/cancel"
```

If GitHub returns `409` because the run has not entered the cancellable queue, do not delete the
run or its artifacts. Capture the run ID, timestamps, API response, and zero-job result, then
escalate through the repository Actions UI/owner support path until GitHub reconciles the run.
After reconciliation, verify that no queued run older than one hour remains and that the next
trusted `main` push executes on the expected runner labels. Never paste registration tokens,
personal access tokens, or runner credentials into the incident record.

## Host-side runner service health

The current online state of the four persistent runners is checked on this host,
not inferred from a GitHub-hosted workflow. Install the versioned script and
systemd units from `scripts/check-runner-services.sh` and `ops/systemd/` as root:

```bash
sudo install -o root -g root -m 0755 scripts/check-runner-services.sh \
  /usr/local/sbin/harmonize-runner-health-check
sudo install -o root -g root -m 0644 \
  ops/systemd/harmonize-runner-health-check.service \
  /etc/systemd/system/harmonize-runner-health-check.service
sudo install -o root -g root -m 0644 \
  ops/systemd/harmonize-runner-health-check.timer \
  /etc/systemd/system/harmonize-runner-health-check.timer
sudo systemctl daemon-reload
sudo systemctl enable --now harmonize-runner-health-check.timer
sudo systemctl start harmonize-runner-health-check.service
```

The oneshot fails if any of these exact services is not enabled and active with
its documented dedicated `User`/`Group` and confinement properties:
`harmonizeproject-jellyfin`, `harmonizeproject-jellyfin-runtime`,
`harmonizeproject-jellyfin-release`, or `harmonizeproject-jellyfin-dependabot`.
It verifies `NoNewPrivileges`, private temporary/device namespaces,
`ProtectSystem=strict`, the expected `ProtectHome` mode, `UMask=0077`, and
`LimitCORE=0`; a unit that is running but has been weakened therefore fails
closed before it is treated as healthy. Each read-only `systemctl` query is
retried at most three times with a one- and two-second backoff to tolerate a
transient systemd D-Bus endpoint failure; an exhausted query still fails
closed.
The timer runs two minutes after boot and every five minutes thereafter. Inspect
the live result with `systemctl status harmonize-runner-health-check.service` and
`journalctl -u harmonize-runner-health-check.service`.

## Independent runner-health monitoring

`.github/workflows/runner-health.yml` runs every 15 minutes, and on
`workflow_dispatch`, on the trusted `harmonizeproject-jellyfin` self-hosted
runner. Keeping this read-only monitor on the local runner removes recurring
GitHub-hosted Actions usage. It has only `actions: read` and `contents: read`
permissions, does not check out repository code, uses no secrets, and cannot
modify or cancel runs. The host-side systemd timer remains independent: it is the
source of truth for current service state and detects a total runner-pool outage
when this Actions monitor cannot start.

The workflow fails closed when the Actions API reports a queued workflow run older
than one hour, or when the latest successful trusted `main` run no longer shows
the required `harmonizeproject-jellyfin`, `harmonizeproject-jellyfin-runtime`,
and `harmonizeproject-jellyfin-release` labels on successful jobs. The host-side
timer above checks current service state for all four runners, including the
`harmonizeproject-jellyfin-dependabot` service. A failure is an operator signal;
follow the stale queued-run procedure above after checking the run's ref, jobs,
and runner use. Because this monitor shares the trusted runner pool, the
host-side timer is the detector to use during a complete pool outage.

The repository runner inventory endpoint requires repository-administration access
that the read-only workflow `GITHUB_TOKEN` cannot receive. The split between the
host-side service check and this read-only queue/label monitor is intentional: it
avoids storing a long-lived personal token or silently treating an unauthorized
runner-inventory request as a healthy result.

## Actions storage and billing

Self-hosted execution does not consume GitHub-hosted runner minutes, but generated
Actions artifacts and remote caches remain separate billing resources. This
repository therefore does not use `actions/cache`: the build runner's local NuGet
cache is retained under its locked service account, while pull-request jobs restore
dependencies directly. Every CI and security artifact declares `retention-days: 7`.

The published GitHub release ZIP, checksums, and manifest are release assets, not
workflow artifacts; storage cleanup must never delete those assets. When reducing
existing usage, scope deletion to generated Actions artifacts and caches only,
preserve the latest release-run evidence when operationally useful, and recheck
the repository's artifact/cache totals after cleanup. Existing accrued charges do
not disappear when storage is deleted; deletion only stops future storage accrual.

Untrusted pull requests, including Dependabot update branches, are validated by
`.github/workflows/pull-request-validation.yml` on the ephemeral GitHub-hosted
`ubuntu-24.04` runner. That workflow has only `contents: read`, does not receive secrets,
and never publishes packages, creates tags, or invokes any persistent identity. Do not
add a pull-request trigger to the trusted main workflow or route pull-request code to the
`harmonizeproject-jellyfin` or `harmonizeproject-jellyfin-release` labels.

The repository Actions policy is selected-only with full-commit-SHA pinning required. Its
allowlist contains only `actions/checkout`, `actions/setup-dotnet`, `actions/cache`,
`actions/upload-artifact`, `actions/download-artifact`, `actions/github-script`, and
`codecov/codecov-action`, each
matched with an `@*` policy pattern so Dependabot can refresh the pinned SHA. Local reusable
workflows remain enabled, while broad GitHub-owned and Marketplace-verified allowances stay
disabled. Verify the live policy before changing workflows:

```bash
gh api repos/AnalyticETH/HarmonizeProject-Jellyfin/actions/permissions
gh api repos/AnalyticETH/HarmonizeProject-Jellyfin/actions/permissions/selected-actions
```

The build, security-scan, and pull-request workflows also run
`scripts/validate-workflow-inventory.mjs`. It fails closed if a new workflow file is
added without updating the reviewed inventory, if an action is outside the selected
allowlist or is not pinned to a full commit SHA, or if a new workflow routes code to a
persistent runner without the trusted `main` guard. It also requires every concrete job to
declare a positive `timeout-minutes` value no greater than 30; local reusable-workflow
caller jobs are exempt because GitHub's caller syntax does not accept that key, while the
called workflow's concrete jobs remain bounded. `scripts/test-workflow-contracts.mjs`
exercises temporary negative fixtures for write-permission leakage and missing timeouts.
This keeps the runner-boundary invariant enforceable when workflows change.

## Version maintenance

Automatic in-place updates are disabled because the application directories are
root-owned. `harmonize-runner-version-check.timer` checks every installed runner version against
the latest official `actions/runner` release every day. A mismatch leaves the oneshot
service failed and records every installed version in the system journal:

```bash
systemctl status harmonize-runner-version-check.service
journalctl -u harmonize-runner-version-check.service
systemctl list-timers harmonize-runner-version-check.timer
```

Before GitHub's 30-day update deadline, an administrator must confirm every installed runner is
idle, download the official Linux x64 archive, verify the SHA-256 digest published by the
GitHub Releases API, stop every runner service, extract the verified archive over each
installation without replacing `.runner` or `.credentials*`, restore the documented
ownership/modes, and restart and re-verify each service. Never place a registration,
removal, repository, or personal access token in this repository or a command transcript.

## Verification

After installation or update, verify boot enablement, process identity, confinement,
labels, and live workflow execution:

```bash
systemctl is-enabled actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service
systemctl is-enabled actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-runtime.service
systemctl is-enabled actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-release.service
systemctl is-enabled actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-dependabot.service
systemd-analyze security actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service
systemd-analyze security actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-runtime.service
systemd-analyze security actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-release.service
systemd-analyze security actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-dependabot.service
gh api repos/AnalyticETH/HarmonizeProject-Jellyfin/actions/runners
sudo -u harmonize-runtime-runner env \
  HOME=/var/lib/harmonize-runtime-runner/home \
  XDG_RUNTIME_DIR=/run/user/RUNTIME_UID \
  DOCKER_HOST=unix:///run/user/RUNTIME_UID/docker.sock \
  docker info
sudo -u harmonize-dependabot-runner env \
  HOME=/var/lib/harmonize-dependabot-runner/home \
  XDG_RUNTIME_DIR=/run/user/980 \
  DOCKER_HOST=unix:///run/user/980/docker.sock \
  docker info
```

The authoritative setup and security references are GitHub's
[adding runners](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/add-runners),
[service configuration](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/configure-the-application),
[self-hosted runner reference](https://docs.github.com/en/actions/reference/runners/self-hosted-runners),
and [secure-use guidance](https://docs.github.com/en/actions/reference/security/secure-use).
