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
| **Discover Bridge** | Return every private bridge found by the Hue discovery service and bounded local mDNS (`_hue._tcp.local`); the address field offers all candidates so multi-room mappings can choose the correct bridge. |
| **Link Bridge** | Press the physical button on your Bridge, then click this button to auto-generate keys. |
| **Test Connection** | Verify bridge credentials and, when selected, that the entertainment area has controllable channels. If a Client Key is present, also run a short DTLS stream probe that captures a complete light-state snapshot before activation and restores it afterward. Disconnecting or canceling the request stops the diagnostic lifecycle safely. |
| **System Diagnostics** | Run a non-mutating local health check for saved configuration validity, FFmpeg/OpenSSL availability and versions, active bridge lifecycle contention, and playback/diagnostic readiness. **Validate Saved Targets** additionally checks every enabled default/inherited/custom bridge mapping for reachability, selected-area presence, and controllable channels without opening a DTLS stream. |
| **Backup and Restore** | Export global settings, per-user profiles, and saved color scenes as a credential-safe JSON document. Import is atomic, preserves matching stored keys on the same server, and includes an in-page password-field wizard for explicit replacement keys during migrations. |
| **Live Sync Status** | Show the active Jellyfin user, selected bridge/area, captured profiles, effective FPS, sent/skipped/failed stream updates, reconnect attempts, seek-recovery restarts, frame health, cleanup warnings, and safe per-session stop controls while playback is running. Distinct mapped bridges/areas can be streamed concurrently. |
| **Startup recovery** | If the plugin or Jellyfin service starts while an unpaused video is already playing, recover the active session at Jellyfin's current position so viewers do not need to stop and restart playback. |
| **Completed-session summary** | Keep the most recent video session's outcome, duration, frame/packet telemetry, reconnects, seek recoveries, and cleanup warnings visible after playback ends; summaries never contain bridge credentials or playback tokens. Audio-only and other non-video playback is ignored safely. |
| **Hue App Key** | "Username" for the REST API. The key is stored server-side and is never returned by the configuration endpoint; leave the field blank to keep it, or use Link Bridge to replace it. |
| **Hue Client Key** | "ClientKey" for the streaming API. The key is stored server-side and is never returned by the configuration endpoint; leave the field blank to keep it, or use Link Bridge to replace it. |
| **Entertainment Area ID** | UUID of the specific area to sync. |
| **Target FPS** | Frames per second to process (Default: 20). Lower = less CPU. |
| **Frame Sampling Resolution** | RGB frame size used for color extraction: 80×45 (lowest CPU), 160×90 (default), or 320×180 (more spatial detail). |
| **Video Fit Mode** | Stretch (default), Fit with letterbox bars, or Crop to fill the 16:9 sampling frame while preserving source aspect ratio. |
| **Video Deinterlacing** | Off (default), Auto for frames flagged as interlaced, or On to deinterlace every frame. Useful for television and DVD sources. |
| **Color Sampling Breadth** | Size of the neighborhood sampled around each Hue channel's screen position (1-50%, default: 15%). Smaller values follow fine detail; larger values reduce noise. |
| **Color Sampling Mode** | Average (default), center-weighted, or center-pixel sampling for balancing ambient stability against detail. |
| **Temporal Color Smoothing** | Blend the previous frame into new colors to reduce flicker (0-90%, default: 0%). Higher values create smoother but slower transitions. |
| **Performance Profile** | Target FPS, frame resolution, video fit, deinterlacing, sampling breadth/mode, and smoothing can be overridden per user while blank fields inherit global settings. |
| **Execution Profile** | GPU acceleration, additional FFmpeg flags, FFmpeg stall timeout, and Hue REST/DTLS retry attempts can be overridden per user while blank fields inherit global settings; the effective policy is captured when playback starts. |
| **Channel Profile** | Select a comma-separated subset of entertainment channel IDs globally; use **Load Channel IDs** after choosing an area to read available IDs, then edit the list. Blank global fields drive every channel. A populated per-user channel override takes precedence, while blank per-user fields inherit the global selection. The active selection is captured with the playback session. |
| **Solid Color Preview** | Choose a color, brightness, and 1-30 second duration to preview the default target (or the current mapping target). The plugin captures and restores the selected lights automatically, stops promptly when canceled, and refuses to overlap active playback. |
| **Cleanup diagnostics** | Light capture and restoration retry each light using the captured network policy. Playback, probes, and previews refuse to activate when the snapshot is incomplete; one shared bridge lease also prevents playback and diagnostics from overlapping. Partial restoration or failed entertainment-area deactivation remains visible as a sanitized warning in Live Sync Status and probe/preview results. DTLS startup, writes, and reconnects stop with playback or diagnostic cancellation. |
| **Saved Color Scenes** | Save up to 50 named color, brightness, and duration presets. Apply a saved scene to the default target or the current mapping; presets contain no bridge credentials. |
| **Restore Light State After Sync** | Save and restore each light's original state after playback. Per-user mappings can override this policy while blank fields inherit the global setting. |
| **Hue Shift** | Rotate synced colors around the hue wheel (-180° to 180°, default: 0°) to correct a room's color bias or create a creative palette. |
| **RGB Channel Gains** | Independently scale red, green, and blue channels from 50-200% (default: 100%) for room-specific white-balance correction before saturation and hue processing. |
| **Output Brightness** | Final 0-100% brightness scale applied after boost, saturation, and hue shift (default: 100%). Use it to cap room brightness without changing color balance. |
| **Blackout Threshold** | Set all channels to black when the sampled frame's average brightness falls below 0-255 (default: 15). Set to 0 to disable blackout handling. |
| **Color Change Threshold** | Suppress Hue packets until the RGB16 color delta reaches 0-255 (default: 10). Lower values follow subtle changes; higher values reduce network traffic. |
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

When different viewers are watching at the same time, sessions mapped to different
bridge/entertainment-area targets run independent FFmpeg and DTLS pipelines concurrently.
Two sessions targeting the same area remain serialized because a Hue entertainment area
accepts one active stream. The Live Sync Status panel lists each active session, target,
and stop control; stopping one session leaves the other viewer and Jellyfin playback running.
Existing mappings can be updated with **Edit** or removed with **Delete**. Editing keeps the
selected Jellyfin user fixed while allowing the bridge address, credentials, and area to change.
To keep a per-user profile while using the global bridge, create or edit an enabled mapping and
leave Bridge Address, App Key, Client Key, and Entertainment Area blank; those target fields
inherit the global configuration. Enter all four fields when targeting a custom bridge.
To leave a user's playback unchanged, edit or create that mapping and uncheck **Enable Hue Sync
for this user**; bridge credentials are not required for a disabled mapping. Users without a
mapping continue to use the default bridge settings.

When editing an enabled mapping, the existing App Key and Client Key are kept securely on the
server and are not displayed in the browser. Leave those fields blank to keep the stored keys, or
enter replacement keys to rotate them. The mapping list reports credential presence without
revealing the key values.

Each mapping can also define optional per-user color, performance, execution, channel, and restoration profiles. Color fields cover
Brightness Boost, RGB Channel Gains, Color Saturation, Hue Shift, Output Brightness, Blackout Threshold, and
Color Change Threshold. Performance
fields cover Target FPS, Frame Resolution, Video Fit, Deinterlacing, Sampling Breadth/Mode, and
Temporal Color Smoothing. Execution fields cover GPU acceleration, additional FFmpeg flags, the FFmpeg
stall timeout, and Hue REST/DTLS retry attempts. The restoration profile chooses whether that user captures and restores
the original per-light state or uses the cinema/default cleanup behavior. Channel fields accept a comma-separated
list of Hue entertainment channel IDs; the global **Load Channel IDs** button reads the selected area's available IDs
from the bridge, and only the chosen channels are captured, dimmed, streamed, and restored. A per-user channel
override can narrow or replace the global selection; a blank per-user field inherits the global profile, and a blank
global field drives every channel.
The mapping **Test Connection** action validates the list against the selected area and, when a Client Key is present, probes only the selected channels; stale IDs are reported before any DTLS stream is opened.
The **Preview Current Color** action uses the color, brightness, duration, and saved-scene controls from the global preview section with that mapping's bridge, area, and channel profile; a blank mapping channel field inherits the global selection.
Leave any profile field
blank to inherit the current global setting; populated overrides apply only to that user's playback,
including when the mapping uses the default bridge.

Mappings can also override Cinema Mode, its dim level, and pause behavior. Choose **Inherit global
setting** to keep the defaults, or enable/disable cinema mode, set a separate 0-100% dim level, and
choose whether pausing that user's playback keeps the last colors or restores the captured light state.

### Admin API

The configuration page uses authenticated administrator endpoints under `/HueSync`:

| Endpoint | Purpose |
| :--- | :--- |
| `GET /HueSync/DiscoverBridge` | Discover private/local Hue Bridge addresses. `ipAddress` remains the first result for compatibility; `ipAddresses` contains every distinct candidate. |
| `GET /HueSync/DiscoverBridges` | Discover every private/local Hue Bridge address in one pass. The configuration page uses this route for multi-room bridge selection. |
| `POST /HueSync/EntertainmentAreas` | Load areas with `{ "ipAddress": "...", "appKey": "", "userId": "..." }` in the request body. A blank key may use the stored global key or the stored custom mapping key only when both the bridge target and user ID match. |
| `POST /HueSync/EntertainmentChannels` | Load the selected area's channel IDs with `{ "ipAddress": "...", "appKey": "", "userId": "...", "entertainmentAreaId": "..." }`; stored credentials remain server-side when the target matches. |
| `POST /HueSync/TestConnection` | Verify bridge credentials and optional entertainment-area readiness. Supplying `clientKey` also runs a short activate/send/stop DTLS probe with light-state restoration; supplying `channelIds` (comma-separated) validates and probes only that channel profile. `userId` enables matching redacted custom mapping credentials. |
| `POST /HueSync/Preview` | Display a bounded solid color and restore the selected lights. Request fields include `ipAddress`, `appKey`, `clientKey`, optional `userId`, `entertainmentAreaId`, optional `channelIds`, `red`, `green`, `blue` (0-255), `brightnessPercent` (0-100), and `durationSeconds` (1-30). Active playback must be stopped first; matching stored mapping credentials may be used without sending secrets to the browser. |
| `GET /HueSync/ColorPresets` | List saved, credential-free color scenes sorted by name. |
| `POST /HueSync/ColorPresets` | Save or update a named color scene with `name`, RGB values, `brightnessPercent`, and `durationSeconds`; names are case-insensitive and values are validated. |
| `DELETE /HueSync/ColorPresets/{name}` | Delete one saved color scene by name. |
| `GET /HueSync/Status` | Read sanitized runtime state, active Jellyfin user and target, active performance/color/execution/channel/restoration profile, frame count, effective FPS, stream packet counters, reconnect attempts, seek-recovery restart count and last seek position, FFmpeg/DTLS health, cleanup warnings, the credential-free `lastSession` summary, whether the current sync can be stopped safely, and a `sessions` array for concurrent playback workers. |
| `GET /HueSync/Diagnostics` | Run a non-mutating, cancellation-aware local prerequisite check for configuration validity, FFmpeg/OpenSSL versions, bridge lifecycle contention, and playback/diagnostic readiness. No bridge credentials are returned. |
| `GET /HueSync/TargetDiagnostics` | Validate every saved default, inherited, and enabled custom bridge target without mutating bridge state; reports reachability, selected-area presence, controllable channel counts, credential presence, and sanitized readiness messages. |
| `GET /HueSync/Configuration/Export` | Download a credential-safe JSON backup containing global settings, per-user profile fields, target labels, credential-presence flags, and saved color scenes. Secret values are never included. |
| `POST /HueSync/Configuration/Import` | Atomically restore an export document. Matching stored global/mapping keys are preserved when omitted; explicit global or mapping keys may be supplied for migration, and invalid documents leave the current configuration unchanged. The configuration page keeps replacement keys in memory only and sends them once in this request. Active playback must be stopped first. |
| `POST /HueSync/Stop` | Stop Hue output for the current playback session, restore lights, and leave Jellyfin playback running. Pass `playSessionId` to stop one listed concurrent session. |
| `GET/POST /HueSync/Configuration` | Read or update default plugin settings, including the global channel profile, without serializing per-user mappings or global credentials to the configuration page. Responses expose `hasAppKey`/`hasClientKey` presence flags; blank key fields preserve stored values and `clearStoredCredentials` explicitly removes both global keys. |
| `GET /HueSync/EntertainmentAreas` | Legacy query-string-compatible area loading for existing clients; `userId` can select a matching stored custom mapping, but POST is preferred so keys do not appear in URLs. |
| `GET/POST /HueSync/UserMappings` | List or save per-user bridge mappings, sync enable flags, optional playback/color-threshold/performance/execution/channel/restoration-profile overrides; GET responses redact stored credentials and report `InheritsDefaultBridge`. |
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
- **Per-user Profiles**: Playback, color, performance, execution, channel, restoration inheritance, validation, and API persistence
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
* **Discovery finds no bridge:** the Jellyfin server must be able to reach the local network and
  allow mDNS/Bonjour traffic. Cloud discovery is tried first, then the plugin queries the local
  `_hue._tcp.local` service for private addresses. If both paths are unavailable, use a private
  IP or `.local` host name manually.
* **No areas are listed:** verify the bridge IP and App Key, then click **Refresh
  Entertainment Areas**. The selected area must contain color-capable lights.
* **Lights stop updating:** run **System Diagnostics** first to confirm that `ffmpeg` and `openssl` are available to the Jellyfin
  service account and inspect the Jellyfin server log for `Hue Sync` and `FFmpeg` entries. If a
  frame stream ends or fails, or startup cannot complete, the plugin now rolls back immediately,
  restores saved lights—including color-temperature/mirek mode where applicable—and deactivates the area automatically. If repeated DTLS writes and
  reconnect attempts fail, synchronization also stops instead of leaving the lights frozen,
  restores/deactivates safely, and retains the failure diagnostic in the Live Sync Status panel.
  Before any output mutation, playback also requires a complete light-state snapshot; a bridge
  that cannot return every selected light is reported as a startup error instead of leaving a
  partial restoration plan.
  Light restoration retries each light using the active Network Retry Attempts policy. If the
  bridge remains unavailable or area deactivation fails, Live Sync Status shows a cleanup warning
  with the restored/failed count so the remaining lights can be recovered manually.
  If FFmpeg remains running but stops producing complete frames, the configured FFmpeg Stall
  Timeout ends synchronization through the same cleanup path; increase it for slow storage or
  hardware decoding, or lower it to recover faster from a stuck pipeline.
* **A mapping is ignored:** an enabled custom mapping must include a valid bridge address, App
  Key, Client Key, and Entertainment Area ID. To use the global target, leave all mapping target
  fields blank; the mapping list will show **Inherited from global**. A mapping with **Enable Hue
  Sync for this user** unchecked intentionally leaves that user's playback unchanged.
* **A saved target needs attention:** open **System Diagnostics** and click **Validate Saved
  Targets**. The report checks each enabled default or per-user target separately, including
  inherited mappings, without starting a DTLS stream or changing light state. Missing keys,
  unreachable bridges, deleted areas, and empty entertainment areas are reported directly.

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

### Version 1.5.66 (Current)
- **Credential-entry migration wizard**: Backup and Restore now offers password fields for replacement global and per-user bridge keys, so cross-server imports do not require hand-editing JSON; entered values remain in page memory and are never exported

### Version 1.5.65
- **Credential-safe configuration portability**: Export global settings, user profiles, target labels, and saved color scenes without serializing App/Client Keys; atomically import with same-server key preservation and explicit migration-key support

### Version 1.5.64
- **Concurrent multi-room playback**: Independent sessions mapped to distinct Hue targets stream concurrently while same-target lifecycles remain serialized

### Version 1.5.63
- **Startup playback recovery**: Recover active unpaused video sessions when the plugin starts, including independent target workers

### Version 1.5.62
- **Completed-session telemetry**: Retain a sanitized last-session summary and ignore audio-only playback before lifecycle startup

### Version 1.5.61
- **Seek-aware playback recovery**: large forward skips and backward seeks restart video capture at the viewer's current position without restoring the active light scene
- **Seek telemetry**: Live Sync Status and `GET /HueSync/Status` report capture restart count and the last seek position without exposing bridge credentials

### Version 1.5.60
- **Playback quality telemetry**: Live Sync Status and `GET /HueSync/Status` now report effective FPS, successfully sent updates, color-threshold skips, failed sends, and DTLS reconnect attempts for the active session

### Version 1.5.59
- **Saved target validation**: System Diagnostics can now validate every enabled default, inherited, and custom bridge mapping in one non-mutating pass, showing reachability, area selection, channel counts, and sanitized credential status

### Version 1.5.58
- **Active mapping visibility**: Live Sync Status and the status API now identify the Jellyfin user driving the current bridge/area, making per-user routing verifiable during playback

### Version 1.5.57
- **Complete multi-bridge discovery**: Cloud and local mDNS results are combined and de-duplicated; the configuration page offers every private bridge candidate for global or per-user mapping setup

### Version 1.5.56
- **Credential-safe custom mapping diagnostics**: Existing custom mappings can refresh areas and channels or run Test Connection/Preview without re-entering stored secrets; resolution requires the matching mapping user ID and bridge target

### Version 1.5.55
- **Credential-safe global configuration**: Configuration responses expose only global credential presence flags; blank secret edits preserve stored keys, explicit clearing is supported, and implicit fallback is restricted to the configured bridge target
- **Credential-safe administrator UI**: The browser keeps global App/Client Keys blank while still loading areas, channels, connection tests, and previews through the protected server-side fallback

### Version 1.5.54
- **Cancellation-safe DTLS lifecycle**: Playback and diagnostics cancel OpenSSL startup, color writes, delayed reconnects, and area reactivation; stopped streams cannot resurrect a background tunnel

### Version 1.5.53
- **Local bridge discovery**: Discover Bridge now falls back to bounded mDNS/DNS-SD (`_hue._tcp.local`) when cloud discovery is unavailable or incomplete

### Version 1.5.52
- **Activation-failure cleanup**: Test Connection and solid-color Preview now deactivate the entertainment area and restore captured lights even when an activation response fails or is ambiguous

### Version 1.5.51
- **Playback-aligned diagnostics**: System Diagnostics now probes Jellyfin's configured FFmpeg encoder path before falling back to `PATH`, so bundled encoder installations report accurate readiness

### Version 1.5.50
- **End-to-end request cancellation**: Bridge discovery, registration, entertainment-area reads, area configuration reads, and streaming-area activation now honor the originating API request token
- **Activation cleanup safety**: If cancellation arrives while the bridge activation request is in flight, the diagnostic still deactivates the area and restores the captured light state before releasing the lifecycle lease

### Version 1.5.49
- **System Diagnostics**: Add a non-mutating setup and runtime report for configuration validity, FFmpeg/OpenSSL availability and versions, lifecycle contention, and playback/diagnostic readiness
- **Actionable setup feedback**: Add a configuration-page diagnostics panel that explains missing prerequisites without exposing bridge credentials

### Version 1.5.48
- **Cancellation-safe diagnostics**: Test Connection probes and solid-color previews observe request cancellation during capture, activation waits, DTLS startup, and preview holds
- **Guaranteed cleanup on cancellation**: A canceled diagnostic still stops the stream and restores the entertainment area and captured light state before releasing the shared bridge lease

### Version 1.5.47
- **Shared bridge lifecycle gate**: Playback and Test Connection/preview diagnostics now reserve one process-wide bridge lease, closing the race between the API's point-in-time playback check and startup
- **Safe contention handling**: Playback reports a diagnostic-busy error, while diagnostics return the existing busy result during active playback; leases are released on stop, pause, rollback, shutdown, and restoration

### Version 1.5.46
- **Process-wide diagnostic lock**: The shared Test Connection/preview gate now lives in the singleton tester service, so concurrent API requests cannot bypass serialization by resolving separate tester instances

### Version 1.5.45
- **Diagnostic lifecycle serialization**: Test Connection probes and solid-color previews now reserve one shared lifecycle; a concurrent request returns a clear busy result without touching the bridge
- **Bridge-state isolation**: Prevent overlapping capture, activation, DTLS, deactivation, and restoration operations from corrupting one another's snapshots

### Version 1.5.44
- **Safe state capture**: Capture each selected light with the configured retry policy, report attempted/captured/failed counts, and deduplicate shared light IDs
- **Mutation guardrails**: Playback, Test Connection, and solid-color previews refuse to activate or dim the area unless every selected light has a restorable snapshot
- **Startup rollback**: A failed capture cannot trigger cinema-mode output during cleanup, while existing sanitized restoration and deactivation warnings remain visible

### Version 1.5.43
- **Restoration reliability**: Retry each saved light independently using the active network policy, report aggregate restore counts, and preserve a sanitized cleanup warning when any light or the entertainment-area deactivation fails
- **Probe and preview safety**: Test Connection and solid-color previews now report cleanup failures instead of claiming success after an incomplete restore
- **Runtime diagnostics**: Surface cleanup warnings in Live Sync Status and `GET /HueSync/Status` without exposing bridge credentials

### Version 1.5.42
- **Inherited-target visibility**: Per-user mapping lists now label global-bridge inheritance and show the global default target instead of presenting a valid inherited mapping as incomplete
- **Inherited-target editing**: Editing a mapping that uses the global bridge loads the global entertainment areas and uses the global target for connection tests, previews, and channel discovery while keeping the saved mapping fields blank
- **Mapping API diagnostics**: `GET /HueSync/UserMappings` reports `InheritsDefaultBridge` without exposing credentials

### Version 1.5.41
- **Default-bridge mapping inheritance**: Enabled per-user mappings can leave Bridge Address, App Key, Client Key, and Entertainment Area blank to inherit the global target; switching from a custom bridge clears stale credentials and area data

### Version 1.5.40
- **Reusable color scenes**: Save, apply, update, and delete up to 50 named solid-color presets from the preview controls; scenes are global, credential-free, and available for default or mapping previews
- **Preset API**: Add authenticated CRUD endpoints with name uniqueness, value validation, and persistence regression coverage
- **Preview reliability**: Re-activate the entertainment area before DTLS reconnects during probes and previews

### Version 1.5.39
- **Solid color preview**: Add an administrator color picker with brightness and duration controls for the default target and current per-user mapping, backed by a bounded DTLS preview that saves and restores light state
- **Preview API**: Add `POST /HueSync/Preview` with channel-profile validation, active-playback protection, sanitized results, and regression coverage for color conversion and API wiring

### Version 1.5.38
- **Global channel profiles**: Choose a default entertainment-channel subset from the main configuration, inherit it from mappings with blank channel overrides, and keep explicit per-user selections authoritative

### Version 1.5.37
- **Channel-aware connection diagnostics**: Mapping Test Connection validates selected channel IDs against the area, reports stale IDs before probing, and limits the DTLS probe and temporary state capture to the selected channels

### Version 1.5.36
- **Per-user channel profiles**: Load available entertainment channel IDs for a selected area, choose which IDs each mapped user drives, and keep capture, cinema dimming, streaming, restoration, and Live Sync Status aligned with the captured selection

### Version 1.5.35
- **Per-user execution profiles**: Override GPU acceleration, additional FFmpeg flags, stall timeout, and Hue REST/DTLS retry attempts per mapping while inheriting global defaults; the captured policy and sanitized diagnostics are shown in Live Sync Status

### Version 1.5.34
- **Per-user color scene policies**: Override blackout and color-change thresholds per mapping while inheriting global defaults; the complete active color policy is captured at playback start and shown in Live Sync Status

### Version 1.5.33
- **Per-user light-state restoration**: Choose whether each mapped user captures/restores original light state or follows the cinema/default cleanup behavior; blank fields inherit the global setting and the active policy is reported in Live Sync Status

### Version 1.5.32
- **Per-user performance profiles**: Override capture FPS, frame resolution, video fit, deinterlacing, sampling, and temporal smoothing per mapping while preserving global inheritance and reporting the active session profile

### Version 1.5.31
- **RGB white-balance calibration**: Adjust red, green, and blue channel gains globally or per mapping (50-200%, neutral 100%) before saturation and hue processing

### Version 1.5.30
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
