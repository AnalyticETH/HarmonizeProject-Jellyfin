# Production open-source release readiness

Status: preparation in progress; public production availability is not approved.
Prepared version: `1.5.461.0`. Repository visibility and published assets are
owner-controlled; pushing `main` automatically invokes the release workflow.

## Verified baseline

Evidence inspected on 2026-09-06 America/New_York (2026-09-07 UTC).

| Requirement | Evidence | Result |
| --- | --- | --- |
| Existing release provenance | `v1.5.460.0`, release ID `383784665`, source `98f8ee5e99e5efb498f097944e676f69e03e6ede`; downloaded ZIP and manifest pass their SHA-256 sidecars | Verified for the existing private release only |
| Existing trusted CI | Run `34071927844` at `98f8ee5`; build/tests, format, dependency audit, Gitleaks, Semgrep, deterministic helper parity, both Jellyfin smoke jobs, publication succeed; Linux helper reports 1,483 passed tests | Baseline evidence; does not verify subsequent changes |
| Redistribution notices | Existing `v1.5.460.0` ZIP and manifest contain only the two DLLs and `meta.json`, despite source-tree `LICENSE` and `NOTICE` | Corrected in the prepared package contract; existing assets remain unchanged |
| Public contribution execution | PR, build, security, packaging, and release jobs select `[self-hosted, linux, x64, local-docker]`; successful trusted jobs use `gaming-pc-analyticeth-harmonizeproject-jellyfin-01` | Untrusted/trusted isolation is not established |
| Repository visibility | GitHub repository API reports `private` | Unchanged; publication needs explicit owner approval |
| Dependency alerts | Authenticated open Dependabot alert query returns `[]` | No open alerts at inspection time; not a complete security audit |
| Administrative security controls | Runner inventory, branch protection, and fork-contributor approval queries return HTTP 403; private vulnerability reporting returns HTTP 404 | Unverified, not disabled or passing by inference |

## Prepared package contract

| Entry | Role |
| --- | --- |
| `BouncyCastle.Cryptography.dll` | Bundled managed DTLS implementation |
| `Jellyfin.Plugin.Hue.dll` | Plugin assembly and embedded administrator page |
| `LICENSE` | Complete GPL-3.0-only license |
| `NOTICE` | Project copyright and runtime dependency notices |
| `meta.json` | Jellyfin manifest; `assemblies` still names only the two DLLs |

The canonical workflow and both local helpers stage this exact set. The packager
preserves file bytes, deterministic order, timestamps, and non-executable 0644
permissions. The source-bound release manifest hashes all five entries and
rejects archive/staging mismatches. Executable regressions reject missing
licensing files in package creation, archive inspection, manifest creation, and
runtime preflight; manifest regressions also reject changed license/notice bytes.

## Publication gates

| Gate | Required evidence before public production availability |
| --- | --- |
| Untrusted PR isolation | Owner chooses GitHub-hosted PR workers or separately provisioned single-job workers with no trusted runner state, shared writable caches, host Docker socket, release credentials, or privileged network access. Change PR selectors and their validators together, then prove a real fork PR runs on the intended isolated boundary. Read-only tokens, checkout cleanup, and a Docker label alone are insufficient. Keep retired legacy runners offline. |
| Owner review and Actions policy | Owner verifies required reviews/checks, CODEOWNERS enforcement, restricted bypasses, fork approval, immutable action pins, and least-privilege workflow permissions from live administrative settings. Current token cannot establish these controls. |
| Private security intake | Enable and test GitHub private vulnerability reporting or document a working owner-approved private contact usable by a new contributor. An already-established private channel is not a complete public intake path. |
| Exact-source release verification | Review and commit the prepared changes, run the complete trusted CI at that source SHA, verify all package and manifest digests/source binding, and publish a new unused version. Never replace an existing tag or asset to repair the old archive. |
| Source availability and license authority | Owner confirms authority for the declared GPL-3.0-only license and provides accessible corresponding source for the exact binary release, with dependency notices. Public GitHub source/release URLs are inaccessible while the repository remains private. |
| Physical Hue acceptance | Record plugin/source version, Jellyfin/.NET/FFmpeg versions, bridge firmware and light models; verify registration with explicit certificate confirmation, playback sync, pause/resume/stop, original-state restoration, scheduled cues, concurrent users/rooms, bridge disconnect/reconnect, and server restart. Capture sanitized logs and cleanup outcomes. Automated loopback DTLS and container boot do not prove hardware behavior. |
| Upgrade and rollback | On a disposable representative installation, back up private configuration securely, upgrade from the prior release, check settings/automation preservation, then restore the previous package/configuration and verify startup, playback, and cleanup. Do not publish credentials or private backup files as evidence. |
| Public cutover | Explicit owner approval to change repository visibility/publish, followed by unauthenticated checks of repository/source links, installation assets, checksums, contributor forms, support, and security intake. Preparation does not authorize this mutation. |

## Verification commands

```bash
node scripts/validate-open-source-release.mjs
node scripts/validate-release-docs.mjs
node scripts/validate-release-package.mjs
node scripts/validate-workflow-inventory.mjs
node scripts/validate-trusted-workflow.mjs
node scripts/validate-pull-request-workflow.mjs
node scripts/test-workflow-contracts.mjs
dotnet restore Jellyfin.Plugin.Hue.sln --locked-mode
dotnet build Jellyfin.Plugin.Hue.sln --configuration Release --no-restore
dotnet test Jellyfin.Plugin.Hue.sln --configuration Release --no-build
dotnet format Jellyfin.Plugin.Hue.sln --verify-no-changes --no-restore
```

Run `build-release.sh` only from a clean committed checkout. Verify its ZIP and
manifest against an independent canonical package from that same source commit,
including byte-for-byte `LICENSE`/`NOTICE` comparisons. Run
`scripts/verify-jellyfin-runtime-smoke.py` against the actual candidate ZIP for
both digest-pinned images documented in `README.md`. Run all remaining API,
configuration, dependency, and scanner gates listed in `CONTRIBUTING.md` and
`.github/workflows/dotnet-ci.yml`; a passing source-file validator alone does not
clear the publication gates.
