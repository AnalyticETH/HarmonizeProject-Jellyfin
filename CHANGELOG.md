# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
