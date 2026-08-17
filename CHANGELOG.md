# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
