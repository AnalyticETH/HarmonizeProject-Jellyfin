# Self-hosted runner operations

This repository uses repository-scoped Linux x64 runners on the Jellyfin host. The
installations follow GitHub's self-hosted-runner and service guidance and deliberately
separate build state from release authority.

| Purpose | Runner label | Service account | Writable state |
| --- | --- | --- | --- |
| Build, tests, formatting, package audit, Gitleaks, Semgrep, packaging | `harmonizeproject-jellyfin` | `harmonize-runner` | `/var/lib/harmonize-runner/actions-runner/_work`, `_diag`, `_temp`, dedicated home/cache |
| GitHub Release publication only | `harmonizeproject-jellyfin-release` | `harmonize-release-runner` | `/var/lib/harmonize-release-runner/actions-runner/_work`, `_diag`, `_temp`, dedicated home/cache |

Both identities are locked system users with `nologin`, no sudo, Docker, LXD, or
supplementary groups, and no access to the interactive user's home or GitHub CLI
credentials. Their root-owned runner installations are read-only inside systemd;
credentials are `0440` and work/home/cache directories are `0700`. The services use
`ProtectSystem=strict`, `ProtectHome`, private devices and temporary directories,
namespace and SUID/SGID restrictions, an empty capability set, and bounded resources.

Persistent runners accept only repository-controlled trusted `main` pushes, the main-only
operator recovery dispatch, and scheduled default-branch security scans. The trusted main
and scheduled security workflows expose no pull-request or non-main push triggers, and every self-hosted
job has a `github.ref == 'refs/heads/main'` guard as defense in depth. Pull-request and non-main code
must not be routed to either identity. The release label is reserved for the single
`contents:write` job. Every job also has a bounded `timeout-minutes` budget (20 minutes for
build/test, 15 minutes for quality, security, and packaging, and 10 minutes for release
publication) so a stalled network operation or tool cannot hold a persistent runner forever.

Untrusted pull requests, including Dependabot update branches, are validated by
`.github/workflows/pull-request-validation.yml` on the ephemeral GitHub-hosted
`ubuntu-24.04` runner. That workflow has only `contents: read`, does not receive secrets,
and never publishes packages, creates tags, or invokes either persistent identity. Do not
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

## Version maintenance

Automatic in-place updates are disabled because the application directories are
root-owned. `harmonize-runner-version-check.timer` checks both installed versions against
the latest official `actions/runner` release every day. A mismatch leaves the oneshot
service failed and records both versions in the system journal:

```bash
systemctl status harmonize-runner-version-check.service
journalctl -u harmonize-runner-version-check.service
systemctl list-timers harmonize-runner-version-check.timer
```

Before GitHub's 30-day update deadline, an administrator must confirm both runners are
idle, download the official Linux x64 archive, verify the SHA-256 digest published by the
GitHub Releases API, stop both services, extract the verified archive over each
installation without replacing `.runner` or `.credentials*`, restore the documented
ownership/modes, and restart and re-verify both services. Never place a registration,
removal, repository, or personal access token in this repository or a command transcript.

## Verification

After installation or update, verify boot enablement, process identity, confinement,
labels, and live workflow execution:

```bash
systemctl is-enabled actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service
systemctl is-enabled actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-release.service
systemd-analyze security actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service
systemd-analyze security actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-release.service
gh api repos/AnalyticETH/HarmonizeProject-Jellyfin/actions/runners
```

The authoritative setup and security references are GitHub's
[adding runners](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/add-runners),
[service configuration](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/configure-the-application),
[self-hosted runner reference](https://docs.github.com/en/actions/reference/runners/self-hosted-runners),
and [secure-use guidance](https://docs.github.com/en/actions/reference/security/secure-use).
