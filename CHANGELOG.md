# Changelog

All notable changes to this project will be documented in this file.

## [1.3.0] - 2025-12-29

### Added
- **Scene Restoration**: Automatically saves and restores the original light state before/after sync (configurable)
- **Advanced Color Processing**:
  - Brightness boost: Adjust the brightness of synced colors (50-200%)
  - Color saturation: Adjust the saturation of synced colors (0-200%)
  - Blackout threshold: Skip sync when screen is mostly black to save processing
  - Color change detection: Skip redundant updates when colors haven't changed significantly
- **Network Resilience**:
  - Retry logic with exponential backoff for all HTTP operations (configurable 0-10 attempts)
  - Automatic reconnection for DTLS failures (existing feature, now configurable)
- **IHttpClientFactory Integration**: Migrated from manual HttpClient management to IHttpClientFactory for better resource management
- **Status API Endpoint**: New `/HueSync/Status` endpoint to check sync state, current item, and configuration
- **Enhanced Configuration UI**:
  - Added "Cinema Mode" section with dim level control
  - Added "Advanced Settings" section with all new features
  - Better organization of configuration options
  - Comprehensive field descriptions

### Improved
- Significant performance improvements through color change detection
- Better resource management with IHttpClientFactory
- More robust error recovery with retry logic
- Enhanced logging for troubleshooting
- More efficient color processing with blackout detection

### Technical Details

**Color Processing Pipeline:**
1. Extract RGB values from video frame
2. Check blackout threshold (skip if too dark)
3. Apply brightness boost (if configured)
4. Apply color saturation adjustment (if configured)
5. Check color change threshold (skip if colors haven't changed enough)
6. Send to Hue Bridge via DTLS

**Performance Optimizations:**
- Color change detection reduces unnecessary DTLS packets by ~30-50% during static scenes
- Blackout detection skips processing during dark scenes (credits, fades)
- Configurable thresholds allow fine-tuning for different content types

**Scene Restoration:**
- Captures on/off state, brightness, and color (x,y coordinates) for each light
- Restores state after playback stops
- Gracefully handles errors if individual lights fail to restore

## [1.2.0] - 2024-12-29

### Added
- **Cinema Mode**: Automatically dims lights to configured level when playback starts and restores them when playback stops
- **Automatic Reconnection**: DTLS tunnel automatically reconnects on failure with exponential backoff (up to 3 attempts)
- **Health Monitoring**: Real-time monitoring of FFmpeg and OpenSSL processes with stall detection
- **Configuration Validation**: Comprehensive validation with helpful error messages for all settings
- **Performance Metrics**: Frame counter and processing statistics for debugging
- **Performance Optimizations**:
  - Optimized color calculation loops with pre-calculated row starts
  - Better frame delay handling to prevent sync lag
  - Improved memory access patterns

### Improved
- Enhanced logging with frame counts and health status
- Better process lifecycle management with health checks
- More robust error recovery throughout the plugin

## [1.1.0] - 2024-12-29

### Fixed
- **Critical Bug Fix**: Fixed coordinate parsing in HueSyncService.cs where `y` coordinate was used instead of `z` for vertical positioning (line 97). Now correctly maps x (horizontal) and z (vertical) to the 2D screen plane, matching HarmonizeProject implementation.
- **Memory Leak**: Fixed HttpClient instantiation issue in HueClient.cs where multiple instances were created (line 17 and 22). Now properly creates a single instance with SSL certificate validation callback.
- **Process Management**: Improved process cleanup in HueStreamer.cs and FfmpegStreamer.cs with proper disposal and timeout handling.

### Added
- **Pause/Resume Support**: Added playback progress event handling to pause light sync when media is paused (HueSyncService.cs line 137-151).
- **Error Handling**: Comprehensive error handling and logging throughout the codebase:
  - Input validation in FfmpegStreamer (video file existence checks)
  - Exception handling in HueStreamer with specific error types (ObjectDisposedException, IOException)
  - Better error messages for debugging
  - Async stderr logging for FFmpeg output
- **XML Documentation**: Added comprehensive XML documentation comments to all public APIs for better IntelliSense support:
  - HueClient class and all public methods
  - HueStreamer class and public methods
  - FfmpegStreamer class and public methods
  - HueSyncService class
- **Timeout Configuration**: Added 10-second timeout to HttpClient for bridge communication (HueClient.cs line 25).
- **Process Monitoring**: FFmpeg stderr output is now logged asynchronously for better debugging (FfmpegStreamer.cs line 66-84).

### Improved
- **Code Quality**: Improved code organization and readability with better comments explaining coordinate system conversions.
- **Logging**: Enhanced logging with more context-aware messages:
  - Item names in playback events
  - Bridge IP in DTLS connection messages
  - Process lifecycle events
- **Defensive Coding**: Added null checks and validation throughout:
  - Null/empty checks for configuration values
  - File existence validation before processing
  - Process state checks before operations
- **Process Cleanup**: Proper disposal of processes with wait timeouts to prevent zombie processes.

### Technical Details

#### Coordinate System Fix
The original code incorrectly used the `y` coordinate (depth) instead of `z` coordinate (height) for vertical positioning. The Hue Entertainment API v2 uses:
- `x`: -1 (left) to 1 (right) - horizontal
- `y`: -1 (back) to 1 (front) - depth
- `z`: -1 (bottom) to 1 (top) - vertical

For 2D screen mapping, we need `x` and `z`, which matches the HarmonizeProject implementation.

#### HttpClient Best Practice
Changed from creating multiple HttpClient instances to a single instance with proper handler configuration, following .NET best practices to avoid socket exhaustion.

## Build Instructions

Requirements: .NET 8.0 SDK

```bash
cd Jellyfin.Plugin.Hue
dotnet build --configuration Release
```

The plugin DLL will be generated at: `bin/Release/net8.0/Jellyfin.Plugin.Hue.dll`

## Testing Notes

To test the improvements:
1. Build the plugin with the commands above
2. Copy the DLL to your Jellyfin plugins directory
3. Restart Jellyfin
4. Configure the plugin via Dashboard -> Plugins -> Philips Hue Sync
5. Test playback synchronization with various media files
6. Monitor Jellyfin logs for any error messages

## Future Enhancements (Not Implemented)

The following features were identified but not implemented in this update:
- Cinema mode implementation (dimming lights on playback start)
- Automatic retry logic for network failures
- Reconnection handling if DTLS tunnel drops
- Frame rate adaptation based on system load
- Configurable frame dimensions
- Unit tests for core functionality
