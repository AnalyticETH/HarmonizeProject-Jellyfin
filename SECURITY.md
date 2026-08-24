# Security Policy

## Supported versions

The latest `1.5.x` release is supported with security fixes. Older releases should be upgraded before troubleshooting a security report.

## Reporting a vulnerability

Please report suspected vulnerabilities privately through a GitHub Security Advisory for this repository. Do not open a public issue or include credentials, bridge keys, access tokens, or other sensitive data in a report.

Include the affected release, reproduction steps, impact, and any suggested mitigation. Reports are triaged against the plugin code, its NuGet dependencies, and the GitHub Actions workflows.

## Automated coverage

Every trusted `main` push runs the named self-hosted CI runner with:

- A dedicated, least-privileged `harmonize-runner` service account and isolated home directory
- No `workflow_dispatch`, pull-request, or non-main push trigger, plus a job-level `refs/heads/main` guard and a separate release-runner identity for the `contents:write` publication job
- Locked-mode NuGet restores with committed dependency content hashes
- NuGet vulnerability auditing through `dotnet list package --vulnerable --include-transitive`
- Blocking Gitleaks history scanning with a redacted JSON artifact
- Blocking Semgrep static analysis with the explicit `p/default` ruleset and a JSON artifact; the scanner and all transitive packages come from `.github/semgrep/requirements.txt`, a Python 3.12/x86_64 SHA-256 lock validated before `pip --require-hashes` installation
- Immutable commit-SHA references for third-party GitHub Actions
- Repository-level GitHub Actions SHA-pinning enforcement (`sha_pinning_required=true`) while retaining the current action allowlist
- Dependabot monitoring for the hash-locked Semgrep environment and its transitive packages
- Bounded job timeouts that release a persistent runner when a restore, scanner, or release step stalls

Bridge HTTP transport also disables automatic redirects and excludes raw bridge response bodies from
registration/start/stop failure logs, preventing credential-shaped response fields from being sent to
an unintended host or retained in Jellyfin logs.

The repository-controlled weekly default-branch security workflow reruns the blocking Gitleaks and Semgrep gates. Both scanner jobs carry the same `refs/heads/main` guard, so pull-request and non-main code never reach the persistent runner.

The identity split, host confinement, daily upstream-version monitor, and manual verified-update procedure are documented in [SELF_HOSTED_RUNNERS.md](SELF_HOSTED_RUNNERS.md).
