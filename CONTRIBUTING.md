# Contributing

Thank you for improving the Philips Hue Sync Plugin for Jellyfin. Contributions
must preserve the plugin's credential-safety, lifecycle-cleanup, Jellyfin ABI,
and reproducible-release contracts.

## Before opening a change

- Read [SECURITY.md](SECURITY.md) before reporting a security issue. Never put
  Hue App Keys, Client Keys, bridge certificates, access tokens, or private
  server logs in an issue or pull request.
- Read [SUPPORT.md](SUPPORT.md) for troubleshooting and issue triage guidance.
- Keep changes focused. Update `README.md`, `CHANGELOG.md`, and the relevant
  validator or test when a public behavior or release contract changes.
- Do not commit generated `bin/`, `obj/`, `publish/`, `release-package/`, test
  results, credentials, or local runner state.

## Development environment

The supported build is .NET SDK `8.0.424`, pinned by `global.json`. Restores use
the committed lock files and must remain locked:

```bash
dotnet restore --locked-mode
dotnet build Jellyfin.Plugin.Hue.sln --configuration Release --no-restore
dotnet test Jellyfin.Plugin.Hue.sln --configuration Release --no-build
dotnet format Jellyfin.Plugin.Hue.sln --verify-no-changes --no-restore
```

If the host does not have the SDK, use the documented Docker boundary from the
README and mount a local NuGet cache. Do not install dependencies into the
repository or weaken the lock files to make a test pass.

Run the repository contracts before submitting a change:

```bash
node scripts/validate-open-source-release.mjs
node scripts/validate-config-page.mjs
node scripts/test-config-page-exports.mjs
node scripts/validate-release-docs.mjs
node scripts/validate-dependabot.mjs
node scripts/validate-pull-request-workflow.mjs
node scripts/validate-workflow-inventory.mjs
node scripts/validate-trusted-workflow.mjs
node scripts/test-workflow-contracts.mjs
node scripts/validate-release-package.mjs
node scripts/validate-api-docs.mjs
```

The full trusted workflow additionally runs locked dependency vulnerability
checks, Gitleaks, Semgrep, coverage, the Linux release-helper parity check, and
Jellyfin 10.9.0/10.10.7 runtime smoke before publication.

## Pull requests

Describe the behavior change, affected Jellyfin versions, security implications,
and the exact verification commands. Include focused regression tests for new
validation, cancellation, persistence, routing, or bridge-lifecycle behavior.

Keep public API and configuration changes backward-compatible unless the pull
request explains the migration. Configuration exports and status projections
must remain credential-free. New bridge or FFmpeg inputs need bounded parsing,
fail-closed validation, and cancellation coverage.

Reviewers may request changes to documentation, tests, dependency locks, or
release contracts before merge. Changes to `.github/`, `ops/`, release helpers,
dependency locks, security policy, and plugin source are owner-reviewed through
the repository `CODEOWNERS` file.

## Release changes

The trusted `main` workflow publishes the version declared consistently in
`meta.json` and `Jellyfin.Plugin.Hue/Jellyfin.Plugin.Hue.csproj`. A release
change must:

1. Bump both four-part versions and add a matching SemVer section to
   `CHANGELOG.md`.
2. Keep the current release bullets synchronized with `meta.json`.
3. Preserve the exact package contents, sidecar checksums, source-commit
   manifest, and deterministic-build contract.
4. Pass the full local contracts before pushing to `main`.

Do not overwrite an existing published tag or release. The workflow fails closed
when a tag, draft, target commit, asset set, or digest does not match the new
source commit.
