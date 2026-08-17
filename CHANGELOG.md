# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
