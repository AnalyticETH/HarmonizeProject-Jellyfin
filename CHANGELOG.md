# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

No unreleased changes.

## [1.5.310] - 2026-08-26

### Streaming resilience and cancellation
- **Fail-closed channel frames**: malformed, null, or short RGB16 payloads are rejected before threshold comparison, transport health checks, or reconnect scheduling.
- **Cancellation-aware startup**: entertainment-area activation waits now stop immediately when the linked sync lifecycle is cancelled.
- **Regression coverage**: streaming tests verify malformed frames never emit packets or trigger reconnects after a valid stream has started.

## [1.5.309] - 2026-08-26

### Scheduler reliability, target validation, and configuration navigation
- **DST-safe deferred cues**: persisted waits now retain a UTC timestamp, migrate legacy local-only entries safely, and expire from elapsed instants rather than wall-clock arithmetic.
- **Global target integrity**: partially populated default bridge targets are validated even when custom user/device mappings exist, while intentionally blank global targets remain valid for custom-only configurations.
- **Configuration navigation**: the administrator page now provides a keyboard-friendly section index with focusable anchors for status, diagnostics, bridge targets, scenes, scheduling, mappings, and advanced settings.
- **Regression coverage**: scheduler, configuration validation, and static administrator-page contracts cover UTC migration, incomplete targets, navigation links, and focusable sections.

## [1.5.308] - 2026-08-26

### Bridge target safety, stream recovery, and accessible telemetry
- **Area-target correctness**: entertainment configuration responses now select the requested resource by ID and fail closed when identified responses describe a different area, while preserving legacy responses without resource IDs.
- **Transport recovery**: disposed and I/O-failed DTLS connections are invalidated only when still active and enter the existing serialized, bounded reconnect flow without closing a replacement stream.
- **Accessible diagnostics**: administrator action results expose atomic live status/alert regions while high-frequency scheduler telemetry remains quiet for screen readers.
- **Regression coverage**: Hue client, streaming lifecycle, and configuration-page contracts cover target selection, transport disposal, stale-connection races, and live-region semantics.

## [1.5.307] - 2026-08-26

### Bridge reliability and administrator telemetry
- **Fail-closed channel validation**: connection diagnostics now ignore malformed, non-numeric, negative, and out-of-range entertainment channel IDs and skip DTLS probing when no controllable channels remain.
- **Reconnect resilience**: failed internal DTLS startup attempts retain the active target and lifecycle state so later frames can consume the remaining bounded retry budget; public stops still clear stale reconnect state.
- **Playlist duration visibility**: saved playlist selectors and schedule status, upcoming-occurrence, and history tables now render bounded total durations alongside their step and pass metadata.
- **Regression coverage**: API, streaming lifecycle, and administrator configuration-page contracts cover channel validation, reconnect retry preservation, and playlist telemetry rendering.

## [1.5.306] - 2026-08-26

### Import and scheduler lifecycle hardening
- **Lifecycle-safe configuration import**: administrator import validation and submission now cancel superseded or hidden-page requests, suppress stale responses, clear approval state on teardown, and prevent duplicate in-flight imports.
- **Malformed mapping protection**: invalid nested device-target property types now return a sanitized HTTP 400 without mutating persisted mappings.
- **Deferred-cue correctness**: persisted playback-deferred occurrences are validated against the current schedule timing definition before replay, with stale state removed safely after edits or imports.
- **Regression coverage**: API, scheduler, and administrator configuration-page tests cover malformed input, stale lifecycle responses, page teardown, and edited deferred cues.
- **Trusted-main recovery**: the release workflow now exposes a manual dispatch trigger for recovering transient Actions startup failures without granting manual runs release-publication behavior.

## [1.5.305] - 2026-08-26

### Mapping and schedule lifecycle hardening
- **Stale mapping-edit protection**: administrator mapping edits now cancel superseded requests and ignore late success, failure, and cleanup callbacks after navigation or pagehide.
- **Fail-closed schedule routes**: malformed null target routes remain visible to validation and are rejected before configuration mutation during direct saves and imports.
- **Partial schedule state preservation**: omitted `Enabled` and `SkipNextOccurrence` fields retain existing values during partial imports and direct updates, while explicit values still override them.
- **Regression coverage**: API and configuration-page contracts cover stale lifecycle responses, malformed routes, and omitted-versus-explicit schedule state.

## [1.5.304] - 2026-08-26

### Credential and scheduling hardening
- **Masked credential entry**: global and per-user Hue App Key and Client Key fields now use password controls with new-password autocomplete hints.
- **Recursive JSON ambiguity rejection**: direct mapping writes reject case-variant duplicate properties at every nested object/array level before credentials or device targets are materialized.
- **DST fall-back correctness**: scheduled cues now require the resolved UTC occurrence minute as well as the local wall-clock minute, preventing an ambiguous time from running at the wrong instant or repeating after restart.
- **Regression coverage**: UI, nested mapping, and time-zone fall-back contracts cover the new fail-closed behavior.

## [1.5.303] - 2026-08-26

### Mapping input security
- **Ambiguous JSON rejection**: direct user-mapping writes now reject duplicate property names that differ only by case before materializing nested device targets.
- **Regression coverage**: the API test proves an oversized case-variant payload is rejected without mutating persisted configuration.

## [1.5.302] - 2026-08-26

### Export and backend hardening
- **Button recovery**: page lifecycle teardown now routes tracked administrator export cancellation through common cleanup so navigation or bfcache restore cannot leave export controls disabled.
- **Deterministic regression harness**: CI covers all administrator JSON, CSV, and iCalendar export routes, response types, filenames, stale suppression, cancellation, failures, and duplicate-click bounds.
- **Bounded server inputs and retries**: configuration imports and direct mapping writes reject oversized nested device collections before planning or materialization, while Hue retry settings are clamped to a finite 0-10 policy for legacy persisted values and direct callers.
- **Regression coverage**: API and Hue client tests cover nested import capacity, early mapping rejection, and retry clamping without mutating configuration.

## [1.5.301] - 2026-08-26

### Export lifecycle reliability
- **Credential-safe export guards**: configuration, scheduled-cue JSON/CSV/iCalendar, and session-history exports now cancel or ignore stale page, filter, and horizon requests while restoring controls after teardown.
- **Regression contracts**: lifecycle request propagation, query scope checks, cleanup flags, and filter-cancellation handlers are validated by the configuration-page contract.

## [1.5.300] - 2026-08-25

### Concurrency, report, and dependency-gate safety
- **Run/cancel lifecycle correctness**: bulk scheduled-cue Run and Cancel actions now bypass the configuration-writer lease so long-running runs can execute and active runs remain cancellable.
- **Stale-safe occurrence exports**: JSON occurrence reports are bound to the active page generation and filter query, with teardown cleanup that permits a reopened page to export again.
- **Untrusted-change gate**: Dependabot and pull-request changes now receive read-only build, test, format, vulnerability, Gitleaks, and Semgrep validation on an ephemeral GitHub-hosted runner; persistent self-hosted identities remain trusted-main-only.
- **Regression contracts**: mutation-filter, report-lifecycle, and PR workflow boundary tests protect the new paths.

## [1.5.299] - 2026-08-25

### Capacity and report lifecycle safety
- **Bounded mapping saves**: the public user-mapping endpoint now rejects a new 101st row before configuration assignment or persistence while allowing safe edits at the exact limit.
- **Scheduled-cue lifecycle protection**: status, conflict, occurrence, and history loaders ignore superseded responses, and conflict JSON exports preserve the current report scope across navigation and filter changes.
- **Regression coverage**: capacity, no-mutation, exact-limit update, and administrator lifecycle contracts cover the new paths.

## [1.5.298] - 2026-08-25

### Release reproducibility
- **Canonical release archives**: Linux, Windows, and CI now use one deterministic Python packager with sorted entries, fixed ZIP timestamps/metadata, and fixed DEFLATE settings.
- **Determinism regression gate**: the release workflow builds identical-byte archives with different source order and mtimes, requires identical SHA-256 output, and still verifies the strict checksum sidecar.

## [1.5.297] - 2026-08-25

### Import capacity safety
- **Pre-normalization collection bounds**: configuration imports now reject oversized color-preset, scene-playlist, and scene-schedule collections before cloning or normalization, including playlist parallel step arrays, playlist targets, schedule target routes, and excluded dates.
- **Regression coverage**: exact-limit and limit-plus-one API tests cover top-level and nested import capacity while preserving atomic no-mutation behavior.

### Mapping editor reliability
- **Entertainment-area lifecycle protection**: bridge area loading now uses page generations, target fingerprints, same-key cancellation, and stale-response guards across success, failure, and cleanup callbacks.

## [1.5.296] - 2026-08-25

### Mapping editor reliability
- **Target-scoped stale-response protection**: entertainment-area, channel-ID, route-area, and playback-device discovery requests now carry page generations and target fingerprints, preventing late responses from overwriting a newer mapping draft.
- **Request cancellation**: newer target requests and page teardown abort tracked mapping requests when supported.
- **Regression contracts**: static checks protect the channel-write guard and target fingerprint lifecycle wiring.

## [1.5.295] - 2026-08-25

### Configuration capacity safety
- **Bounded user mappings**: persisted user-mapping collections accept at most 100 rows, and oversized imports fail before cloning, normalization, or configuration mutation.
- **Regression coverage**: exact-limit and limit-plus-one tests cover configuration validation and credential-safe import preflight.

## [1.5.294] - 2026-08-25

### Administrator page reliability
- **Stale-response protection**: configuration, saved-scene, playlist, scheduled-cue, user, mapping, session-history, and diagnostics loaders now track page generations and abortable requests, preventing hidden-page or superseded responses from overwriting a reopened page.

### Administrator accessibility
- **Explicit control names**: manual entertainment-area, scheduled weekday, and playback-device route controls now have visible labels and ARIA names.

## [1.5.293] - 2026-08-25

### Diagnostic lifecycle safety
- **Serialized live validation**: target diagnostics and support-bundle target validation now hold the shared bridge diagnostic lease for the complete snapshot, preventing playback/diagnostic overlap and returning `409 Conflict` when the bridge is already owned.

### Release integrity
- **PowerShell checksum verification**: the Windows release helper now parses and independently verifies the generated SHA-256 sidecar filename and digest.

## [1.5.292] - 2026-08-25

### Administrator observability
- **Sync start telemetry**: Live Sync Status now renders the localized `syncStartedAtUtc` timestamp alongside duration and playback freshness, matching the API and support bundle.

### Administrator accessibility
- **Live status announcements**: runtime status and errors now expose polite/assertive live regions.
- **Accessible controls**: device-route editor fields and dynamically generated migration credential inputs now have explicit accessible names.

## [1.5.291] - 2026-08-25

### Administrator runtime safety
- **Fail-closed status refresh**: failed or superseded runtime-status requests now clear stale playback, target, health, and session telemetry instead of leaving old values visible.
- **Polling lifecycle**: hidden-page teardown invalidates and aborts the current runtime request when supported, releases the loading guard, and prevents late responses from overwriting a newer page state.

### API documentation parity
- **Status contract**: document `playbackObservedAtUtc` in the public `GET /HueSync/Status` contract and protect it with the API documentation validator.

## [1.5.290] - 2026-08-25

### Security boundary hardening
- **Private resolved peers**: `.local` bridge names are resolved once and every returned address must be private, link-local, or unique-local before the exact address is used for REST or managed DTLS. Mixed or public DNS/mDNS answers fail closed before Hue credentials leave the process.
- **Regression coverage**: verify literal address rejection, mixed DNS-answer rejection, and exact credential-free endpoint selection for the shared REST/DTLS boundary.

### Administrator observability
- **Playback freshness**: Live Sync Status now displays the localized timestamp of the latest Jellyfin playback observation, or `—` when no active timeline exists.
- **Contract coverage**: API/support-bundle serialization and configuration-page validators protect the observation timestamp across active, paused, and stopped playback states.

## [1.5.289] - 2026-08-25

### Managed DTLS interoperability coverage
- **Loopback handshake coverage**: deterministic Bouncy Castle DTLS 1.2 PSK tests now negotiate Hue's exact cipher suite and identity, deliver an encrypted/decrypted payload, and verify health and close behavior without bridge hardware.
- **Cancellation coverage**: a silent loopback UDP peer verifies that an unresponsive handshake is canceled within a bounded interval without leaving a background worker or socket open.
- **Secret-scan hygiene**: the fixture derives its non-secret PSK bytes at runtime, with one precise historical false-positive fingerprint ignored rather than weakening the scanner globally.

## [1.5.288] - 2026-08-25

### Self-hosted runner resilience
- **FFmpeg startup tolerance**: the bounded diagnostics version probe now allows up to 10 seconds for a contended self-hosted runner while still failing closed on hangs or startup errors.
- **Capability preflight**: CI now runs the same short, 8 kHz stereo PCM capture used by diagnostics before tests, reporting a clear runner dependency failure when FFmpeg is missing or unusable.

## [1.5.287] - 2026-08-25

### Reproducible release tooling
- **Locked local restores**: Linux and PowerShell release helpers now use `dotnet restore --locked-mode`, matching CI and failing closed when the committed NuGet dependency graph drifts.
- **Release contract coverage**: documentation validation now protects the locked-restore requirement alongside publish-authoritative package contents.

## [1.5.286] - 2026-08-25

### Scheduled-cue observability
- **Per-target status visibility**: the administrator scheduler status now renders each credential-free target label, success/skip/failure state, target message, channel counts, and cleanup warning instead of showing only the aggregate outcome.
- **Per-target history visibility**: retained scheduled-cue history now includes the same independent target outcomes, preserving partial multi-room failures and restoration warnings after a run.
- **Configuration-page contract coverage**: static validation protects the target-outcome renderer and both runtime/history data paths.

## [1.5.285] - 2026-08-25

### Shutdown lifecycle safety
- **Host-cancellation-aware shutdown**: service stop now propagates Jellyfin's host shutdown token through concurrent workers, paused cleanup, lifecycle acquisition, sync-loop waits, and bridge cleanup so a blocked capture or DTLS loop cannot indefinitely delay application termination.
- **Deferred resource disposal**: a cancellation source remains owned until an interrupted sync loop exits, preventing stale-loop writes and disposal races while reporting credential-free cleanup warnings.
- **Regression coverage**: verify service shutdown completes promptly when the sync loop does not complete until after host cancellation.

## [1.5.284] - 2026-08-25

### Configuration portability safety
- **Side-effect-free import planning**: configuration imports now deep-clone existing mapping rows, including nested device-target credentials and profile overrides, before legacy mapping-ID normalization; invalid documents and persistence failures cannot mutate live row identities or credentials.
- **Regression coverage**: verify invalid imports, duplicate legacy row IDs, nested device credentials, and failed saves leave the live mapping objects unchanged.

## [1.5.283] - 2026-08-25

### Configuration portability safety
- **Stable-row import matching**: credential-safe configuration imports now match per-user mappings by exact `mappingId`, use user-ID fallback only when the destination row is unique, and fail closed for ambiguous duplicate groups.
- **Exact partial merge**: partial imports replace only the selected mapping row, preserve its matching credentials, and key normalized import diffs by stable mapping ID instead of deleting every row for a user.
- **Regression coverage**: verify exact duplicate-row credential preservation, isolated partial merges, and id-less duplicate rejection without mutating the live configuration.

## [1.5.282] - 2026-08-25

### Diagnostics safety
- **Honest saved-target readiness**: target diagnostics now return credential-free duplicate mapping groups, including all-disabled groups, block affected bridge checks before contacting Hue, and keep `AllTargetsReady` false until every duplicate group is resolved.
- **Administrator warning**: the configuration page shows an amber duplicate-group warning instead of reporting all saved targets as ready.
- **Regression coverage**: verify enabled/disabled, multiple-enabled, and all-disabled duplicate groups without bridge requests or credential leakage.

## [1.5.281] - 2026-08-25

### Diagnostics safety
- **Fail-closed current-light capture**: direct, selected-device-route, and all-target capture now rejects ambiguous duplicate Jellyfin-user mappings before credential resolution or bridge activity, matching playback and scene-automation safety.
- **Regression coverage**: cover enabled/disabled duplicates, multiple enabled rows, exact device routes, all-target capture, and zero bridge requests on rejection.

## [1.5.280] - 2026-08-25

### User-mapping lifecycle
- **Deterministic duplicate resolution**: add an atomic administrator workflow to retain one exact stable mapping row and remove every sibling from a duplicate Jellyfin-user group using an optimistic reconciliation report version.
- **Runtime fail-closed behavior**: playback, credential resolution, scene validation, and scheduled-target resolution no longer choose an arbitrary duplicate mapping; unresolved duplicates are blocked until an enabled, valid keeper is selected.
- **Regression coverage**: verify keeper selection, canonical identity repair, stale-report and invalid-keeper rejection, persistence rollback, runtime ambiguity protection, and credential-free responses.

### Packaging
- **Publish-authoritative release scripts**: Linux and PowerShell release helpers now publish before copying the managed Bouncy Castle DTLS dependency, matching the self-hosted CI package contract.

## [1.5.279] - 2026-08-25

### User-mapping lifecycle
- **Exact-row enable/disable**: carry stable mapping IDs through bulk enabled-state actions so duplicate rows cannot update or scrub the wrong credentials.
- **Fail-closed legacy state changes**: user-ID-only bulk enable/disable now returns `409 Conflict` when duplicate rows make the selection ambiguous; exact mapping-ID requests retain dependency validation and transactional rollback.
- **Regression coverage**: verify selected duplicate isolation, ambiguous legacy rejection, credential preservation, and credential-free result serialization.

## [1.5.278] - 2026-08-25

### User-mapping lifecycle
- **Exact-row deletion**: carry stable mapping IDs through single-row, bulk, and dependency actions so deleting one duplicate row cannot remove its siblings.
- **Fail-closed legacy deletes**: user-ID-only deletion now returns `409 Conflict` when duplicate rows make the selection ambiguous; exact mapping-ID requests retain dependency checks and transactional rollback.
- **Regression coverage**: verify exact single/bulk deletion, duplicate rejection, dependency selection, sibling preservation, and credential-free responses.

## [1.5.277] - 2026-08-25

### User-mapping lifecycle
- **Duplicate-safe edits**: carry stable non-secret mapping row IDs through the administrator editor, allow exact-row updates without deleting sibling duplicates, and reject ambiguous user-ID-only saves before mutation.
- **Regression coverage**: verify ambiguous duplicate rejection, exact-row editing, credential preservation, and API/configuration-page contract markers.

## [1.5.276] - 2026-08-25

### User-mapping lifecycle
- **Safe stale-row cleanup**: add stable non-secret mapping row identities, optimistic reconciliation report versions, and an atomic administrator cleanup endpoint for missing or malformed rows with dependency checks and rollback-safe persistence.
- **Duplicate protection**: duplicate, healthy, renamed, referenced, and stale-report selections fail closed; the configuration page exposes credential-free cleanup status without attempting ambiguous user-ID deletion.
- **Regression coverage**: verify exact-row deletion, stale report rejection, dependency blocking, credential omission, and API/UI documentation contracts.

## [1.5.275] - 2026-08-25

### User-mapping lifecycle
- **Credential-free reconciliation**: compare persisted mapping IDs and names with Jellyfin's live user directory, refresh existing unique users atomically, and leave missing, malformed, and duplicate mappings unchanged for deliberate administrator cleanup.
- **End-to-end administration**: add reconciliation API/UI contracts, documentation guards, and lifecycle regression coverage without exposing Hue credentials.

## [1.5.274] - 2026-08-25

### Runtime telemetry
- **Credential-free playback progress**: expose active media position, duration, bounded progress percentage, pause state, and observation time through runtime status and support bundles while clearing the timeline when playback ends.
- **Administrator visibility**: show the playback timeline alongside existing live Hue stream quality and seek-recovery telemetry.

### Documentation and tests
- **Status contract parity**: document playback timeline fields and enforce exactly one current release heading in the release documentation validator.
- **Regression coverage**: verify active and paused timeline snapshots, API/support-bundle projection, lifecycle cleanup, and credential omission.

## [1.5.273] - 2026-08-25

### Documentation
- **Accurate release requirements**: remove the obsolete OpenSSL installation requirement from the GitHub release template and state that the managed Hue DTLS transport is included in the package.

### Packaging
- **Patch release metadata**: keep the project, manifest, release notes, and published artifact version aligned at 1.5.273.0.

## [1.5.272] - 2026-08-25

### Security
- **Managed DTLS transport**: replace the OpenSSL child process with Bouncy Castle DTLS 1.2 PSK so Hue App and Client Keys never appear in process arguments or `/proc` command-line inspection.
- **No external tunnel prerequisite**: system diagnostics and playback readiness no longer require an OpenSSL executable; the managed transport is packaged with the plugin.

### Packaging and tests
- **Dependency-complete release archive**: ship `BouncyCastle.Cryptography.dll` beside the plugin assembly and verify it in local and self-hosted release contracts.
- **DTLS regression coverage**: cover the Hue cipher-suite contract, connected UDP adapter, and cancellation-bounded handshake cleanup.

## [1.5.271] - 2026-08-25

### Reliability
- **Bounded cancellation cleanup**: diagnostic, preview, pause, startup rollback, and playback restoration now use an independent 30-second cleanup budget that survives request cancellation but releases lifecycle ownership when a bridge or custom transport never completes.
- **Partial cleanup telemetry**: timed-out per-light restoration reports attempted, restored, and failed/deadline counts through the existing credential-free cleanup warnings.

### Tests
- **Cleanup deadline coverage**: non-completing Hue requests now verify prompt cancellation and accurate remaining-light failure accounting.

## [1.5.270] - 2026-08-25

### API and scheduler hardening
- **Credential-free target parity**: normalize valid GUID mapping IDs to canonical D-format text across scheduler status, upcoming occurrences, scene/playlist preview results, skip/failure telemetry, restored history, and mapping summaries while retaining fail-closed opaque legacy values.

### UI and tests
- **Legacy route editor matching**: compare brace/N-format mapping and device-route IDs by GUID value so persisted legacy targets remain selectable.
- **Parity coverage**: verify canonical mapping summaries, live/occurrence/history metadata, and playlist preview telemetry without exposing credentials.

## [1.5.269] - 2026-08-24

### API hardening
- **GUID-equivalent target references**: canonicalize schedule and playlist target user IDs during save/import and compare valid GUIDs by value across runtime resolution, mapping CRUD, dependency checks, and legacy brace/N-format routes while retaining fail-closed handling for malformed IDs.

### UI and documentation
- **Import target warning**: explain that stored bridge keys are preserved only for the same bridge target and that a changed target requires replacement keys or an explicit credential clear.

### Tests
- **Target compatibility coverage**: verify brace/N-format schedule routes import and execute, and legacy GUID mappings update/delete without duplicate records or lost credentials.

## [1.5.268] - 2026-08-24

### Security
- **Global credential target binding**: normal configuration saves and backup imports now preserve omitted global App/Client keys only for the same bridge target; changing targets requires replacement credentials or an explicit clear operation and fails closed before mutation otherwise.

### UI
- **Import refresh parity**: successful configuration imports now reload scheduled cues along with settings, mappings, saved scenes, and playlists.

## [1.5.267] - 2026-08-24

### API hardening
- **Import user-ID validation**: require imported per-user mapping IDs to be valid Jellyfin GUIDs, normalize accepted brace/N-format values to canonical D-format text before merge and duplicate detection, and reject malformed documents before mutation.

### Tests
- **Import mapping coverage**: verify malformed import IDs preserve the complete configuration, brace-format IDs preserve matching stored credentials after normalization, and canonical-equivalent duplicate IDs fail closed.

## [1.5.266] - 2026-08-24

### API hardening
- **Jellyfin user-ID validation**: require the public per-user mapping save request to use a valid Jellyfin user GUID, normalize accepted IDs to canonical D-format text, and reject malformed IDs before configuration mutation.

### Tests
- **Mapping request coverage**: verify brace-format GUID canonicalization and malformed public user IDs return a clear `400` without changing persisted mappings.

## [1.5.265] - 2026-08-24

### Portability
- **Cross-platform scheduled time zones**: export scheduled cues with a canonical IANA identifier, resolve legacy Windows IDs on Unix hosts (and IANA IDs on Windows), and normalize imported cues to the portable value without falling back to the destination server's local zone.
- **Credential-free timezone metadata**: expose both the host ID and canonical IANA ID through the time-zone catalog, cue results, upcoming occurrences, runtime status, and backup/restore documents; the administrator selector now submits the canonical value.

### Tests
- **Timezone migration coverage**: verify Windows/IANA alias resolution, canonical export/import round trips, unmappable-zone rejection, API metadata, and configuration-page contracts.

## [1.5.264] - 2026-08-24

### Device routes
- **Setup-complete route editor**: the administrator mapping page can now add, update, remove, and area-discover nested playback-device routes without hand-editing JSON. New routes require their own bridge, App Key, Client Key, and entertainment area; blank keys on existing routes remain server-side and are preserved on save.
- **Scoped device discovery**: recent playback-device discovery sends a valid selected Jellyfin user ID to the elevated endpoint, reducing unnecessary session metadata and keeping the exact device identity case-sensitive.
- **Direct preview metadata**: successful direct previews now return credential-free target-user and exact target-route metadata for the selected user/device route.

### Tests
- **Route editor and preview contracts**: static page contracts cover route CRUD controls, secret omission, scoped discovery, and route-aware preview propagation; API tests cover direct-preview target metadata and secret-free serialization.

## [1.5.263] - 2026-08-24

### Device routes
- **Preview parity**: selecting a nested playback-device route in the administrator mapping editor now sends its exact device identity through the direct color preview path, so Preview Current Color uses the same stored bridge, area, channel profile, and credentials as area/channel/test diagnostics.

### Tests
- **Direct-preview coverage**: verifies blank redacted credentials resolve to the exact nested route, while missing-user, multi-target, wrong-case, and wrong-bridge requests fail closed without falling back to outer or global keys.

## [1.5.262] - 2026-08-24

### Device routes
- **Playback-device discovery**: the administrator mapping editor can discover recent Jellyfin device IDs and select configured routes without copying opaque, case-sensitive session identifiers by hand.
- **Route-aware diagnostics**: entertainment-area, channel, and connection-test requests accept an explicit device ID and resolve only that user's exact nested route credentials, with no fallback to another mapping or the global target.

### Security
- **Credential-free discovery**: the new playback-device endpoint returns bounded identity/activity metadata only; bridge keys, playback titles, and route secrets remain server-side.

### Tests
- **Nested-route coverage**: exact device matching, wrong-case/wrong-bridge fail-closed behavior, stored nested credential use, channel loading, bounded deduplication, and credential absence are covered.

## [1.5.261] - 2026-08-24

### Reliability
- **Health checks before threshold suppression**: static scenes now verify DTLS stream health and attempt reconnection before color-change threshold skips, preventing a dead OpenSSL process from remaining broken indefinitely.

### Tests
- **Static-scene recovery coverage**: verifies an unhealthy stream is not reported as a successful threshold skip.

## [1.5.260] - 2026-08-24

### Reliability
- **Scoped IPv6 bridge discovery**: mDNS responses now retain the receiving interface scope on link-local AAAA addresses, so discovered `fe80::` bridges remain routable on the correct local interface.

### Tests
- **Scoped discovery coverage**: parser and bridge-registration URI regressions verify link-local zones are preserved without scoping unique-local addresses.

## [1.5.259] - 2026-08-24

### Reliability
- **Restarted finite-cue accounting**: persisted skipped schedule occurrences no longer consume finite cue run limits when scheduler state is rehydrated after a restart.

### Tests
- **Restart history coverage**: verifies skipped persisted history remains auditable without exhausting the restored finite schedule.

## [1.5.258] - 2026-08-24

### Reliability
- **Skip retry safety**: a failed `SkipNextOccurrence` persistence no longer consumes the in-memory occurrence slot or removes deferred state, so the same cue can retry safely on the next scheduler pass.
- **Mapping credential writes**: the user-mapping POST contract now preserves explicitly entered top-level and nested device bridge keys while keeping persisted/read models credential-free.

### Security
- **Structured dependency gate**: the self-hosted NuGet vulnerability check now validates the SDK JSON schema and blocks on both top-level and transitive vulnerability entries instead of parsing human-readable output.

### Tests
- **Failure-path coverage**: scheduler persistence retry, JSON mapping credential round-trip/redaction, and structured vulnerability-report contracts are covered by focused checks.

## [1.5.257] - 2026-08-24

### Reliability
- **Session restart isolation**: canceled video/audio capture loops are now awaited before shared FFmpeg and Hue streamers are reused, preventing stale colors from a predecessor playback session from reaching a replacement session.
- **IPv6 bridge discovery**: local mDNS discovery now queries scoped IPv6 multicast on each multicast-capable interface, so IPv6-only Hue bridges can be discovered alongside existing IPv4 results.

### Tests
- **Lifecycle and discovery coverage**: gated stop-to-restart streaming coverage and ULA/link-local AAAA mDNS contracts verify the new boundaries without exposing credentials.

## [1.5.256] - 2026-08-24

### Security
- **Self-hosted checkout isolation**: every workflow checkout now disables persisted GitHub credentials, preventing repository-controlled build/test code from reading a token from `.git/config`.
- **Release token narrowing**: the release tag step uses an explicit, short-lived authentication header only for the required tag lookup and push.
- **Read-only scheduler telemetry**: schedule status/history reads no longer persist lazy history/deferred-run repairs while an administrator configuration mutation or scheduler lifecycle is active; repairs are deferred to a coordinated writer.

### Release integrity
- **Tested-artifact packaging**: the release package now consumes the build-and-test publish artifact instead of rebuilding a second, untested DLL.
- **SDK reproducibility**: CI and local tooling are pinned to the current .NET 8.0.424 SDK feature band.

## [1.5.255] - 2026-08-24

### Security
- **Generic configuration-route isolation**: the built-in Jellyfin plugin-configuration JSON no longer emits global, per-user, or device-route Hue credentials.
- **Guarded configuration writes**: generic plugin configuration updates are rejected so they cannot bypass Hue import normalization, credential preservation, or lifecycle serialization; administrators must use `/HueSync/Configuration`.

### Tests
- **Generic API boundary coverage**: verify credential-free raw configuration JSON and rejection of unguarded plugin configuration replacement.

## [1.5.254] - 2026-08-24

### Security
- **Configuration writer serialization**: administrator configuration writers now share the lifecycle gate with imports, while scheduler evaluation and manual cue lifecycles block stale writes and post-run persistence races.
- **Import readiness contract**: canImport now correctly becomes false during scheduler evaluation and scheduled lifecycle finalization, with explicit credential-free blocker flags.

### Tests
- **Writer and barrier coverage**: scheduler-evaluation contention, lifecycle gate ownership, active playback/diagnostics, active cues, and import conflict paths are covered.

## [1.5.253] - 2026-08-24

### Security
- **Configuration lifecycle barrier**: imports now coordinate with the shared bridge lifecycle gate and scheduler evaluation barrier, refusing active playback, diagnostics, active cues, or in-flight scheduler evaluation before replacing configuration.
- **Credential-safe readiness detail**: import validation exposes explicit diagnostic and configuration-mutation blockers without returning bridge credentials.

### Tests
- **Lifecycle race coverage**: gate, playback, diagnostic, active-cue, and scheduler-preflight tests verify conflict responses and unchanged configuration.

## [1.5.252] - 2026-08-24

### Security
- **Active-cue import guard**: configuration validation and import now refuse to replace scheduled cues while a restorative run is active, returning credential-free diagnostics and preserving the live configuration.

### Tests
- **Import lifecycle coverage**: validation and conflict-import paths preserve an in-flight cue until its run completes, then allow the inactive import.

## [1.5.251] - 2026-08-24

### Added
- **Upcoming-target UI parity**: administrator occurrence rows now distinguish all-enabled targets, default-bridge inclusion, selected mapping IDs, and nested user/device routes while preserving legacy labels.

### Tests
- **Occurrence target contract**: validate safe target metadata rendering and exact API target selection without exposing bridge credentials.

## [1.5.250] - 2026-08-24

### Security
- **Active-cue update guard**: an existing scheduled cue cannot be replaced while its restorative run is active; the API returns `409 Conflict` and preserves the configured cue.

### Tests
- **Schedule lifecycle coverage**: verify an in-flight cue update is rejected atomically and the original definition remains unchanged until the run completes.

## [1.5.249] - 2026-08-24

### Added
- **Pause telemetry status parity**: `/HueSync/Status` and support-bundle runtime diagnostics now expose the effective pause behavior and pause brightness without exposing credentials.

### Tests
- **Hosted status coverage**: verify pause fields are projected from the live service, inherited by support bundles, serialized safely, and remain null when no sync service is hosted.

## [1.5.248] - 2026-08-24

### Added
- **History target-mode UI parity**: the administrator history table now distinguishes all-enabled targets, default-bridge inclusion, selected mapping IDs, and nested device routes while preserving a legacy label fallback.

### Security
- **Credential-free history rendering**: target metadata remains text-only and the live status region is accessible without introducing raw HTML or bridge secrets.

### Tests
- **Configuration-page target contract**: validate history target fields, safe summary rendering, and the accessible status marker.

## [1.5.247] - 2026-08-24

### Added
- **Scheduled-history target-mode parity**: retained cue history now preserves the credential-free `targetAllEnabledMappings` mode through in-memory results, persisted history, JSON exports, and CSV exports.

### Tests
- **History target-mode coverage**: verify all-enabled mode survives persistence/reload and CSV serialization while legacy entries default safely and secrets remain absent.

## [1.5.246] - 2026-08-24

### Security
- **Active-cue deletion guard**: single scheduled-cue deletion now uses the same transactional service guard as bulk deletion and refuses to remove a cue while its restorative run is active.

### Tests
- **Deletion lifecycle coverage**: verify a running cue returns `409 Conflict` and remains configured until its run completes.

## [1.5.245] - 2026-08-24

### Security
- **RFC 5545-safe calendar folding**: fold iCalendar content lines by UTF-8 octets and keep Unicode scalars intact so multibyte schedule metadata cannot produce invalid physical lines.

### Tests
- **Multibyte calendar coverage**: verify 75-octet physical-line limits, continuation semantics, scalar boundaries, metadata unfolding, and credential absence.

## [1.5.244] - 2026-08-24

### Added
- **iCalendar target parity**: expose credential-free target-selection properties for all-enabled mappings, exact selected user IDs, nested playback-device routes, and default-target inclusion alongside the existing upcoming cue metadata.

### Tests
- **Calendar export coverage**: verify lower-camel route JSON, escaped iCalendar values, and absence of bridge credentials in the calendar feed.

## [1.5.243] - 2026-08-24

### Security
- **Duplicate registration prevention**: disable global and per-user Link Bridge controls for each confirmed in-flight request, clear their busy state on synchronous or asynchronous failure, and keep registration errors credential-free.

### Tests
- **Configuration-page lifecycle coverage**: validate confirmation gating, in-flight guards, button re-enablement, and absence of raw registration logging for both registration surfaces.

## [1.5.242] - 2026-08-24

### Security
- **Non-retriable bridge registration**: Link Button registration now performs a single transport attempt, preventing generic network retries from repeating a non-idempotent credential-creation request after a transient failure.

### Tests
- **Registration retry coverage**: verify transient registration failure results in exactly one bridge request even when normal retry attempts are configured.

## [1.5.241] - 2026-08-24

### Added
- **Credential-free scheduled-occurrence CSV parity**: export exact selected target user IDs, nested playback-device routes, and default-target inclusion alongside the existing upcoming-cue plan and timing fields.

### Tests
- **Occurrence export coverage**: verify escaped target-selection JSON remains aligned with the occurrence JSON contract and never includes bridge credentials.

## [1.5.240] - 2026-08-24

### Added
- **Credential-free scheduled-history CSV parity**: export exact selected target user IDs, nested playback-device routes, and default-target inclusion alongside bounded run telemetry.
- **Bridge registration API contract**: document and regression-test the authenticated Link Button registration flow, private-target validation, and credential handling guidance.

### Tests
- **API and export coverage**: verify registration rejects public targets without bridge contact, trims valid private targets, returns bridge credentials only in the response, and preserves escaped route JSON in history CSV.

## [1.5.239] - 2026-08-24

### Fixed
- **Audio capability probe resilience**: allow the bounded FFmpeg PCM startup probe more headroom on busy self-hosted hosts so diagnostics do not report a false unavailable state while preserving a hard timeout.

### Tests
- **Self-hosted environment verification**: retain the real FFmpeg PCM capture assertion in the full runner gate.

## [1.5.238] - 2026-08-24

### Fixed
- **Raw preview target-mode safety**: reject ambiguous requests that combine the legacy specific user mapping with broadcast or selected target modes before any bridge activity.
- **Failure telemetry parity**: direct preview resolution failures and bulk saved-scene exception results retain normalized credential-free user/device routes and selected-target metadata.
- **Preview selector readiness**: malformed metadata and incomplete nested device routes now keep saved-scene, playlist, and current-light preview selectors unavailable instead of allowing stale bridge requests.

### Tests
- **Mixed-target regression coverage**: verify raw preview rejects legacy/selected combinations and direct route failures preserve exact route IDs without credentials.

## [1.5.237] - 2026-08-24

### Fixed
- **Scheduled-route telemetry labels**: credential-free status, occurrence, history, and preview results now identify exact nested user/device route IDs instead of collapsing every route selection to a generic count.

### Tests
- **Route-label regression coverage**: verify direct scheduled and playlist preview route labels remain readable while credentials stay absent.

## [1.5.236] - 2026-08-24

### Fixed
- **Preview route failure parity**: playlist-preview failures and bulk-preview exception results now retain credential-free exact user/device route IDs alongside the existing target metadata.
- **Preview API contract parity**: raw, saved-scene, and saved-playlist preview documentation now describes exact nested playback-device routes and the trusted build validates the endpoint/source contract.

### Tests
- **Route failure telemetry coverage**: verify malformed saved playlists retain normalized device-route metadata without credentials.

## [1.5.235] - 2026-08-24

### Fixed
- **Scheduled-route dependency protection**: mapping dependency reports and lifecycle operations now account for exact nested device routes, and removing a device still referenced by a cue is rejected before persistence.
- **Import and runtime parity**: partial configuration imports preserve omitted scheduled routes, and direct scheduled runs now carry route IDs through result, history, persisted-history, and runtime telemetry.

### Tests
- **Route lifecycle regression coverage**: validate nested-route dependency blocking, partial-import preservation, and direct scheduled-route telemetry without exposing credentials.

## [1.5.234] - 2026-08-24

### Fixed
- **Scheduled device-route targeting**: allow cues to select exact nested playback-device routes using credential-free user/device IDs, carry them through scheduler telemetry and backup/restore, and reject missing, disabled, duplicate, or malformed routes before persistence.
- **Release installation parity**: document the published ZIP and SHA-256 sidecar, verify the archive before extraction, and install the DLL with the required `meta.json` manifest.

### Tests
- **Release documentation contract**: validate README package guidance against the workflow's canonical archive names, exact package contents, and checksum verification step.

## [1.5.233] - 2026-08-24

### Fixed
- **Fail-closed preview target metadata**: keep target selectors and preview, capture, and playlist controls disabled while saved-scene, playlist, or mapping metadata is unavailable, replace failed target lists with an explicit reload message instead of silently falling back to the default bridge, and gate direct preview entry points before any POST.

### Tests
- **Configuration-page target readiness contracts**: validate separate scene and playlist metadata readiness, disabled failure controls, explicit unavailable-target messaging, and guards across direct, bulk, saved-scene, playlist, capture, and mapping previews.

## [1.5.232] - 2026-08-24

### Security
- **Fail-closed bulk mapping deletion**: reject null or blank user mapping IDs before normalization or configuration mutation.
- **Release artifact verification**: generate a SHA-256 sidecar for each release package, verify it on the isolated release runner, and publish it with the ZIP for downstream integrity checks.

### Tests
- **Malformed bulk-delete coverage**: verify mixed valid, null, and blank selections return HTTP 400 and preserve every mapping.

## [1.5.231] - 2026-08-24

### Security
- **Fail-closed preview target selection**: reject null or blank target user IDs across direct, saved-scene, bulk saved-scene, playlist, and bulk playlist previews before any default-bridge fallback or bridge activity.

### Fixed
- **Test runner refresh**: update the private xUnit Visual Studio adapter to 4.0.0 with a refreshed locked package hash while keeping the production plugin dependency set unchanged.

### Tests
- **Malformed target coverage**: verify each preview endpoint returns HTTP 400 and invokes neither the stream tester nor bridge HTTP client for blank target IDs.

## [1.5.230] - 2026-08-24

### Security
- **Hash-locked Semgrep**: install the blocking static-analysis tool and its transitive dependencies only from a reviewed Python 3.12/x86_64 SHA-256 lock, with platform/version validation and `pip check` before scanning.
- **Actions policy enforcement**: enable repository-level immutable-SHA enforcement while retaining the existing action allowlist.
- **Scanner dependency maintenance**: register the hash-locked Semgrep environment with Dependabot so transitive security updates are surfaced and reviewed.

### Fixed
- **Import preflight enforcement**: keep Review and Import disabled until a successful non-mutating validation, invalidate that approval when migration credentials change, and fail closed in the submit handler.

### Tests
- **Workflow lock coverage**: validate the Semgrep lock format, package hashes, and pinned version before installation.
- **Configuration-page coverage**: protect import-button disabled state, credential-change invalidation, and the submit preflight guard.

## [1.5.229] - 2026-08-24

### Security
- **Credential-safe bridge registration**: stop logging registration response objects and raw registration errors in the administrator browser console, and replace the raw-response recovery prompt with credential-free guidance.
- **Fail-closed capture selection**: reject blank target user IDs before current-light capture can fall back to the default bridge.

### Tests
- **Credential redaction coverage**: protect the registration workflow with a static contract that rejects response/error console logging and raw-response prompts.
- **Selection validation coverage**: verify blank target user IDs return a client error without contacting a bridge.

## [1.5.228] - 2026-08-24

### Fixed
- **Malformed capture routes**: reject null or blank-user batch current-light routes before target fallback, preventing an invalid selection from silently capturing the default bridge.
- **Bulk preview telemetry**: render per-target results for saved-scene and playlist bulk previews so explicit device-route success and failure remains visible to administrators.

### Tests
- **Fail-closed selection coverage**: verify malformed batch routes return a client error without contacting a bridge.

## [1.5.227] - 2026-08-24

### Fixed
- **Case-sensitive device routes**: require exact Jellyfin `DeviceId` matching during current-light capture so a casing variant cannot resolve to the wrong saved route, while keeping user mapping IDs case-insensitive.
- **Batch route selection**: allow distinct case-sensitive device routes to be selected together and reject only true duplicate user/device pairs.
- **Scene cue route selection**: apply the same mixed user/device identity rules to selected scene targets without delimiter-based collisions.

### Tests
- **Capture route regression coverage**: verify case-sensitive single-route rejection and successful batch capture of device IDs that differ only by case.

## [1.5.226] - 2026-08-24

### Fixed
- **Fail-closed release verification**: defer tag creation until package and changelog checks pass, and refuse to publish when remote tag lookup fails instead of treating an unavailable remote as an unused tag.

### Tests
- **Release-gate coverage**: keep release-tag mutation after all non-mutating checks and verify the created remote tag resolves to the workflow commit.

## [1.5.225] - 2026-08-24

### Fixed
- **Release provenance**: create and verify each release tag at the exact workflow commit before publishing, preventing serialized self-hosted runs from attaching a package to a newer moving `main` tip.

### Tests
- **Release-gate coverage**: pin the GitHub release action to the verified workflow commit and fail closed when an existing tag points elsewhere.

## [1.5.224] - 2026-08-24

### Fixed
- **Disabled target safety**: disable user-mapping preview options when their mapping is disabled and discard restored selections that are no longer eligible, avoiding predictable server-side preview failures.

### Tests
- **Regression coverage**: extend the configuration-page contract validator to protect disabled parent target options and restoration filtering.

## [1.5.223] - 2026-08-24

### Fixed
- **Device-route profile preservation**: keep explicit device targets distinct when they share a bridge and entertainment area but use different channel profiles, so selected previews do not silently drop a requested route.

### Tests
- **Regression coverage**: verify same-area device routes with distinct channel profiles both resolve and retain their channel selections.

## [1.5.222] - 2026-08-24

### Fixed
- **Diagnostic cancellation recovery**: re-enable the administrator diagnostics Cancel control when a cancellation request fails or reports no active operation, allowing a safe retry while diagnostics remain active.

### Tests
- **Regression coverage**: extend the configuration-page contract validator to protect the diagnostic cancellation lifecycle.

## [1.5.221] - 2026-08-24

### Added
- **Preview route telemetry**: return sanitized explicit `{userId, deviceId}` selections in normal, saved-scene, and playlist preview results so device-specific outcomes remain identifiable without exposing credentials.
- **Cancellation recovery**: re-enable the administrator preview Cancel control when a cancellation request fails or reports no active operation, allowing a safe retry while the original request is still running.

### Security
- **Malformed playlist resilience**: reject null saved-scene references during color-preset rename validation instead of throwing from the migration path.

### Tests
- **Regression coverage**: verify route telemetry remains credential-free and malformed playlist references return a validation response without mutating configuration.

## [1.5.220] - 2026-08-24

### Added
- **Device-route preview parity**: normal, saved-scene, bulk saved-scene, and playlist previews now accept credential-free `{userId, deviceId}` routes and resolve nested device bridge profiles without exposing secrets or changing persisted target selections.
- **Administrator target controls**: expose nested device routes in preview selectors while preserving legacy default, user-mapping, and all-target behavior.

### Security
- **Fail-closed route validation**: malformed or incomplete device-route payloads are rejected before any credential-bearing fallback, device IDs remain case-sensitive, and single-playlist previews preflight selected routes like bulk previews.

### Tests
- **Regression coverage**: verify nested device-route credentials, channel profiles, labels, and preview request plumbing.

## [1.5.219] - 2026-08-24

### Security
- **Malformed nested-target resilience**: configuration-import matching ignores null persisted device targets, mapping validation runs even when global synchronization is disabled, and user-mapping identity comparisons normalize whitespace before replacement.

### Tests
- **Regression coverage**: cover null nested import targets, disabled-global null mappings, whitespace-padded mapping replacement, and global credential clearing without touching per-user or device-route secrets.

## [1.5.218] - 2026-08-24

### Added
- **Credential lifecycle control**: add a confirmation-gated administrator control to clear stored global Hue credentials without changing per-user or device-route secrets.

### Security
- **Malformed mapping resilience**: null user-mapping entries are ignored by runtime bridge, playback, override, and summary lookups instead of causing a playback-time exception.

### Tests
- **Regression coverage**: exercise null-safe runtime mapping resolution and the credential-clearing contract alongside the full configuration-page validation.

## [1.5.217] - 2026-08-24

### Added
- **Device-route current-light capture**: select an explicit per-user device route from a dedicated credential-free capture control, capture device-only mappings, and receive redacted device identity in single and batch results.

### Tests
- **Regression coverage**: verify single and batch current-light capture resolves explicit device routes, uses the route bridge, preserves target metadata, and never serializes bridge secrets.

## [1.5.216] - 2026-08-24

### Added
- **Device-route migration credentials and diagnostics**: expose nested device-route replacement-key fields in the import wizard and include explicit device routes, device identity, and route labels in target diagnostics and support bundles.

### Fixed
- **Fail-safe malformed mappings**: null user-mapping entries now produce validation errors instead of throwing, and sync can rely on complete explicit device routes without a global bridge target.

## [1.5.215] - 2026-08-24

### Fixed
- **Live device-route telemetry**: map the active device identity and route-match state through the top-level status API so the administrator live panel reflects the effective playback route.

## [1.5.214] - 2026-08-24

### Fixed
- **Import nullability hardening**: disabled or legacy mappings with absent nested route collections now normalize safely without a nullable dereference warning.

## [1.5.213] - 2026-08-24

### Added
- **Automatic playback device routing**: configure up to 25 exact, case-sensitive Jellyfin SessionInfo.DeviceId routes under each user mapping. Each route selects its own private Hue bridge, entertainment area, credentials, and optional channel subset, with device-to-user-to-global fallback.
- **Credential-safe device mapping workflows**: mapping summaries omit nested keys, same-server edits preserve stored credentials, cross-server imports accept explicit DeviceTargetCredentials, and bulk enable/disable rollback restores nested routes.
- **Runtime observability and administrator controls**: expose the active device, route-match state, session history metadata, mapping route counts, and a bounded JSON route editor.

### Tests
- **Regression coverage**: validate exact device matching, case sensitivity, fallback precedence, channel inheritance, bounded target counts, duplicate IDs, bridge/credential/area requirements, and malformed channel overrides.

## [1.5.212] - 2026-08-24

### Security
- **Redirect-safe bridge transport**: disable automatic HTTP redirects for Hue bridge clients so credential-bearing requests cannot be followed to an unintended host.

### Tests
- **Regression coverage**: verify the bridge HTTP handler keeps redirect following disabled while retaining scoped local-certificate validation.

## [1.5.211] - 2026-08-24

### Security
- **Bridge target validation**: reject loopback, unspecified, multicast, and broadcast IP addresses before certificate exceptions or bridge requests, while retaining private, link-local, unique-local, and `.local` Hue targets.

### Tests
- **Regression coverage**: verify reserved and non-routable address classes fail closed without changing supported private bridge discovery.

## [1.5.210] - 2026-08-24

### Security
- **FFmpeg capability boundary**: restrict custom flags to decoder, thread, and hardware-tuning options with bounded values; reject alternate inputs/outputs, protocols, headers, filters, scripts, arbitrary paths, option smuggling, duplicates, and oversized text before playback or import.

### Tests
- **Regression coverage**: verify safe hardware-device paths and decoder/thread options remain supported while capability-expanding flags and unsafe values fail closed in configuration and both FFmpeg builders.

## [1.5.209] - 2026-08-24

### Security
- **Support-bundle FFmpeg redaction**: omit global and per-user custom FFmpeg flag values from support documents while retaining credential-free configured-state telemetry; intentional backup exports remain available for migration.
- **POST-only entertainment-area loading**: remove the legacy query-string route so Hue app keys cannot be sent in URLs or access logs.

### Tests
- **Regression coverage**: verify support bundles redact sentinel FFmpeg values and the secret-bearing GET route is not exposed.

## [1.5.208] - 2026-08-23

### Fixed
- **In-process schedule overlap recovery**: re-evaluate later scheduled cues against elapsed scheduler time after a long restorative cue, so a cue that crosses its minute is recovered once even when restart catch-up is disabled.
- **Run-slot safety**: retain stable UTC occurrence claims and one-time completion behavior while recovering overlapped cues.

### Security
- **Attribute-safe mapping controls**: encode user mapping identifiers for HTML attributes so imported or mutated identifiers cannot break out of administrator action buttons.

### Tests
- **Regression coverage**: verify a later one-time cue runs exactly once after a blocking cue, records catch-up telemetry, and disables itself safely.

## [1.5.207] - 2026-08-23

### Fixed
- **Complete non-success telemetry**: retain effective direct-scene brightness on skipped and failed cue results so history and CSV reports stay accurate for every outcome.
- **Stable schedule request API shape**: keep `durationSeconds` an integer while tracking JSON presence separately, preserving omitted-field semantics without changing compiled consumers; explicit zero still clears a duration override.

### Tests
- **Regression coverage**: verify skipped-cue brightness history and explicit zero duration clearing alongside the scheduled brightness round-trip and export contracts.

## [1.5.206] - 2026-08-23

### Added
- **Direct scheduled-scene brightness overrides**: allow nullable `brightnessPercent` overrides on single-scene cues, with 0-100 validation and inherited saved-scene brightness when omitted; playlist cues retain each step's saved brightness.
- **Credential-free brightness telemetry**: carry effective scheduled-scene brightness through runtime status, run results, upcoming occurrence JSON/CSV/iCalendar reports, persisted history, configuration backup/restore, and the administrator UI.
- **Safe partial schedule edits**: preserve an existing cue's duration and brightness when an update omits those fields, while explicit JSON null continues to clear an override.

### Tests
- **Regression coverage**: verify brightness validation/inheritance, explicit null clearing, partial-update preservation, scheduler stream propagation, status/history/occurrence exports, backup portability, and configuration-page contracts.

## [1.5.205] - 2026-08-23

### Added
- **Explicit playlist rename workflow**: add a credential-free `POST /HueSync/ScenePlaylists/{name}/Rename` operation and administrator button that atomically migrates scheduled-cue references while preserving every playlist step, target, repeat, and playback-order setting.
- **Direct scheduled-scene RGB overrides**: allow nullable `red`, `green`, and `blue` channel overrides on single-scene cues, with 0-255 validation and inherited saved-scene colors when omitted; explicit JSON null clears an existing override.
- **Credential-free RGB telemetry**: carry effective scheduled-scene colors through runtime status, run results, upcoming occurrence JSON/CSV/iCalendar reports, and persisted history without exposing bridge credentials.

### Security
- **Bounded self-hosted jobs**: add documented timeout budgets to every build, code-quality, security-scan, package, and release-publication job.

### Tests
- **Regression coverage**: verify rename normalization, collision/blank-name rejection, metadata preservation, cue-reference migration, RGB inheritance/range rules, explicit null clearing, runtime payloads, exports, and credential-free responses.

## [1.5.204] - 2026-08-23

### Added
- **Per-step playlist RGB colors**: add nullable `stepRed`, `stepGreen`, and `stepBlue` channel overrides bounded to 0-255; null or omitted values inherit each referenced saved scene's channel values.
- **End-to-end color parity**: apply effective RGB values to non-continuous and continuous playlist previews, scheduled occurrence plans, runtime/history telemetry, API/UI CRUD, duplication, CSV/iCalendar plan exports, and credential-free backup/restore.
- **Regression coverage**: verify RGB validation/inheritance, runtime stream payloads, continuous multi-target plans, API round trips, occurrence exports, and configuration-page contracts.

## [1.5.203] - 2026-08-23

### Added
- **Per-step playlist effects**: add nullable `stepEffects` overrides for Solid, Pulse, Rainbow, Candle, Temperature, Aurora, Fire, Ocean, Lightning, and Starlight; blank/null values inherit the referenced saved scene effect, with full administrator, API, scheduler, telemetry, duplication, and backup parity
- **Continuous playlist streaming**: preflight the complete expanded plan, then render every step for one target through a single capture, entertainment-area activation, DTLS session, deactivation, and restoration lifecycle
- **Stable target telemetry**: correlate playlist outcomes by resolved target position so distinct targets with duplicate display labels retain independent completion and failure details

### Security
- **Reproducible dependency restore**: commit NuGet dependency locks with content hashes and require locked-mode restores before build, formatting, vulnerability audit, packaging, and release
- **Dedicated self-hosted runners**: remove manual, pull-request, and non-main triggers; restrict every self-hosted job to trusted `main`, including scheduled default-branch security scans; isolate the `contents:write` release job under a separate least-privileged runner identity; and fail release creation when the version tag already exists

## [1.5.202] - 2026-08-23

### Added
- **Per-step playlist effect speeds**: saved-scene playlists now accept nullable `stepEffectSpeedPercent` overrides bounded to 25-400%; omitted or null values inherit the referenced scene's animation rate
- **End-to-end speed parity**: apply effective per-step speeds to non-solid playlist previews and scheduled runs, expanded occurrence plans, runtime/history telemetry, API/UI, duplication, and credential-free backup/restore
- **Regression coverage**: verify effect-speed validation/inheritance, stream propagation, scheduler/history telemetry, API round trips, backup portability, and configuration-page contracts
- **Fail-closed release security**: gate packaging and release publication on build/tests, formatting, dependency vulnerability checks, full-history Gitleaks, and Semgrep; pin Gitleaks 8.30.1 and Semgrep 1.174.0, verify the official Gitleaks checksum manifest/archive, and run from isolated temporary paths without privileged host installation

## [1.5.201] - 2026-08-23

### Added
- **Per-step playlist fade curves**: saved-scene playlists now accept nullable `stepTransitionCurves` overrides for Linear, SmoothStep, EaseIn, EaseOut, and EaseInOut; omitted or null values inherit the referenced scene curve
- **End-to-end curve parity**: apply effective per-step curves to easing-capable previews, scheduled plans, runtime/status/history telemetry, API/UI, occurrence displays, duplication, and credential-free backup/restore
- **Regression coverage**: verify curve validation/inheritance, runtime propagation, scheduler/history telemetry, API round trips, backup portability, and configuration-page contracts

## [1.5.200] - 2026-08-23

### Added
- **Per-step playlist transitions**: saved-scene playlists now accept nullable `stepTransitionSeconds` and `stepTransitionOutSeconds` overrides bounded to 0-30 seconds; null or omitted values inherit the referenced scene's fade settings and short holds clamp safely at runtime
- **End-to-end transition and offset parity**: carry effective per-step fades and cumulative start offsets through previews, scheduled cues, runtime/status/history telemetry, occurrence JSON/CSV/iCalendar plans, API/UI, duplication, and credential-free backup/restore
- **Regression coverage**: verify transition validation/inheritance/clamping, API and backup round trips, preview/scheduler payloads, persisted history, and configuration-page contracts

## [1.5.199] - 2026-08-23

### Added
- **Expanded upcoming playlist plans**: scheduled occurrence JSON now exposes the exact credential-free expanded playlist steps for the occurrence date, including stable shuffle/repeat order, saved position, effective brightness, hold duration, transitions, and cumulative start offsets
- **Export and administrator parity**: carry the step plan through occurrence CSV, iCalendar metadata, and the administrator upcoming-cue table without exposing credentials
- **Regression coverage**: verify override inheritance, repeat offsets, shuffle stability, JSON/CSV/iCalendar exports, and configuration-page contracts

## [1.5.198] - 2026-08-23

### Added
- **Per-step playlist brightness**: saved-scene playlists now accept credential-free `stepBrightnessPercent` overrides in parallel with `presetNames`; `null` or an omitted legacy list inherits each referenced scene's brightness, while explicit values remain bounded to 0-100 percent
- **End-to-end brightness/history parity**: carry effective per-step brightness through previews, scheduled cues, repeat passes, runtime/status/history telemetry, API/UI, duplication, and credential-safe backup/restore, including persisted credential-free playlist step results across restart
- **Regression coverage**: verify brightness validation, inheritance, API/UI round trips, preview/scheduler payloads, persistence, duplication, and credential-safe portability

## [1.5.197] - 2026-08-23

### Added
- **Per-step playlist timing**: saved-scene playlists now accept credential-free `stepDurationSeconds` overrides in parallel with `presetNames`; `0` or an omitted legacy list inherits each referenced scene's duration, while explicit holds remain bounded to 1-30 seconds
- **End-to-end duration parity**: carry effective step timing through previews, scheduled cues, repeat totals, runtime/history/status/occurrence telemetry, API/UI, duplication, and credential-safe backup/restore
- **Regression coverage**: verify validation, inheritance, API/UI round trips, preview timing, scheduler totals, and legacy playlist compatibility

## [1.5.196] - 2026-08-23

### Added
- **Configurable transition curves**: saved scenes and direct previews now support Linear, SmoothStep, EaseIn, EaseOut, and EaseInOut easing for fade-in and fade-out transitions, with Linear preserved for legacy configurations
- **End-to-end transition parity**: carry the normalized curve through playlist/scheduled execution, runtime/history/status/occurrence metadata, API/UI responses, CSV/iCalendar exports, duplication, and credential-free backup/restore
- **Regression coverage**: validate supported curves and deterministic easing math while preserving the original linear preview contract

## [1.5.195] - 2026-08-23

### Added
- **Deterministic playlist shuffle**: saved playlists now support canonical `Sequential` or date-seeded `Shuffle` playback per repeat pass, with stable retry behavior and original saved-position telemetry
- **End-to-end playlist parity**: carry playback order through previews, scheduled cues, runtime/history/status/occurrence metadata, duplication, backup/restore, API contracts, and the administrator editor
- **Regression coverage**: verify legacy sequential defaults, canonical shuffle normalization, stable pass ordering, saved-position reporting, API round trips, and configuration-page contracts

## [1.5.194] - 2026-08-23

### Added
- **Starlight scene effect**: deterministic cool white/blue twinkles with sharp glints and independent channel phases for previews, saved scenes, playlists, and scheduled cues
- **Portable effect support**: carry Starlight through validation, API/UI, backup/restore, occurrence metadata, and administrator controls
- **Regression coverage**: verify deterministic bounded frames, seed intensity, channel phase, canonical API persistence, and configuration-page contracts

## [1.5.193] - 2026-08-23

### Added
- **Solar-noon scheduled cues**: schedule scenes at NOAA solar noon with the same bounded offsets, portable coordinates, selected time zones, recurrence, and scheduler recovery as the existing solar modes
- **Complete solar-noon portability**: carry the new timing mode through validation, API/status/CSV/iCalendar metadata, backup/restore, and administrator controls
- **Regression coverage**: verify NOAA equation-of-time calculations, timezone-aware occurrences, polar-day compatibility, canonical API persistence, and configuration-page contracts

## [1.5.192] - 2026-08-23

### Added
- **Nautical twilight scheduled cues**: schedule scenes at NauticalDawn or NauticalDusk using the sun's 12°-below-horizon events
- **Astronomical twilight scheduled cues**: schedule scenes at AstronomicalDawn or AstronomicalDusk using the sun's 18°-below-horizon events
- **Complete solar portability**: carry all standard twilight bands through validation, scheduler recovery, API/status/CSV/iCalendar metadata, backup/restore, and administrator controls
- **Regression coverage**: verify chronological twilight ordering, timezone-aware occurrences, canonical API persistence, and configuration-page contracts

## [1.5.191] - 2026-08-23

### Added
- **Civil twilight scheduled cues**: schedule saved scenes and playlists at CivilDawn or CivilDusk, using the sun's 6°-below-horizon events with the same bounded offsets, portable coordinates, and selected cue time zone as sunrise/sunset
- **Scheduler parity**: carry civil dawn/dusk through validation, occurrence previews, due checks, missed-cue recovery, API/status/CSV/iCalendar metadata, backup/restore, and administrator controls
- **Regression coverage**: verify civil-twilight NOAA calculations, timezone-aware scheduling, canonical API persistence, and configuration-page contracts

## [1.5.190] - 2026-08-23

### Added
- **Lightning scene effect**: deterministic electric blue/white storm flashes with independent channel phases for previews, saved scenes, playlists, and scheduled cues
- **Portable effect support**: carry Lightning through validation, API/UI, backup/restore, occurrence metadata, and administrator controls
- **Regression coverage**: verify deterministic animation, bounded RGB16 output, per-channel phase, and API/config contracts

## [1.5.189] - 2026-08-23

### Fixed
- **Cross-midnight solar offsets**: keep the unshifted sunrise/sunset calendar date as the recurrence anchor while allowing the bounded -720 to +720 minute offset to resolve into the adjacent local date
- **Scheduler recovery and de-duplication**: match shifted solar occurrences across neighboring base dates so due checks, stable run slots, next-run previews, and missed-cue recovery remain correct after local midnight
- **Regression coverage**: verify shifted UTC/local instants, due detection, upcoming previews, and catch-up recovery for solar events that cross midnight

## [1.5.188] - 2026-08-23

### Added
- **Solar-aware scheduled cues**: schedule saved scenes and playlists at local **Fixed**, **Sunrise**, or **Sunset** times with bounded -720 to +720 minute offsets
- **Portable location and time-zone rules**: persist decimal latitude/longitude per solar cue, resolve the event in the selected cue time zone with DST-safe UTC instants, and skip polar day/night dates without inventing a run
- **Credential-free observability**: expose timing mode, offset, coordinates, and resolved occurrence metadata through schedule CRUD, status, upcoming occurrences, CSV, iCalendar, backup/restore, and the administrator editor
- **Regression coverage**: verify NOAA solar calculations, midnight-crossing events, polar no-event handling, validation, normalization, and scheduler due/upcoming behavior

## [1.5.187] - 2026-08-23

### Added
- **Fire and Ocean scene effects**: add deterministic high-energy red/amber/yellow Fire flicker and rolling blue/cyan Ocean waves to previews, saved scenes, playlists, and scheduled cues
- **Portable effect compatibility**: carry both effects through configuration normalization, API validation, backup/restore, administrator controls, and scheduler telemetry
- **Regression coverage**: verify bounded deterministic frames, independent channel phases, effect normalization, and UI/API contracts

## [1.5.186] - 2026-08-23

### Added
- **Quarter-turn orientation calibration**: support Rotate90Clockwise and Rotate90Counterclockwise for entertainment areas mounted sideways, alongside the existing mirror and 180-degree modes
- **Consistent spatial transforms**: apply quarter-turn transforms to video sampling and audio spatial/stereo routing with global and per-user inheritance
- **Portable controls and coverage**: expose both quarter-turn modes through validation, configuration portability, administrator selectors, and coordinate-transform regression tests

## [1.5.185] - 2026-08-23

### Added
- **Spatial orientation calibration**: correct physically mirrored, vertically flipped, or 180-degree-rotated Hue entertainment areas with Normal, MirrorHorizontal, MirrorVertical, and Rotate180 modes
- **Consistent video/audio mapping**: apply the selected orientation to video sampling coordinates and audio spatial/stereo routing, with per-user inheritance and runtime telemetry
- **Portable administrator controls**: carry orientation through configuration save/load, backup/import, mapping summaries, and the global/per-user configuration page with validation and regression coverage

## [1.5.184] - 2026-08-23

### Added
- **Color temperature correction**: tune global or per-user white balance from 1000 K to 20000 K with a neutral 6500 K daylight default
- **Consistent runtime profiles**: apply color temperature after channel gains to both video and audio color streams and expose the effective value in status, backup/import, and mapping summaries
- **Administrator controls and coverage**: add validated color-temperature controls to the global and per-user profiles with API/UI round-trip and color-math regression tests
- **Contrast correction**: tune global or per-user contrast around mid-gray from 50% to 200% with the neutral 100% default
- **Consistent runtime profiles**: apply contrast correction after gamma to both video and audio color streams and expose the effective value in status, backup/import, and mapping summaries
- **Administrator controls and coverage**: add validated contrast controls to the global and per-user profiles with API/UI round-trip and color-math regression tests
- **Gamma color correction**: tune global or per-user mid-tone brightness from 0.5 to 2.5 while retaining a neutral 1.0 default
- **Consistent runtime profiles**: apply gamma correction to both video and audio color streams and expose the effective value in status, backup/import, and mapping summaries
- **Administrator controls and coverage**: add validated gamma controls to the global and per-user profiles with API/UI round-trip and color-math regression tests

## [1.5.181] - 2026-08-22

### Added
- **Configurable dark-scene behavior**: choose whether frames below the blackout threshold turn every channel black or preserve the last streamed colors
- **Per-user dark-scene overrides**: inherit the global policy or choose a different policy for each mapped user
- **Operational visibility and portability**: expose the effective policy in runtime status, configuration backup/import, mapping summaries, and administrator controls

### Reliability
- **Consistent video/audio handling**: preserve temporal color history and avoid redundant Hue writes when KeepLastColors is active
- **Regression coverage**: validate policy values, inheritance, runtime resolution, status reporting, and API/UI round trips

## [1.5.180] - 2026-08-20

### Security
- **Blocking repository scans**: make Gitleaks secret scanning and Semgrep static analysis fail CI on findings or scanner errors, while preserving redacted JSON artifacts for review
- **Reliable Semgrep configuration**: use the explicit `p/default` ruleset so disabling metrics cannot silently skip the scan
- **Action supply-chain hardening**: pin every third-party GitHub Action to an immutable commit SHA and keep all jobs on the named self-hosted runner
- **Security reporting**: add a private-vulnerability reporting policy covering plugin code, dependencies, and workflow changes
- **Dependency hardening**: update the test SDK and coverage collector to current stable releases, and refresh setup-dotnet and Codecov actions to their current major versions

## [1.5.179] - 2026-08-20

### Added
- **Pause-time dimming**: choose KeepLastColors, RestoreLightState, or DimToCinemaLevel so paused playback can dim captured lights to the effective cinema level without resetting their streamed colors
- **Per-user pause policy**: mappings can inherit or override the global pause behavior while reusing the existing effective cinema dim level

### Reliability
- **Brightness-only pause updates**: capture the required snapshot even when final restoration is disabled, update each light with retries, preserve the active playback lifecycle for resume, and clear snapshots during cleanup
- **Runtime telemetry and regression coverage**: expose the effective pause policy/dim level while paused and cover validation, brightness payloads, and lifecycle behavior

## [1.5.178] - 2026-08-20

### Added
- **Target-aware scheduled playback conflicts**: choose the historical process-wide conflict scope or allow scheduled cues to continue when active playback owns a different bridge/entertainment-area target
- **Scoped lifecycle arbitration**: matching-target scheduled previews reserve only their resolved Hue resource, preserving independent multi-room playback and credential-safe status telemetry

### Reliability
- **Portable controls and regression coverage**: carry the conflict scope through configuration save/load, backup/import, administrator controls, and target-specific scheduler tests

## [1.5.177] - 2026-08-20

### Added
- **Per-cue playback conflict overrides**: each scheduled cue can inherit the global Skip/Defer policy or explicitly choose Skip or bounded Defer without changing other cues
- **Effective policy telemetry**: schedule API results, runtime status, configuration UI, and credential-safe import/export expose both the persisted override and the effective policy

### Reliability
- **Policy-aware deferred-state cleanup**: persisted deferred occurrences are retained only for enabled cues whose effective policy is Defer, preventing stale waits after a cue-level policy change
- **Regression coverage**: verify per-cue policy validation, API round trips, global inheritance, override queueing, and override skip behavior

## [1.5.176] - 2026-08-20

### Added
- **Restart-safe deferred scene cues**: persist one credential-free deferred occurrence per scheduled cue so a Jellyfin restart cannot silently discard a cue still inside its bounded playback wait window
- **Restored-cue telemetry**: expose restored pending state and mark restored deferred successes and expirations in scheduler status, history, JSON, CSV, and administrator controls

### Reliability
- **Deferred-state cleanup**: remove malformed, duplicate, disabled, and deleted-cue entries before replay, and clear persisted state after completion, expiry, or policy cleanup without interrupting scheduler execution

## [1.5.175] - 2026-08-20

### Added
- **Playback-aware scheduled cues**: choose the historical immediate **Skip** behavior or a bounded **Defer** policy that holds one automatic occurrence while playback owns the bridge, retries it after playback ends, and never consumes a finite execution limit while waiting
- **Deferred-cue safety and telemetry**: expired waits are recorded as sanitized skipped outcomes, one-time cues are disabled safely, and scheduler status/history expose pending, deferred, playback-active, policy, and wait-window state without credentials
- **Portable administrator controls**: carry the playback conflict policy and 1-120 minute defer window through configuration validation, API settings, credential-safe backup/import, and the configuration page
- **Regression coverage**: verify defer queueing, post-playback execution, expiry, telemetry, validation, and API round trips

## [1.5.174] - 2026-08-20

### Added
- **Configurable beat-pulse onset threshold**: require a bounded 0-100% normalized energy rise before an enabled beat pulse attacks, while the default 0% preserves existing response
- **Per-user threshold profiles**: inherit or override the onset threshold with runtime clamping, active status telemetry, administrator controls, and credential-safe backup/import support
- **Regression coverage**: verify threshold math, defaults, bounds, inheritance, and API/configuration round trips

## [1.5.173] - 2026-08-20

### Added
- **Configurable audio response smoothing**: blend each decoded low/mid/high analysis window with the previous window from 0-90% to reduce spectral flicker while preserving immediate 0% behavior
- **Per-user response profiles**: inherit or override smoothing with bounded runtime clamping, active status telemetry, administrator controls, and credential-safe backup/import support
- **Regression coverage**: verify smoothing across mixed and source-channel energy, defaults, bounds, inheritance, and API round trips

## [1.5.172] - 2026-08-20

### Added
- **Configurable audio band gains**: independently scale low, mid, and high audio energy from 0-200% while neutral 100% values preserve the original analyzer balance
- **Per-user band-balance profiles**: inherit or override each band gain with bounded runtime clamping, active status telemetry, administrator controls, and credential-safe backup/import support
- **Regression coverage**: verify band-gain scaling, defaults, bounds, inheritance, runtime resolution, and API round trips

## [1.5.171] - 2026-08-20

### Added
- **Configurable audio noise gate**: suppress low-level mixed RMS windows with a bounded 0-100% threshold while zero preserves the existing analyzer behavior
- **Per-user noise-floor profiles**: inherit or override the global gate with runtime clamping, active status telemetry, administrator controls, and credential-safe backup/import support
- **Regression coverage**: verify gate threshold behavior, defaults, bounds, inheritance, and API round trips

## [1.5.170] - 2026-08-20

### Added
- **Configurable beat-pulse release**: carry an enabled audio beat pulse into subsequent frames with a bounded 0-100% release tail; zero preserves the instantaneous transient behavior
- **Per-user audio release profiles**: inherit or override the global beat-pulse release with clamped validation, live runtime telemetry, administrator controls, and credential-safe backup/import support
- **Regression coverage**: verify release-tail math, defaults, bounds, inheritance, and runtime resolution

## [1.5.169] - 2026-08-20

### Added
- **Configurable history retention**: choose 1-25 completed playback sessions and 1-100 scheduled-cue runs to retain in memory and optionally across Jellyfin restarts, preserving the existing 25/100 defaults
- **Immediate safe trimming**: reducing a retention window trims newest-first sanitized history when the configuration is saved without affecting active playback, credentials, or finite cue counters
- **Portable administrator controls**: carry retention preferences through the settings API, credential-safe backup/import, validation, and the configuration page
- **Regression coverage**: verify defaults, bounds, API round trips, persisted-history loading, and runtime trimming for both history types

## [1.5.168] - 2026-08-20

### Added
- **Selectable audio source channels**: choose the backward-compatible Mono mix, Stereo left/right spatial blending, or isolate the Left or Right PCM source channel for Spatial routing
- **Per-user source-channel selectors**: inherit or override Mono/Stereo/Left/Right with validation, safe fallback, live status telemetry, and credential-safe backup/import support
- **Regression coverage**: verify source-channel isolation, symmetric non-Spatial behavior, configuration inheritance, and invalid-mode validation

## [1.5.167] - 2026-08-20

### Added
- **Stereo audio source routing**: optionally preserve left/right PCM energy so Spatial audio playback blends the source channels across physical Hue positions, while Mono remains the backward-compatible default and Uniform/Mirror stay symmetric
- **Per-user source-channel profiles**: inherit or override Mono/Stereo with validation, safe runtime fallback, active status telemetry, and credential-free backup/import support
- **Portable and testable stereo analysis**: carry the source-channel mode through configuration save/load, mapping summaries, backup/export/import, administrator controls, and regression coverage

## [1.5.166] - 2026-08-19

### Added
- **Audio spatial routing**: choose Spatial (backward-compatible position-aware routing), Uniform (one mixed response for every channel), or Mirror (symmetric center-to-edge routing) for Audio and All-media playback
- **Per-user routing profiles**: inherit or override the global mode with validation, safe runtime fallback, active status telemetry, and credential-free backup/import support
- **Portable and testable audio routing**: carry modes through configuration save/load, mapping summaries, backup/export/import, administrator controls, and regression coverage

## [1.5.165] - 2026-08-19

### Added
- **Audio visualizer palettes**: choose Spectrum, low/mid/high Band RGB, Warm, Cool, or Monochrome rendering for Audio and All-media playback while Spectrum preserves the existing default output
- **Per-user palette profiles**: inherit or override the global palette with canonical validation, safe runtime fallback, live active-palette telemetry, and credential-free mapping summaries
- **Portable and testable audio presentation**: carry palettes through configuration save/load, backup/export/import, administrator controls, and regression coverage

## [1.5.164] - 2026-08-19

### Added
- **Audio Beat Pulse**: optionally add a bounded transient brightness response to rising low/mid/high audio energy, so attacks and beats create visible flashes without changing steady loudness behavior
- **Per-user beat response**: inherit or override the global 0-100% pulse setting with clamped runtime resolution and validation
- **Credential-safe telemetry and portability**: expose the active pulse profile and carry it through the configuration API, administrator UI, mapping summaries, backup/import documents, and regression coverage

## [1.5.163] - 2026-08-19

### Added
- **Configurable audio-band spread**: average each low/mid/high center across a bounded 0-100% neighborhood so real-world frequencies between configured centers remain reactive while the default 0% preserves exact-center analysis
- **Per-user spread profiles**: inherit or override the global spread setting with bounded validation and runtime clamping
- **Live telemetry and portability**: expose the effective spread in runtime status and carry it through the configuration API, administrator UI, mapping summaries, backup/import documents, and regression coverage

## [1.5.162] - 2026-08-19

### Added
- **Configurable audio band centers**: tune the low, mid, and high spectral-analysis frequencies from 20-3900 Hz while retaining the 90/420/1600 Hz defaults
- **Per-user audio analysis profiles**: inherit or override band centers and expose the effective values in live status telemetry
- **Portable validation**: carry frequency settings through the configuration API, mapping summaries, backup/import documents, and regression coverage

## [1.5.161] - 2026-08-19

### Added
- **Audio capability diagnostics**: run a bounded, tokenized FFmpeg lavfi sine probe and verify PCM s16le 8 kHz stereo output instead of treating `ffmpeg -version` as proof that audio capture works
- **Audio readiness gating**: block playback readiness only when the active global or per-user playback scopes can start audio and the capture probe fails
- **Credential-safe diagnostics**: expose the capture result and whether it is required through System Diagnostics, the API, and support-bundle data without returning command output or secrets
- **Regression coverage**: verify missing-tool handling, safe capture status propagation, and the diagnostic page's audio-readiness markers

## [1.5.160] - 2026-08-19

### Added
- **Audio visualizer controls**: tune the audio-reactive loudness envelope from 25-400% globally or per user without changing the final brightness policy
- **Live audio telemetry**: show the effective per-session audio sensitivity in the credential-safe runtime status surface
- **Regression coverage**: verify sensitivity inheritance, bounds, API/configuration round-trips, and spatial audio color scaling

## [1.5.159] - 2026-08-19

### Added
- **Audio-reactive playback**: choose Audio-only or All-media playback scopes to decode a bounded PCM window and map low/mid/high spectral energy to spatial Hue colors
- **Safe audio capture**: emit tokenized FFmpeg `s16le` output without shell parsing, with the same bridge leases, light restoration, retries, seek recovery, cancellation, and stall cleanup as video
- **Credential-safe telemetry**: audio sessions reuse existing status/history targets and preserve the default AllVideo behavior for existing installations
- **Regression coverage**: verify audio scope selection, deterministic band analysis/color mapping, and safe FFmpeg audio arguments

## [1.5.158] - 2026-08-19

### Added
- **Aurora scene effect**: add a deterministic drifting green/cyan/blue/violet palette to manual previews, saved scenes, playlists, and scheduled cues while preserving seed intensity and target restoration
- **End-to-end effect compatibility**: accept canonical Aurora values through configuration, API, stream generation, administrator controls, backup/restore metadata, and scheduler telemetry
- **Regression coverage**: verify Aurora phase progression, deterministic frames, configuration normalization, and credential-free API forwarding

## [1.5.157] - 2026-08-19

### Added
- **Multi-room current-light capture**: capture the default bridge, every distinct enabled target, or a deliberate selected target subset from the administrator scene editor
- **Weighted aggregate seeding**: combine successful per-room RGB/brightness samples into a credential-free scene-editor seed while preserving independent target failures and partial-read details
- **Safe batch lifecycle**: serialize multi-target capture with playback and diagnostics, deduplicate inherited physical targets, honor each saved channel profile, and never return light IDs or bridge credentials
- **Regression coverage**: verify multi-target selection, inherited-target deduplication, aggregate color weighting, partial failures, and credential-safe batch results

## [1.5.156] - 2026-08-19

### Added
- **Temperature scene effect**: add a deterministic warm-to-cool white-balance sweep to manual previews, saved scenes, playlists, and scheduled cues while preserving each scene's brightness level
- **End-to-end effect validation**: accept canonical Temperature values through configuration, API, stream generation, and administrator controls while preserving all legacy effects
- **Regression coverage**: verify warm/cool frame progression, deterministic animation, configuration normalization, and credential-safe API forwarding

## [1.5.155] - 2026-08-19

### Added
- **Current-light color capture**: the administrator scene editor can read one selected Hue target's current RGB color and brightness, honoring its saved channel profile, then seed a preview or reusable scene without sending credentials through the browser
- **Color-space conversion**: convert Hue xy chromaticity and mirek color-temperature states into sanitized sRGB samples while retaining average room brightness and off-light behavior
- **Safe capture lifecycle**: current-light capture is cancellation-aware, serialized with playback and other diagnostics, rejects stale channel profiles, and reports partial light-state reads without exposing light IDs or bridge secrets
- **Regression coverage**: verify xy/mirek conversion, averaged/off-light samples, target resolution, lifecycle contention, and credential-safe capture responses

## [1.5.154] - 2026-08-19

### Fixed
- **Early FFmpeg flag validation**: global and per-user custom FFmpeg flags now use the same parser during configuration validation, rejecting malformed quoted values before playback starts
- **Actionable configuration feedback**: unterminated quotes return a clear administrator-facing validation error while preserving safe tokenized process arguments
- **Regression coverage**: verify invalid global and per-user execution profiles fail validation consistently

## [1.5.153] - 2026-08-19

### Fixed
- **Safe FFmpeg process arguments**: build playback commands with one `ArgumentList` token per option so media paths containing spaces or quotes cannot be misparsed
- **Custom FFmpeg flag parsing**: support quoted values and escaped quotes/backslashes without shell interpretation, while rejecting unterminated values before playback starts
- **Cross-platform playback parity**: align FFmpeg process construction with the existing safe OpenSSL and diagnostics launch paths
- **Regression coverage**: verify quoted flags, Windows paths, seek formatting, and malformed custom flags

## [1.5.152] - 2026-08-19

### Fixed
- **Service-level target override normalization**: saved-playlist execution now treats empty or whitespace-only target override lists as omitted, preserving the playlist's persisted target mode for every caller
- **Credential-safe playlist parity**: normalize, trim, and deduplicate direct service overrides consistently with the individual and bulk API endpoints
- **Regression coverage**: verify an empty service override preserves saved playlist target telemetry and execution

## [1.5.151] - 2026-08-19

### Fixed
- **Empty target override normalization**: treat an empty `targetUserIds` array as no selected-target override so explicit all-target playlist previews remain broadcasts and saved targets remain preserved
- **Playlist preview parity**: normalize individual and bulk target-selection requests consistently before validation and credential-free execution
- **Regression coverage**: verify empty target selections cannot suppress enabled mapping fan-out or leak persisted bridge credentials

## [1.5.150] - 2026-08-19

### Added
- **One-off playlist target overrides**: preview an individual saved playlist on its saved target, the default bridge, every enabled target, or a deliberate subset of enabled mappings without changing the playlist definition
- **Credential-free individual playlist previews**: send nullable target-selection metadata while the server resolves persisted credentials, channel profiles, validation, and restorative execution
- **Administrator preview target picker**: add a saved-target/default/all/mapping multi-select beside the individual playlist preview actions and retain explicit all-target broadcast behavior

## [1.5.149] - 2026-08-19

### Added
- **Target-aware bulk playlist previews**: preview selected saved playlists on each playlist's saved target, the default bridge, every enabled target, or a deliberate subset of enabled mappings with optional default-bridge inclusion
- **Credential-free playlist target override**: send only nullable target-selection metadata from the administrator page while the server resolves persisted credentials and channel profiles
- **Administrator bulk target picker**: add an exclusive saved-target/default/all choice and multi-select mapping override beside playlist bulk actions
- **Bulk target regression coverage**: verify selected playlist previews fan out to the default bridge and selected mapping without exposing credentials

## [1.5.148] - 2026-08-19

### Added
- **Raw preview target parity**: run administrator color previews on the default bridge, every enabled target, or a deliberate subset of enabled mappings with optional default-bridge inclusion
- **Credential-free raw preview execution**: selected-target previews resolve persisted credentials and channel profiles server-side and return selected IDs, default inclusion, and per-target outcomes without secrets
- **Administrator target picker**: add a multi-select target control to the raw color preview while retaining explicit all-target broadcast controls

## [1.5.147] - 2026-08-19

### Added
- **Saved-scene preview target parity**: preview individual or bulk saved scenes on the default bridge, every enabled target, or a deliberate subset of enabled mappings with optional default-bridge inclusion
- **Credential-free target telemetry**: expose selected mapping IDs, default-target inclusion, per-target outcomes, and aggregate channel counts without bridge credentials
- **Administrator target selector**: choose saved-scene preview targets from the default/all/mapping multi-select while retaining an explicit all-target broadcast action

## [1.5.146] - 2026-08-19

### Added
- **Persisted saved-playlist targets**: save the global bridge, every enabled mapping, or a deliberate subset of enabled mappings with optional default-bridge inclusion; selected target mode survives playlist CRUD, previews, duplication, scheduled playlist cues, and credential-safe backup/restore
- **Playlist target lifecycle safety**: validate selected mapping IDs atomically and include saved-playlist target references in mapping dependency reports and disable/delete protection
- **Administrator target editor**: use an exclusive All enabled option or a deliberate multi-selection for saved playlists while normal previews preserve each playlist's saved target mode

## [1.5.145] - 2026-08-19

### Added
- **Selected scheduled-cue targets**: choose a credential-free subset of enabled user mappings, optionally including the default bridge, for single-scene and playlist-backed cues
- **Target-safe scheduling telemetry**: expose selected mapping IDs, default-target inclusion, labels, upcoming occurrences, runtime status, history, import/export, and per-target outcomes without returning credentials
- **Atomic target validation and dependencies**: reject duplicate, missing, disabled, over-capacity, and mixed target modes before persistence; mapping disable/delete dependency checks now include selected-target references
- **Administrator target editor**: multi-select default, enabled mappings, or a deliberate subset while keeping All enabled targets exclusive

## [1.5.144] - 2026-08-19

### Fixed
- **Per-cue run serialization**: automatic and manual executions of the same scheduled cue now refuse to overlap, preventing duplicate bridge lifecycles, counters, and history entries
- **Navigation-safe bulk runs**: leaving the administrator page requests cancellation of an in-flight bulk cue sequence so remaining cues do not continue after the page is gone; bridge cleanup still completes normally

## [1.5.143] - 2026-08-19

### Added
- **Atomic bulk scheduled-cue Run Now**: run up to 50 selected cues sequentially through the restorative lifecycle, preflighting every saved-scene or playlist reference and target before bridge activity
- **Per-cue runtime telemetry**: retain credential-free success/failure results, continue after ordinary runtime failures, and stop the remaining sequence safely when cancellation is requested
- **Bulk cancellation**: request cleanup-aware cancellation for every active manually started cue in a selected batch without interrupting bridge restoration
- **Administrator workflow**: add Run Selected and Cancel Selected Runs controls with aggregate progress and per-cue outcome summaries

## [1.5.142] - 2026-08-19

### Added
- **Bulk restorative saved-scene previews**: preview up to 50 selected scenes sequentially on the default target or every enabled target, with state restoration between scenes
- **Bulk restorative playlist previews**: preview up to 50 selected playlists sequentially while preserving each playlist's saved target unless an all-target override is requested
- **Preflight and cancellation safety**: resolve every selected object, validate references and targets before the first bridge call, retain per-item failures, and stop remaining work safely on cancellation
- **Administrator preview controls**: add credential-free aggregate results, per-item status, and cancellable Preview Selected actions beside bulk duplication and deletion

## [1.5.141] - 2026-08-19

### Added
- **Atomic bulk scene duplication**: create independent copies of up to 50 selected saved scenes with bounded unique names and preserved visual metadata
- **Atomic bulk playlist duplication**: create independent copies of up to 50 selected saved-scene playlists with fresh stable IDs and preserved target/repeat metadata
- **Capacity-safe administrator variants**: resolve every selected source before mutation and roll back validation or persistence failures without changing originals or references
- **Administrator multi-selection**: add Duplicate Selected controls for saved scenes and playlists alongside dependency-safe deletion

## [1.5.140] - 2026-08-19

### Added
- **Atomic bulk cue duplication**: create up to 50 disabled scheduled-cue copies with fresh IDs, unique names, reset counters, and cleared Skip Next markers
- **Capacity-safe variants**: resolve every selected cue before mutation and refuse the complete request when the schedule limit, validation, or persistence would be exceeded
- **Administrator multi-selection**: add Duplicate Selected beside the existing bulk cue lifecycle controls while leaving original cues unchanged

## [1.5.139] - 2026-08-19

### Added
- **Atomic bulk mapping enable/disable**: change the sync state of up to 50 selected per-user mappings in one administrator operation
- **Lifecycle-safe mapping state**: scheduled-cue references block disabling, incomplete custom targets block enabling, and persistence failures restore every selected mapping
- **Credential hygiene**: disabling mappings clears stored custom bridge targets just like the single-mapping save workflow, with credential-free result summaries
- **Administrator multi-selection**: add Enable Selected and Disable Selected controls beside mapping cleanup actions

## [1.5.138] - 2026-08-19

### Added
- **Atomic bulk counter reset**: reset and re-enable up to 50 selected scheduled cues while clearing pending Skip Next markers
- **Lifecycle-safe reset**: active cues, missing IDs, and persistence failures block the complete selection and restore all prior state
- **Administrator recovery workflow**: add a Reset Counters action beside the existing bulk scheduled-cue controls while preserving retained cue history

## [1.5.137] - 2026-08-19

### Added
- **Atomic bulk mapping deletion**: remove up to 50 selected per-user bridge mappings by user ID in one administrator operation
- **Dependency-safe mapping cleanup**: scheduled-cue references, missing user IDs, and persistence failures block the complete selection and leave every mapping unchanged
- **Administrator multi-selection**: add Select All, Clear Selection, and Delete Selected mapping controls with credential-free results and refresh

## [1.5.136] - 2026-08-19

### Added
- **Atomic bulk scene deletion**: remove up to 50 selected saved scenes by normalized name in one administrator operation
- **Dependency-safe cleanup**: playlist references, direct cues, playlist-backed cues, missing names, and persistence failures block the complete selection and leave every scene unchanged
- **Administrator multi-selection**: add Select All, Clear Selection, and Delete Selected scene controls with credential-free refresh of playlists and scheduled cues

## [1.5.135] - 2026-08-19

### Added
- **Atomic bulk playlist deletion**: remove up to 50 selected saved-scene playlists by stable ID in one administrator operation
- **Dependency-safe cleanup**: scheduled-cue references, missing IDs, and persistence failures block the complete selection and leave every playlist unchanged
- **Administrator multi-selection**: add Select All, Clear Selection, and Delete Selected controls with credential-free refresh of playlists and dependent cues

## [1.5.134] - 2026-08-19

### Added
- **Credential-safe import diff**: Validate Import now reports normalized added, removed, changed, and unchanged counts for mappings, saved scenes, playlists, and scheduled cues before replacement
- **Migration key visibility**: show global settings and App Key/Client Key change state as booleans without returning any secret values
- **Administrator change summary**: render the server-calculated import impact in the Backup and Restore wizard before review and import

## [1.5.133] - 2026-08-19

### Added
- **Atomic bulk cue deletion**: remove up to 50 selected scheduled cues in one administrator operation while preserving retained cue history
- **Active-lifecycle protection**: a running cue, missing ID, or persistence failure blocks the complete deletion set and restores the previous collection
- **Administrator cleanup workflow**: add a confirmed Delete Selected action beside the existing bulk enable, disable, and skip controls

## [1.5.132] - 2026-08-19

### Added
- **Atomic bulk Skip Next controls**: mark or clear the next automatic occurrence for up to 50 selected scheduled cues without changing recurrence definitions, targets, scenes, or finite-run counters
- **All-or-nothing safeguards**: active, disabled, exhausted, futureless, missing, or persistence-blocked cues leave the complete selected set unchanged
- **Administrator multi-action workflow**: add explicit Skip Next Selected and Clear Selected Skips actions alongside bulk enable/disable controls

## [1.5.131] - 2026-08-19

### Added
- **Spreadsheet-ready telemetry**: add credential-free CSV exports for upcoming cue occurrences, duration-aware conflicts, scheduled-cue history, and completed playback-session history
- **Filter continuity**: CSV downloads preserve the active cue, outcome, and 7/31/90/366-day report-horizon filters used by the administrator JSON views
- **CSV safety**: emit UTF-8 CSV with deterministic invariant formatting, explicit local/UTC columns, RFC-style quoting, and spreadsheet formula-marker protection for labels

## [1.5.130] - 2026-08-19

### Added
- **Atomic bulk cue administration**: select and enable or disable up to 50 scheduled cues in one operation without changing timing, targets, scenes, or finite-run counters
- **All-or-nothing safety**: active cues, exhausted finite cues, missing IDs, and persistence failures leave the entire selected set unchanged
- **Administrator multi-selection**: add select-all and clear-selection controls with credential-free status refresh after each bulk action

## [1.5.129] - 2026-08-19

### Added
- **Per-user playback media scope**: let each user mapping inherit the global all-video/movies/episodes/other-video policy or override it for that user's room/profile
- **Consistent lifecycle safety**: apply the effective per-user scope to new and recovered starts while preserving progress and stop cleanup for active sessions
- **Credential-free observability**: include the override in mapping summaries, backup/restore documents, and effective active-session status without exposing bridge secrets

## [1.5.128] - 2026-08-19

### Added
- **Playback media scope**: choose whether Hue Sync starts for all video items, movies only, TV episodes only, or other video such as music and home videos
- **Safe scope changes**: changing the scope leaves existing playback lifecycle cleanup intact while filtering only new and recovered sync starts
- **Visible policy telemetry**: the selected media scope is included in the configuration API, Live Sync Status, diagnostics, and credential-safe configuration exports

## [1.5.127] - 2026-08-19

### Added
- **Credential-safe support bundle**: add `GET /HueSync/Diagnostics/SupportBundle` and an administrator download action that combines local prerequisites, saved-target validation, runtime telemetry, playback and scheduled-cue history, scheduler status, and redacted configuration metadata into one reviewable JSON document
- **Cancellation-aware collection**: the support bundle reuses the administrator diagnostics cancellation lifecycle while target checks run, and clearly warns that private labels and media metadata may remain even though bridge credentials and playback tokens are omitted

## [1.5.126] - 2026-08-19

### Added
- **Configurable schedule report horizon**: let administrators inspect and export the next 7, 31, 90, or 366 days of occurrence and conflict telemetry instead of being limited to the default 31-day window
- **Consistent report scope**: apply the selected horizon to the on-screen tables, occurrence/conflict JSON downloads, and iCalendar export while retaining server-side bounds

## [1.5.125] - 2026-08-19

### Added
- **Exportable schedule diagnostics**: download credential-free JSON reports for upcoming cue occurrences and duration-aware schedule conflicts using the active cue filter
- **Troubleshooting-ready telemetry**: keep the same server-calculated time-zone, recurrence, target, priority, duration, and ordering data from the administrator tables in the downloaded reports

## [1.5.124] - 2026-08-19

### Added
- **Cue-scoped upcoming views**: add an administrator cue selector that filters the 31-day occurrence preview and iCalendar download through the existing credential-free `scheduleId` API contract
- **Cue-scoped history views**: combine stable cue selection with outcome filtering for retained history and JSON export, with disabled cues clearly labeled and no bridge data exposed
- **Regression coverage**: extend configuration-page validation to require the new selectors and schedule-filter query wiring

## [1.5.123] - 2026-08-19

### Added
- **Configuration import preflight**: add a credential-safe `POST /HueSync/Configuration/ValidateImport` path that applies the same normalization, dependency, and full configuration checks as atomic import without mutating the live server
- **Migration readiness report**: show planned object totals, matching-key preservation, validation errors, and active-playback blocking state before an administrator confirms a backup restore
- **Administrator validation action**: add a **Validate Import** button to the Backup and Restore wizard so malformed or incomplete documents can be corrected before replacement

## [1.5.122] - 2026-08-19

### Added
- **Outcome-filtered scheduled-cue history**: filter retained cue runs by Succeeded, Failed, Skipped, or Recovered through the history API and credential-free JSON export
- **Focused history monitor**: add an administrator outcome selector so failed or recovered cue runs can be inspected without scanning unrelated telemetry

## [1.5.121] - 2026-08-19

### Fixed
- **Cue-scoped conflict diagnostics**: make the documented `scheduleId` filter return collisions involving the selected cue while retaining the opposing cue's timing and ordering context

### Added
- **Focused conflict monitor**: add an administrator selector for inspecting all overlaps or only those involving one enabled scheduled cue

## [1.5.120] - 2026-08-19

### Added
- **Scheduled-cue conflict diagnostics**: add a bounded, credential-free report and administrator monitor for upcoming cue execution windows that overlap after effective scene or playlist durations are applied
- **Time-zone-aware ordering guidance**: conflict results include both UTC and cue-local instants, overlap duration, target labels, priorities, and the scheduler's serialized execution guidance

### Security
- Conflict analysis reuses local recurrence and duration calculations only; it never contacts Hue bridges or serializes bridge credentials

## [1.5.119] - 2026-08-19

### Reliability
- **Fail-safe scheduled skips**: a cue never runs when clearing its pending Skip Next marker cannot be persisted; the marker remains available for a later retry
- **One-time completion rollback**: a one-time cue's in-memory enabled state is restored when its completed-state persistence fails

### Security
- Scheduler persistence failures remain bounded to server logs and cannot turn an administrator skip request into an unintended bridge preview

## [1.5.118] - 2026-08-19

### Reliability
- **Transactional global settings**: failed administrator configuration saves now restore every changed setting, including retained session and scheduled-cue history

### Security
- Global configuration persistence failures return a bounded administrator message while serializer and filesystem details remain only in server logs

## [1.5.117] - 2026-08-19

### Reliability
- **Transactional saved-scene updates**: failed scene saves now restore the previous in-memory collection and return a sanitized 500 response
- **Transactional scheduled-cue lifecycle**: failed cue saves, deletes, and direct run-counter resets now roll back state instead of rethrowing persistence exceptions

### Security
- Persistence failures expose only bounded administrator messages; raw serializer and filesystem exception details remain in the server log

## [1.5.116] - 2026-08-19

### Added
- **Per-user mapping dependency audit**: inspect every scheduled cue that targets a user mapping through `GET /HueSync/UserMappings/{userId}/Dependencies`, including cue IDs, names, and enabled state
- **Administrator mapping reference inspection**: add View Cue References to each per-user mapping so disabling or deleting a referenced mapping is explainable before an action is attempted

### Security
- Mapping dependency results remain credential-free and expose only bounded user and cue labels/counts; bridge addresses, app keys, client keys, playback tokens, and target details remain server-side

### Reliability
- **Transactional mapping lifecycle**: roll back in-memory mapping changes and return sanitized 500 responses when mapping save or deletion persistence fails

## [1.5.115] - 2026-08-19

### Added
- **Saved-playlist dependency audit**: inspect every scheduled cue that references a playlist through `GET /HueSync/ScenePlaylists/{name}/Dependencies`, including cue IDs, names, and enabled state
- **Administrator cue-reference inspection**: add View Cue References to the Saved Scene Playlists editor so dependency-protected deletion is explainable before an action is attempted

### Security
- Playlist dependency results remain credential-free and expose only bounded playlist and cue labels/counts; bridge addresses, app keys, client keys, playback tokens, and target details remain server-side

## [1.5.114] - 2026-08-19

### Added
- **Complete saved-scene dependency graph**: show scheduled cues that reach a scene through a dependent playlist, including direct-versus-playlist reference type and the playlist name
- **Transactional saved-scene deletion**: roll back the in-memory scene collection and return a sanitized 500 response if persistence fails

### Security
- Dependency results remain credential-free and expose only bounded scene, playlist, and cue labels/counts; bridge addresses, app keys, client keys, playback tokens, and target details remain server-side

## [1.5.113] - 2026-08-19

### Added
- **Saved-scene dependency audit**: inspect dependent playlists, repeated scene-step counts, and direct scheduled cues before changing or deleting a scene through `GET /HueSync/ColorPresets/{name}/Dependencies`
- **Administrator reference inspection**: add View References to the Saved Scene editor so dependency-protected deletion is explainable before an action is attempted

### Security
- Dependency results contain only saved-scene, playlist, and cue labels/counts plus enabled state; bridge addresses, app keys, client keys, playback tokens, and private target details remain server-side

## [1.5.112] - 2026-08-19

### Added
- **Reference-safe saved-scene rename**: preserve all visual/effect metadata while atomically migrating matching saved-playlist and direct scheduled-cue references to the new name
- **Administrator rename workflow**: enter a replacement scene name and use Rename Scene so existing automation follows the scene instead of being left behind by a save-as action

### Security
- Rename results and dependency migration contain only bounded scene names and counts; bridge addresses, app keys, client keys, playback tokens, and private user data remain server-side

## [1.5.111] - 2026-08-19

### Added
- **Reference-safe saved-scene deletion**: reject deletion while a saved playlist or scheduled cue still references the scene
- **Dependency-aware administrator feedback**: return sanitized counts for dependent playlists and scheduled cues so administrators know exactly what must be changed first

### Security
- Dependency results contain only counts and never expose bridge addresses, app keys, client keys, playback tokens, or private user data

## [1.5.110] - 2026-08-19

### Added
- **Reference-safe playlist lifecycle**: renaming an existing saved playlist now migrates every scheduled cue that references its old name in the same atomic configuration save
- **Dependency-protected deletion**: deleting a playlist referenced by one or more scheduled cues returns a conflict with the dependent cue count, preserving a runnable configuration until those cues are changed or removed
- **Migration-safe backup/restore**: configuration imports use the playlist's stable ID to migrate retained or imported cue references when a playlist name changes
- **Administrator refresh workflow**: playlist saves refresh the scheduled-cue editor so renamed playlist references and labels are immediately visible

### Security
- Playlist rename migration changes only credential-free scene names; bridge addresses, app keys, client keys, and playback tokens remain server-side and are never returned by the dependency checks

## [1.5.109] - 2026-08-19

### Added
- **Repeatable saved playlists**: repeat an ordered saved-scene sequence up to 10 passes without duplicating playlist steps; legacy playlists continue to run once
- **Bounded repeat safety**: enforce the existing 10-minute aggregate playlist duration cap across every pass before a playlist can be saved, imported, scheduled, or previewed
- **Repeat-aware telemetry and portability**: expose pass count and expanded step outcomes through playlist CRUD, previews, scheduled-cue status/occurrences/iCalendar metadata, retained history, and credential-safe backup/restore
- **Administrator workflow**: configure and review playlist passes directly in the Saved Scene Playlists editor

### Security
- Repeat metadata contains only bounded scene references and a numeric pass count; bridge addresses, app keys, client keys, and playback tokens remain server-side and absent from playlist telemetry and backups

## [1.5.108] - 2026-08-19

### Added
- **Scheduled playlist cues**: schedule either one saved scene or an ordered saved-scene playlist with the same time-zone, recurrence, date-window, priority, finite-run, skip, cancellation, restoration, status, occurrence, calendar, history, backup, and restore behavior
- **Playlist run telemetry**: expose credential-free playlist identity, total duration, target aggregates, and ordered per-scene step outcomes for Run Now and automatic executions
- **Administrator workflow**: select saved scenes or playlists directly in the scheduled-cue editor, with playlist duration overrides disabled because each scene retains its saved hold duration

## [1.5.107] - 2026-08-19

### Added
- **Saved scene playlists**: compose up to 20 existing saved scenes into an ordered, reusable playlist with credential-free CRUD, bounded names, and duplicate/delete administrator actions
- **Sequential playlist previews**: play every scene in order through the restorative lifecycle, with default-target, selected-mapping, and all-enabled-target modes, per-step telemetry, aggregate target outcomes, channel counts, and cleanup warnings
- **Portable playlist definitions**: include playlist order, target mode, and saved-scene references in credential-safe configuration export/import while preserving server-side bridge credentials

### Security
- Playlist requests contain only saved-scene names and target selectors; bridge addresses, app keys, client keys, and playback tokens remain server-side and are absent from playlist results and import/export telemetry
- Playlist references and all enabled targets are preflight-validated before the first bridge call, and each step remains cancellable and state-restoring

## [1.5.106] - 2026-08-19

### Added
- **Saved-scene preview endpoint**: preview a persisted scene by name through `POST /HueSync/ColorPresets/{name}/Preview` against the default target, a selected enabled mapping, or every distinct enabled target
- **Saved-scene administrator actions**: add default-target and all-enabled-target buttons beside the saved-scene selector, with sequential per-target status for broadcast previews
- **Consistent preview telemetry**: return the saved effect, animation speed, RGB/brightness, effective duration and fades, target outcomes, channel counts, and cleanup warnings through the same sanitized result shape as immediate previews

### Security
- Saved-scene previews resolve bridge credentials and channel profiles only from server-side configuration; the browser sends a scene name and target mode, and no app keys, client keys, bridge addresses, or playback tokens enter the result
- Invalid saved scenes, incomplete enabled targets, conflicting target selections, and active playback are rejected before a preview starts

## [1.5.105] - 2026-08-19

### Added
- **Saved channel-profile diagnostics**: Validate each default, inherited, and custom target's effective channel profile against the channel IDs currently returned by its entertainment area
- **Preflight telemetry**: Expose selected-channel counts, profile validity, and missing IDs through `GET /HueSync/TargetDiagnostics` and the administrator diagnostics table

### Security
- Diagnostics remain non-mutating and credential-free; profile validation rejects malformed or stale IDs before playback or scene-preview lifecycles can change bridge state

## [1.5.104] - 2026-08-19

### Added
- **Immediate all-target previews**: add an administrator action and `targetAllEnabledMappings` preview mode that runs the selected effect sequentially on the valid global target and every distinct enabled custom mapping
- **Per-target preview telemetry**: return credential-free target labels, channel counts, success/failure messages, and cleanup warnings while preserving the existing single-target preview contract

### Security
- All-target previews resolve bridge credentials and channel profiles only from server-side configuration, reject incomplete enabled targets before starting, and never serialize app keys, client keys, bridge addresses, or playback tokens

## [1.5.103] - 2026-08-19

### Added
- **All-enabled-target scheduled cues**: add a credential-free broadcast target mode that runs one saved scene sequentially on the default bridge and every distinct enabled user-mapped target
- **Per-target telemetry**: expose sanitized success/failure and cleanup outcomes for each target through immediate runs, status, history, and persisted history
- **Portable target mode**: preserve the broadcast setting through schedule CRUD, duplication, upcoming occurrences, calendar metadata, and credential-safe backup/restore

### Security
- Broadcast schedules contain only a boolean mode flag; bridge addresses, app keys, client keys, and playback tokens remain server-side
- Target execution is sequential and restorative, continues to report independent target failures, and rejects incomplete enabled targets before starting

## [1.5.102] - 2026-08-19

### Added
- **Saved-scene duplication**: add a credential-free administrator action and `POST /HueSync/ColorPresets/{name}/Duplicate` endpoint for creating editable variants without re-entering visual settings
- **Safe scene copies**: preserve effect, animation speed, RGB color, brightness, duration, and fade transitions while assigning a bounded unique name and leaving the source scene and its scheduled cues unchanged

### Security
- Duplicate requests are administrator-authorized, validate the complete resulting preset collection before persistence, roll back failed saves, and never expose bridge credentials or playback tokens

## [1.5.101] - 2026-08-19

### Added
- **Deterministic scheduled-cue priorities**: add a bounded 0-100 per-cue priority so higher-priority automatic cues run first when multiple cues are due together, while equal priorities retain saved configuration order
- **Priority-aware administration**: expose priority through credential-free CRUD, backup/restore, status, occurrence previews, duplication, and the scheduled-cue editor

### Security
- Priority values are validated server-side, default to zero for existing configurations, and never expose bridge credentials or playback tokens

## [1.5.100] - 2026-08-19

### Added
- **Missed-cue recovery**: add an opt-in 0-120 minute global recovery window for the most recent automatic occurrence missed during a short Jellyfin restart or outage; older missed occurrences are not replayed in a burst
- **Recovery telemetry**: expose recovered runs, including recovered skips, through scheduler status and sanitized history while preserving recurrence, run limits, one-time disable behavior, and manual Run Now semantics

### Security
- Recovery is bounded, administrator-configurable, credential-free, and uses the existing serialized restorative preview lifecycle; no bridge credentials or playback tokens enter telemetry or backups

## [1.5.99] - 2026-08-19

### Added
- **Skip Next Cue**: mark exactly one upcoming automatic scheduled-scene occurrence to be skipped without changing the cue's recurrence, target, saved scene, or finite limit
- **Reversible administrator control**: clear a pending skip before it is due; upcoming previews, runtime status, configuration backup/restore, and sanitized history expose the pending/consumed state

### Security
- Skip transitions remain administrator-authorized, reject active/disabled/exhausted/futureless cues, persist atomically with rollback, never affect manual Run Now, and expose no bridge credentials

## [1.5.98] - 2026-08-19

### Added
- **Per-cue enable/disable control**: add a credential-free `Enable/Disable Cue` administrator action and `POST /HueSync/SceneSchedules/{id}/Enabled` endpoint that changes only the selected cue's active state
- **Guarded automation lifecycle**: refuse state changes while a cue is running and require an explicit run-counter reset before re-enabling an exhausted finite cue

### Security
- Enabled-state changes remain administrator-authorized, validate finite limits, roll back failed persistence, and expose no bridge credentials or playback tokens

## [1.5.97] - 2026-08-19

### Added
- **Safe scheduled-cue duplication**: add a credential-free `Duplicate Cue` administrator action and `POST /HueSync/SceneSchedules/{id}/Duplicate` endpoint for creating variations without re-entering timing, target, recurrence, effect, or finite-limit metadata
- **Fresh copy lifecycle**: duplicated cues receive a new stable ID, a unique bounded name, a reset execution counter, and disabled state so administrators can edit and enable them deliberately

### Security
- Cue duplication is administrator-authorized, validates the complete resulting configuration before persistence, and never copies or returns bridge credentials or playback tokens

## [1.5.96] - 2026-08-19

### Added
- **Resettable finite cues**: add a credential-free `Reset Run Counter` administrator action and `POST /HueSync/SceneSchedules/{id}/ResetRunCount` endpoint that clears an execution limit and re-enables the cue
- **Safe reset lifecycle**: refuse resets while a cue is active, preserve retained run history as an audit trail, and clear live last-run telemetry for a fresh execution window

### Security
- Counter resets are administrator-authorized, serialized against active bridge operations, persisted atomically, and expose no bridge credentials or playback tokens

## [1.5.95] - 2026-08-19

### Added
- **Finite scheduled-cue execution limits**: stop recurring scene cues after a bounded 1-365 execution count, or leave the default `0` for unlimited runs
- **Restart-safe run counters**: persist finite-cue execution counts in the credential-free schedule definition and automatically disable a cue when its limit is reached
- **Limit-aware observability and backup**: expose maximum, current, and remaining runs through CRUD/status responses and preserve them through configuration export/import
- **Administrator controls**: configure the execution limit in the scheduled-cue editor and inspect `current / maximum` counters in the scheduler monitor

### Security
- Execution limits and counters are bounded and validated at every configuration/API boundary; backup and telemetry surfaces continue to omit bridge credentials and playback tokens

## [1.5.94] - 2026-08-19

### Added
- **Configurable animated speed**: set a bounded 25-400% rate for Pulse, Rainbow, and Candle previews and saved scenes while Solid remains unchanged
- **Portable speed metadata**: preserve the selected rate through scene CRUD, scheduled execution, status, upcoming occurrences, iCalendar, history, and credential-safe backup/restore
- **Speed-aware administrator surfaces**: edit, apply, and inspect effect speed in the preview controls, scheduled-cue lists, runtime telemetry, upcoming runs, and retained history

### Security
- Effect speed is bounded and validated at every API/configuration boundary; animated frames continue through the serialized, cancellable DTLS lifecycle with captured-light restoration

## [1.5.93] - 2026-08-19

### Added
- **Candle scene effect**: add a deterministic warm flicker effect alongside Solid, Pulse, and Rainbow for manual previews and reusable scenes
- **Portable Candle metadata**: preserve the new effect through scheduled cues, occurrence and iCalendar exports, history, and credential-safe backup/restore
- **Effect-aware editor copy**: expose Candle in the administrator selector and validation while retaining Solid compatibility for legacy scenes

### Security
- Candle frames remain bounded and credential-free, use the existing serialized DTLS lifecycle and cancellation path, and restore captured light state on completion or cancellation

## [1.5.92] - 2026-08-18

### Added
- **Saved-scene effects**: add bounded Solid, breathing Pulse, and hue-cycling Rainbow effects to manual previews and reusable scenes
- **Scheduled effect playback**: scheduled cues, scheduler status, upcoming occurrences, iCalendar exports, history, and credential-safe backups preserve the selected effect while retaining the restorative capture/restore lifecycle
- **Effect-aware administrator controls**: select an effect, save it with a scene, apply it back to the editor, and preview it on either the default or current mapping target

### Security
- Effects remain credential-free visual metadata; all animated frames use the existing serialized DTLS lifecycle, cancellation path, bounded durations, and captured-light restoration

## [1.5.91] - 2026-08-18

### Added
- **Cancellable system diagnostics**: expose a shared Cancel Active Diagnostics action while FFmpeg/OpenSSL prerequisite checks or saved-target validation are running
- **Diagnostic page-exit cleanup**: leaving the administrator page requests cancellation for in-flight non-mutating diagnostics, preserving the normal request and process cleanup path

### Security
- Diagnostic cancellation returns only a bounded status/count result; the existing prerequisite and target reports remain credential-free and never mutate Hue bridge state

## [1.5.90] - 2026-08-18

### Added
- **Cancellable connection diagnostics**: expose the shared Cancel Active Diagnostic action while default and per-user mapping Test Connection probes are running, including clear cancellation, completion, and failure status feedback
- **Page-exit diagnostic cleanup**: leaving the administrator page now requests cancellation for an in-flight Test Connection probe just as it does for previews and manual scene cues

### Security
- Test Connection cancellation continues to use the credential-free `POST /HueSync/Preview/Cancel` response and the restorative bridge lifecycle; no bridge credentials or playback tokens are exposed to the administrator page

## [1.5.89] - 2026-08-18

### Added
- **Cancellable administrator previews**: expose a visible Cancel Active Preview action for solid-color and channel-mapping previews, including automatic cancellation when the configuration page is left
- **Cancellable manual scene cues**: expose a visible Cancel Running Cue action and a sanitized `POST /HueSync/SceneSchedules/{id}/Cancel` endpoint for long Run Now operations
- **Restorative cancellation lifecycle**: cancellation flows through the linked request token so entertainment areas are deactivated and captured light states are restored before a preview or cue ends

### Security
- Cancellation endpoints return only bounded status and message DTOs; bridge credentials, connection details, and playback tokens remain excluded from administrator responses

## [1.5.88] - 2026-08-18

### Added
- **Saved-scene fade-out transitions**: optionally ramp each solid-color scene from its target RGB16 values back to dark at the end of the configured duration, alongside the existing fade-in support
- **Bookended preview lifecycle**: validate that fade-in and fade-out together fit inside the preview, hold duration, or saved-scene duration while preserving state capture, cancellation, and restoration safety
- **Portable fade metadata**: carry `transitionOutSeconds` through preview requests/results, saved-scene CRUD, effective scheduler status, upcoming occurrences, iCalendar export, and credential-safe backup/restore

### Security
- Fade-in and fade-out durations are bounded integers whose combined duration cannot exceed the scene or effective cue duration; credential-free scene, schedule, occurrence, calendar, and backup responses continue to omit bridge credentials and playback tokens

## [1.5.87] - 2026-08-18

### Added
- **Saved-scene fade-in transitions**: optionally ramp each solid-color scene from dark to its target RGB16 values over 0-30 seconds within the configured scene duration
- **Portable transition metadata**: carry `transitionSeconds` through preview requests/results, saved-scene CRUD, scheduler status, upcoming occurrences, iCalendar export, and credential-safe backup/restore
- **Schedule-aware fade timing**: scheduled cues inherit the saved scene's fade and clamp it to a shorter per-cue duration when necessary; the administrator editor and runtime tables show the effective fade

### Security
- Fade durations are bounded integers that cannot exceed the scene or effective cue duration; credential-free scene, schedule, occurrence, calendar, and backup responses continue to omit bridge credentials and playback tokens

## [1.5.86] - 2026-08-18

### Added
- **Bounded scheduled-cue intervals**: run daily, weekly, monthly, monthly-weekday, or yearly cues every N calendar units, from 1 through 365
- **Deterministic cadence anchors**: intervals greater than one use the recurring cue's `startDate` as the portable day, week, month, or year anchor while existing windows, exclusions, timezone, DST, and short-month rules continue to apply
- **Portable interval metadata**: carry `recurrenceInterval` through the administrator editor, CRUD, readiness/status telemetry, upcoming previews, iCalendar output, and credential-safe backup/restore

### Security
- Recurrence intervals are bounded integers and credential-free responses continue to exclude bridge credentials, connection details, and playback tokens

## [1.5.85] - 2026-08-18

### Added
- **Yearly scheduled cues**: run a saved scene on a selected month and calendar day each year for birthdays, anniversaries, and holidays, with short-month clamping
- **Portable yearly recurrence**: carry `monthOfYear` and yearly date rules through cue CRUD, readiness/status telemetry, upcoming previews, iCalendar output, and credential-safe backup/restore
- **Administrator yearly editor**: choose the month and day directly while existing daily, weekly, monthly-day, monthly-weekday, and one-time cues remain compatible

### Security
- Yearly recurrence metadata is limited to bounded month/day integers; bridge credentials, connection details, and playback tokens remain excluded from cue/status/calendar/backup responses

## [1.5.84] - 2026-08-18

### Added
- **Monthly weekday scheduled cues**: run a saved scene on the first through fifth or last matching weekday of each month, such as first Monday or last Friday
- **Portable ordinal-weekday recurrence**: carry `weekOfMonth` and `dayOfWeek` through cue CRUD, readiness/status telemetry, upcoming previews, iCalendar output, and credential-safe backup/restore
- **Administrator monthly-weekday editor**: choose the ordinal and weekday directly while existing daily, weekly, monthly-day, and one-time cues remain compatible

### Security
- Monthly-weekday metadata is limited to bounded ordinal and weekday integers; bridge credentials, connection details, and playback tokens remain excluded from cue/status/calendar/backup responses

## [1.5.83] - 2026-08-18

### Added
- **Daily scheduled cues**: run a saved scene every calendar day at a selected local time, with date windows and exclusions
- **Portable daily recurrence**: carry the new recurrence mode through cue CRUD, readiness/status telemetry, upcoming previews, iCalendar output, and credential-safe backup/restore
- **Administrator daily editor**: add a daily recurrence choice that correctly ignores weekday and day-of-month controls

### Security
- Daily recurrence metadata is a bounded mode only; bridge credentials, connection details, and playback tokens remain excluded from cue/status/calendar/backup responses

## [1.5.82] - 2026-08-18

### Added
- **Monthly scheduled cues**: run a saved scene on a selected calendar day each month alongside the existing weekly weekday mode
- **Short-month handling**: monthly days beyond a month's length run on that month's final calendar day, so day 31 remains useful year-round
- **Portable recurrence controls**: carry recurrence mode and day-of-month through cue CRUD, readiness/status telemetry, upcoming previews, iCalendar output, and credential-safe backup/restore

### Security
- Recurrence metadata is limited to bounded mode/day values; bridge credentials, connection details, and playback tokens remain excluded from cue/status/calendar/backup responses

## [1.5.81] - 2026-08-18

### Added
- **Per-cue hold duration overrides**: schedule one saved scene at different event lengths without duplicating the scene; blank or `0` inherits the saved scene's duration, while `1-30` seconds applies only to that cue
- **Portable duration metadata**: carry the override through cue CRUD, effective status and occurrence previews, iCalendar event lengths, and credential-safe backup/restore
- **Administrator duration controls**: edit, preview, and monitor each cue's effective hold duration directly from the scheduler page

### Security
- Duration metadata is a bounded integer only; bridge credentials, connection details, and playback tokens remain excluded from cue/status/calendar/backup responses

## [1.5.80] - 2026-08-18

### Added
- **One-time scheduled cues**: schedule a saved scene for one exact calendar date in the cue's selected time zone without manufacturing a weekday mask or date window; successful automatic runs disable the cue so restarts cannot repeat it
- **Portable one-time automation**: carry one-time dates through cue CRUD, readiness/status telemetry, occurrence previews, iCalendar downloads, and credential-safe backup/restore
- **Administrator one-time editor**: add a one-time date control that disables conflicting recurring date rules and weekday selection

### Security
- One-time cue metadata contains only a bounded calendar date and existing credential-free scene/target labels; bridge credentials, connection details, and playback tokens remain excluded

## [1.5.79] - 2026-08-18

### Added
- **Calendar interoperability**: export the bounded upcoming scheduled-cue preview as an RFC 5545 iCalendar feed with UTC event times and cue timezone metadata
- **Administrator calendar action**: add a one-click `.ics` download beside the upcoming-occurrence table

### Security
- Calendar events contain only cue names, saved-scene names, target labels, timezone metadata, and timestamps; bridge credentials and connection details remain excluded

## [1.5.78] - 2026-08-18

### Added
- **Global scheduled-automation pause**: pause or resume all recurring scene cues without deleting or editing individual schedules
- **Pause-aware scheduler status**: expose the automation toggle in credential-free status telemetry and the administrator summary
- **Manual override clarity**: keep the individual Run Now action available while recurring automation is paused

### Security
- The pause control contains only a boolean scheduling preference; bridge credentials, connection details, and playback tokens remain excluded

## [1.5.77] - 2026-08-17

### Added
- **Upcoming cue preview**: calculate a bounded list of future occurrences using each cue's timezone, DST behavior, weekday mask, date window, and exclusions
- **Credential-free occurrence API**: add `GET /HueSync/SceneSchedules/Occurrences` with bounded horizon, limit, and cue filtering controls
- **Administrator calendar visibility**: show the next 31 days of upcoming cue-local and UTC occurrences beside scheduler readiness and history

### Security
- Occurrence previews contain only cue names, saved-scene names, target labels, timezone metadata, and timestamps; bridge credentials and connection details remain excluded

## [1.5.76] - 2026-08-17

### Added
- **Scheduled cue exclusions**: optionally skip up to 100 specific calendar dates inside a recurring cue's date window for holidays, maintenance, or room blackout days
- **Timezone-local exclusion runtime**: due checks and next-run calculations compare exclusions against the cue's selected timezone while preserving inclusive date-window behavior
- **Portable exclusion controls**: expose normalized excluded dates through schedule CRUD, credential-safe backup/restore, readiness/status telemetry, and the administrator editor

### Security
- Exclusion metadata contains only bounded calendar dates; bridge credentials, connection details, and playback tokens remain excluded from schedule/status responses

## [1.5.75] - 2026-08-17

### Added
- **Bounded scheduled cues**: optionally set inclusive start and end calendar dates for each recurring scene cue; blank dates preserve the existing ongoing behavior
- **Date-window-aware runtime**: evaluate due checks, next runs, readiness, status, backup/restore, and API round-trips against calendar dates in the cue's selected time zone
- **Administrator date controls**: add date inputs and visible date ranges to the scheduled-cue editor, selector, and runtime monitor

### Security
- Date-window metadata contains only calendar dates and schedule labels; bridge credentials, connection details, and playback tokens remain excluded from schedule/status responses

## [1.5.74] - 2026-08-17

### Added
- **Per-cue time zones**: recurring scene cues can use any time zone installed on the Jellyfin host while existing blank values continue to use the server's local zone
- **DST-aware scheduling**: due checks and next-run calculations convert through the selected zone, skip nonexistent spring-forward wall-clock times, and expose both cue-local and UTC next-run values
- **Time-zone catalog API and UI**: add `GET /HueSync/SceneSchedules/TimeZones` and a validated administrator selector for portable schedule setup

### Security
- Time-zone metadata is limited to system zone IDs, display names, and offsets; credentials and bridge connection details remain excluded from schedule/status responses

## [1.5.73] - 2026-08-17

### Added
- **Persistent scheduled-cue history**: optionally retain the newest 100 sanitized cue runs across Jellyfin restarts and hydrate each cue's last outcome and run count on startup
- **Cue history operations**: add credential-free `GET /HueSync/SceneSchedules/History`, JSON export, clear-history control, and a configuration-page history table with per-run results

### Security
- Cue retention is opt-in and stores only cue names, saved-scene names, target labels, outcomes, messages, cleanup warnings, and timestamps; bridge credentials, connection details, and playback tokens remain excluded

## [1.5.72] - 2026-08-17

### Added
- **Scheduled cue readiness diagnostics**: report whether each enabled cue can run with the current saved scene, mapping, bridge target, time, day mask, credentials, area, and channel profile before its next occurrence
- **Preflight visibility in the scheduler monitor**: show ready/not-ready state and a sanitized reason alongside next-run and last-run telemetry so configuration errors can be corrected before a cue fires

### Security
- Readiness checks are local and credential-free in their output; App Keys, Client Keys, bridge addresses, area IDs, channel profiles, and playback tokens remain server-side

## [1.5.71] - 2026-08-17

### Added
- **Scheduled cue observability**: expose each cue's next server-local occurrence, active-run state, run count, last outcome, result message, and cleanup warning through `GET /HueSync/SceneSchedules/Status`
- **Configuration-page scheduler monitor**: add a refreshable status table showing target, selected days/time, next run, last run, and execution results; status refreshes alongside Live Sync Status while the page is open
- **Resilient background execution**: unexpected bridge or scheduler exceptions are converted into sanitized cue failures so one network error does not terminate the hosted automation loop

### Security
- Automation status remains credential-free: it reports only saved scene names, mapping labels, schedule timing, and sanitized execution telemetry; bridge App Keys, Client Keys, and playback tokens never enter runtime status

## [1.5.70] - 2026-08-17

### Added
- **Scheduled scene cues**: run saved color scenes automatically on selected days and server-local times, with a one-click Run Now action for immediate testing
- **Target-aware scene automation**: cues can address the global Hue target or any enabled user mapping while resolving current credentials only on the server at execution time
- **Safe scheduler lifecycle**: scheduled cues reuse the serialized activate/DTLS/send/deactivate/restore preview lifecycle, skip duplicate polling within a minute, and yield to active playback or another diagnostic
- **Portable automation configuration**: credential-safe configuration export/import now includes recurring scene cues without bridge keys or client tokens

### Security
- Scene schedules contain only names, saved-scene references, day/time flags, and target mapping IDs; bridge App Keys, Client Keys, and playback tokens are never stored or returned by the schedule API

## [1.5.69] - 2026-08-17

### Added
- **Opt-in persistent session history**: retain the bounded 25-entry sanitized playback history across Jellyfin restarts and restore it into Live Sync Status when the service starts
- **Privacy-aware retention control**: add a Retain history across Jellyfin restarts setting; disabling it or clearing history removes stored entries without stopping playback

### Security
- Persisted history stores only the existing aggregate telemetry, target labels, item/user labels, and cleanup diagnostics; bridge credentials and Jellyfin playback tokens are never included, and configuration backups omit the stored entries

## [1.5.68] - 2026-08-17

### Added
- **Session history operations**: filter recent summaries by outcome, export a credential-free JSON troubleshooting document, and clear retained history without stopping active playback
- **Administrator history controls**: add outcome filtering, Export JSON, and Clear History actions to the Recent Hue Sessions panel

### Security
- History exports contain only the same sanitized aggregate telemetry and target labels as the administrator endpoint; clearing removes the in-memory last-session pointer and retained summaries without touching bridge credentials

## [1.5.67] - 2026-08-17

### Added
- **Completed-session history**: retain the 25 most recent sanitized Hue playback summaries in memory, aggregate concurrent worker sessions, expose them through `GET /HueSync/History`, and render a refreshable Recent Hue Sessions table in the administrator configuration page

### Security
- Session history contains aggregate playback telemetry and target labels only; bridge credentials and Jellyfin playback tokens are never retained, exported, or serialized

## [1.5.66] - 2026-08-17

### Added
- **Credential-entry migration wizard**: Backup and Restore now provides password fields for replacement global and per-user bridge keys, so cross-server imports do not require hand-editing JSON

### Security
- Replacement keys remain in page memory only, are sent only with the authenticated atomic import request, and are never included in downloaded exports or import responses

## [1.5.65] - 2026-08-17

### Added
- **Credential-safe configuration backup**: export global playback settings, per-user bridge/profile mappings, and saved color scenes as a versioned JSON document without serializing App Keys or Client Keys
- **Atomic configuration restore**: import the export document with validation and rollback guarantees; matching stored credentials are preserved on the same server, while explicit replacement keys support migrations
- **Configuration portability UI**: add administrator Export Configuration and Import Configuration actions with active-playback protection and clear credential-handling guidance

## [1.5.64] - 2026-08-17

### Added
- **Concurrent multi-room playback**: independent Jellyfin video sessions mapped to different Hue bridges or entertainment areas now stream simultaneously, each with its own FFmpeg capture, DTLS connection, cancellation, restoration, and telemetry lifecycle
- **Target-scoped lifecycle arbitration**: same-target playback remains serialized while distinct Hue targets can run together; diagnostics remain process-wide and cannot overlap any playback stream
- **Multi-session runtime controls**: `GET /HueSync/Status` exposes sanitized active-session snapshots, and `POST /HueSync/Stop?playSessionId=...` stops one selected session without stopping Jellyfin playback
- **Startup recovery for multiple viewers**: active sessions found during plugin startup are recovered independently when their configured targets do not conflict

## [1.5.63] - 2026-08-17

### Added
- **Startup playback recovery**: when the plugin starts while Jellyfin already has an unpaused video session, Hue sync now resumes at the server-reported position instead of waiting for a new PlaybackStart event
- **Recovered-session lifecycle matching**: startup recovery maps later Jellyfin progress/stop notifications back to the recovered lifecycle so seek recovery, pause behavior, cleanup, and last-session telemetry remain intact

## [1.5.62] - 2026-08-17

### Added
- **Completed-session telemetry**: Live Sync Status and `GET /HueSync/Status` retain a sanitized `lastSession` summary with the outcome, duration, frame/packet counters, effective FPS, reconnects, seek recoveries, and cleanup warnings from the most recent video playback

### Fixed
- **Video-only playback guard**: audio-only and other non-video Jellyfin playback events are ignored before Hue or FFmpeg lifecycle startup, with a clear idle status message when no sync is active

## [1.5.61] - 2026-08-17

### Added
- **Seek-aware playback recovery**: large forward skips and backward seeks now restart FFmpeg at the viewer's current position while keeping the active Hue scene and saved light-state lifecycle intact
- **Seek telemetry**: Live Sync Status and `GET /HueSync/Status` report capture restart count and the most recent seek position without exposing credentials

## [1.5.60] - 2026-08-17

### Added
- **Playback quality telemetry**: Live Sync Status and `GET /HueSync/Status` now report effective FPS, successfully sent updates, color-threshold skips, failed sends, and DTLS reconnect attempts for the active session
- **Session-scoped counters**: stream metrics reset for each new DTLS playback session and remain credential-free

## [1.5.59] - 2026-08-17

### Added
- **Saved-target diagnostics**: add a credential-safe `GET /HueSync/TargetDiagnostics` report and **Validate Saved Targets** administrator action that checks every enabled default, inherited, and custom bridge mapping for reachability, selected-area presence, and controllable channels without opening a DTLS stream
- **Multi-room setup feedback**: report per-user target readiness, inherited-target status, credential presence, area names, and channel counts in the configuration page

### Security
- Target diagnostics expose only bridge address, user/area labels, and App/Client Key presence flags; stored credential values are never serialized

## [1.5.58] - 2026-08-17

### Added
- Live Sync Status and `GET /HueSync/Status` now report the active Jellyfin user identity alongside the selected bridge and entertainment area, making per-user mapping selection observable during playback

### Security
- Runtime user diagnostics include only the sanitized Jellyfin user ID and display name; bridge credentials and session tokens remain excluded

## [1.5.57] - 2026-08-17

### Added
- Bridge discovery now combines all valid cloud and local mDNS results, de-duplicates them, and exposes the complete candidate list to the global and per-user configuration controls
- Added `GET /HueSync/DiscoverBridges`; the existing `DiscoverBridge` route now includes `ipAddresses` while preserving its first-result `ipAddress` field

### Changed
- Multi-room administrators can choose a discovered bridge from address suggestions instead of being limited to the first bridge returned by discovery

## [1.5.56] - 2026-08-17

### Added
- Redacted custom per-user mapping credentials can now be used by administrator area loading, channel discovery, Test Connection, and Preview operations

### Security
- Custom mapping credential fallback requires both the matching persisted `UserId` and bridge target; arbitrary targets cannot borrow another mapping's stored keys
- The browser continues to send blank mapping secrets, keeping App and Client Keys server-side while preserving existing mapping edits

## [1.5.55] - 2026-08-17

### Security
- Global App and Client Keys are redacted from `GET /HueSync/Configuration`; the response reports presence flags instead of secret values
- Blank configuration-page secret fields preserve stored credentials, while an explicit clear operation removes both global keys
- Server-side credential fallback is restricted to the configured global bridge target, including area loading, channel discovery, connection tests, and previews

### Changed
- The administrator UI keeps global credentials blank in browser state, explains stored-key behavior, and continues setup operations through the protected server-side fallback

## [1.5.54] - 2026-08-17

### Fixed
- DTLS startup, color writes, reconnect delays, and entertainment-area reactivation now honor the active playback or diagnostic cancellation token
- Stopped streams clear their saved reconnect target so delayed background work cannot resurrect a tunnel after playback ends

## [1.5.53] - 2026-08-17

### Added
- Local mDNS/DNS-SD bridge discovery for `_hue._tcp.local` when cloud discovery cannot find a bridge

### Changed
- Discovery is bounded, cancellation-aware, and filters local results to private bridge addresses before returning them

## [1.5.52] - 2026-08-17

### Fixed
- Failed or ambiguous DTLS activation attempts in Test Connection and solid-color Preview now deactivate the entertainment area and restore the captured light state before returning

### Changed
- Diagnostic failures now treat a lost activation response as unsafe to ignore, preserving the same cleanup guarantee as cancellation and stream failures

## [1.5.51] - 2026-08-17

### Added
- System Diagnostics now probes Jellyfin's configured FFmpeg encoder path before falling back to the server PATH

### Changed
- FFmpeg readiness now reflects the executable used by playback, including bundled Jellyfin encoder installations

## [1.5.50] - 2026-08-17

### Added
- End-to-end request cancellation for bridge discovery, registration, entertainment-area reads, area configuration reads, and streaming-area activation

### Changed
- API preflight calls now stop promptly when the originating request is canceled instead of continuing retries in the background
- Canceled activation attempts still run best-effort entertainment-area deactivation and saved-light restoration cleanup

## [1.5.49] - 2026-08-17

### Added
- Non-mutating System Diagnostics report for configuration validity, FFmpeg/OpenSSL availability, and bridge lifecycle contention
- Configuration-page diagnostics panel with actionable prerequisite and readiness status

### Changed
- Local executable probes use bounded, cancellation-aware version checks and never expose bridge credentials

## [1.5.48] - 2026-08-17

### Added
- Request-aware cancellation for Test Connection probes and solid-color previews

### Changed
- Diagnostic light-state capture, activation delays, DTLS handshakes, and preview holds now stop promptly when the request is canceled
- Cancellation after bridge activation still runs the normal stream stop, entertainment-area deactivation, and light-state restoration cleanup

## [1.5.47] - 2026-08-17

### Added
- Shared `HueBridgeLifecycleGate` coordination between playback and Test Connection/preview diagnostics

### Changed
- Playback startup now refuses to mutate Hue output while a diagnostic lifecycle holds the bridge
- Diagnostics now fail safely during playback even when the API `IsSyncing` check races a new playback start
- Playback lifecycle leases are released across normal stop, startup rollback, pause, shutdown, and restoration cleanup paths

## [1.5.46] - 2026-08-17

### Fixed
- Registered `HueStreamTester` as a singleton so the diagnostic lifecycle gate is shared across API requests
- Prevented separate Test Connection and preview requests from creating independent locks and touching the bridge concurrently

## [1.5.45] - 2026-08-17

### Added
- Serialized Test Connection probes and solid-color previews with an explicit busy result when another diagnostic lifecycle is already using the bridge

### Changed
- Concurrent diagnostic requests now fail without touching the bridge, preventing overlapping snapshots, area activation, DTLS streams, and restoration from interfering with one another

## [1.5.44] - 2026-08-17

### Added
- Result-aware light-state capture with per-light retry handling, duplicate-light de-duplication, and captured/attempted/failed diagnostics

### Changed
- Playback now refuses to mutate Hue output when a complete restoration snapshot cannot be captured
- Test Connection probes and solid-color previews fail safely before area activation when light-state capture is incomplete
- Startup rollback no longer applies cinema-mode restoration when cinema output was never attempted

## [1.5.43] - 2026-08-17

### Added
- Result-aware light-state restoration with per-light retry handling and aggregate attempted/restored/failed counts
- Sanitized cleanup warnings in Live Sync Status, `GET /HueSync/Status`, connection probes, and solid-color previews

### Changed
- Playback cleanup now reports incomplete light restoration and failed entertainment-area deactivation instead of silently treating partial cleanup as successful
- DTLS Test Connection and preview flows now fail visibly when their deactivation or light-state restoration cleanup is incomplete

## [1.5.42] - 2026-08-17

### Added
- `InheritsDefaultBridge` in sanitized user-mapping summaries so administrators can distinguish global inheritance from incomplete custom credentials

### Changed
- Mapping lists now display inherited global bridge and area targets explicitly
- Editing an inherited mapping loads the global entertainment areas and uses the global target for connection tests, previews, and channel discovery without copying target fields into the mapping
- Troubleshooting guidance now documents the blank-target inheritance workflow

## [1.5.41] - 2026-08-17

### Changed
- Enabled per-user mappings can now leave Bridge Address, App Key, Client Key, and Entertainment Area blank to inherit the global bridge configuration
- Clearing a custom mapping's bridge target removes its stored bridge credentials and area instead of retaining stale secrets
- Configuration-page guidance now explains default-bridge inheritance for per-user profiles

### Added
- Regression coverage for default-bridge mapping persistence, credential cleanup, and partial-target rejection

## [1.5.40] - 2026-08-17

### Added
- Reusable named color scenes with configurable RGB, brightness, and preview duration
- Authenticated color-preset CRUD endpoints with case-insensitive updates and a 50-scene safety limit
- Configuration-page scene picker, apply, save/update, and delete controls shared by default and mapping previews
- Regression coverage for preset validation, persistence, sorting, update semantics, deletion, and invalid requests

### Changed
- DTLS probes and solid-color previews re-activate the entertainment area before reconnecting after a stream failure

## [1.5.39] - 2026-08-17

### Added
- Solid-color preview controls with configurable color, brightness, duration, and default or per-user mapping targets
- Bounded `POST /HueSync/Preview` endpoint with channel-profile validation and active-playback protection
- Preview lifecycle that saves selected light state, activates the entertainment area, sends a DTLS color packet, and restores state automatically
- Regression coverage for preview color conversion, channel filtering, invalid values, duration limits, and API wiring

## [1.5.38] - 2026-08-17

### Added
- Optional global entertainment channel profiles with all-channel inheritance when blank
- Configuration-page channel discovery for the default profile
- Regression coverage for global channel parsing, validation, persistence, and per-user inheritance

### Changed
- Per-user mappings with blank channel overrides now inherit the global channel selection; explicit per-user lists remain authoritative
- Default Test Connection now validates the configured global channel profile

## [1.5.37] - 2026-08-17

### Added
- Channel-aware per-user Test Connection diagnostics with available, selected, and missing channel reporting
- DTLS stream probes now honor the selected mapping channel profile and restore only the probed lights
- Regression coverage for selected-channel probing, stale channel profiles, malformed requests, and filtered probe colors

### Changed
- Mapping Test Connection now validates saved channel IDs against the selected entertainment area before opening a stream

## [1.5.36] - 2026-08-17

### Added
- Optional per-user entertainment channel profiles with global all-channel inheritance
- Channel-ID discovery from the selected entertainment area in the configuration page and API
- Active channel-selection diagnostics in sanitized runtime status and the configuration page
- Regression coverage for channel parsing, validation, persistence, filtered state capture, runtime resolution, and status/API wiring

### Changed
- Per-user channel selections now consistently constrain cinema dimming, video color streaming, and light-state restoration

## [1.5.35] - 2026-08-17

### Added
- Optional per-user execution profiles for GPU acceleration, FFmpeg flags, stall timeout, and network retries with global inheritance
- Active execution diagnostics in sanitized runtime status and the configuration page
- Regression coverage for execution resolution, validation, persistence, startup wiring, and status/API behavior

### Changed
- FFmpeg, retry, reconnect, and stall monitoring now use the execution policy captured at playback startup for each user

## [1.5.34] - 2026-08-17

### Added
- Optional per-user blackout and color-change thresholds with global inheritance
- Complete active color-policy diagnostics covering boost, RGB gains, saturation, hue, output brightness, blackout, and packet-change thresholds
- Regression coverage for threshold resolution, validation, persistence, status/API wiring, and session policy capture

### Changed
- Color processing settings are captured at playback startup so configuration edits cannot change an in-flight session's color behavior
- Playback cleanup restores saved light state even if the global sync toggle is disabled after a session has started

## [1.5.33] - 2026-08-17

### Added
- Optional per-user light-state restoration policies with global inheritance
- Active restoration-policy diagnostics in sanitized runtime status responses and the configuration page
- Regression coverage for restoration-policy resolution, persistence, lifecycle behavior, and status/API wiring

### Changed
- Per-user restoration policy is captured at playback startup so pause, stop, and terminal cleanup use a consistent light-state strategy

## [1.5.32] - 2026-08-17

### Added
- Optional per-user playback-performance profiles for target FPS, frame resolution, video fit, deinterlacing, sampling, and temporal color smoothing
- Active performance-profile details in sanitized runtime status responses and the configuration page
- Regression coverage for performance override inheritance, validation, persistence, runtime resolution, and status/API wiring

### Changed
- Per-user performance overrides are captured at playback startup so each session uses a consistent FFmpeg and sampling pipeline

## [1.5.31] - 2026-08-17

### Added
- Global red, green, and blue channel gains for room-specific white-balance correction
- Optional per-user RGB channel-gain overrides with global inheritance
- Regression coverage for channel-gain application, validation, persistence, and UI/API wiring

### Changed
- RGB channel gains are applied before saturation and hue processing while preserving the neutral 100% defaults

## [1.5.30] - 2026-08-17

### Added
- Optional per-user pause behavior overrides for multi-room mappings
- Blank pause behavior fields inherit the global keep-colors or restore-light-state setting
- Regression coverage for pause profile resolution, validation, persistence, lifecycle cleanup, and UI/API wiring

### Changed
- Pause cleanup now uses the effective behavior captured for the active playback user

## [1.5.29] - 2026-08-17

### Added
- Optional per-user cinema-mode enablement and dim-level overrides for multi-room mappings
- Blank playback profile fields inherit the global cinema-mode settings
- Regression coverage for per-user playback resolution, cleanup behavior, validation, persistence, and UI/API wiring

### Changed
- Playback cleanup now restores lights according to the effective cinema-mode setting used by the active user

## [1.5.28] - 2026-08-17

### Added
- Optional per-user color profiles for brightness boost, saturation, hue shift, and output brightness
- Blank per-user profile fields inherit the global settings while existing mappings remain compatible
- Regression coverage for per-user profile resolution, validation, persistence, redacted mapping summaries, and UI/API wiring

### Changed
- Per-user overrides are resolved live for the mapped playback session without affecting other users

## [1.5.27] - 2026-08-17

### Added
- Configurable -180 to 180 degree global hue shift for room-specific color correction or creative palettes
- Regression coverage for hue wrapping, chromatic color rotation, validation, persistence, and UI/API wiring

### Changed
- Hue rotation is applied in HSL alongside saturation before final output brightness scaling

## [1.5.26] - 2026-08-17

### Added
- Configurable 0-100% final output brightness control independent of Brightness Boost
- Regression coverage for output-brightness scaling, validation, persistence, and UI/API wiring

### Changed
- Output brightness is applied after boost and saturation so the ceiling dims without changing hue

## [1.5.25] - 2026-08-17

### Added
- Configurable Off, Auto, and On FFmpeg deinterlacing modes
- Active deinterlacing diagnostics in Live Sync Status
- Regression coverage for deinterlace filters, validation, persistence, and status

### Changed
- Auto deinterlacing only processes frames marked as interlaced; Off remains the default

## [1.5.24] - 2026-08-17

### Added
- Configurable Stretch, Fit, and Crop video scaling modes
- Active video fit diagnostics in Live Sync Status
- Regression coverage for fit filters, validation, persistence, and status

### Changed
- FFmpeg preserves Stretch as the default while Fit and Crop handle non-16:9 source aspect ratios

## [1.5.23] - 2026-08-17

### Added
- Configurable 80x45, 160x90, and 320x180 RGB frame sampling resolutions
- Active frame resolution diagnostics in Live Sync Status
- Regression coverage for resolution validation, scaling, FFmpeg filters, and high-resolution sampling

### Changed
- FFmpeg output and spatial sampling now use the selected resolution while 160x90 remains the default

## [1.5.22] - 2026-08-17

### Added
- Configurable Average, CenterWeighted, and CenterPixel spatial sampling modes
- Regression coverage for sampling-mode validation, persistence, and color extraction

### Changed
- Sampling mode is captured with each playback session while Average preserves the prior behavior

## [1.5.21] - 2026-08-17

### Changed
- Keep-colors pause/resume now preserves the original playback-start light snapshot for final restoration
- Service shutdown now restores saved light state before deactivating Hue output

### Added
- Regression coverage for same-session snapshot ownership and shutdown restoration

## [1.5.20] - 2026-08-17

### Added
- Configurable pause behavior to keep last synced colors or restore the original captured light state
- Regression coverage for pause-time restoration and status/lifecycle ownership

### Changed
- Pause status now reports whether colors are being kept or the original light state is being restored

## [1.5.19] - 2026-08-17

### Changed
- The Network Retry Attempts setting now controls both Hue REST retries and DTLS reconnect attempts
- A zero retry setting now explicitly disables DTLS reconnect attempts while leaving initial stream setup unchanged

### Added
- Regression coverage for DTLS retry-attempt clamping and configuration wiring

## [1.5.18] - 2026-08-17

### Added
- Configurable 0-90% temporal color smoothing to reduce frame-to-frame flicker
- Regression coverage for smoothing weights, history initialization, and safe clamping

### Changed
- Sync clears smoothing history at blackout transitions so bright scenes recover immediately

## [1.5.17] - 2026-08-17

### Added
- Configurable 1-50% color-sampling breadth around each Hue channel's screen position
- Regression coverage for sampling-breadth validation, normalization, and scoped configuration persistence

### Changed
- Sync now uses the configured sampling breadth while preserving the previous 15% default

## [1.5.16] - 2026-08-17

### Added
- Redacted per-user mapping responses that report whether stored App/Client Keys exist without returning their values
- Scoped `/HueSync/Configuration` read/write endpoints that keep `UserMappings` out of the configuration-page settings flow
- Regression coverage for credential redaction, safe blank-key edits, and preservation of mappings during default-setting updates

### Changed
- Editing a per-user mapping now leaves key fields blank and preserves stored credentials unless replacement values are entered
- Default configuration saves no longer fetch and round-trip the full plugin configuration (including per-user mappings) through the browser

## [1.5.15] - 2026-08-17

### Added
- Mode-aware scene restoration for Hue color-temperature lights using their saved `mirek` value
- Safe restoration for lights without a color resource, without sending an invented XY color
- Regression coverage for valid, invalid, and absent color-temperature state

### Changed
- Light-state restoration now sends either `color_temperature`, `color`, or neither according to the state captured before playback

## [1.5.14] - 2026-08-17

### Added
- Configurable 1-60 second FFmpeg stall timeout with a longer startup grace period
- Automatic terminal cleanup when FFmpeg stops producing complete video frames
- Regression coverage for configured stall cleanup and administrator-facing diagnostics

### Changed
- Live Sync Status now reports the FFmpeg stall as an actionable error while restoring lights and deactivating the entertainment area

## [1.5.13] - 2026-08-17

### Added
- Immediate rollback for partially initialized sync sessions when startup fails or is cancelled
- Regression coverage for startup restoration, area deactivation, stream ownership, and diagnostics

### Changed
- Startup now disposes unowned FFmpeg streams and restores/deactivates Hue state before waiting for a later playback event

## [1.5.12] - 2026-08-17

### Added
- Bounded DTLS send-failure handling so a stream that cannot recover does not leave lights frozen
- Regression coverage for repeated send failures, error reporting, and terminal cleanup

### Changed
- Repeated Hue stream write failures now stop synchronization, restore saved lights, deactivate the entertainment area, and clear runtime ownership

## [1.5.11] - 2026-08-17

### Added
- Automatic terminal cleanup when the FFmpeg frame stream ends or fails unexpectedly
- Light-state restoration and entertainment-area deactivation on terminal sync-loop exits
- Regression coverage for EOF cleanup, failure cleanup, runtime ownership, and error preservation

### Changed
- Terminal stream failures now release FFmpeg/DTLS resources and publish a stable administrator-facing runtime state

## [1.5.10] - 2026-08-17

### Added
- Per-user **Enable Hue Sync for this user** control for multi-user Jellyfin deployments
- Credential-free disabled mappings for users whose playback should remain unaffected
- Regression coverage for mapping opt-out behavior, default fallbacks, and runtime startup suppression

### Changed
- Disabled user mappings are enforced before configuration validation, FFmpeg startup, or bridge network calls
- Existing mappings remain enabled by default when the new setting is absent from saved configuration

## [1.5.9] - 2026-08-17

### Added
- Protected `POST /HueSync/Stop` control for administrators to stop Hue output without stopping Jellyfin playback
- Session-level suppression so paused or in-flight progress events cannot immediately restart a manually stopped sync
- Regression coverage for manual stop cleanup, stale stop notifications, and runtime status availability

### Changed
- The live configuration status panel now exposes a `Stop Current Sync` action that restores saved lights and deactivates the entertainment area

## [1.5.8] - 2026-08-17

### Added
- Optional non-destructive DTLS stream probe from the default and per-user Test Connection flows
- Explicit stream probe result fields for API clients, including stream readiness and a sanitized diagnostic message
- Regression coverage for probe channel validation, API wiring, and malformed-area safety

### Changed
- `HueStreamer.SendColors` now reports whether a packet was successfully written so diagnostics can distinguish an open process from a usable stream

## [1.5.7] - 2026-08-17

### Added
- Live runtime status panel with automatic refresh in the configuration page
- Sanitized `/HueSync/Status` diagnostics for lifecycle state, active target, frame count, sync duration, and FFmpeg/DTLS health
- Regression coverage for runtime snapshots and status responses without credential exposure

### Changed
- Playback startup, pause, stop, bridge, and video-pipeline failures now publish actionable administrator-facing diagnostics

## [1.5.6] - 2026-08-17

### Added
- Non-destructive bridge connection testing in the default and per-user configuration flows
- Validation that a selected entertainment area contains controllable channels
- Regression coverage for successful area diagnostics, invalid targets, and channel validation

### Changed
- Connection diagnostics report bridge reachability, area count, and selected-area readiness without starting a stream

## [1.5.5] - 2026-08-17

### Added
- Explicit Edit and Cancel controls for per-user bridge mappings
- Body-based `POST /HueSync/EntertainmentAreas` API for secure area loading
- Regression coverage for secure area requests and missing credentials

### Changed
- Configuration UI now uses the body-based area endpoint so app keys do not appear in URLs
- Existing `GET /HueSync/EntertainmentAreas` clients remain supported

## [1.5.4] - 2026-08-17

### Added
- Authenticated `/HueSync/DiscoverBridge` endpoint backed by the Hue discovery service
- One-click bridge discovery in the default and per-user mapping configuration forms
- API controller regression coverage for successful and unsuccessful discovery

## [1.5.2] - 2026-08-16

### Changed
- Use the Hue bridge HTTPS API for link-button registration, as required by current bridge firmware
- Scope the local certificate exception to private bridge addresses instead of disabling TLS validation for public discovery
- Require bridge targets to be private/local addresses or .local mDNS names
- Add regression coverage for registration transport security, address validation, and certificate validation boundaries

## [1.5.3] - 2026-08-16

### Fixed
- Keep plugin JSON and encoding dependencies on the .NET 8 servicing line so the release loads on Jellyfin's .NET 8 runtime
- Pin vulnerable legacy transitive framework packages to safe compatible versions and make the CI advisory scan pass

## [1.5.0] - 2026-08-16

### Added
- Validation for per-user bridge mappings, including credentials, addresses, duplicate users, and area IDs
- Regression coverage for malformed bridge responses, transient HTTP failures, and invalid Hue stream packets

### Changed
- Hue bridge requests now retry transient network/server failures without retrying authentication or input errors
- Playback cleanup is resilient across stop, pause, resume, and service shutdown races
- FFmpeg process ownership and health monitoring are safe across repeated starts and stops
- DTLS reconnect attempts are serialized and force the first frame after a reconnect to be sent
- Release metadata and packaging documentation now track the current 1.5.0 release

## [1.4.0] - 2025-01-05

### Added
- Per-user bridge mappings for multi-user Jellyfin setups
- Each Jellyfin user can now map to a different Hue bridge and entertainment area
- New configuration UI section for managing user-to-bridge mappings
- API endpoints for user mapping management (`/HueSync/UserMappings`)

### Changed
- Users without a specific mapping automatically fall back to default bridge settings
- Improved API error handling and stability

## [1.3.1] - 2024-12-01

### Changed
- Code quality improvements: refactored magic numbers into named constants
- Improved resource cleanup and disposal in service shutdown
- Enhanced maintainability with better code organization
- Added `.editorconfig` for consistent code formatting

### Fixed
- Minor bug fixes and stability improvements

## [1.3.0] - 2024-11-15

### Added
- Scene restoration: automatically saves and restores original light states
- Advanced color processing: brightness boost, saturation control, and blackout detection
- Network resilience: retry logic with exponential backoff for HTTP operations
- Status API: new `/HueSync/Status` endpoint for monitoring sync state
- Cinema mode: automatic light dimming during playback

### Changed
- ~30-50% reduction in unnecessary updates during static scenes

## [1.2.0] - 2024-10-01

### Added
- Cinema mode with configurable dim levels
- Automatic DTLS reconnection with exponential backoff
- Health monitoring for FFmpeg and OpenSSL processes
- Configuration validation with helpful error messages

## [1.1.0] - 2024-09-01

### Fixed
- Critical coordinate mapping bug for proper light positioning

### Added
- Pause/resume support for playback
- Improved error handling and process management
- Enhanced logging and XML documentation

## [1.0.0] - 2024-08-01

### Added
- Initial release
- Real-time video sync with Philips Hue lights
- Hue Entertainment API (DTLS) support
- FFmpeg-based video frame extraction
- Automatic bridge discovery and registration
- Entertainment area selection
- Configurable target FPS
- GPU acceleration support
