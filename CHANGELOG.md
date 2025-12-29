# Changelog

All notable changes to this project will be documented in this file.

> **Note**: This project is currently in active development. For detailed release history, see the "Recent Changes" section in README.md.

## [1.3.0] - 2025-12-29

### Added
- Scene restoration (save/restore original light states)
- Advanced color processing (brightness boost, saturation control, blackout detection)
- Network resilience with retry logic and exponential backoff
- Status API endpoint (`/HueSync/Status`)
- Enhanced configuration UI

### Improved
- Performance optimizations (~30-50% reduction in unnecessary updates)
- Better resource management with IHttpClientFactory
- More robust error recovery

## [1.2.0] - 2024-12-29

### Added
- Cinema mode with configurable dim levels
- Automatic DTLS reconnection
- Health monitoring for processes
- Configuration validation

## [1.1.0] - 2024-12-29

### Fixed
- Critical coordinate mapping bug
- Memory leaks and process management issues

### Added
- Pause/resume support
- Comprehensive error handling and logging
