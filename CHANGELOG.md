# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.5.2] - 2026-08-16

### Changed
- Use the Hue bridge HTTPS API for link-button registration, as required by current bridge firmware
- Scope the local certificate exception to private bridge addresses instead of disabling TLS validation for public discovery
- Require bridge targets to be private/local addresses or .local mDNS names
- Add regression coverage for registration transport security, address validation, and certificate validation boundaries

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
