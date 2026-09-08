# Release readiness

Prepared version: `1.5.461.0`. Repository-side build, packaging, workflow, and
container verification are complete for commit `d13fca97936d41086af0d7ee0844999494611898`.
Physical Hue acceptance remains a separate installation check.

## CI and workflow layout

All repository workflows use GitHub-hosted runners:

| Workflow scope | Runner |
| --- | --- |
| Pull requests, trusted CI, security scans, scheduled health | `ubuntu-24.04` |
| Linux native build | `ubuntu-24.04` |
| Windows native build | `windows-2022` |
| macOS Intel build | `macos-15-intel` |
| macOS Apple Silicon build | `macos-15` |

The workflows pin third-party actions to commit SHAs, use bounded job timeouts,
keep pull-request permissions read-only, and validate the runner inventory before
trusted work. No repository-owned runner service or host configuration is
required.

## Container verification

| Check | Result |
| --- | --- |
| Release build | Pinned `mcr.microsoft.com/dotnet/sdk:8.0.424`; `1,850` tests passed with zero failures or skips and zero build warnings/errors |
| Package | Deterministic five-file ZIP; SHA-256 `7a8833dba5e73342bb12228478f43b6b0b448e807a691b2bf2116fce1ec5e33e` |
| Jellyfin 10.9.0 | Digest-pinned runtime loaded the plugin, passed `/health`, and passed restrictive-container cleanup checks |
| Jellyfin 10.10.7 | Digest-pinned runtime loaded the plugin, passed `/health`, and passed restrictive-container cleanup checks |
| Media probes | Both runtime images passed PCM generation, H.264/MPEG-2 encode/decode, FLAC/AAC decode, scaled RGB output, and FFmpeg process replacement |
| Upgrade and rollback | Persistent `/config` drill passed `1.5.460.0` -> `1.5.461.0` -> `1.5.460.0`; the marker survived all phases |
| Authenticated API flow | Startup, admin authentication, configuration read, credential-safe export, import validation, and atomic import passed in Jellyfin 10.10.7 |
| Clean-clone parity | A fresh GitHub-origin clone produced a byte-identical package |

The runtime images are pinned to:

- Jellyfin 10.9.0: `jellyfin/jellyfin@sha256:d659991fdbda4d2963c807747fbd1ee237bfd15971a3923158719eb248ddea67`
- Jellyfin 10.10.7: `jellyfin/jellyfin@sha256:3b38dae4c3ddd6ebc7378538fba4d3f314070ebefbdb3d688166b7c8658fb123`

## Package contract

Every release archive contains exactly:

```text
BouncyCastle.Cryptography.dll
Jellyfin.Plugin.Hue.dll
LICENSE
NOTICE
meta.json
```

`LICENSE` is the complete GPL-3.0-only text. `NOTICE` contains project and
bundled dependency attribution. The release manifest records the source commit,
archive digest, packaged file hashes, and the locked dependency graph. See the
[release package reference](CONTRIBUTING.md#release-package-contents).

## Physical acceptance

After installation, test one or more real Hue Bridge V2 systems with a configured
Entertainment Area. Record the plugin version, Jellyfin version, bridge firmware,
and light models, then verify:

- registration and certificate confirmation;
- video and audio playback synchronization;
- pause, resume, seek, stop, and original-light-state restoration;
- saved scenes, playlists, and scheduled cues;
- concurrent users or rooms;
- bridge disconnect/reconnect and Jellyfin restart recovery; and
- sanitized logs and cleanup outcomes.

Container and loopback tests do not substitute for this physical check.

## Verification commands

```bash
node scripts/validate-open-source-release.mjs
node scripts/validate-release-docs.mjs
node scripts/validate-release-package.mjs
node scripts/validate-workflow-inventory.mjs
node scripts/validate-trusted-workflow.mjs
node scripts/validate-pull-request-workflow.mjs
node scripts/test-workflow-contracts.mjs
node scripts/test-documentation-contracts.mjs
dotnet restore Jellyfin.Plugin.Hue.sln --locked-mode
dotnet build Jellyfin.Plugin.Hue.sln --configuration Release --no-restore
dotnet test Jellyfin.Plugin.Hue.sln --configuration Release --no-build
dotnet format Jellyfin.Plugin.Hue.sln --verify-no-changes --no-restore
```

Run `build-release.sh` only from a clean checkout. Verify the ZIP and manifest
sidecars and run `scripts/verify-jellyfin-runtime-smoke.py` against both pinned
Jellyfin images before publishing a new package.
