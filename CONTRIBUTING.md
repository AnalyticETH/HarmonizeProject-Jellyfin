# Contributing

Contributions must preserve the plugin's credential-safety, lifecycle-cleanup, Jellyfin ABI,
and reproducible-release contracts.

## Before opening a change

- Read [SECURITY.md](SECURITY.md) before reporting a security issue. Never put
  Hue App Keys, Client Keys, bridge certificates, access tokens, or private
  server logs in an issue or pull request.
- Read [SUPPORT.md](SUPPORT.md) for troubleshooting and issue triage guidance.
- Keep changes focused. Update the relevant reference document, `CHANGELOG.md`,
  and validator or test when a public behavior or release contract changes.
- Keep `README.md` human-oriented and within 150 lines and 900 words. Put settings
  in [docs/CONFIGURATION.md](docs/CONFIGURATION.md), endpoint contracts in
  [docs/API.md](docs/API.md), and release history in `CHANGELOG.md`.
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

If the host does not have the SDK, run these commands in a .NET SDK container
matching `global.json`, with the checkout and a local NuGet cache mounted.
Do not install dependencies into the repository or weaken the lock files to
make a test pass.

Run the repository contracts before submitting a change:

```bash
node scripts/validate-open-source-release.mjs
node scripts/test-documentation-contracts.mjs
node scripts/test-release-publication-contracts.mjs
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

The hosted `native-platform-build.yml` workflow separately restores, builds,
tests, formats, and publishes verification artifacts on Linux x64, Windows x64,
macOS Intel, and macOS Apple Silicon. These are platform-build checks; the
canonical deterministic release ZIP remains produced by the trusted Linux
release job.

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

## Development reference

### Building

Use .NET SDK `8.0.424`, pinned by `global.json`. Run commands from the repository
root unless a block explicitly changes directories.

#### Quick Build (Recommended)

Use the provided build scripts to create a release package:

**Linux/macOS:**
```bash
./build-release.sh
```

**Windows:**
```powershell
.\build-release.ps1
```

These scripts will:
- Build the plugin in Release mode
- Run all tests
- Create a release package with proper versioning
- Generate a zip file ready for distribution

Both helpers restore from the committed, content-hashed NuGet dependency graph with
`dotnet restore --locked-mode`; dependency drift stops the release before build or packaging.

#### Manual Build

```bash
dotnet restore Jellyfin.Plugin.Hue/Jellyfin.Plugin.Hue.csproj --locked-mode
dotnet build Jellyfin.Plugin.Hue/Jellyfin.Plugin.Hue.csproj --configuration Release --no-restore
```

The plugin DLL will be generated at:
`Jellyfin.Plugin.Hue/bin/Release/net8.0/Jellyfin.Plugin.Hue.dll`

> **Note**: The DLL path is useful for local build inspection. For an installation, use the release ZIP so the required `meta.json` manifest is installed alongside the plugin assembly.

#### Release Package Contents

The local release scripts produce `jellyfin-plugin-hue-v<version>.zip` and matching archive and manifest `.sha256` sidecars, while the GitHub release workflow publishes the canonical `jellyfin-plugin-hue-release.zip`, its checksum sidecar, and the matching `jellyfin-plugin-hue-release.manifest.json` plus `.sha256` sidecar. Each archive contains exactly:

* `BouncyCastle.Cryptography.dll` — the managed DTLS transport dependency
* `Jellyfin.Plugin.Hue.dll` — the plugin assembly, including the embedded configuration page
* `LICENSE` - the complete GPL-3.0-only license
* `NOTICE` - project copyright and bundled runtime dependency notices
* `meta.json` — the Jellyfin plugin manifest and release version

The release manifest records the exact source commit, archive digest, each packaged
file's size and SHA-256 digest, and the resolved hash-locked NuGet graph. The graph
includes every resolved package/content hash from the plugin's committed lock file.
The source-commit binding ties the published bytes to the reviewed workflow commit.

Trusted `main` CI also boots the canonical published ZIP inside disposable localhost-only Jellyfin
containers before release publication by running `scripts/verify-jellyfin-runtime-smoke.py` on the
GitHub-hosted `ubuntu-24.04` runner. The runtime matrix covers the
official Jellyfin 10.9.0 linux/amd64 image pinned as
`jellyfin/jellyfin@sha256:d659991fdbda4d2963c807747fbd1ee237bfd15971a3923158719eb248ddea67`
and Jellyfin 10.10.7 pinned as
`jellyfin/jellyfin@sha256:3b38dae4c3ddd6ebc7378538fba4d3f314070ebefbdb3d688166b7c8658fb123`.
Each matrix leg asserts `/health` readiness and plugin-load markers within one startup
deadline, including image lookup/pull and Docker commands. Cleanup has a separate
10-second budget. If removal cannot be confirmed, the check fails and reports the
retained container and runtime directory for operator recovery; forced host termination
cannot guarantee cleanup. This is a pre-publication integrity gate, not proof of a production deployment.

The local release helpers require a clean Git checkout, established by successful
repository-root, status, HEAD, and commit-object checks before artifact cleanup.
`.gitattributes` pins packaged, embedded,
and build text inputs to LF. Helper contract tests compare checkout bytes with
`core.autocrlf=false` and `true`; fixture package parity is not native Windows/macOS
compiled-package proof. `node scripts/test-release-helper-contracts.mjs` exercises every
available helper shell; provide PowerShell through `HUE_RELEASE_TEST_POWERSHELL` when it
is not on PATH. Portable runtime-smoke self-tests run without POSIX UID APIs, while live
runtime identity/permission checks require a POSIX host.

The version in `meta.json`, the project file, and the local archive name must match. Keep the
archive, both checksum sidecars, and the matching manifest together for auditability; install the
archive contents in a `HueSync_<version>` directory under the Jellyfin plugins directory, and verify the
published archive and manifest before extraction:

```bash
sha256sum --check --strict jellyfin-plugin-hue-v<version>.zip.sha256
sha256sum --check --strict jellyfin-plugin-hue-v<version>.manifest.json.sha256
```

If the .NET SDK is not installed on a Linux host, the same build can be run with Docker:

```bash
docker run --rm -v "$PWD:/src" -v /tmp/hue-nuget:/root/.nuget/packages \
  -w /src mcr.microsoft.com/dotnet/sdk:8.0 sh -lc \
  'apt-get update -qq && apt-get install -y -qq python3 zip unzip && ./build-release.sh'
```

### Project Structure
*   `Configuration/`: Plugin settings UI and logic.
*   `Service/`: Background service for monitoring playback.
*   `Hue/`: Logic for communicating with the Hue Bridge (REST & DTLS).
*   `Video/`: FFmpeg wrappers for frame extraction.

### Testing

The test suite covers:
- **Color Processing**: RGB/HSL conversion, sampling, and round-trip conversions
- **Configuration Validation**: All plugin settings and edge cases
- **Per-user Profiles**: Playback, color, performance, execution, channel, restoration inheritance, validation, and API persistence
- **HueClient Integration**: REST API parsing, error handling, and retry logic
- **Bridge discovery API**: Authenticated bridge discovery with local-address filtering
- **HueStreamer Protocol**: Binary packet construction, color encoding, and coordinate mapping

#### Running Tests

```bash
# Verify the committed content-hashed dependency graph first
dotnet restore --locked-mode

# Run all tests
dotnet test --no-restore

# Run tests with detailed output
dotnet test --no-restore --verbosity detailed

# Run tests with code coverage
dotnet test --no-restore --collect:"XPlat Code Coverage"

# Run specific test class
dotnet test --no-restore --filter FullyQualifiedName~ColorProcessingTests
dotnet test --no-restore --filter FullyQualifiedName~HueClientTests
dotnet test --no-restore --filter FullyQualifiedName~HueStreamerTests
```

The test suite does not require a physical Hue bridge or FFmpeg installation; bridge calls,
packet construction, and playback lifecycle paths are exercised with deterministic test
doubles. A real bridge is only needed for an end-to-end playback check after installation.

#### Test Coverage

The test suite uses deterministic bridge and FFmpeg test doubles; physical Hue
playback acceptance remains separate from local and container verification.

| Gate | Contract |
| --- | --- |
| Trusted CI | Main-only build/tests, format, dependency audit, Gitleaks, and split Semgrep scans; bounded jobs and immutable action pins |
| Release verification | Tested publish output, deterministic package/manifest parity, Linux helper execution, both pinned Jellyfin runtime smoke targets, verified tag/source binding and four release assets |
| Coverage | Nonempty TRX and Cobertura evidence is required; Codecov is optional when its token is absent and blocking when configured |
| Retention | Generated CI/security artifacts expire after one day; published release assets are not cleanup targets |
| PR validation | Read-only workflow permissions on an ephemeral GitHub-hosted `ubuntu-24.04` runner; real fork-PR execution is owner-waived for this release |
| Native platform builds | Hosted verification artifacts for Linux x64, Windows x64, macOS Intel, and macOS Apple Silicon; these do not replace physical Hue acceptance |
| Dependency maintenance | Dependabot covers all three NuGet projects, Actions, and the hash-locked Semgrep environment on GitHub-hosted runners |

The trusted workflow accepts main-only `workflow_dispatch` validation/recovery.
Publication requires a trusted `main` push; manual dispatch does not publish a
release. Any legacy self-hosted runner services are outside the workflow contract
and must remain offline until the owner deregisters them.

See [SECURITY.md](SECURITY.md) for scanner integrity and credential boundaries,
[SELF_HOSTED_RUNNERS.md](SELF_HOSTED_RUNNERS.md) for the retired-runner note,
[RELEASE_READINESS.md](RELEASE_READINESS.md) for owner-controlled gates, and
[the trusted workflow](.github/workflows/dotnet-ci.yml) for executable policy.

### Performance Benchmarks

The project includes BenchmarkDotNet benchmarks for performance-critical operations:

```bash
# Run all benchmarks
cd Jellyfin.Plugin.Hue.Benchmarks
dotnet run -c Release -- --all

# Run specific benchmark suite
dotnet run -c Release -- --color   # Color processing benchmarks
dotnet run -c Release -- --packet  # Packet building benchmarks
```

Benchmarks measure:
- RGB ↔ HSL color conversion (single and batch)
- Region sampling for light positions
- Brightness, saturation, hue-shift, and output-brightness adjustments
- HueStream packet construction
- Color change detection

## Project origins
*   Inspired by [HarmonizeProject](https://github.com/MCPCapital/HarmonizeProject) for the video analysis and DTLS logic.
*   Built on the [Jellyfin Plugin SDK](https://github.com/jellyfin/jellyfin-plugin-template).
