# Jellyfin Philips Hue Sync Plugin

<div align="center">

![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9.0+-00A4DC?style=flat-square&logo=jellyfin)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet)
![License](https://img.shields.io/badge/license-GPL--3.0-green?style=flat-square)
[![codecov](https://codecov.io/gh/AnalyticETH/jellyfin-hue/graph/badge.svg)](https://codecov.io/gh/AnalyticETH/jellyfin-hue)

**Immersive lighting for your Jellyfin Media Server.**

</div>

## Overview
The **Philips Hue Sync Plugin** for Jellyfin brings the theater experience to your home. It synchronizes your Philips Hue lights with your media in real-time, extending the colors from your screen into your room. 

Unlike simple "cinema mode" automations that just dim the lights, this plugin actively analyzes the video stream and uses the **Hue Entertainment API** to drive your lights with low latency, matching the on-screen action.

## How It Works
1.  **Playback Detection**: The plugin monitors Jellyfin for video playback events.
2.  **Video Analysis**: When playback starts, it spawns a lightweight background process (`ffmpeg`) to extract color data from the video file in real-time.
3.  **Light Mapping**: It maps the video colors to your specific Hue Entertainment Area configuration (Left, Right, Center, etc.).
4.  **DTLS Streaming**: Color updates are streamed securely and instantly to your Hue Bridge via DTLS (Datagram Transport Layer Security).

## Prerequisites
*   **Jellyfin Server**: Version 10.9.0 or newer.
*   **Philips Hue Bridge**: V2 (Square) bridge required for Entertainment API support.
*   **Hue Lights**: Color-capable Hue lights added to an **Entertainment Area** in the Hue App.
*   **Server Dependencies**: 
    *   `ffmpeg` (Usually bundled with Jellyfin, or system installed)
    *   `openssl` (Required for the secure tunnel to the Bridge)

## Installation

### Manual Installation
1.  Download the latest release DLL.
2.  Navigate to your Jellyfin plugins directory:
    *   **Linux**: `/var/lib/jellyfin/plugins`
    *   **Windows**: `%ProgramData%\Jellyfin\Server\plugins`
    *   **Docker**: `/config/plugins`
3.  Create a folder named `HueSync`.
4.  Place `Jellyfin.Plugin.Hue.dll` inside.
5.  Restart Jellyfin.

## Configuration
Go to **Dashboard -> Plugins -> Philips Hue Sync** to configure the plugin.

| Setting | Description |
| :--- | :--- |
| **Hue Bridge Address** | The private/local IP address of your bridge (or a .local mDNS host name). |
| **Discover Bridge** | Ask the Hue discovery service for a bridge address and fill it into the form automatically. |
| **Link Bridge** | Press the physical button on your Bridge, then click this button to auto-generate keys. |
| **Test Connection** | Verify bridge credentials and, when selected, that the entertainment area has controllable channels. If a Client Key is present, also run a short DTLS stream probe that saves and restores current light state. |
| **Hue App Key** | "Username" for the REST API (auto-filled). |
| **Hue Client Key** | "ClientKey" for the streaming API (auto-filled). |
| **Entertainment Area ID** | UUID of the specific area to sync. |
| **Target FPS** | Frames per second to process (Default: 20). Lower = less CPU. |
| **Frame Sampling Resolution** | RGB frame size used for color extraction: 80×45 (lowest CPU), 160×90 (default), or 320×180 (more spatial detail). |
| **Video Fit Mode** | Stretch (default), Fit with letterbox bars, or Crop to fill the 16:9 sampling frame while preserving source aspect ratio. |
| **Video Deinterlacing** | Off (default), Auto for frames flagged as interlaced, or On to deinterlace every frame. Useful for television and DVD sources. |
| **Color Sampling Breadth** | Size of the neighborhood sampled around each Hue channel's screen position (1-50%, default: 15%). Smaller values follow fine detail; larger values reduce noise. |
| **Color Sampling Mode** | Average (default), center-weighted, or center-pixel sampling for balancing ambient stability against detail. |
| **Temporal Color Smoothing** | Blend the previous frame into new colors to reduce flicker (0-90%, default: 0%). Higher values create smoother but slower transitions. |
| **Hue Shift** | Rotate synced colors around the hue wheel (-180° to 180°, default: 0°) to correct a room's color bias or create a creative palette. |
| **Output Brightness** | Final 0-100% brightness scale applied after boost, saturation, and hue shift (default: 100%). Use it to cap room brightness without changing color balance. |
| **When Playback Is Paused** | Keep the last synced colors (default) or restore the original light state captured at playback start. Sync resumes automatically. Restoring uses the **Restore Light State After Sync** setting. |
| **Custom Flags** | Add hardware acceleration flags here (e.g. `-hwaccel auto`). |
| **FFmpeg Stall Timeout** | Stop synchronization and restore the lights when no complete video frame arrives within 1-60 seconds (Default: 5). FFmpeg startup receives an extended codec-initialization grace period. |
| **Network Retry Attempts** | Number of retry attempts for Hue REST requests and DTLS stream recovery (0-10, default: 3). |
| **Enable Real-time Sync** | Master toggle for the sync feature. |

### Per-User Bridge Mappings

For multi-user Jellyfin setups, you can map individual users to different Hue bridges and entertainment areas. This is useful when:
- Different rooms have their own Hue bridges
- Different users watch in different locations (e.g., living room vs. bedroom)
- You want to disable sync for certain users

To configure per-user mappings:
1. Go to the **Per-User Bridge Mappings** section in the plugin settings
2. Select a Jellyfin user from the dropdown
3. Click **Discover Bridge** (or enter a private/local bridge address manually)
4. Click **Link Bridge** and press the physical button on that bridge
5. Select the entertainment area for that bridge
6. Click **Add User Mapping**

Users without a mapping will use the default bridge settings configured above.
Existing mappings can be updated with **Edit** or removed with **Delete**. Editing keeps the
selected Jellyfin user fixed while allowing the bridge address, credentials, and area to change.
To leave a user's playback unchanged, edit or create that mapping and uncheck **Enable Hue Sync
for this user**; bridge credentials are not required for a disabled mapping. Users without a
mapping continue to use the default bridge settings.

When editing an enabled mapping, the existing App Key and Client Key are kept securely on the
server and are not displayed in the browser. Leave those fields blank to keep the stored keys, or
enter replacement keys to rotate them. The mapping list reports credential presence without
revealing the key values.

Each mapping can also define an optional per-user color profile for Brightness Boost, Color
Saturation, Hue Shift, and Output Brightness. Leave any profile field blank to inherit the current
global setting; populated overrides apply only to that user's playback, including when the mapping
uses the default bridge.

Mappings can also override Cinema Mode, its dim level, and pause behavior. Choose **Inherit global
setting** to keep the defaults, or enable/disable cinema mode, set a separate 0-100% dim level, and
choose whether pausing that user's playback keeps the last colors or restores the captured light state.

### Admin API

The configuration page uses authenticated administrator endpoints under `/HueSync`:

| Endpoint | Purpose |
| :--- | :--- |
| `GET /HueSync/DiscoverBridge` | Discover a private/local Hue Bridge address. |
| `POST /HueSync/EntertainmentAreas` | Load areas with `{ "ipAddress": "...", "appKey": "..." }` in the request body. |
| `POST /HueSync/TestConnection` | Verify bridge credentials and optional entertainment-area readiness. Supplying `clientKey` also runs a short activate/send/stop DTLS probe with light-state restoration. |
| `GET /HueSync/Status` | Read sanitized runtime state, active target, frame count, FFmpeg/DTLS health, and whether the current sync can be stopped safely. |
| `POST /HueSync/Stop` | Stop Hue output for the current playback session, restore lights, and leave Jellyfin playback running. |
| `GET/POST /HueSync/Configuration` | Read or update default plugin settings without serializing per-user mappings to the configuration page. |
| `GET /HueSync/EntertainmentAreas` | Legacy query-string-compatible area loading for existing clients. |
| `GET/POST /HueSync/UserMappings` | List or save per-user bridge mappings, sync enable flags, optional playback/color-profile overrides; GET responses redact stored credentials. |
| `DELETE /HueSync/UserMappings/{userId}` | Remove one per-user bridge mapping. |

### Generating Hue Credentials (Manual Fallback)
If the **Link Bridge** button doesn't work for you, you can generate keys manually:
1.  Go to `https://<BRIDGE_IP>/debug/clip.html`
2.  Press the **Link Button** on your Hue Bridge.
3.  Post to `/api` with body: `{"devicetype":"jellyfin_plugin#server", "generateclientkey":true}`
4.  Copy the `username` (App Key) and `clientkey` (Client Key) from the response.

## Development

### Building

Requirements: .NET 8.0 SDK.

#### Quick Build (Recommended)

Use the provided build scripts to create a release package:

**Linux/macOS:**
```bash
./build-release.sh
```

**Windows:**
```powershell
.\build-release.ps1
```

These scripts will:
- Build the plugin in Release mode
- Run all tests
- Create a release package with proper versioning
- Generate a zip file ready for distribution

#### Manual Build

```bash
cd Jellyfin.Plugin.Hue
dotnet build --configuration Release
```

The plugin DLL will be generated at:
`bin/Release/net8.0/Jellyfin.Plugin.Hue.dll`

> **Note**: You only need this one file. The other DLLs in that folder are dependencies that Jellyfin already provides.

#### Release Package Contents

The release scripts produce `jellyfin-plugin-hue-v<version>.zip` containing exactly:

* `Jellyfin.Plugin.Hue.dll` — the plugin assembly, including the embedded configuration page
* `meta.json` — the Jellyfin plugin manifest and release version

The version in `meta.json`, the project file, and the archive name must match. Install both
files together in a `HueSync` directory under the Jellyfin plugins directory.

If the .NET SDK is not installed on a Linux host, the same build can be run with Docker:

```bash
docker run --rm -v "$PWD:/src" -v /tmp/hue-nuget:/root/.nuget/packages \
  -w /src mcr.microsoft.com/dotnet/sdk:8.0 sh -lc \
  'apt-get update -qq && apt-get install -y -qq zip unzip && ./build-release.sh'
```

### Project Structure
*   `Configuration/`: Plugin settings UI and logic.
*   `Service/`: Background service for monitoring playback.
*   `Hue/`: Logic for communicating with the Hue Bridge (REST & DTLS).
*   `Video/`: FFmpeg wrappers for frame extraction.

### Testing

The test suite covers:
- **Color Processing**: RGB/HSL conversion, sampling, and round-trip conversions
- **Configuration Validation**: All plugin settings and edge cases
- **HueClient Integration**: REST API parsing, error handling, and retry logic
- **Bridge discovery API**: Authenticated bridge discovery with local-address filtering
- **HueStreamer Protocol**: Binary packet construction, color encoding, and coordinate mapping

#### Running Tests

```bash
# Run all tests
dotnet test

# Run tests with detailed output
dotnet test --verbosity detailed

# Run tests with code coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test class
dotnet test --filter FullyQualifiedName~ColorProcessingTests
dotnet test --filter FullyQualifiedName~HueClientTests
dotnet test --filter FullyQualifiedName~HueStreamerTests
```

The test suite does not require a physical Hue bridge or FFmpeg installation; bridge calls,
packet construction, and playback lifecycle paths are exercised with deterministic test
doubles. A real bridge is only needed for an end-to-end playback check after installation.

#### Test Coverage

Tests are automatically run in CI/CD on:
- Push to main/master/develop branches
- Pull requests
- Manual workflow dispatch

See `.github/workflows/dotnet-ci.yml` for the full CI/CD configuration.

### Troubleshooting

* **Registration fails:** press the physical Link button immediately before clicking
  **Link Bridge**. Hue registration and all v2 REST requests use the bridge's HTTPS API;
  current bridge firmware no longer supports the old HTTP endpoint. The configured target
  must be a private/local bridge address or a .local mDNS name.
* **Discovery finds no bridge:** the Jellyfin server must be able to reach the local network;
  use a private IP or .local host name manually when the discovery service cannot see your bridge.
* **No areas are listed:** verify the bridge IP and App Key, then click **Refresh
  Entertainment Areas**. The selected area must contain color-capable lights.
* **Lights stop updating:** check that `ffmpeg` and `openssl` are available to the Jellyfin
  service account and inspect the Jellyfin server log for `Hue Sync` and `FFmpeg` entries. If a
  frame stream ends or fails, or startup cannot complete, the plugin now rolls back immediately,
  restores saved lights—including color-temperature/mirek mode where applicable—and deactivates the area automatically. If repeated DTLS writes and
  reconnect attempts fail, synchronization also stops instead of leaving the lights frozen,
  restores/deactivates safely, and retains the failure diagnostic in the Live Sync Status panel.
  If FFmpeg remains running but stops producing complete frames, the configured FFmpeg Stall
  Timeout ends synchronization through the same cleanup path; increase it for slow storage or
  hardware decoding, or lower it to recover faster from a stuck pipeline.
* **A mapping is ignored:** enabled mappings must include a valid bridge address, App Key,
  Client Key, and Entertainment Area ID. A mapping with **Enable Hue Sync for this user**
  unchecked intentionally leaves that user's playback unchanged; users without a mapping use
  the default bridge.

### Performance Benchmarks

The project includes BenchmarkDotNet benchmarks for performance-critical operations:

```bash
# Run all benchmarks
cd Jellyfin.Plugin.Hue.Benchmarks
dotnet run -c Release -- --all

# Run specific benchmark suite
dotnet run -c Release -- --color   # Color processing benchmarks
dotnet run -c Release -- --packet  # Packet building benchmarks
```

Benchmarks measure:
- RGB ↔ HSL color conversion (single and batch)
- Region sampling for light positions
- Brightness, saturation, hue-shift, and output-brightness adjustments
- HueStream packet construction
- Color change detection

## Recent Changes

### Version 1.5.30 (Current)
- **Per-user playback profiles**: Choose Cinema Mode, dim level, and pause behavior independently per mapping; blank fields inherit the global settings

### Version 1.5.29
- **Per-user cinema profiles**: Choose Cinema Mode independently per mapping and optionally set that user's dim level; blank fields inherit the global settings

### Version 1.5.28
- **Per-user color profiles**: Override Brightness Boost, Color Saturation, Hue Shift, and Output Brightness for an individual mapping; blank fields inherit the global settings

### Version 1.5.27
- **Global hue shift**: Rotate synced colors -180° to 180° for room-specific correction or creative palettes while preserving lightness

### Version 1.5.26
- **Output brightness control**: Cap synced light brightness from 0-100% after boost/saturation while preserving color balance

### Version 1.5.25
- **Configurable video deinterlacing**: Choose Off for progressive video, Auto for flagged interlaced frames, or On for sources that need forced deinterlacing; the active mode is reported in Live Sync Status

### Version 1.5.24
- **Configurable video fit modes**: Stretch (backward-compatible), Fit with letterbox bars, or Crop to fill the selected sampling resolution while preserving source aspect ratio

### Version 1.5.23
- **Configurable frame sampling resolution**: Choose 80×45 for the lowest CPU use, 160×90 for the compatible default, or 320×180 for finer spatial detail; the active resolution is reported in Live Sync Status

### Version 1.5.22
- **Configurable color sampling modes**: Choose stable Average neighborhood sampling, sharper CenterWeighted sampling, or exact CenterPixel sampling per playback session

### Version 1.5.21
- **Session-safe light restoration**: Keep-colors pause/resume no longer overwrites the original playback-start light snapshot, and service shutdown restores saved state before releasing Hue output

### Version 1.5.20
- **Configurable pause behavior**: Keep the last synced colors or restore the original captured light state when playback pauses; the previous keep-last-colors behavior remains the default
- **Pause lifecycle diagnostics**: Live Sync Status reports the selected pause behavior and preserves the playback item while paused

### Version 1.5.19
- **Unified network recovery**: The existing Network Retry Attempts setting now controls both Hue REST retries and DTLS reconnect attempts, including an explicit zero-retry mode

### Version 1.5.18
- **Temporal color smoothing**: Optionally blend up to 90% of the previous frame into new colors to reduce flicker while preserving immediate blackout transitions

### Version 1.5.17
- **Configurable color sampling**: Tune the neighborhood sampled around each Hue channel from 1-50% to balance fine detail against visual stability; the default remains 15%

### Version 1.5.16
- **Credential-safe mapping edits**: Per-user mapping reads report credential presence without exposing App/Client Keys, and blank key fields preserve existing credentials during edits
- **Scoped configuration flow**: Default settings load/save through `/HueSync/Configuration` without round-tripping per-user mappings through the browser

### Version 1.5.15
- **Mode-aware scene restoration**: Color-temperature lights return to their saved mirek state, while lights without color resources are restored without an invalid XY payload

### Version 1.5.14
- **FFmpeg stall recovery**: Configurable 1-60 second frame timeout with startup grace now ends stalled pipelines, restores lights, deactivates the area, and preserves an actionable Live Sync Status error

### Version 1.5.13
- **Startup rollback**: Partial startup failures and cancellations now dispose unowned FFmpeg streams, restore lights, deactivate the area, and preserve the diagnostic before playback moves on

### Version 1.5.12
- **Bounded stream recovery**: Repeated DTLS send failures now terminate sync safely, restore lights, deactivate the area, and publish an actionable error instead of leaving stale output active

### Version 1.5.11
- **Terminal cleanup**: Ended or failed FFmpeg frame streams now restore lights, stop the DTLS process, and deactivate the entertainment area without waiting for a later playback-stop event

### Version 1.5.10
- **Per-user opt-out**: Disable Hue sync for selected Jellyfin users without requiring or storing bridge credentials for that mapping; unmapped users continue using the default bridge

### Version 1.5.9
- **Safe runtime stop**: Administrators can stop the active Hue sync from the live status panel without stopping playback; saved lights are restored and the session remains suppressed until playback ends
- **Session-safe lifecycle**: Stale playback stop notifications cannot reset a newer playback session after a manual stop

### Version 1.5.8
- **DTLS setup probe**: Test Connection can now verify the Client Key and send a low-intensity probe packet through the selected entertainment area before playback

### Version 1.5.7
- **Live runtime status**: The configuration page now shows lifecycle state, active bridge/area, frame count, sync duration, and FFmpeg/DTLS health with automatic refresh
- **Actionable diagnostics**: Startup, pause, stop, bridge, and video-pipeline failures are surfaced as sanitized status messages without exposing Hue credentials

### Version 1.5.6
- **Connection diagnostics**: Test bridge reachability and selected-area readiness without starting playback

### Version 1.5.5
- **Mapping management**: Explicit Edit and Cancel controls for per-user bridge mappings
- **Credential-safe area loading**: The configuration UI sends app keys in the request body instead of URLs

### Version 1.5.4
- **Bridge discovery**: Authenticated `/HueSync/DiscoverBridge` endpoint and one-click discovery for default and per-user mappings

### Version 1.5.3
- **Hue HTTPS compatibility**: Link-button registration uses the TLS-protected bridge API required by current firmware
- **Scoped certificate handling**: Self-signed bridge certificates are accepted only for private/local bridge addresses; public discovery uses normal TLS validation
- **Local bridge target validation**: Configuration and API mapping inputs reject public hosts and malformed URLs
- **.NET 8 runtime compatibility**: Plugin JSON and encoding dependencies stay on the .NET 8 servicing line and pass the vulnerability scan

### Version 1.5.0
- **Playback lifecycle hardening**: Stop, pause, resume, and shutdown paths serialize cleanup and bridge deactivation safely
- **Bridge resilience**: Transient HTTP/network failures are retried while authentication and input errors fail fast
- **Process safety**: FFmpeg ownership/health checks and DTLS reconnects are safe across repeated starts and stops
- **Configuration validation**: Per-user mappings validate addresses, credentials, area IDs, and duplicate users

### Version 1.4.0
- **Per-User Bridge Mappings**: Map different Jellyfin users to different Hue bridges and entertainment areas
- **Multi-Room Support**: Perfect for households with multiple viewing locations
- **Fallback Behavior**: Users without mappings automatically use default bridge settings

### Version 1.3.0
- **Scene Restoration**: Automatically saves and restores original light states
- **Advanced Color Processing**: Brightness boost, saturation, hue shift, output brightness, and blackout detection
- **Network Resilience**: Retry logic with exponential backoff for HTTP operations
- **Status API**: New `/HueSync/Status` endpoint for monitoring sync state
- **Cinema Mode**: Automatic light dimming during playback
- **Performance**: ~30-50% reduction in unnecessary updates during static scenes

### Version 1.2.0
- Cinema mode with configurable dim levels
- Automatic DTLS reconnection with exponential backoff
- Health monitoring for FFmpeg and OpenSSL processes
- Configuration validation with helpful error messages

### Version 1.1.0
- Fixed critical coordinate mapping bug for proper light positioning
- Added pause/resume support for playback
- Improved error handling and process management
- Enhanced logging and XML documentation

## License
This project is licensed under the GPL-3.0 License.

## Acknowledgements
*   Inspired by [HarmonizeProject](https://github.com/MCPCapital/HarmonizeProject) for the video analysis and DTLS logic.
*   Built on the [Jellyfin Plugin SDK](https://github.com/jellyfin/jellyfin-plugin-template).
