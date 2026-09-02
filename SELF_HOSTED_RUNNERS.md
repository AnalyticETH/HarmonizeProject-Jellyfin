# Self-hosted runner operations

This repository now uses one replacement repository-scoped Linux x64 runner. Every
workflow job, including release and pull-request validation, selects the exact label
set `[self-hosted, linux, x64, local-docker]`. The runner is registered as
`gaming-pc-analyticeth-harmonizeproject-jellyfin-01`; its GitHub runner ID is
host-specific and must be discovered from the repository runner API. The five older
Harmonize runner services and registrations are retired and must remain offline.

| Purpose | Runner label | Runner identity | Writable state |
| --- | --- | --- | --- |
| Build, tests, formatting, security, packaging, runtime smoke, release, PR validation | `local-docker` plus `self-hosted`, `linux`, `x64` | `gaming-pc-analyticeth-harmonizeproject-jellyfin-01` | Runner-managed workspace and Docker state; inspect the live runner host rather than assuming a repository path |

The replacement runner must be treated as a shared local Docker boundary. Keep its runner
registration and workspace isolated from interactive credentials, use ephemeral per-job
workspaces where the runner supports them, and do not grant workflows more GitHub permissions
than their checked-in job declarations. The workflow-level security restrictions remain
unchanged: pull-request jobs receive no secrets or write permissions, and trusted jobs retain
their `main` guards and bounded `timeout-minutes` values. The release job still has the only
`contents: write` permission, but it executes on the same replacement runner as requested.

Native Dependabot execution on this runner remains an owner-controlled GitHub setting. Enable
**Dependabot on self-hosted runners** only after confirming the replacement runner is online;
the setting is not represented in `.github/dependabot.yml`.

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

## Retired legacy services

The former five systemd runner services and their host-side health/version timers
are retired. They must be stopped, disabled, and absent from the repository's live
runner inventory. Do not re-enable them to troubleshoot a queued job; use the
replacement runner API checks below. The old unit files and scripts remain only as
reviewable historical teardown references and must not be installed again.

```bash
for unit in \
  actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service \
  actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-runtime.service \
  actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-release.service \
  actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-dependabot.service \
  actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-pr.service; do
  systemctl is-enabled "$unit" 2>/dev/null || true
  systemctl is-active "$unit" 2>/dev/null || true
done
systemctl is-enabled harmonize-runner-health-check.timer 2>/dev/null || true
systemctl is-enabled harmonize-runner-version-check.timer 2>/dev/null || true
```

## Independent runner-health monitoring

`.github/workflows/runner-health.yml` runs every 15 minutes, and on
`workflow_dispatch`, on the replacement `local-docker` self-hosted runner.
Keeping this read-only monitor on the local runner removes recurring GitHub-hosted
Actions usage. It has only `actions: read` and `contents: read` permissions, does
not check out repository code, uses no secrets, and cannot modify or cancel runs.
The GitHub runner API is the source of truth for the replacement runner; no
legacy systemd health timer is required.

The workflow records a diagnostic when the Actions API reports a queued workflow run
older than one hour, or when the latest successful trusted `main` run metadata is
partial. The current job's exact replacement-runner selector is the authoritative
health gate; follow the stale queued-run procedure above when the diagnostic persists.

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
dependencies directly. Every CI and security artifact declares `retention-days: 1`.

The published GitHub release ZIP, checksums, and manifest are release assets, not
workflow artifacts; storage cleanup must never delete those assets. When reducing
existing usage, scope deletion to generated Actions artifacts and caches only,
preserve the latest release-run evidence when operationally useful, and recheck
the repository's artifact/cache totals after cleanup. Existing accrued charges do
not disappear when storage is deleted; deletion only stops future storage accrual.

Untrusted pull requests, including Dependabot update branches, are validated by
`.github/workflows/pull-request-validation.yml` on the replacement `local-docker`
runner. That workflow has only `contents: read`, does not receive secrets, and never
publishes packages or creates tags. Keep its checkout/workspace cleanup enabled on
the replacement runner and do not route any workflow to a retired label.

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

The replacement runner's owner is responsible for keeping its container/image and
Actions runner binary current. Before an update, confirm the runner is idle from
the repository API, update the runner using the host's provisioning mechanism, and
verify it returns online with the same `local-docker` label. Never place a
registration, removal, repository, or personal access token in this repository or
a command transcript. The retired five-runner version timer must remain disabled.

## Verification

After provisioning or updating, verify the replacement runner and live workflow
execution:

```bash
gh api repos/AnalyticETH/HarmonizeProject-Jellyfin/actions/runners \
  --jq '.runners[] | {id,name,os,status,busy,labels: [.labels[].name]}'
gh run list --repo AnalyticETH/HarmonizeProject-Jellyfin --limit 5 \
  --json databaseId,status,conclusion,headBranch,workflowName
gh workflow run '.NET CI/CD' --ref main
```

The expected live inventory is one online, idle runner named
`gaming-pc-analyticeth-harmonizeproject-jellyfin-01` with labels
`self-hosted`, `Linux`, `X64`, and `local-docker`; the latest dispatched run must
show the same labels on every executed job and must report an empty `billable`
object. Release assets are checked separately and must not be deleted as part of
runner maintenance.

The authoritative setup and security references are GitHub's
[adding runners](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/add-runners),
[service configuration](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/configure-the-application),
[self-hosted runner reference](https://docs.github.com/en/actions/reference/runners/self-hosted-runners),
and [secure-use guidance](https://docs.github.com/en/actions/reference/security/secure-use).
