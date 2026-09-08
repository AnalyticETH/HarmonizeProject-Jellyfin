# Production open-source release readiness

Status: owner-approved release execution; production acceptance remains unverified.
Prepared version: `1.5.461.0`. Repository visibility and published assets are
owner-controlled; pushing `main` automatically invokes the release workflow.

Owner runner decision: retain `gaming-pc-analyticeth-harmonizeproject-jellyfin-01`
with `[self-hosted, linux, x64, local-docker]` for all existing workflow jobs.
Runner selection is settled; no GitHub-hosted migration, replacement runner, or
routing change is requested. The owner also approved the remaining merge and
publication work. Authorization does not establish public-PR isolation, hardware
acceptance, native-platform parity, or license authority, and does not permit
modifying or bypassing protected machine policies.

The current protected GitHub policy blocks repository creation and public
visibility changes. Release assets can be published in the existing private
repository; public cutover cannot be completed through this agent under that
policy. The authorized private-reporting enable request returned HTTP 404, and
runner/fork administration remains inaccessible through the current token.

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

## Live gate refresh

Authenticated read-only checks on 2026-09-07, 18:43-18:46 UTC, confirm the following.
No repository settings, workflows, releases, or credentials were changed.

| Gate | Current evidence | Remaining limit |
| --- | --- | --- |
| Main ruleset | Active ruleset `21321676` protects `refs/heads/main` against deletion/non-fast-forward updates and requires strict CI checks, one approval, code-owner/last-push review, stale-review dismissal, and resolved threads | The configured owner has an `always` bypass; legacy protection GET still returns 403. This verifies the readable ruleset, not every enforcement/administrative control |
| Runner and fork controls | Runner inventory and both fork-approval/private-fork workflow settings return 403 with `Resource not accessible by personal access token` | Current inventory, approval policy, and untrusted/trusted isolation remain unverified |
| GitHub scanning features | Code-scanning setup returns 403 with `Code scanning is not enabled for this repository`; secret-scan history returns 404 with `Advanced Security is disabled on this repository` | These explicit API messages establish feature-unavailable states, not the state of every security control. Custom blocking Semgrep/Gitleaks workflows are separate |
| Private reporting | Private vulnerability reporting returns 404 `Not Found` | Setting and new-contributor access remain unverified; an existing private contact is not a demonstrated public intake path |
| Release and exact-source CI | Repository remains private; remote main and latest `v1.5.460.0` release remain at `98f8ee5`. Run `34071927844` and its nine jobs succeeded there. The local `cc78244` reference returns zero Actions runs | Neither that baseline nor the later successful runner-health run verifies the uncommitted working-tree fixes |
| Primary Hue specification | The official URL still redirects to sign-in; the supported browser runtime reports no connected browser | Authenticated specification access remains unavailable. No sign-in, alternate control path, or access-boundary bypass was attempted |

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

## Committed verification

| Source | Verification | Boundary |
| --- | --- | --- |
| `62d552cb98ddcafc36d8ee7b812ae9a4254f2d43` | PR #63 run `34177970321` passed build/tests/format/contracts and secret/static scans on existing runner `26`; the PR merge tree matches the commit tree | PR integration evidence, not trusted-main publication verification |
| `4bd567940d1a3debe11126a93eda1e2b49fa290f` | Real Bash release helper in a clean clone: locked restore, .NET SDK 8.0.424, zero build warnings/errors, 1,850 passed tests without failures/skips, formatting pass, five-file ZIP and source-bound manifest with verified sidecars | Linux local evidence; subsequent release-note changes require a new exact-source package |
| `4bd5679` runtime package | ZIP SHA-256 `5f8f321e9f150a518e2e5a7f19f4f5c3591cd39b942d0ecd5f1a45fd6c357964`; DLL SHA-256 `af71a1dccdafe7d610c4ef14d4a6c37dae5683282151080a8d0dbdf058c1f18a`; both pinned Jellyfin versions load the plugin and return `Healthy`, with networking disabled and cleanup confirmed | Startup acceptance only; not physical Hue, upgrade/rollback, or native Windows/macOS evidence |

Local evidence is retained under `/tmp/harmonize-commit-verification.Eh7Uuz`.
The scoped host check found no existing Jellyfin/Hue acceptance installation and
no native Windows/macOS execution target. It did not scan the LAN or inspect
credentials. Absence of a local target does not establish that no target exists
elsewhere; those acceptance checks need an owner-designated installation.

## Locally verified review corrections

The review corrections target release `1.5.461.0`. The local evidence below uses
mocked bridges and disposable build/test environments. Remote CI is recorded
separately and neither evidence class proves physical Hue acceptance.

| Review finding | Correction | Local evidence |
| --- | --- | --- |
| F01: publication inventory | Exact five-file names, single JSON document, matching version/source, nonempty array dependency graph; both checksum gates retained | 24 cases execute the actual workflow shell against current packager/manifest output, including malformed and checksum-invalid inputs |
| F02: Hue resource identity | Resolve typed entertainment services to validated light renderer references before capture; deduplicate shared renderers | 24 direct resolver cases with unrelated IDs, invalid/ambiguous references, selection, retry, cancellation, and bounds; existing capture tests retain their state-validation coverage |
| F03/F04: recovered playback identity/routing | Bind real progress/stop IDs, preserve recovery identity across startup/seek/pause/resume, and keep unrelated starts on normal routing | Five recovered-start integration cases plus existing lifecycle/stale-event coverage |
| F05: cleanup ownership | Retain failed concurrent cleanup with stable status/control identity and target lease; retire only after successful cleanup | 12 ordinary failure/retry, host/configuration-stop, other-target, and newer-generation cases |
| F06/F07: mapping-save invariants | Same-target key preservation and candidate effective-frequency validation before mutation | 42 targeted cases plus existing identity, disabled/inherited mapping, and persistence regressions |
| F08/F09: playback snapshots | Same-target replacements, seeks, and resumes reuse original renderer states and capture newly selected lights within a combined bound; pause restoration is independent of final-stop restoration and retains failed obligations | 14 handoff/capture/pause regressions, including shared renderers, changed selections, failed capture, global/user policies, retry/host-stop recovery, and the combined 256-light limit |
| F10: stream liveness | Owned five-second heartbeat, eight-second silence guard, reactivation before initial/delayed frames, and bounded synchronous UDP sends; cancellation and configured retry limits remain authoritative | Fake-time/DTLS static, blackout, suppressed-producer, expired-session, cancellation, replacement, reconnect-budget, and UDP loopback regressions |
| F11/F12: HTTP completion and Hue acknowledgements | One attempt deadline covers headers/body; late-created streams are disposed; mutations require error-free typed resource acknowledgement and preserve per-light failure accounting | 99 direct response contracts, including shared deadlines, late disposal/faults, cancellation, unknown-length size limits, and 60 previously false-success mutation cases |
| F13-F16: administrator request ownership | Discovery uses consistent credential snapshots and releases controls by request ownership; stale credential-clear confirmations and canceled mapping edits cannot mutate a later page lifecycle | Certificate-preflight, all six discovery loaders, replacement-edit cancellation, and confirmation ownership regressions pass in the configuration-page harness |
| F17-F20: scheduling and conflict projections | Account for elapsed cleanup, revisit newly due higher-priority cues, enumerate the complete requested horizon including solar offset boundaries, and include sequential distinct-target duration without changing per-target metadata | 23 scheduler regressions; the dense 366-day, one-result fixture now allocates less than 2 MB versus 43,513,384 bytes before correction |
| F21/F22: release provenance and checkout bytes | Root/status/HEAD/commit-object checks precede cleanup; packaged, embedded, and build text inputs use explicit LF attributes | 38 Bash/PowerShell guard cases and 93 LF/autocrlf checkout-input comparisons; identical fixture ZIPs/manifests; PowerShell runs on Linux, not native Windows |
| F23/F24: runtime-smoke portability and deadlines | Portable self-tests avoid POSIX-only APIs; live permission checks stay POSIX-only; Docker operations share a startup deadline, pulls honor larger supplied budgets, and cleanup has its own 10-second bound | 18 offline command/permission/deadline/cleanup contracts plus package self-tests; failed cleanup retains and reports its exact disposable resources |
| F25: ZIP compression | Supply the declared DEFLATE level for each entry | Existing deterministic-package self-test now checks actual compressor selection |
| F26: Markdown link coverage | Validate titled inline links and full, collapsed, and shortcut references while excluding code examples | 72 documentation regressions, including missing files/anchors and checkout traversal; README remains 119 lines and 618 words |
| F27: captured playback targets | Keep each playback on its captured bridge, area, credentials, and channel profile through pause/resume/seek; apply target edits to the next playback while honoring current certificate trust and disable controls | 23 regressions cover all pause policies, fresh playback, stale/manual/configuration stops, concurrent routes, admission/host-stop races, failed resume, completed pipelines, and recovered primary/worker identities |
| F28: Hue v2 serialization | Include the canonical 36-byte area UUID and seven-byte byte-addressed channel records; bind areas per DTLS lifecycle, reject malformed configuration UUIDs, and own frames before callbacks/reconnects | Full-byte published/derived fixtures, channel/configuration/API boundaries, area ownership, caller-mutation regressions, and production/prototype byte comparisons; [protocol evidence](docs/HUE_STREAM_PROTOCOL.md) is not physical acceptance |
| F29: RGB16 conversion | Replace compatibility halving with full-range RGB8 byte replication across video, audio, cinema, previews, effects, and fallback white; retain explicit brightness policies and low-intensity probes | Production encoding, dimming, effects/fades, cinema/fallback, and preview-output regressions; unchanged settings produce higher numeric output and are not migrated |
| F30: repeatable local packaging | Ignore only generated root-level release manifests and sidecars so successful builds do not block the next provenance check | Real-Git regression fails before the fix and passes afterward: four release outputs ignored, five unrelated/nested files still visible; both shell provenance guards remain unchanged |

Combined validation: Release build with zero warnings/errors, all 1,850 .NET
tests passed with no skips, formatting verification passed, and
documentation/workflow/package contracts passed. The refreshed pinned production
Semgrep scan reports zero findings, scanner errors, or fixpoint timeouts across
68 files; the earlier JavaScript/Python scans also passed and those sources are
unchanged by F27. None of the publication gates below is waived. Protocol fixtures
do not substitute for bridge acceptance.

All 30 recorded findings (F01-F30) are corrected locally; F27 follows the
approved captured-target policy. Native Windows/macOS compiled-package parity is
unverified. Some SDK-suite environment probes return early without FFmpeg;
the separate real-decoder checks below are not included in the 1,850-test count.

## Earlier working-tree runtime evidence

These pre-commit startup and media checks use the post-F27 working-tree DLL with
SHA-256 `f5049e758abc0caf92318bf5c941342ede89ba8ff573d9ee9909d7516c711693`.

| Scope | Evidence | Boundary |
| --- | --- | --- |
| Earlier five-file ZIP | SHA-256 `57c2f6918da8b3790f172d1da93f95af84f5cb9f99e5997ff5ef9a1849672866`; staged from a no-build publish with isolated bookkeeping and unchanged shared input hashes | Working-tree smoke bundle only; no release manifest or commit-bound provenance is generated |
| Jellyfin 10.9.0 and 10.10.7 startup | Both digest-pinned images load `Philips Hue Sync 1.5.461.0` and return `Healthy`; networking disabled, no published ports, UID/GID 1000:1000, original restrictive Docker flags retained; container and fresh configuration removal confirmed | Default-configuration startup, not upgrade/rollback or Hue behavior; pinned compatibility images are not production-version recommendations |
| Actual CPU media decoding | In both image environments, one real-PCM capability check, five H.264/MPEG-2 video cases, three FLAC/AAC decoding-and-analysis cases, and two real-process stop/replacement cases pass with FFmpeg 6.0.1/7.0.2 | Generated local fixtures and cached .NET 8.0.30; no GPU, arbitrary-library, physical-light, or production-timing claim |

Local evidence directories: `/tmp/harmonize-pinned-playback.znNFDY` (final build,
1,850-test TRX, coverage, workflow contracts, and production scan),
`/tmp/harmonize-working-tree-smoke-f27.qLoIqL` (five-file bundle and runtime
startup), and `/tmp/harmonize-final-ffmpeg.RGk96q` (actual media decoding and
process cleanup). Temporary evidence is not a published or durable release record.

### Earlier supporting checks

These checks precede F27 and are retained as evidence for the previous working-tree
DLL, SHA-256 `53cbfae3848535c670d3d85d0028564a222684089634e10a6b08939c87c259b8`.
They were not rerun against the final DLL and are not new performance or advisory
measurements for it.

| Scope | Evidence | Boundary |
| --- | --- | --- |
| Serializer measurement | Production wrappers emit 108/164-byte packets for 8/16 channels, allocating 136/192 bytes per call; byte-equivalent prototype setup passes | Short warmed local measurements, not full-frame, DTLS, or hardware FPS benchmarks |
| Dependency snapshot and redistribution | Pre-F27 NuGet advisory queries report no vulnerable packages across all three projects; 124 package/version pairs match lockfile/assets hashes. Online BouncyCastle 2.7.0 package verification passes without reported errors/warnings; its DLL matches the then-staged runtime and its MIT terms are included in NOTICE | Known-advisory/package verification only, not unknown-vulnerability, host/image-security, or owner-license-authority proof |

The root `publish/` and `release-package/` directories contain stale 1.5.428.0
outputs and are not current verification artifacts. Earlier commit-bound 1.5.461.0
artifacts from `cc78244` likewise do not represent this working tree. They remain
untouched; a future release requires a new clean, reviewed, exact-source build.

## Captured playback target policy

F27 is corrected under the owner-approved policy: a playback keeps its original
bridge, entertainment area, credentials, and copied channel selection through
pause, resume, and seek. Target edits apply to the next playback. Certificate
trust and master/per-user disable controls remain live; snapshots do not bypass
certificate revalidation or create new cleanup obligations after a completed
restore-on-pause.

Admission and worker startup share the captured target. Paused/inactive captures
retain status and stop ownership, including recovered real-ID bindings after
failed resume or pipeline completion. Terminal stops clear the capture, and
stale stops cannot retire a newer playback. The 23
`CapturedPlaybackTarget_` regression cases pass in the final 1,850-test run;
the initial 11 cases failed before implementation. The original bug-confirming
harness is historical evidence, not a passing-test contract for this policy.

The v2 serializer matches the corroborated implementation layout; primary-spec
access and physical interoperability acceptance remain unverified.

## Publication gates

| Gate | Required evidence before public production availability |
| --- | --- |
| Untrusted PR isolation | Retain the owner-selected existing runner. Before enabling untrusted public PR execution, prove that its job boundary excludes trusted runner state, shared writable caches, the host Docker socket, release credentials, and privileged network access; verify a real fork PR follows that boundary. Read-only tokens, checkout cleanup, and a Docker label alone are insufficient. Runner selection is not pending; isolation evidence remains unverified. Keep retired legacy runners offline. |
| Owner review and Actions policy | Owner reviews the verified main ruleset and its `always` bypass, and verifies remaining fork-approval, enforcement, and administrative controls. Readable ruleset metadata does not establish the inaccessible settings or an isolated PR execution boundary. |
| Private security intake | Enable and test GitHub private vulnerability reporting or document a working owner-approved private contact usable by a new contributor. An already-established private channel is not a complete public intake path. |
| Exact-source release verification | Review and commit the prepared changes, run the complete trusted CI at that source SHA, verify all package and manifest digests/source binding, and publish a new unused version. Never replace an existing tag or asset to repair the old archive. |
| Source availability and license authority | Owner confirms authority for the declared GPL-3.0-only license and provides accessible corresponding source for the exact binary release, with dependency notices. Public GitHub source/release URLs are inaccessible while the repository remains private. |
| Physical Hue acceptance | Record plugin/source version, Jellyfin/.NET/FFmpeg versions, bridge firmware and light models; verify registration with explicit certificate confirmation, playback sync, pause/resume/stop, original-state restoration, scheduled cues, concurrent users/rooms, bridge disconnect/reconnect, and server restart. Capture sanitized logs and cleanup outcomes. Automated loopback DTLS and container boot do not prove hardware behavior. |
| Upgrade and rollback | On a disposable representative installation, back up private configuration securely, upgrade from the prior release, check settings/automation preservation, then restore the previous package/configuration and verify startup, playback, and cleanup. Do not publish credentials or private backup files as evidence. |
| Native platform parity | Run the documented package/build checks on native Windows and macOS with the same reviewed source. Linux PowerShell, checkout-filter comparisons, and Linux container startup are not native compiled-package evidence. |
| Public cutover | Owner approval for the remaining release work is granted; protected host policies remain authoritative. Public availability still requires an allowed visibility change and unauthenticated checks of repository/source links, installation assets, checksums, contributor forms, support, and security intake. Authorization is not evidence that cutover or production acceptance occurred. |

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
both digest-pinned images documented in
[the packaging reference](CONTRIBUTING.md#release-package-contents). Run all remaining API,
configuration, dependency, and scanner gates listed in `CONTRIBUTING.md` and
`.github/workflows/dotnet-ci.yml`; a passing source-file validator alone does not
clear the publication gates.
