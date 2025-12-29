# Jellyfin Philips Hue Sync Plugin

<div align="center">

![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9.0+-00A4DC?style=flat-square&logo=jellyfin)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet)
![License](https://img.shields.io/badge/license-GPL--3.0-green?style=flat-square)

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
| **Hue Bridge IP** | The local IP address of your bridge. |
| **Link Bridge** | Press the physical button on your Bridge, then click this button to auto-generate keys. |
| **Hue App Key** | "Username" for the REST API (auto-filled). |
| **Hue Client Key** | "ClientKey" for the streaming API (auto-filled). |
| **Entertainment Area ID** | UUID of the specific area to sync. |
| **Target FPS** | Frames per second to process (Default: 20). Lower = less CPU. |
| **Custom Flags** | Add hardware acceleration flags here (e.g. `-hwaccel auto`). |
| **Enable Real-time Sync** | Master toggle for the sync feature. |

### Generating Hue Credentials (Manual Fallback)
If the **Link Bridge** button doesn't work for you, you can generate keys manually:
1.  Go to `https://<BRIDGE_IP>/debug/clip.html`
2.  Press the **Link Button** on your Hue Bridge.
3.  Post to `/api` with body: `{"devicetype":"jellyfin_plugin#server", "generateclientkey":true}`
4.  Copy the `username` (App Key) and `clientkey` (Client Key) from the response.

## Development

### Building
Requirements: .NET 8.0 SDK.

```bash
cd Jellyfin.Plugin.Hue
dotnet build --configuration Release
```

The plugin DLL will be generated at:
`bin/Release/net8.0/Jellyfin.Plugin.Hue.dll`

> **Note**: You only need this one file. The other DLLs in that folder are dependencies that Jellyfin already provides.

### Project Structure
*   `Configuration/`: Plugin settings UI and logic.
*   `Service/`: Background service for monitoring playback.
*   `Hue/`: Logic for communicating with the Hue Bridge (REST & DTLS).
*   `Video/`: FFmpeg wrappers for frame extraction.

### Testing

The test suite covers color space conversion, configuration validation, edge cases, and round-trip conversions.

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
```

#### Test Coverage

Tests are automatically run in CI/CD on:
- Push to main/master/develop branches
- Pull requests
- Manual workflow dispatch

See `.github/workflows/dotnet-ci.yml` for the full CI/CD configuration.

## Recent Changes

### Version 1.3.0 (Current)
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
