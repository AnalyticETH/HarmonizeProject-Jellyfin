# Security Policy

## Supported versions

The latest `1.5.x` release is supported with security fixes. Older releases should be upgraded before troubleshooting a security report.

## Reporting a vulnerability

If private vulnerability reporting is enabled for this repository, please use its GitHub Security Advisory channel. If GitHub does not offer that private channel for your account, contact the repository owner through an already-established private channel and ask for one before sharing technical details. Do not open a public issue or include credentials, bridge keys, access tokens, or other sensitive data in a report.

Include the affected release, reproduction steps, impact, and any suggested mitigation. Reports are triaged against the plugin code, its NuGet dependencies, and the GitHub Actions workflows.

Private vulnerability reporting is not a launch gate for the owner-approved
`1.5.461.0` release. Keep using a private channel when one is available; never
put credentials, bridge keys, access tokens, or private server logs in a public
issue or pull request.

## Automated coverage

Every trusted `main` push runs on an ephemeral GitHub-hosted `ubuntu-24.04` runner with:

- Separate ephemeral jobs for pull-request validation and trusted release work; public PR code does not share a repository-owned runner, host Docker socket, or persistent workspace with release jobs.
- A main-only `workflow_dispatch` recovery trigger for operators, with no pull-request or non-main push trigger in the trusted workflow, plus a job-level `refs/heads/main` guard and job-scoped `contents:write` for publication
- Release packaging generates SHA-256 sidecars for the ZIP and a deterministic release manifest, carries them with the inter-job artifact, verifies them in the release job, and publishes the manifest beside the release ZIP
- The schema-version-2 release manifest records the exact 40-character source commit, each packaged file's size and SHA-256 digest, plus every resolved package/content hash from the plugin's committed NuGet lock file; helper and canonical manifests must compare byte-for-byte and the release job rejects a source-commit mismatch
- Locked-mode NuGet restores with committed dependency content hashes
- NuGet vulnerability auditing through `dotnet list package --vulnerable --include-transitive`
- Blocking Gitleaks history scanning with a redacted JSON artifact
- Blocking split Semgrep static analysis: a reviewed SHA-256 snapshot of the `p/default` ruleset scans tracked C#, YAML, JSON, PowerShell, and shell source/configuration, while separately pinned `p/javascript` and `p/python` snapshots scan repository JavaScript and Python scripts. The embedded administrator-page JavaScript is extracted from the DLL-embedded configuration page into a temporary file and scanned with the pinned JavaScript rules; the explicit Express/React framework-only exclusions in `.github/semgrep/config-page-excludes.txt` keep generic and browser rules blocking without applying server-framework analyses to this vanilla page. Its larger-than-default target-size allowance and report target-presence assertion prevent the embedded scan from silently skipping the extracted page. Each JSON report blocks findings, scanner errors, fixpoint-analysis timeouts, and an absent embedded target. Snapshot downloads are hash-verified and configuration-validated before use. The scanner and all transitive packages come from `.github/semgrep/requirements.txt`, a Python 3.12/x86_64 SHA-256 lock validated before `pip --require-hashes` installation
- A source-controlled trusted-workflow boundary validator that parses job blocks to check main-only triggers and guards, the pinned GitHub-hosted runner label, job-aware release permissions, bounded concrete-job timeouts, immutable action references, and strict release-version extraction; executable negative fixtures cover permission leakage and missing timeout contracts
- Run-scoped Semgrep state: each scan keeps its hash-locked virtual environment, reviewed rule snapshots, extracted administrator JavaScript, and reports under a workflow-run/attempt directory, preserving fail-closed findings, scanner-error, and timeout gates
- Host shutdown cancellation leaves an observed deferred cleanup pass that retries lifecycle-lock, bridge deactivation, and concurrent-worker cleanup before a service restart can accept new playback
- Credential-free scene, playlist, status, diagnostics, and user-mapping projections use a shared read lease and fail closed with a retryable conflict while configuration mutation or scheduler evaluation is active
- Bulk actions, target-selection previews/captures, mapping reconciliation controls, and other small administrator request bodies are capped at 64 KiB before model binding; larger playlist/schedule/mapping documents remain capped at 1 MiB and imports at 8 MiB
- Browser configuration restores reject local files larger than 8 MiB before `FileReader` parsing, malformed top-level collection shapes fail closed before credential-field rendering, and entertainment-configuration responses are capped at 256 channels before downstream enumeration
- Administrator bridge discovery requests are page-generation-owned, abort on teardown, and ignore stale bridge-input edits before applying discovered addresses or status
- Immutable commit-SHA references for third-party GitHub Actions
- Repository-level GitHub Actions supply-chain policy is selected-only with `sha_pinning_required=true`: `actions/checkout`, `actions/setup-dotnet`, `actions/cache`, `actions/upload-artifact`, `actions/download-artifact`, `actions/github-script` (the pinned Codecov composite dependency), and `codecov/codecov-action` are the only approved action repositories (each matched with an `@*` policy pattern and pinned to a full commit SHA in the workflows); local reusable workflows remain allowed
- Dependabot monitoring for the hash-locked Semgrep environment and its transitive packages
- Bounded job timeouts that stop a hosted job when a restore, scanner, or release step stalls
- Coverage publication keeps the immutable Codecov action optional and explicit: every trusted build retains
  its Cobertura artifact, reports when `CODECOV_TOKEN` is absent, and fails closed when a configured upload
  cannot be authenticated or completed
- Test and coverage evidence is fail-closed: a trusted build must produce a non-empty TRX result and Cobertura
  report, and their artifact uploads reject missing files before release packaging can proceed

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

The repository-controlled weekly default-branch security workflow reruns the blocking Gitleaks and Semgrep gates. Both scanner jobs carry the same `refs/heads/main` guard. The separate PR workflow runs untrusted code only on an ephemeral GitHub-hosted runner.

Pull requests, including Dependabot updates, use the separate
`.github/workflows/pull-request-validation.yml` workflow on `ubuntu-24.04`. It grants
only `contents: read`, does not expose repository secrets, performs no release, tag, or
write operation, and receives a fresh hosted workspace for every job.

The `native-platform-build.yml` workflow builds and tests Linux x64, Windows x64,
macOS Intel, and macOS Apple Silicon and uploads one verification artifact per
target. Read-only workflow permissions are defense in depth; the owner has
waived real fork-PR execution and private vulnerability-reporting setup as launch
gates. Physical Hue, upgrade/rollback, licensing/source authority, and protected
visibility remain separate release-readiness questions; see
[RELEASE_READINESS.md](RELEASE_READINESS.md).

Former self-hosted runner details are retained only as a short retirement note in
[SELF_HOSTED_RUNNERS.md](SELF_HOSTED_RUNNERS.md).
