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
| **Hue Bridge IP** | The private/local IP address of your bridge (or a .local mDNS host name). |
| **Link Bridge** | Press the physical button on your Bridge, then click this button to auto-generate keys. |
| **Hue App Key** | "Username" for the REST API (auto-filled). |
| **Hue Client Key** | "ClientKey" for the streaming API (auto-filled). |
| **Entertainment Area ID** | UUID of the specific area to sync. |
| **Target FPS** | Frames per second to process (Default: 20). Lower = less CPU. |
| **Custom Flags** | Add hardware acceleration flags here (e.g. `-hwaccel auto`). |
| **Enable Real-time Sync** | Master toggle for the sync feature. |

### Per-User Bridge Mappings

For multi-user Jellyfin setups, you can map individual users to different Hue bridges and entertainment areas. This is useful when:
- Different rooms have their own Hue bridges
- Different users watch in different locations (e.g., living room vs. bedroom)
- You want to disable sync for certain users

To configure per-user mappings:
1. Go to the **Per-User Bridge Mappings** section in the plugin settings
2. Select a Jellyfin user from the dropdown
3. Enter the Bridge IP for that user's location
4. Click **Link Bridge** and press the physical button on that bridge
5. Select the entertainment area for that bridge
6. Click **Add User Mapping**

Users without a mapping will use the default bridge settings configured above.

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
* **No areas are listed:** verify the bridge IP and App Key, then click **Refresh
  Entertainment Areas**. The selected area must contain color-capable lights.
* **Lights stop updating:** check that `ffmpeg` and `openssl` are available to the Jellyfin
  service account and inspect the Jellyfin server log for `Hue Sync` and `FFmpeg` entries.
* **A mapping is ignored:** the mapping must include a valid bridge address, App Key, Client
  Key, and Entertainment Area ID. Users without a complete mapping use the default bridge.

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
- Brightness and saturation adjustments
- HueStream packet construction
- Color change detection

## Recent Changes

### Version 1.5.3 (Current)
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
- **Advanced Color Processing**: Brightness boost, saturation control, and blackout detection
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
