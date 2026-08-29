# Security Policy

## Supported versions

The latest `1.5.x` release is supported with security fixes. Older releases should be upgraded before troubleshooting a security report.

## Reporting a vulnerability

If private vulnerability reporting is enabled for this repository, please use its GitHub Security Advisory channel. If GitHub does not offer that private channel for your account, contact the repository owner through an already-established private channel and ask for one before sharing technical details. Do not open a public issue or include credentials, bridge keys, access tokens, or other sensitive data in a report.

Include the affected release, reproduction steps, impact, and any suggested mitigation. Reports are triaged against the plugin code, its NuGet dependencies, and the GitHub Actions workflows.

## Automated coverage

Every trusted `main` push runs the named self-hosted CI runner with:

- A dedicated, least-privileged `harmonize-runner` service account and isolated home directory
- A main-only `workflow_dispatch` recovery trigger for operators, with no pull-request or non-main push trigger, plus a job-level `refs/heads/main` guard and a separate release-runner identity for the `contents:write` publication job
- Release packaging generates SHA-256 sidecars for the ZIP and a deterministic release manifest, carries them with the inter-job artifact, verifies them on the isolated release runner, and publishes the manifest beside the release ZIP
- The schema-version-2 release manifest records the exact 40-character source commit, each packaged file's size and SHA-256 digest, plus every resolved package/content hash from the plugin's committed NuGet lock file; helper and canonical manifests must compare byte-for-byte and the release runner rejects a source-commit mismatch
- Locked-mode NuGet restores with committed dependency content hashes
- NuGet vulnerability auditing through `dotnet list package --vulnerable --include-transitive`
- Blocking Gitleaks history scanning with a redacted JSON artifact
- Blocking split Semgrep static analysis: a reviewed SHA-256 snapshot of the `p/default` ruleset scans tracked C#, YAML, JSON, PowerShell, and shell source/configuration, while separately pinned `p/javascript` and `p/python` snapshots scan repository JavaScript and Python scripts. The embedded administrator-page JavaScript is extracted from the DLL-embedded configuration page into a temporary file and scanned with the pinned JavaScript rules; the explicit Express/React framework-only exclusions in `.github/semgrep/config-page-excludes.txt` keep generic and browser rules blocking without applying server-framework analyses to this vanilla page. Each JSON report blocks findings, scanner errors, and fixpoint-analysis timeouts. Snapshot downloads are hash-verified and configuration-validated before use. The scanner and all transitive packages come from `.github/semgrep/requirements.txt`, a Python 3.12/x86_64 SHA-256 lock validated before `pip --require-hashes` installation
- A source-controlled trusted-workflow boundary validator that checks main-only triggers and guards, self-hosted runner identities, isolated release permissions, immutable action references, and strict release-version extraction
- Run-scoped Semgrep state: each self-hosted and pull-request scan keeps its hash-locked virtual environment, reviewed rule snapshots, extracted administrator JavaScript, and reports under a workflow-run/attempt directory, preventing canceled-run cleanup from invalidating a newer scan while preserving fail-closed findings, scanner-error, and timeout gates
- Host shutdown cancellation leaves an observed deferred cleanup pass that retries lifecycle-lock, bridge deactivation, and concurrent-worker cleanup before a service restart can accept new playback
- Credential-free scene, playlist, status, diagnostics, and user-mapping projections use a shared read lease and fail closed with a retryable conflict while configuration mutation or scheduler evaluation is active
- Immutable commit-SHA references for third-party GitHub Actions
- Repository-level GitHub Actions supply-chain policy is selected-only with `sha_pinning_required=true`: `actions/checkout`, `actions/setup-dotnet`, `actions/cache`, `actions/upload-artifact`, `actions/download-artifact`, `actions/github-script` (the pinned Codecov composite dependency), and `codecov/codecov-action` are the only approved action repositories (each matched with an `@*` policy pattern and pinned to a full commit SHA in the workflows); local reusable workflows remain allowed
- Dependabot monitoring for the hash-locked Semgrep environment and its transitive packages
- Bounded job timeouts that release a persistent runner when a restore, scanner, or release step stalls
- Coverage publication keeps the immutable Codecov action optional and explicit: every trusted build retains
  its Cobertura artifact, reports when `CODECOV_TOKEN` is absent, and fails closed when a configured upload
  cannot be authenticated or completed

Bridge HTTP transport also disables automatic redirects and excludes raw bridge response bodies from
registration/start/stop failure logs, preventing credential-shaped response fields from being sent to
an unintended host or retained in Jellyfin logs. Local bridge certificates are pinned by SHA-256
fingerprint before registration or any credential-bearing request; the administrator UI performs a
credential-free probe and requires explicit confirmation, while unpinned or changed certificates fail
closed.

Entertainment-area configuration parsing also fails closed when an area identity is present but
malformed or does not match the requested resource. The legacy first-entry fallback is retained only
for responses where every returned resource omits its identity, so malformed bridge metadata cannot
silently supply another area's channel layout.

The repository-controlled weekly default-branch security workflow reruns the blocking Gitleaks and Semgrep gates. Both scanner jobs carry the same `refs/heads/main` guard, so pull-request and non-main code never reach the persistent runner.

Pull requests, including Dependabot updates, use the separate
`.github/workflows/pull-request-validation.yml` workflow on an ephemeral GitHub-hosted
`ubuntu-24.04` runner. It grants only `contents: read`, does not expose repository
secrets, and performs no release, tag, or write operation. This preserves the persistent
self-hosted runner boundary: untrusted pull-request code never executes under either
`harmonize-runner` or `harmonize-release-runner`.

The identity split, host confinement, daily upstream-version monitor, and manual verified-update procedure are documented in [SELF_HOSTED_RUNNERS.md](SELF_HOSTED_RUNNERS.md).
