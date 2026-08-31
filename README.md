# Jellyfin Philips Hue Sync Plugin

<div align="center">

![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9.0+-00A4DC?style=flat-square&logo=jellyfin)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet)
![License](https://img.shields.io/badge/license-GPL--3.0-green?style=flat-square)

**Immersive lighting for your Jellyfin Media Server.**

</div>

## Overview
The **Philips Hue Sync Plugin** for Jellyfin brings the theater experience to your home. It synchronizes your Philips Hue lights with supported video and audio media in real-time, extending screen colors or audio-reactive energy into your room.

Unlike simple "cinema mode" automations that just dim the lights, this plugin actively analyzes video frames or bounded audio windows and uses the **Hue Entertainment API** to drive your lights with low latency. Video scopes follow the on-screen action; audio scopes map low/mid/high energy to the configured visualizer.

## How It Works
1.  **Playback Detection**: The plugin monitors Jellyfin for supported video and audio playback events.
2.  **Media Analysis**: When playback starts, it uses a bounded FFmpeg pipeline to extract video colors or audio energy in real-time, according to the selected media scope.
3.  **Light Mapping**: It maps video colors or audio-reactive bands to your specific Hue Entertainment Area configuration (Left, Right, Center, etc.).
4.  **DTLS Streaming**: Color updates are streamed securely and instantly to your Hue Bridge via DTLS (Datagram Transport Layer Security).

## Prerequisites
*   **Jellyfin Server**: Version 10.9.0 or newer.
*   **Philips Hue Bridge**: V2 (Square) bridge required for Entertainment API support.
*   **Hue Lights**: Color-capable Hue lights added to an **Entertainment Area** in the Hue App.
*   **Server Dependencies**: 
    *   `ffmpeg` (Usually bundled with Jellyfin, or system installed)
    *   Managed Bouncy Castle DTLS transport (included in the release package; no OpenSSL process is required)

## Installation

### Manual Installation
1.  Open the latest [GitHub release](https://github.com/AnalyticETH/HarmonizeProject-Jellyfin/releases) and download `jellyfin-plugin-hue-release.zip`, its `jellyfin-plugin-hue-release.zip.sha256` sidecar, and the matching `jellyfin-plugin-hue-release.manifest.json` plus `.sha256` sidecar.
2.  Verify the archive before extracting it:
    ```bash
    sha256sum --check --strict jellyfin-plugin-hue-release.zip.sha256
    sha256sum --check --strict jellyfin-plugin-hue-release.manifest.json.sha256
    ```
3.  Navigate to your Jellyfin plugins directory:
    *   **Linux**: `/var/lib/jellyfin/plugins`
    *   **Windows**: `%ProgramData%\Jellyfin\Server\plugins`
    *   **Docker**: `/config/plugins`
4.  Create a folder named `HueSync` and extract `jellyfin-plugin-hue-release.zip` into it.
5.  Optionally inspect the manifest to audit the exact source commit, packaged file hashes, and locked NuGet dependency graph.
6.  Confirm that `BouncyCastle.Cryptography.dll`, `Jellyfin.Plugin.Hue.dll`, and `meta.json` are directly inside the `HueSync` folder.
7.  Restart Jellyfin.

## Configuration
Go to **Dashboard -> Plugins -> Philips Hue Sync** to configure the plugin.

The configuration page starts with a keyboard-friendly **Configuration sections** index. Use it to jump directly to live status, diagnostics, backup/restore, bridge and target setup, scene tools, scheduling, per-user mappings, or advanced settings without traversing the entire page.

| Setting | Description |
| :--- | :--- |
| **Hue Bridge Address** | The private/local IP address of your bridge (or a .local mDNS host name). |
| **Discover Bridge** | Return up to 256 private bridges found by the Hue discovery service and bounded local mDNS (`_hue._tcp.local`); the address field offers every candidate within that safety ceiling so multi-room mappings can choose the correct bridge. |
| **Verify/Trust Certificate** | Probe the selected local bridge without credentials, compare the SHA-256 fingerprint with the physical bridge identity, and explicitly pin it before Link Bridge, tests, playback, or scheduled scenes can send an App Key. Changed or unpinned certificates fail closed. |
| **Link Bridge** | Press the physical button on your Bridge, then click this button to auto-generate keys. |
| **Test Connection** | Verify bridge credentials and, when selected, that the entertainment area has controllable channels. If a Client Key is present, also run a short DTLS stream probe that captures a complete light-state snapshot before activation and restores it afterward. While a probe is running, the page exposes **Cancel Active Diagnostic**; disconnecting, canceling, or leaving the page stops the diagnostic lifecycle safely. |
| **System Diagnostics** | Run a non-mutating local health check for saved configuration validity, FFmpeg availability/version, managed DTLS readiness, a bounded PCM audio-capture probe, active bridge lifecycle contention, and playback/diagnostic readiness. Audio capture is required only when the active global or per-user playback scope can start audio. **Validate Saved Targets** additionally checks every enabled default/inherited/custom bridge mapping for reachability, selected-area presence, controllable channels, and stale IDs in the effective global/per-user channel profile without opening a DTLS stream. **Export Support Bundle** collects these results, credential-free configuration metadata, runtime telemetry, bounded playback/scheduler history, and scheduler status into one reviewable JSON document. **Cancel Active Diagnostics** safely stops either long-running check or support-bundle target validation, and leaving the page requests the same cancellation. |
| **Backup and Restore** | Export global settings, per-user profiles, saved scene effects, saved scene playlists with repeat passes and `Sequential`/`Shuffle` playback order, scheduled scene cues, and history-retention preferences—including Solid/Pulse/Rainbow/Candle/Temperature/Aurora/Fire/Ocean/Lightning/Starlight/Matrix metadata, bounded 25-400% rates, optional per-step effect-speed/fade-in/fade-out transitions and fade curves, one-time dates, optional per-cue hold durations, priorities, recurring date windows, and portable Fixed/SolarNoon/Sunrise/Sunset/CivilDawn/CivilDusk/NauticalDawn/NauticalDusk/AstronomicalDawn/AstronomicalDusk timing with bounded offsets and decimal coordinates—as a credential-safe JSON document. **Validate Import** runs the same normalization, dependency, and full-configuration preflight without changing the server, including planned totals, active-playback readiness, and a credential-safe added/removed/changed/unchanged diff for every imported object collection. Import is atomic, preserves matching stored keys only when the global bridge target is unchanged, and requires replacement keys or an explicit clear operation before accepting a changed target; the in-page password-field wizard supplies migration keys without echoing them. |
| **Live Sync Status** | Show the active Jellyfin user, effective playback media scope, selected bridge/area, captured profiles including effective audio sensitivity, noise gate, low/mid/high band centers and gains, response smoothing, audio-band spread, beat-pulse response, release, and onset threshold, visualizer palette, spatial routing, and Mono/Stereo/Left/Right source-channel mode, effective FPS, sent/skipped/failed stream updates, reconnect attempts, seek-recovery restarts, frame health, cleanup warnings, and safe per-session stop controls while playback is running. Distinct mapped bridges/areas can be streamed concurrently. |
| **Recent Hue Sessions** | Review and filter the configured 1-25 most recent completed sync sessions, including outcome, target, duration, quality counters, and cleanup/error warnings. Export a credential-free JSON troubleshooting document, clear history without stopping playback, or optionally retain the sanitized window across Jellyfin restarts. The administrator can reduce the retained window without changing active playback. |
| **Startup recovery** | If the plugin or Jellyfin service starts while an unpaused video is already playing, recover the active session at Jellyfin's current position so viewers do not need to stop and restart playback. |
| **Completed-session summary** | Keep the most recent media session's outcome, duration, frame/audio telemetry, reconnects, seek recoveries, and cleanup warnings visible after playback ends; summaries never contain bridge credentials or playback tokens. Unsupported media is ignored safely, while Audio and AllMedia scopes process supported audio playback. |
| **Hue App Key** | "Username" for the REST API. The key is stored server-side and is never returned by the configuration endpoint; leave the field blank to keep it only when retaining the configured bridge target, or use Link Bridge/replacement credentials when changing targets. |
| **Hue Client Key** | "ClientKey" for the streaming API. The key is stored server-side and is never returned by the configuration endpoint; leave the field blank to keep it only when retaining the configured bridge target, or use Link Bridge/replacement credentials when changing targets. |
| **Entertainment Area ID** | UUID of the specific area to sync. |
| **Playback Media Scope** | Choose whether Hue Sync starts for all video items (default), movies only, TV episodes only, other video such as music/home videos, audio-only music, or all video and audio. Audio scopes decode a bounded PCM window and map low/mid/high energy to a configurable Hue visualizer; Audio Visualizer Sensitivity (25-400%), Audio Noise Gate (0-100%, default 0%), the low/mid/high band centers (20-3900 Hz, defaults 90/420/1600), Audio Low/Mid/High Band Gains (0-200%, defaults 100/100/100%), Audio Response Smoothing (0-90%, default 0%), Audio Band Spread (0-100%, default 0%), Audio Beat Pulse (0-100%, default 0%), Audio Beat Pulse Release (0-100%, default 0%), Audio Beat Pulse Threshold (0-100%, default 0%), Audio Visualizer Palette (Spectrum, Band, Warm, Cool, or Monochrome; Spectrum is the default), Audio Spatial Routing (Spatial, Uniform, or Mirror; Spatial is the default), and Audio Source Channels (Mono, Stereo, Left, or Right; Mono is the default) control the reactive presentation independently from final brightness, and changing the scope does not interrupt an active session. The noise gate suppresses windows below the normalized mixed RMS threshold to reduce hiss; zero preserves the analyzer. Band gains compensate for room or source imbalance without changing shared RMS loudness; zero mutes a band and 100% preserves the default balance. Response smoothing blends each analysis window with the prior one to reduce spectral flicker; zero remains immediate and higher values retain a bounded tail. A higher release keeps enabled beat flashes visible for a bounded tail; zero remains an instant transient. The beat-pulse threshold ignores small normalized rises before the attack; zero preserves the original sensitivity. Stereo preserves left/right source energy for Spatial routing; Left and Right isolate one source channel; Uniform and Mirror remain symmetric. |
| **Per-User Playback Media Scope** | Each per-user mapping can inherit the global scope or override it for that user's playback, including audio-only or all-media reactive mode, Audio Visualizer Sensitivity, Noise Gate, ordered low/mid/high band centers and gains, Response Smoothing, Audio Band Spread, Audio Beat Pulse, Release, and Threshold, Audio Visualizer Palette, Audio Spatial Routing, and Audio Source Channels, so a room or family profile can tune audio independently. Existing sessions still clean up normally when the effective scope changes. |
| **Target FPS** | Frames per second to process (Default: 20). Lower = less CPU. |
| **Frame Sampling Resolution** | RGB frame size used for color extraction: 80×45 (lowest CPU), 160×90 (default), or 320×180 (more spatial detail). |
| **Video Fit Mode** | Stretch (default), Fit with letterbox bars, or Crop to fill the 16:9 sampling frame while preserving source aspect ratio. |
| **Video Deinterlacing** | Off (default), Auto for frames flagged as interlaced, or On to deinterlace every frame. Useful for television and DVD sources. |
| **Color Sampling Breadth** | Size of the neighborhood sampled around each Hue channel's screen position (1-50%, default: 15%). Smaller values follow fine detail; larger values reduce noise. |
| **Color Sampling Mode** | Average (default), center-weighted, or center-pixel sampling for balancing ambient stability against detail. |
| **Screen / Room Orientation** | Normal (default), MirrorHorizontal, MirrorVertical, Rotate180, Rotate90Clockwise, or Rotate90Counterclockwise to correct a physically mirrored, flipped, or rotated entertainment-area layout; the effective orientation applies to video sampling and audio spatial routing and can be overridden per user. |
| **Temporal Color Smoothing** | Blend the previous frame into new colors to reduce flicker (0-90%, default: 0%). Higher values create smoother but slower transitions. |
| **Performance Profile** | Target FPS, frame resolution, video fit, deinterlacing, sampling breadth/mode, Screen / Room Orientation, color smoothing, audio sensitivity/noise gate, low/mid/high band centers and gains, audio response smoothing, Audio Band Spread, Audio Beat Pulse, Release, and Threshold, Audio Visualizer Palette, Audio Spatial Routing, and Audio Source Channels can be overridden per user while blank fields inherit global settings. |
| **Execution Profile** | GPU acceleration, additional FFmpeg flags, FFmpeg stall timeout, and Hue REST/DTLS retry attempts can be overridden per user while blank fields inherit global settings; the effective policy is captured when playback starts. |
| **Channel Profile** | Select a comma-separated subset of entertainment channel IDs globally; use **Load Channel IDs** after choosing an area to read available IDs, then edit the list. Blank global fields drive every channel. A populated per-user channel override takes precedence, while blank per-user fields inherit the global selection. Profile text is bounded to 4,096 characters before parsing, and the active selection is captured with the playback session. |
| **Automatic Playback Device Routes** | Nest up to 25 exact-match routes under a per-user mapping using the administrator route editor. **Discover Playback Devices** reads a bounded, user-scoped list of recent Jellyfin sessions so you can select the exact, case-sensitive `SessionInfo.DeviceId` instead of guessing it; Add / Update / Remove Route then manages the nested bridge, area, channel, and credential fields without hand-editing JSON. **Load Route Channel IDs** fills the selected route's channel profile without changing the outer user profile; Test Connection and Preview Current Color use the same route-specific channels, then inherit the outer mapping and global profiles when the route field is blank. Selecting a configured route makes Refresh Areas, Load Route Channel IDs, Test Connection, and Preview Current Color use that route's stored bridge credentials without returning keys to the browser. Routes can select a different private Hue bridge, App Key, Client Key, entertainment area, and channel subset for each playback device. Unknown devices fall back to the user's existing target and then the global target. Mapping summaries and exports never return nested secrets; same-server edits preserve stored keys, while cross-server imports accept explicit DeviceTargetCredentials. |
| **Scene Effect Preview** | Choose a color, **Solid**, breathing **Pulse**, hue-cycling **Rainbow**, warm flickering **Candle**, warm-to-cool **Temperature**, drifting green-to-violet **Aurora**, high-energy **Fire**, rolling blue/cyan **Ocean**, electric blue/white **Lightning**, cool white/blue **Starlight**, or green/cyan cascading **Matrix** effect, bounded 25-400% animation speed, brightness, 1-30 second duration, and optional 0-30 second Fade in and Fade out. Select **Linear**, **SmoothStep**, **EaseIn**, **EaseOut**, or **EaseInOut** to shape both fades; missing legacy values remain Linear. The setting is preserved through saved scenes, playlists, scheduled cues, direct/all-target previews, API/UI responses, CSV/iCalendar metadata, and credential-free backup/restore. |
| **Saved Scene and Target Lifecycle** | Capture current light color from a default, selected, or all-enabled target set; weighted aggregate capture seeds the editor without exposing credentials. Preview saved scenes on the default target, a selected subset, or all enabled mappings with sequential state restoration and per-target outcomes. Compose up to 20 scenes into playlists with 1-10 repeat passes, saved-order or stable UTC-date-seeded Shuffle playback, optional per-step RGB channel, effect, bounded hold, brightness, effect-speed, fade-in/fade-out, and fade-curve overrides; blank/null values inherit the saved scene and `0` hold inherits its duration. A playlist preview validates the complete expanded plan before bridge mutation, then renders every step for each target in one capture/activate/DTLS/deactivate/restore lifecycle. Rename and duplicate scenes/playlists with reference migration, inspect credential-free dependencies, and use atomic bulk duplicate/delete actions that block unsafe referenced changes. Cancel active previews safely; active playback still blocks overlap. |
| **Preview target selection** | The raw color preview and saved-scene preview target pickers can select the default bridge, every enabled target, or a deliberate subset of enabled mappings with optional default-bridge inclusion. The explicit All Targets buttons remain broadcast overrides, and selected-target previews resolve credentials and channel profiles server-side. |
| **Cleanup diagnostics** | Light capture and restoration retry each light using the captured network policy. Playback, probes, and previews refuse to activate when the snapshot is incomplete; one shared bridge lease also prevents playback and diagnostics from overlapping. Partial restoration or failed entertainment-area deactivation remains visible as a sanitized warning in Live Sync Status and probe/preview results. Scheduled snapshots are persisted without Hue credentials before activation, survive Jellyfin restarts, retry with bounded backoff, and remain visible through credential-free pending-cleanup telemetry until deactivation and restoration both succeed. DTLS startup, writes, and reconnects stop with playback or diagnostic cancellation. |
| **Scheduled Scene Cues** | Run either one saved Solid, Pulse, Rainbow, Candle, Temperature, Aurora, Fire, Ocean, Lightning, Starlight, or Matrix scene or a saved-scene playlist with Saved order or stable UTC-date-seeded Shuffle automatically at a chosen **Fixed**, **SolarNoon**, **Sunrise**, **Sunset**, **CivilDawn**, **CivilDusk**, **NauticalDawn**, **NauticalDusk**, **AstronomicalDawn**, or **AstronomicalDusk** time in the cue's selected time zone against the global target, one enabled user mapping, or a deliberate subset of enabled mappings with optional default-bridge inclusion. Solar cues use decimal latitude/longitude and a bounded -720 to +720 minute offset (for example, `Sunrise +15m`, `CivilDusk -20m`, or `AstronomicalDawn +10m`); the unshifted solar date remains the recurrence/date-window anchor even when the offset moves the actual local run across midnight. Civil, nautical, and astronomical dawn/dusk use the sun's 6°, 12°, and 18°-below-horizon events for blue-hour, navigation, and dark-sky scheduling. Polar day/night dates with no sunrise, sunset, or twilight event are skipped safely. Playlist cues use the selected order for each configured repeat pass, preserve each scene's effect plus its optional per-step RGB channel, hold, brightness, and effect-speed overrides (blank values and 0-second hold inherit the scene values), and report playback order, pass count, expanded total duration, effective per-step colors/brightness/speeds/durations, and saved positions in status/history, with the expanded upcoming occurrence plan (repeat/original position, RGB channels, brightness, effect speed, duration, transitions, and cumulative offset) available through JSON/CSV/iCalendar/UI. Each single-scene cue inherits the saved scene's bounded 25-400% animation speed. Choose daily recurrence, weekly weekday recurrence, monthly calendar-day recurrence, monthly ordinal-weekday recurrence such as first Monday or last Friday, or yearly calendar-date recurrence for birthdays and holidays; day 31 is clamped to the final day in shorter months. Set `Every` to 1 for the normal cadence or to a higher value for every-N-day/week/month/year schedules; values above 1 require the recurring cue's start date as the portable cadence anchor. Use a one-time calendar date for an exact event, or optional inclusive start/end windows and up to 100 excluded dates for recurring holidays and blackout days. Leave a cue's hold duration blank or `0` to inherit the saved scene duration; playlist steps use their own saved per-step override while a single-scene cue may set a bounded 1-30 second override. Inherited effect, speed, fade-in, and fade-out transitions run within the effective duration, with fade-out clamped after fade-in when a cue is shorter. Set a finite maximum of 1-365 executions when a recurring cue should stop automatically; its persisted run counter survives Jellyfin restarts, while `0` remains unlimited. Use **Reset Run Counter** to clear the counter and re-enable an exhausted cue without deleting its retained audit history. Use **Enable/Disable Cue** to pause one rule without editing its schedule, **Skip Next Cue** to omit exactly one upcoming automatic occurrence while preserving future recurrence, and **Duplicate Cue** to create a fresh, disabled copy with the same rules for safe variations. Skipped occurrences are persisted, reversible before the cue is due, recorded in sanitized history, and never suppress manual **Run Now**; if clearing the skip marker cannot be persisted, the cue is left untouched rather than running unexpectedly. One-time cues disable after their skipped occurrence, and failed one-time completion persistence restores their in-memory enabled state. The scheduler monitor and credential-free conflict report flag upcoming duration overlaps before they can delay a later cue, including effective durations, priorities, time-zone-aware instants, and ordering guidance. An optional global missed-cue recovery window (0-120 minutes) can recover the most recent occurrence missed during a short restart/outage without replaying an old burst; recovered runs are labeled in status and history. Choose a per-cue playback policy override of Inherit, Skip, or Defer; Inherit uses the global setting. Choose the playback conflict scope globally: **Any active playback** preserves the historical process-wide conflict check, while **only the cue's target room(s)** lets independent bridge/area targets continue scheduling; unresolved targets conservatively use the process-wide block. Configure the playback conflict policy globally: **Skip** preserves the historical immediate attempt behavior, while **Defer** persists one due occurrence per cue across a Jellyfin restart, waits up to a bounded 1-120 minute window for playback to end, and retries without consuming the cue's execution limit; restored, expired, and completed deferred occurrences remain visible in credential-free status/history and expired waits are recorded as skipped. Successful one-time runs disable themselves so a restart cannot repeat the event. Pause or resume recurring automation without deleting cues; individual **Run Now** remains available while paused and exposes **Cancel Running Cue** until the restorative run ends. Every cue is a short restorative preview, active playback wins, duplicate polling within a selected-zone minute is suppressed, and DST transitions are handled deterministically. The scheduler monitor reports readiness, effect, speed, playlist step count/repeat count/playback order/total duration, automation state, recovery-window state, playback conflict policy, deferred-cue window and pending/restored state, recurrence details and interval, finite execution limits, cue-local/UTC next run, effective hold duration and fade, timing mode/offset/coordinates, a selectable 7/31/90/366-day report horizon, cue-scoped occurrence and calendar filters, downloadable credential-free occurrence and conflict JSON reports, conflict warnings, a credential-free iCalendar download, active state, run count, last outcome (including recovered/skipped/deferred/restored-after-restart runs), cleanup warnings, and an optional 1-100-entry bounded cue-run history that can survive Jellyfin restarts, with cue- and outcome-filtered administrator and JSON-export views. |
| **Playlist Step Overrides** | Each saved playlist step can override RGB channels (blank/null inherits), hold duration (0 inherits), brightness (blank inherits), fade-in, fade-out (blank/null inherits), and fade curve (`Linear`, `SmoothStep`, `EaseIn`, `EaseOut`, or `EaseInOut`; inherited by default) within bounded ranges. Effective colors and transitions are clamped to their safe ranges; expanded preview, scheduled-occurrence, and history plans expose repeat/original positions, effective RGB and curves, plus cumulative `startOffsetSeconds`. |
| **Playlist Step Effect Overrides** | The optional `stepEffects` array is parallel to `presetNames`; each nullable value selects Solid, Pulse, Rainbow, Candle, Temperature, Aurora, Fire, Ocean, Lightning, Starlight, or Matrix, while blank/null inherits the referenced scene effect. Effective effects are shown in preview, occurrence, and retained-history step plans and preserved through API CRUD, duplication, exports, and credential-free backup/restore. |
| **Playlist Step Effect-Speed Overrides** | The optional `stepEffectSpeedPercent` array is parallel to `presetNames`; each value is nullable and inherits the referenced scene's 25-400% effect speed when omitted or null. Effective speeds are applied to playlist previews and scheduled runs and retained in step results, upcoming plans, history, API responses, exports, duplication, and credential-free backup/restore. |
| **Playlist Fade-Curve Overrides** | The optional `stepTransitionCurves` array is parallel to `presetNames`; each value is nullable and inherits the referenced scene curve when omitted or null. Effective curves are applied to easing-capable previews and retained in playlist step results, upcoming occurrence plans, history, API responses, exports, duplication, and credential-free backup/restore. |
| **Restore Light State After Sync** | Save and restore each light's original state after playback. Per-user mappings can override this policy while blank fields inherit the global setting. |
| **Hue Shift** | Rotate synced colors around the hue wheel (-180° to 180°, default: 0°) to correct a room's color bias or create a creative palette. |
| **RGB Channel Gains** | Independently scale red, green, and blue channels from 50-200% (default: 100%) for room-specific white-balance correction before saturation and hue processing. |
| **Output Brightness** | Final 0-100% brightness scale applied after boost, gains, temperature, gamma, contrast, saturation, and hue shift (default: 100%). Use it to cap room brightness without changing color balance. |
| **Gamma Correction** | Adjust mid-tone brightness after channel gains from 0.5-2.5 (default: 1.0). Values above 1.0 lift mid-tones; values below 1.0 deepen them. |
| **Contrast** | Expand or compress contrast around mid-gray from 50-200% (default: 100%) after gamma. Higher values deepen shadows and brighten highlights; lower values compress the range. |
| **Color Temperature** | Shift white balance from 1000-20000 K after channel gains (default: 6500 K neutral daylight). Lower values warm the room; higher values cool it. |
| **Blackout Threshold** | Set all channels to black when the sampled frame's average brightness falls below 0-255 (default: 15). Set to 0 to disable blackout handling. |
| **Dark Scene Behavior** | When a frame is below the blackout threshold, either **Blackout** lights (legacy default) or **KeepLastColors** to preserve the last streamed colors and avoid redundant writes. |
| **Color Change Threshold** | Suppress Hue packets until the RGB16 color delta reaches 0-255 (default: 10). Lower values follow subtle changes; higher values reduce network traffic. |
| **When Playback Is Paused** | Keep the last synced colors (default), restore the original light state captured at playback start, or dim each captured light to the configured cinema level while paused. Sync resumes automatically; final restoration still follows **Restore Light State After Sync**. |
| **Custom Flags** | Add only decoder, threading, or hardware-tuning flags here (for example `-hwaccel vaapi -threads 2` or `-c:v h264_cuvid`). Values may use quoted groups and escaped quotes/backslashes; configuration validation rejects extra inputs, outputs, protocols, headers, filters, scripts, paths, and network-capable options before playback starts. |
| **FFmpeg Stall Timeout** | Stop synchronization and restore the lights when no complete video frame arrives within 1-60 seconds (Default: 5). FFmpeg startup receives an extended codec-initialization grace period. |
| **Network Retry Attempts** | Number of retry attempts for Hue REST requests and DTLS stream recovery (0-10, default: 3). |
| **Enable Real-time Sync** | Master toggle for the sync feature. Saving while disabled immediately stops active Hue sessions, restores captured lights, and re-enabling waits for a fresh playback event. |
| **Temperature Scene Effect** | Choose **Temperature** in the scene editor to sweep deterministically from warm 2200 K candlelight to cool 6500 K daylight and back. The scene's RGB seed controls output level, while effect speed, duration, transitions, playlists, scheduled cues, and target restoration work the same as the other effects. |
| **Aurora Scene Effect** | Choose **Aurora** in the scene editor to drift deterministically through green, cyan, blue, and violet hues. The scene's RGB seed controls output level, while effect speed, duration, transitions, playlists, scheduled cues, and target restoration work the same as the other effects. |
| **Fire Scene Effect** | Choose **Fire** in the scene editor for a deterministic red/amber/yellow flicker with independent channel phases. The RGB seed controls output level, while effect speed, duration, transitions, playlists, scheduled cues, and target restoration work the same as the other effects. |
| **Ocean Scene Effect** | Choose **Ocean** in the scene editor for a deterministic rolling blue/cyan wave with independent channel phases. The RGB seed controls output level, while effect speed, duration, transitions, playlists, scheduled cues, and target restoration work the same as the other effects. |
| **Lightning Scene Effect** | Choose **Lightning** in the scene editor for deterministic electric blue/white storm flashes with independent channel phases. The RGB seed controls output level, while effect speed, duration, transitions, playlists, scheduled cues, and target restoration work the same as the other effects. |
| **Starlight Scene Effect** | Choose **Starlight** in the scene editor for deterministic cool white/blue twinkles with sharp glints and independent channel phases. The RGB seed controls output level, while effect speed, duration, transitions, playlists, scheduled cues, and target restoration work the same as the other effects. |

| **Matrix Scene Effect** | Choose **Matrix** in the scene editor for deterministic green/cyan cascading data-rain trails with smooth envelopes and independent channel phases. The RGB seed controls output level, while effect speed, duration, transitions, playlists, scheduled cues, and target restoration work the same as the other effects. |

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

Users without a mapping will use the default bridge settings configured above. The administrator mapping multi-selection controls can enable, disable, or delete up to 50 mappings atomically; scheduled-cue or saved-playlist target references block disabling and deletion, incomplete custom targets block enabling, and disabling clears stored custom bridge targets.

When different viewers are watching at the same time, sessions mapped to different
bridge/entertainment-area targets run independent FFmpeg and DTLS pipelines concurrently.
Two sessions targeting the same area remain serialized because a Hue entertainment area
accepts one active stream. The Live Sync Status panel lists each active session, target,
and stop control; stopping one session leaves the other viewer and Jellyfin playback running.
Existing mappings can be updated with **Edit** or removed with **Delete**. Editing keeps the
selected Jellyfin user fixed while allowing the bridge address, credentials, and area to change.
Use **View References** beside a mapping to inspect the credential-free scheduled-cue and saved-playlist target lists
before disabling or deleting it; referenced mappings remain protected until those cues are changed
or removed. Mapping saves and deletes are transactional, so a persistence failure restores the
previous in-memory mapping collection.
To keep a per-user profile while using the global bridge, create or edit an enabled mapping and
leave Bridge Address, App Key, Client Key, and Entertainment Area blank; those target fields
inherit the global configuration. Enter all four fields when targeting a custom bridge.
To stop this user's active Hue session immediately, edit or create that mapping and uncheck
**Enable Hue Sync for this user**; the saved change restores captured lights and does not
auto-start again when re-enabled. Bridge credentials are not required for a disabled mapping.
Users without a mapping continue to use the default bridge settings.

When editing an enabled mapping, the existing App Key and Client Key are kept securely on the
server and are not displayed in the browser. Leave those fields blank to keep the stored keys, or
enter replacement keys to rotate them. The mapping list reports credential presence without
revealing the key values.

Each mapping can also define optional per-user color, performance, execution, channel, and restoration profiles. Audio Visualizer Sensitivity covers the low/mid/high reactive envelope (25-400%) independently from final Brightness Boost, while Audio Low/Mid/High Band Gains (0-200%, default inherited) rebalance each band before palette rendering without changing shared RMS loudness, Audio Response Smoothing (0-90%, default inherited) blends adjacent analysis windows to reduce spectral flicker, Audio Band Spread (0-100%, default inherited) averages nearby frequencies around each configured center, Audio Beat Pulse (0-100%, default inherited) adds a transient brightness response to rising energy, Audio Beat Pulse Release (0-100%, default inherited) controls the bounded tail, and Audio Beat Pulse Threshold (0-100%, default inherited) filters small energy rises before the attack. Audio Visualizer Palette (Spectrum, Band, Warm, Cool, or Monochrome; default inherited) controls the color presentation, Audio Spatial Routing (Spatial, Uniform, or Mirror; default inherited) controls whether the bands follow physical positions, share one mix, or mirror around the room center, and Audio Source Channels (Mono, Stereo, Left, or Right; default inherited) controls whether captured energy is mixed, preserved across Spatial placement, or isolated to one source channel. Color fields cover
Brightness Boost, RGB Channel Gains, Color Saturation, Hue Shift, Output Brightness, Gamma Correction, Contrast, Color Temperature, Blackout Threshold, Dark Scene Behavior, and
Color Change Threshold. Performance
fields cover Target FPS, Frame Resolution, Video Fit, Deinterlacing, Sampling Breadth/Mode, Screen / Room
Orientation, and Temporal Color Smoothing. Orientation can correct a mirrored, vertically flipped, or
upside-down physical Hue layout for both video sampling and audio spatial routing. Execution fields cover GPU acceleration, additional FFmpeg flags, the FFmpeg
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
choose whether pausing that user's playback keeps the last colors, restores the captured light state,
or dims to that user's effective cinema level.

### Admin API

The configuration page uses authenticated administrator endpoints under `/HueSync`:

The built-in Jellyfin `/Plugins/{pluginId}/Configuration` JSON route is intentionally
not a supported configuration path for this plugin: raw global, per-user, and nested
device-route credentials are omitted from its response, and generic replacement writes
are rejected. Use `/HueSync/Configuration` for the validated, credential-preserving,
lifecycle-aware settings contract, or `/HueSync/Configuration/Export` and `Import` for
credential-safe migration.

Configuration migration is intentionally two-phase: `ValidateImport` returns a credential-safe
`configurationVersion` token for the exact live snapshot that was reviewed, and `Import` must send
that value as `expectedConfigurationVersion`. A missing token returns `400`; if another
administrator changes any importable setting, credential, mapping, saved scene, playlist, or cue
after validation, the import returns `409` and must be validated again. Runtime and retained
telemetry are excluded from the token so normal history updates do not invalidate an approval.

`POST /HueSync/Preview` and `POST /HueSync/ColorPresets` accept `effect: "Temperature"`, `effect: "Aurora"`, `effect: "Fire"`, `effect: "Ocean"`, `effect: "Lightning"`, `effect: "Starlight"`, and `effect: "Matrix"`. They also accept `transitionCurve: "Linear"`, `"SmoothStep"`, `"EaseIn"`, `"EaseOut"`, or `"EaseInOut"`; blank/omitted legacy values remain Linear. The server validates and canonicalizes each effect and curve, and the stream tester applies the bounded easing to fade-in and fade-out transitions without changing the credential-free target-selection contract. Current-light capture can read one target or a selected/all-target set and seed the editor with a weighted aggregate sample.

Direct `POST /HueSync/ScenePlaylists` and `POST /HueSync/SceneSchedules` bodies are capped at 1 MiB before playlist or schedule normalization, keeping the bounded step, target, and excluded-date collections from being traversed for oversized administrator requests. Bulk actions, target-selection previews/captures, and other small administrator control bodies are capped at 64 KiB before model binding; persisted collection and target-count limits still apply after that boundary.

| Endpoint | Purpose |
| :--- | :--- |
| `GET /HueSync/DiscoverBridge` | Discover private/local Hue Bridge addresses. `ipAddress` remains the first result for compatibility; `ipAddresses` contains every distinct candidate, including the interface scope on link-local IPv6 results. |
| `GET /HueSync/DiscoverBridges` | Discover every private/local Hue Bridge address in one pass. The configuration page uses this route for multi-room bridge selection and preserves link-local IPv6 interface scopes. |
| `GET /HueSync/BridgeCertificate?ipAddress=...` | Read a local bridge's certificate without sending credentials and return its SHA-256 fingerprint. Review the fingerprint out of band before explicitly trusting it; no pin is accepted implicitly. |
| `POST /HueSync/BridgeCertificate/Trust` | Explicitly pin a freshly probed local bridge certificate with `{ "ipAddress": "...", "fingerprint": "<sha256>", "confirm": true }`. The server compares the supplied value with a new credential-free probe and stores no change when they differ. Certificate pins are required before Link Bridge, Test Connection, playback, or scene automation can send App Keys. |
| `DELETE /HueSync/BridgeCertificate/Trust?ipAddress=...` | Forget every stored certificate pin for one validated private bridge host. This does not contact the bridge; no fingerprint remains trusted, and credential-bearing requests fail closed until an administrator explicitly trusts a replacement fingerprint. |
| `GET /HueSync/PlaybackDevices` | Return at most 256 recent, credential-free Jellyfin playback-device identities (`userId`, exact case-sensitive `deviceId`, display labels, client/device metadata, active state, and last activity). An optional valid `userId` query scopes enumeration server-side. The elevated configuration page uses this to build nested route choices; bridge keys and playback titles are never returned. Invalid user IDs return `400`; a session-service failure returns a sanitized `503`; a null session enumeration is treated as an empty result. |
| `POST /HueSync/Register` | Complete the administrator Link Bridge flow after pressing the physical bridge Link Button and explicitly trusting its certificate fingerprint. Send `{ "ipAddress": "..." }` for a private bridge IP or `.local` host name; invalid/public or unpinned targets are rejected before any credential-bearing request. The response contains the generated App Key and Client Key for immediate administrator setup—treat both values as secrets and never log or share them. |
| `POST /HueSync/EntertainmentAreas` | Load areas with `{ "ipAddress": "...", "appKey": "", "userId": "...", "deviceId": "..." }` in the request body. A blank key may use the stored global, custom mapping, or exact case-sensitive nested device-route key only when the bridge target and owning identity match; an explicit device ID never falls back to another target. |
| `POST /HueSync/EntertainmentChannels` | Load the selected area's channel IDs with `{ "ipAddress": "...", "appKey": "", "userId": "...", "deviceId": "...", "entertainmentAreaId": "..." }`; stored credentials remain server-side when the exact user/device/bridge route matches. Responses with more than 256 channels are rejected before downstream enumeration. |
| `POST /HueSync/TestConnection` | Verify bridge credentials and optional entertainment-area readiness. Supplying `clientKey` also runs a short activate/send/stop DTLS probe with light-state restoration; supplying `channelIds` (comma-separated, maximum 4,096 characters) validates and probes only that channel profile. `userId` enables matching redacted custom mapping credentials, and optional `deviceId` selects the exact case-sensitive nested route without falling back to another target. |
| `POST /HueSync/Preview` | Display a bounded Solid, Pulse, Rainbow, Candle, Temperature, Aurora, Fire, Ocean, Lightning, Starlight, or Matrix scene effect and restore the selected lights. Request fields include `ipAddress`, `appKey`, `clientKey`, optional `userId`/exact case-sensitive `deviceId`, `entertainmentAreaId`, optional `channelIds` (maximum 4,096 characters), `effect` (`Solid`, `Pulse`, `Rainbow`, `Candle`, `Temperature`, `Aurora`, `Fire`, `Ocean`, `Lightning`, `Starlight`, or `Matrix`; blank/omitted preserves Solid), `effectSpeedPercent` (25-400; 100 is neutral and Solid ignores it), `red`, `green`, `blue` (0-255 seed color), `brightnessPercent` (0-100), `durationSeconds` (1-30), optional `transitionSeconds`/`transitionOutSeconds` (0-30, with their sum no greater than the duration), and optional `transitionCurve` (`Linear`, `SmoothStep`, `EaseIn`, `EaseOut`, or `EaseInOut`; blank/omitted preserves Linear). Use `{ "targetUserIds": ["jellyfin-user-id-1", "jellyfin-user-id-2"], "includeDefaultTarget": true }` without direct target fields for a deliberate selected subset, or use `targetRoutes: [{ "userId": "jellyfin-user-id-1", "deviceId": "living-room-tv" }]` for exact nested playback-device routes, or set `targetAllEnabledMappings: true` to run sequentially on the valid global target plus every distinct enabled custom mapping; selected/all modes use each target's configured channel profile and return credential-free `targetUserIds`, `targetRoutes`, `includeDefaultTarget`, `targetResults`, aggregate channel counts, and independent cleanup warnings. `targetUserIds` and route `userId` values are Jellyfin user IDs, not stable mapping-row IDs. Active playback must be stopped first; matching stored mapping credentials are resolved server-side and never returned to the browser. |
| `POST /HueSync/Preview/CaptureCurrentColor` | Capture one persisted default target, enabled user mapping, or exact nested playback-device route with `{ "targetUserId": "jellyfin-user-id", "targetDeviceId": "living-room-tv" }` (blank user selects the default bridge; a device selection requires its owning user). The server resolves the stored App Key and effective channel profile, reads the selected area's current light states, converts xy or valid mirek values to averaged RGB, retains average brightness, and returns credential-free sample/count fields for the administrator editor. Stale channel profiles, active playback, concurrent diagnostics, incomplete color data, and ambiguous duplicate user mappings are rejected before bridge contact without changing bridge state or returning light IDs/keys. |
| `POST /HueSync/Preview/CaptureCurrentColors` | Capture the default bridge, every distinct enabled target (`{ "targetAllEnabledMappings": true }`), or a selected subset with `{ "targetUserIds": ["jellyfin-user-id-1", "jellyfin-user-id-2"], "includeDefaultTarget": true }`; use `{ "targetRoutes": [{ "userId": "jellyfin-user-id-1", "deviceId": "living-room-tv" }] }` to capture an exact nested playback-device route. Each target uses its saved channel profile and returns an independent credential-free result; successful samples are weighted into aggregate RGB/brightness fields for scene-editor seeding, while stale mappings or bridge failures remain visible per target and do not hide other rooms. Selected routes and all-target capture fail closed before bridge contact when any Jellyfin user has ambiguous duplicate user mappings. |
| `POST /HueSync/Preview/Cancel` | Request cancellation of the active administrator preview or DTLS diagnostic. The configuration page exposes this action for previews and default/per-user Test Connection probes. The response is credential-free; the linked operation deactivates the entertainment area and restores captured light state before ending. |
| `GET /HueSync/ColorPresets` | List saved, credential-free color scenes sorted by name. |
| `GET /HueSync/ColorPresets/{name}/Dependencies` | Inspect one saved scene's credential-free lifecycle dependency graph before editing or deleting it. Returns `canDelete`, dependent playlist and scheduled-cue counts, playlist IDs/names with repeated reference counts, and cue IDs/names/enabled state plus `referenceType` (`DirectScene` or `Playlist`) and the dependent `playlistName`; bridge credentials and target details are never returned. |
| `POST /HueSync/ColorPresets` | Save or update a named scene with `name`, `effect` (`Solid`, `Pulse`, `Rainbow`, `Candle`, `Temperature`, `Aurora`, `Fire`, `Ocean`, `Lightning`, `Starlight`, or `Matrix`), `effectSpeedPercent` (25-400; 100 is neutral), RGB seed values, `brightnessPercent`, `durationSeconds`, and optional `transitionSeconds`/`transitionOutSeconds` (0-30, with their sum no greater than the scene duration); names are case-insensitive, omitted effects and speed remain Solid/100%, values are validated, and persistence failures leave the previous scene collection unchanged. |
| `POST /HueSync/ColorPresets/{name}/Rename` | Rename one saved scene with `{ "newName": "..." }`, preserving all visual/effect metadata and atomically migrating matching playlist `presetNames` and direct scheduled-cue `presetName` references. The new name is validated, must not collide with another scene, and invalid or failed saves leave the existing configuration unchanged. |
| `POST /HueSync/ColorPresets/{name}/Preview` | Preview one saved scene using server-side credentials and channel profiles. Send `{ "targetUserId": "..." }` for the default target (blank) or one enabled mapping, `{ "targetUserIds": ["jellyfin-user-id-1", "jellyfin-user-id-2"], "includeDefaultTarget": true }` for a deliberate selected subset, or `{ "targetRoutes": [{ "userId": "jellyfin-user-id-1", "deviceId": "living-room-tv" }] }` for an exact nested playback-device route, or `{ "targetAllEnabledMappings": true }` to run sequentially across every distinct enabled target. Selected IDs/routes must be enabled and cannot be mixed with the legacy single/all modes. Target IDs are Jellyfin user IDs; stable `mappingId` values are used only by exact-row mapping administration. The response includes the saved visual metadata, selected IDs/routes, default-target inclusion, credential-free per-target outcomes, channel counts, and cleanup warnings; incomplete targets, invalid saved scenes, and active playback are rejected before execution. |
| `POST /HueSync/ColorPresets/BulkPreview` | Preview up to 50 selected saved scenes sequentially with `{ "presetNames": ["..."], "targetUserId": "", "targetUserIds": ["jellyfin-user-id"], "targetRoutes": [{ "userId": "jellyfin-user-id", "deviceId": "living-room-tv" }], "includeDefaultTarget": true, "targetAllEnabledMappings": false }`. Every scene, target, and validation rule is preflighted before the first bridge call; each outcome is credential-free, runtime failures do not prevent later scenes, and cancellation restores the selected targets before stopping. `targetUserIds` and route `userId` values are Jellyfin user IDs, not stable mapping-row IDs. Set `targetAllEnabledMappings: true` to fan out each scene across every distinct enabled target. |
| `POST /HueSync/ColorPresets/{name}/Duplicate` | Create a uniquely named copy of one saved scene, preserving effect, animation speed, RGB/brightness, duration, and fade transitions. The copy is independently editable, leaves scheduled references to the source unchanged, and never returns bridge credentials. |
| `POST /HueSync/ColorPresets/BulkDuplicate` | Atomically create independent copies of up to 50 selected scene names with `{ "presetNames": ["..."] }`. Every name is resolved before mutation; missing names, capacity limits, validation failures, or save failure leave the original scene collection unchanged. Copies receive bounded unique names and credential-free visual summaries. |
| `DELETE /HueSync/ColorPresets/{name}` | Delete one saved color scene by name; returns a conflict with dependent playlist and scheduled-cue counts while any reference remains. |
| `POST /HueSync/ColorPresets/BulkDelete` | Atomically delete up to 50 selected scene names with `{ "presetNames": ["..."] }`. Every name is resolved before mutation; any missing name, playlist/direct/playlist-backed cue dependency, or save failure leaves the entire scene collection unchanged. The credential-free response includes deleted summaries, remaining count, missing names, and blocked dependency details. |
| `GET /HueSync/ScenePlaylists` | List credential-free saved-scene playlists, including scene references, bounded `stepDurationSeconds` overrides (`0` inherits each scene duration), optional `stepBrightnessPercent`, `stepEffects`, and `stepEffectSpeedPercent` overrides (blank/null inherits the saved scene), nullable fade-in/fade-out/curve overrides, repeat passes, canonical `playbackOrder` (`Sequential` or `Shuffle`), selected target IDs/default-target inclusion, target mode/label, and bounded total duration. `stepEffects` accepts Solid, Pulse, Rainbow, Candle, Temperature, Aurora, Fire, Ocean, Lightning, Starlight, or Matrix. The administrator playlist selector renders the bounded total as a human-readable duration before preview or scheduling. |
| `GET /HueSync/ScenePlaylists/{name}/Dependencies` | Inspect one saved playlist's credential-free scheduled-cue dependencies before deletion. Returns `canDelete`, the dependent cue count, and cue IDs/names/enabled state; bridge credentials and target details are never returned. |
| `POST /HueSync/ScenePlaylists` | Save or update a playlist with an optional stable `id`, bounded `name`, up to 20 `presetNames`, parallel `stepDurationSeconds` (0-30; `0` inherits), nullable `stepBrightnessPercent` (0-100), nullable `stepEffects` (the eleven canonical effects), nullable `stepEffectSpeedPercent` (25-400), nullable fade-in/fade-out/curve arrays, bounded `repeatCount` (1-10), `playbackOrder` (`Sequential` or stable date-seeded `Shuffle`), and a default, single, all-enabled, or selected target definition. Null effect/visual values inherit the referenced scene; references, parallel-array lengths, overrides, targets, and expanded duration are validated atomically before persistence. Renaming an existing playlist atomically updates scheduled cues that reference its old name. |
| `POST /HueSync/ScenePlaylists/{name}/Rename` | Rename one saved-scene playlist with `{ "newName": "..." }`, preserving all playlist metadata and atomically migrating every scheduled cue that references the old name. The credential-free response returns the renamed playlist; collisions, invalid references, validation failures, or persistence failures leave the playlist and cue collections unchanged. |
| `POST /HueSync/ScenePlaylists/{name}/Preview` | Preflight the complete expanded repeat/shuffle plan, including effective `stepEffects`, brightness, speed, hold, fades, and curves, before changing the bridge. Each resolved target then runs every ordered step through one captured-state snapshot, entertainment-area activation, continuous DTLS session, deactivation, and restoration. The response retains effective effect and visual metadata, original/repeat positions, cumulative offsets, per-step outcomes, channel counts, cleanup warnings, and independent target results even when multiple targets share the same display label. Optional single/all/selected target overrides, including `targetRoutes: [{ "userId": "jellyfin-user-id", "deviceId": "living-room-tv" }]` for an exact nested playback-device route, remain credential-free. Target IDs are Jellyfin user IDs; stable `mappingId` values are used only by exact-row mapping administration. |
| `POST /HueSync/ScenePlaylists/BulkPreview` | Preview up to 50 selected playlist IDs with `{ "playlistIds": ["..."], "targetUserId": "...", "targetAllEnabledMappings": true }` or the selected-target override `{ "playlistIds": ["..."], "targetUserIds": ["jellyfin-user-id"], "targetRoutes": [{ "userId": "jellyfin-user-id", "deviceId": "living-room-tv" }], "includeDefaultTarget": true }`. Omitting the nullable target override preserves each playlist's saved target and playback order; all selected playlists and scene references are preflighted before bridge activity, results remain credential-free, later playlists continue after runtime failures, and cancellation stops the remaining sequence safely. |
| `POST /HueSync/ScenePlaylists/{name}/Duplicate` | Create a uniquely named copy of a playlist with a new stable ID while preserving its saved scenes, per-step duration, brightness, effect, effect-speed, fade-in, fade-out, and fade-curve overrides, repeat count, playback order, and target mode. |
| `POST /HueSync/ScenePlaylists/BulkDuplicate` | Atomically create independent copies of up to 50 selected playlist IDs with `{ "playlistIds": ["..."] }`. Every ID is resolved before mutation; missing IDs, capacity limits, validation failures, or save failure leave the original playlists and scheduled-cue references unchanged. Copies receive fresh stable IDs and bounded unique names with credential-free summaries. |
| `DELETE /HueSync/ScenePlaylists/{name}` | Delete one saved-scene playlist by name; returns a conflict with the dependent cue count while any scheduled cue references it. |
| `POST /HueSync/ScenePlaylists/BulkDelete` | Atomically delete up to 50 selected playlist IDs with `{ "playlistIds": ["..."] }`. Every ID is resolved before mutation; any missing ID, scheduled-cue dependency, or save failure leaves the entire playlist collection unchanged. The credential-free response includes deleted summaries, remaining count, and blocked playlist dependency details. |
| **Scheduled cue target modes** | `targetAllEnabledMappings: true` selects **All enabled targets**. For deliberate fan-out, send `targetUserIds: ["jellyfin-user-id-1", "jellyfin-user-id-2"]` and optionally `includeDefaultTarget: true` to run only that credential-free subset (up to 50 enabled mappings), or send `targetRoutes: [{ "userId": "jellyfin-user-id-1", "deviceId": "living-room-tv" }]` for exact nested playback-device routes. `targetUserIds` and route `userId` values are Jellyfin user IDs; stable `mappingId` values are reserved for exact-row mapping operations. Device routes carry IDs only; credentials stay server-side. Duplicate/missing/disabled IDs, stale device routes, and mixed legacy modes are rejected atomically. The scheduler deduplicates physical bridge/area targets, runs selected targets sequentially, and returns credential-free `targetResults`; `targetAllEnabledMappings`, selected IDs, routes, and default-target inclusion are carried by schedule CRUD, status, occurrences, history, duplication, and backup/restore. |
| **Scheduled cue timezone portability** | `timeZoneId` remains the host value for backward compatibility, while `timeZoneIanaId` is the canonical portable IANA value returned by cue, occurrence, runtime-status, and backup/restore APIs. Imports prefer the canonical field and convert legacy Windows/IANA aliases before validation; unmappable values fail closed rather than changing to the destination server's local zone. |
| `GET /HueSync/SceneSchedules` | List credential-free scene cues with exactly one saved-scene `presetName` or saved-playlist `playlistName`, playlist duration and `playlistPlaybackOrder` metadata, deterministic priority, persisted and effective per-cue playback policy, inherited effect/fade metadata, `targetUserId` for legacy single mapping mode, `targetAllEnabledMappings`, `targetUserIds` for selected Jellyfin users, `targetRoutes` for exact nested user/device routes, `includeDefaultTarget`, target label, `timeMode` (`Fixed`, `SolarNoon`, `Sunrise`, `Sunset`, `CivilDawn`, `CivilDusk`, `NauticalDawn`, `NauticalDusk`, `AstronomicalDawn`, or `AstronomicalDusk`), fixed `timeOfDay` or solar `solarOffsetMinutes`/`solarLatitude`/`solarLongitude`, cue time zone, recurrence and interval metadata, finite-run state, date bounds/exclusions, and enabled state. Valid Jellyfin user IDs in all credential-free target metadata are emitted in canonical D-format text; malformed opaque legacy IDs remain unchanged and fail closed. Stable `mappingId` values are only for exact-row mapping operations. |
| `POST /HueSync/SceneSchedules` | Save or update a cue with `id` (optional for new cues), `name`, exactly one of `presetName` or `playlistName`, optional `priority` (`0-100`), optional `playbackPolicy` (`Inherit`, `Skip`, or `Defer`), either legacy `targetUserId`, `targetAllEnabledMappings: true`, selected `targetUserIds` (up to 50 enabled Jellyfin user IDs), or credential-free `targetRoutes` (`userId` plus exact nested `deviceId`) with optional `includeDefaultTarget: true`, `timeMode` (`Fixed`, `SolarNoon`, `Sunrise`, `Sunset`, `CivilDawn`, `CivilDusk`, `NauticalDawn`, `NauticalDusk`, `AstronomicalDawn`, or `AstronomicalDusk`), `timeOfDay` (`HH:mm`, required for `Fixed`), optional solar `solarOffsetMinutes` (-720..720), `solarLatitude` (-90..90), and `solarLongitude` (-180..180) for solar modes, optional `timeZoneId`, recurrence/interval/date rules, finite-run fields, duration override, exclusions, day mask, and `enabled`. Target IDs/routes and timing fields are normalized and validated atomically; credentials are resolved server-side and failed persistence leaves the previous cue collection unchanged. Stable `mappingId` values are not accepted as target-user IDs. |
| `POST /HueSync/SceneSchedules/{id}/Duplicate` | Create a disabled copy of one cue with a unique name, a new stable ID, the same scene/target/timing/recurrence/limit metadata, and a reset execution counter. The copy is safe to edit before enabling and never returns bridge credentials. |
| `DELETE /HueSync/SceneSchedules/{id}` | Delete one scene cue by its stable ID; active cues return `409 Conflict` and remain configured until their restorative run completes, while persistence failures roll back the in-memory cue collection and return a sanitized error. |
| `POST /HueSync/SceneSchedules/{id}/Run` | Run one cue immediately through the serialized, state-restoring preview lifecycle; automatic and manual executions of the same cue cannot overlap, and active playback or another diagnostic safely blocks the run. |
| `POST /HueSync/SceneSchedules/{id}/Cancel` | Request cancellation of an active manual Run Now cue. The response reports whether a run was found; cleanup still deactivates the area and restores captured light state. |
| `POST /HueSync/SceneSchedules/BulkRun` | Run up to 50 selected cue IDs sequentially with `{ "scheduleIds": ["..."] }`. Every cue, saved-scene or playlist reference, and target is validated before the first bridge call; credential-free per-cue results continue after ordinary runtime failures and stop the remaining sequence on cancellation. |
| `POST /HueSync/SceneSchedules/BulkCancel` | Request cleanup-aware cancellation for every active manually started cue among up to 50 selected IDs. The credential-free response reports the canceled IDs; each cue restores bridge state before ending. |
| `POST /HueSync/SceneSchedules/{id}/ResetRunCount` | Reset a cue's persisted execution counter to zero and re-enable it. Active cues cannot be reset; retained history remains available as an audit trail, and direct persistence failures restore the prior counter/state. |
| `POST /HueSync/SceneSchedules/BulkResetRunCount` | Atomically reset and re-enable up to 50 selected cue IDs with `{ "scheduleIds": ["..."] }`, clearing pending Skip Next markers while preserving retained history. Every ID is resolved first; active cues, unknown IDs, and save failures leave the complete selection unchanged, and the credential-free response includes reset counts and updated cue summaries. |
| `POST /HueSync/SceneSchedules/{id}/Enabled` | Enable or disable one cue without changing its schedule definition. Active cues cannot be changed, and an exhausted finite cue must be reset before it can be enabled; the response remains credential-free. |
| `POST /HueSync/SceneSchedules/BulkEnabled` | Atomically enable or disable up to 50 selected cue IDs with `{ "scheduleIds": ["..."], "enabled": true|false }`. Every ID is normalized and validated before persistence; active cues, exhausted finite cues, unknown IDs, and save failures leave the entire selection unchanged. The response includes updated credential-free cue summaries and counts. |
| `POST /HueSync/SceneSchedules/BulkSkipNext` | Atomically mark or clear the next automatic occurrence for up to 50 selected cue IDs with `{ "scheduleIds": ["..."], "skipNextOccurrence": true|false }`. New skip markers require enabled, non-exhausted cues with a future occurrence or an unexpired deferred occurrence; active cues, disabled/futureless cues without a pending defer, unknown IDs, and save failures leave the entire selection unchanged. Manual Run Now remains available and the response includes updated credential-free cue summaries. |
| `POST /HueSync/SceneSchedules/BulkDuplicate` | Atomically create disabled copies of up to 50 selected scheduled-cue IDs with `{ "scheduleIds": ["..."] }`. Every ID is resolved before mutation; capacity limits, validation failures, unknown IDs, or save failure leave the originals and full schedule collection unchanged. Copies receive fresh IDs, unique bounded names, reset execution counters, and cleared Skip Next markers. |
| `POST /HueSync/SceneSchedules/BulkDelete` | Atomically delete up to 50 selected cue IDs with `{ "scheduleIds": ["..."] }`. Every ID is resolved before mutation; active cues, unknown IDs, and save failures leave the entire selection unchanged. Retained cue history remains available as an audit trail, and the response includes deleted credential-free cue summaries and the remaining count. |
| `POST /HueSync/SceneSchedules/{id}/SkipNext` | Mark exactly one upcoming automatic occurrence to be skipped without changing recurrence, limits, or the saved scene. Active, disabled, exhausted, or futureless cues without an unexpired deferred occurrence are rejected; a deferred one-time cue can be canceled while it waits behind playback, and manual Run Now remains available. |
| `DELETE /HueSync/SceneSchedules/{id}/SkipNext` | Clear a pending skip so the next eligible automatic occurrence runs normally. |
| `GET /HueSync/SceneSchedules/TimeZones` | List the Jellyfin host's available system time zones for schedule selection, including the host ID, canonical portable `timeZoneIanaId`, display name, and base UTC offset. |
| `GET /HueSync/SceneSchedules/Status` | Read credential-free scheduler telemetry for the global automation state and every configured cue: saved-scene or playlist identity, playlist step count/repeat count/`playlistPlaybackOrder`/total duration, priority/execution ordering, preflight readiness and reason, effect, effect speed, effective brightness for direct-scene cues, timing mode with solar offset/coordinates when applicable, recurrence/interval/month/day/ordinal-weekday details, selected time zone, finite execution limit and remaining runs, effective hold duration, fade-in, and fade-out transitions, optional one-time date, recurring date window and exclusions, cue-local and UTC next run, active state, pending skip, run count, last run/outcome/message (including skipped occurrences), restart-restored deferred state, cleanup warning, and bounded upcoming overlap warnings. It also reports the count and credential-free IDs, target references, timestamps, retry state, and bounded last error for pending scheduled cleanups; bridge addresses, Hue credentials, and captured light-state JSON are never returned. The administrator status table renders playlist total duration alongside its step/pass/order metadata. Readiness validates saved configuration locally without contacting the bridge. |
| `GET /HueSync/SceneSchedules/Occurrences?limit=50&days=31&scheduleId=...` | Preview bounded upcoming cue occurrences in UTC and the cue-local wall clock, including each saved scene or playlist identity, playlist step count/repeat count/`playlistPlaybackOrder`/total duration plus the expanded credential-free `playlistSteps` plan (repeat/original position, effective effect, brightness, effect speed, duration, transitions, effective transition curve, and cumulative offset), priority, effect, effect speed, effective brightness for direct-scene cues, effective hold duration, fade-in, and fade-out transition, timing mode with solar offset/coordinates, and applying one-time dates, daily/weekly/monthly-day/monthly-weekday/yearly recurrence, bounded recurrence intervals anchored by `startDate`, finite execution limits, ordinal weekday matching, short-month clamping, timezone/DST rules, weekday masks, date windows, exclusions, and polar no-event handling without contacting the bridge. The administrator upcoming-cue table renders each playlist's bounded total duration next to its order and expanded plan. Valid GUID target IDs are emitted in canonical D-format text across occurrence target arrays/routes. `limit` is capped at 50 per response and `days` at 366. |
| `GET /HueSync/SceneSchedules/Conflicts?limit=50&days=31&scheduleId=...` | Report bounded credential-free overlaps between enabled cue execution windows before they run. The report uses effective scene/playlist durations, selected time zones and DST conversion, recurrence intervals, exclusions, pending skips, finite-run limits, and priorities; it returns both occurrence instants, target labels, overlap seconds, and ordering guidance without contacting a bridge. When supplied, `scheduleId` returns only conflicts involving that cue while retaining the opposing cue for context. `limit` is capped at 200 and `days` at 366. |
| `GET /HueSync/SceneSchedules/Conflicts/ExportCsv?limit=50&days=31&scheduleId=...` | Download the same bounded conflict report as UTF-8 CSV with explicit UTC/local occurrence columns, priorities, durations, overlap seconds, and resolution guidance; cue filters and report horizons are preserved and bridge credentials are omitted. |
| `GET /HueSync/SceneSchedules/Calendar?limit=50&days=31&scheduleId=...` | Download the same bounded upcoming occurrences as an RFC 5545 iCalendar feed with UTC event times, cue timezone metadata, Fixed/SolarNoon/Sunrise/Sunset/CivilDawn/CivilDusk/NauticalDawn/NauticalDusk/AstronomicalDawn/AstronomicalDusk timing and solar coordinates, recurrence, interval, priority, finite-limit filtering, effect, effect speed, effective direct-scene brightness, transition metadata, `X-HUE-PLAYLIST-ORDER`, `X-HUE-PLAYLIST-STEP-PLAN`, `X-HUE-TARGET-ALL-ENABLED-MAPPINGS`, `X-HUE-TARGET-USER-IDS`, `X-HUE-TARGET-ROUTES` (lower-camel `userId`/`deviceId` JSON), `X-HUE-INCLUDE-DEFAULT-TARGET`, target/scene-or-playlist descriptions, playlist step count/repeat count, and effective cue durations; `limit` is capped at 50 and `days` at 366, and no bridge credentials are included. |
| `GET /HueSync/SceneSchedules/Occurrences/ExportCsv?limit=50&days=31&scheduleId=...` | Download the same bounded upcoming occurrences as UTF-8 CSV with scene/playlist metadata, expanded `playlistSteps` plan JSON, target labels, `targetAllEnabledMappings`, exact selected `targetUserIds`, nested `targetRoutes` (`userId`/`deviceId`) JSON, `includeDefaultTarget`, Fixed/SolarNoon/Sunrise/Sunset/CivilDawn/CivilDusk/NauticalDawn/NauticalDusk/AstronomicalDawn/AstronomicalDusk timing, solar offset/coordinates, recurrence, effect, effect speed, effective direct-scene brightness, duration/fade, and explicit cue-local/UTC timestamps; `limit` and `days` remain capped and no bridge credentials are included. |
| `GET /HueSync/SceneSchedules/History?limit=100&scheduleId=...&outcome=...` | Read the newest sanitized scheduled-cue runs, optionally filtered by stable cue ID and outcome (`Succeeded`, `Failed`, `Skipped`, `Recovered`, or `Deferred`; recovered/deferred runs also match their underlying result); results include cue/scene-or-playlist/effect/speed/target labels, `targetAllEnabledMappings`, `targetUserIds`, `targetRoutes`, `includeDefaultTarget`, `playlistPlaybackOrder`, ordered playlist step results with effective effect, brightness, effect speed, duration, fade-in/fade-out, transition curve, and `startOffsetSeconds` for in-memory and persisted runs, outcome, restored-after-restart state, message, cleanup warning, timestamp, and retained run count. The administrator history table renders each playlist's bounded total duration beside its expanded steps and pass/order metadata. Valid GUID target IDs are canonicalized to D-format in both live and restored history metadata. |
| `GET /HueSync/SceneSchedules/History/Export?limit=100&scheduleId=...&outcome=...` | Download the same credential-free scheduled-cue history document used by the administrator Export JSON action, including the selected cue and outcome filters. |
| `GET /HueSync/SceneSchedules/History/ExportCsv?limit=100&scheduleId=...&outcome=...` | Download the same filtered scheduled-cue history as one credential-free UTF-8 CSV row per run, including effective direct-scene brightness, outcome, recovery and restored-after-restart state, run count, `targetAllEnabledMappings`, exact selected `targetUserIds`, nested `targetRoutes` (`userId`/`deviceId`) JSON, `includeDefaultTarget`, bounded nested-result counts, messages, and cleanup warnings. |
| `DELETE /HueSync/SceneSchedules/History` | Clear retained scheduled-cue run summaries and reset last-run pointers without stopping an active cue; returns `409 Conflict` while a configuration snapshot or write is in progress. |
| `GET /HueSync/Status` | Read sanitized runtime state, active Jellyfin user and target, active performance/color/execution/channel/restoration profile including effective audio sensitivity, band centers, band spread, beat-pulse response, release, onset threshold, and visualizer palette, effective pause behavior and pause dim level, credential-free playback position/duration/progress/pause telemetry plus the latest `playbackObservedAtUtc` timestamp, the current sync `syncStartedAtUtc` timestamp, frame count, effective FPS, stream packet counters, reconnect attempts, seek-recovery restart count and last seek position, FFmpeg/DTLS health, cleanup warnings, the credential-free `lastSession` summary, whether the current sync can be stopped safely, and a `sessions` array for concurrent playback workers. |
| `GET /HueSync/History?limit=20&outcome=Error` | Read the newest completed Hue session summaries (up to 25), optionally filtered by outcome, including target labels and aggregate playback quality/cleanup telemetry. Results are bounded in memory and never include bridge credentials or playback tokens. |
| `GET /HueSync/History/Export?limit=25&outcome=Error` | Download the same sanitized session-history document used by the administrator Export JSON action for troubleshooting; bridge credentials and playback tokens are omitted. |
| `GET /HueSync/History/ExportCsv?limit=25&outcome=Error` | Download the same filtered completed-session history as credential-free UTF-8 CSV with playback quality counters, timestamps, target metadata, errors, and cleanup warnings. |
| `DELETE /HueSync/History` | Clear retained completed-session summaries and the status API's last-session pointer without stopping active playback; returns `409 Conflict` while a configuration snapshot or write is in progress. |
| `GET /HueSync/Diagnostics` | Run a non-mutating, cancellation-aware local prerequisite check for configuration validity, FFmpeg version, managed DTLS readiness, bounded PCM audio capture, bridge lifecycle contention, and playback/diagnostic readiness. The credential-safe result includes the compatibility-shaped managed `OpenSsl` status, `AudioCapture`, and `AudioCaptureRequired`; no bridge credentials or raw process output are returned. |
| `GET /HueSync/TargetDiagnostics` | Validate every saved default, inherited, and enabled custom bridge target without mutating bridge state; reports reachability, selected-area presence, available and selected channel counts, stale channel-profile IDs, credential presence, and sanitized readiness messages. Credential-free `duplicateMappingGroups` includes stable row IDs, canonical user IDs/names, and enabled counts even when all duplicate rows are disabled; affected targets are blocked before bridge contact and `AllTargetsReady` remains false until each group is resolved. The endpoint holds the shared diagnostic lifecycle lease for the complete multi-target snapshot and returns `409 Conflict` while playback or another diagnostic owns the bridge. |
| `GET /HueSync/Diagnostics/SupportBundle` | Collect a consolidated credential-safe support document containing local diagnostics, saved-target validation, runtime status, bounded playback and scheduled-cue history, scheduler status, and a support-specific redacted configuration export. Bridge keys, playback tokens, and custom FFmpeg flag values are omitted; private labels and media metadata may remain. The operation is cancellation-aware while target validation is running, holds one shared diagnostic lease across the complete snapshot, and returns `409 Conflict` while playback or another diagnostic owns the bridge. |
| `POST /HueSync/Diagnostics/Cancel` | Request cancellation of active non-mutating System Diagnostics or saved-target validation checks. The bounded response reports whether any operation was found; the canceled request still owns its normal process/network cleanup. |
| `GET /HueSync/Configuration/Export` | Download a credential-safe JSON backup containing global settings, per-user profile fields, target labels, credential-presence flags, the session/cue-history retention preferences, the recurring-automation pause preference, saved scene effects and effect speeds, saved scene playlists with per-step duration/brightness/effect-speed/fade overrides, repeat counts, and `playbackOrder`, and recurring or one-time scene cues with optional direct-scene brightness overrides. Bridge credential values and persisted history entries are never included; custom FFmpeg flags are retained for migration and may contain sensitive paths or URLs, so review the backup before sharing. The projection returns `409 Conflict` while a configuration mutation is applying. |
| `POST /HueSync/Configuration/ValidateImport` | Preflight a configuration import without mutating settings or contacting Hue: apply schema normalization, saved-scene/playlist/cue dependency checks, complete configuration validation, and matching-key preservation analysis. The credential-free result includes validation errors, planned object totals, `canImport` (false while playback, diagnostics, another import, scheduler evaluation, a scheduled lifecycle, or a scheduled cue is active), explicit `activeDiagnostic`/`activeConfigurationMutation`/`activeScheduleEvaluation`/`activeScheduleLifecycle`/`activeScheduledCue`/`activePlayback` flags, and a normalized `diff` with added/removed/changed/unchanged counts for mappings, color presets, playlists, and schedules plus global settings/key change flags. The request body is capped at 8 MiB before model binding. Validation returns `409 Conflict` while a configuration mutation is applying. |
| `POST /HueSync/Configuration/Import` | Atomically restore an export document, including saved scene playlists and scene cues. Imported per-user mapping IDs must be valid Jellyfin user GUIDs; accepted brace/N-format values normalize to canonical D-format text before merge, credential preservation, duplicate detection, and mutation. Imported preset, playlist, and schedule collections are capped at their persisted limits before normalization; playlist step arrays and target selections plus schedule target routes and excluded dates are bounded as well. Stable per-row `mappingId` is matched first; a legacy or cross-server ID may fall back to a single matching Jellyfin user row, but an ambiguous duplicate group must provide the exact row ID and otherwise fails closed. Partial merges replace only the exact mapping row instead of deleting every row for that user, and the normalized diff is keyed by stable mapping ID. Schedule/playlist `targetUserId`, `targetUserIds`, and `targetRoutes.userId` references receive the same canonicalization, so legacy brace/N-format routes resolve their mappings and device dependencies consistently. Matching stored global/mapping keys are preserved when omitted; explicit global or mapping keys may be supplied for migration, playlist renames migrate matching cue references by stable playlist ID, and invalid documents leave the current configuration unchanged without assigning IDs, altering credentials, or replacing live mapping objects during planning. The configuration page keeps replacement keys in memory only and sends them once in this request. The request body is capped at 8 MiB before model binding. Active playback, diagnostics, scheduler evaluation, and active scheduled cues must finish first; blocked imports return `409 Conflict` without changing the live configuration. |
| `POST /HueSync/Stop` | Stop Hue output for the current playback session, restore lights, and leave Jellyfin playback running. Pass `playSessionId` to stop one listed concurrent session; an unknown or stale explicit ID returns `409 Conflict` without falling back to the primary session. |
| `GET/POST /HueSync/Configuration` | Read or update default plugin settings, including the global video/audio playback media scope, Audio Visualizer Sensitivity, ordered low/mid/high audio band centers and 0-200% gains, Audio Response Smoothing (0-90%), Audio Band Spread (0-100%), Audio Beat Pulse (0-100%), Audio Beat Pulse Release (0-100%), Audio Beat Pulse Threshold (0-100%), Audio Visualizer Palette (Spectrum, Band, Warm, Cool, or Monochrome), Audio Spatial Routing (Spatial, Uniform, or Mirror), Audio Source Channels (Mono, Stereo, Left, or Right), global channel profile, credential-free SHA-256 bridge certificate pins, recurring scene-automation pause preference, bounded missed-cue recovery window, opt-in persistent session/cue-history preferences, and configurable 1-25 session / 1-100 cue history retention windows, without serializing per-user mappings or global credentials to the configuration page. Responses expose `hasAppKey`/`hasClientKey` presence flags; blank key fields preserve stored values and `clearStoredCredentials` explicitly removes both global keys. POST bodies are capped at 1 MiB before configuration normalization. Reads return `409 Conflict` while a configuration mutation is applying. Failed persistence restores the complete prior settings and retained history and returns a sanitized server error. |
| `GET/POST /HueSync/UserMappings` | List or save per-user bridge mappings, sync enable flags, optional playback-media-scope/audio-sensitivity/ordered audio-band-center/audio-band-gain/audio-response-smoothing/audio-band-spread/audio-beat-pulse/audio-palette/audio-spatial-routing/audio-source-channel/color-threshold/performance/execution/channel/restoration-profile overrides; POST `userId` must be a valid Jellyfin user GUID and malformed IDs fail with `400` before configuration mutation; GET responses redact stored credentials, report `InheritsDefaultBridge`, and include a stable non-secret `mappingId`; the persisted collection accepts up to 100 mapping rows; oversized mapping imports (POST bodies over 1 MiB) are rejected before mapping normalization; edits with an exact row ID preserve other duplicate rows while an ambiguous user-ID-only edit fails closed with `409`. |
| `GET/POST /HueSync/UserMappings/Reconcile` | Compare persisted mapping IDs/names with Jellyfin's live user directory and optionally apply safe identity repairs atomically. Existing users with renamed accounts or legacy brace/N-format IDs are refreshed; missing, malformed, and duplicate mappings are reported but left unchanged. The report is credential-free and includes healthy, drift, missing, malformed, duplicate, and updated counts. |
| `POST /HueSync/UserMappings/Cleanup` | Atomically remove exact stale mapping rows with mapping IDs in `{ "mappingIds": ["row-id"], "expectedReportVersion": "..." }`. This atomic operation allows only missing or malformed rows with no scheduled-cue or saved-playlist references; duplicate, healthy, renamed, referenced, unknown, or stale-report selections return a safe conflict/validation result without mutation. Stable row IDs and the optimistic report version prevent deleting the wrong duplicate; the response is credential-free and includes deleted summaries, protected dependencies, and the new report version. |
| `POST /HueSync/UserMappings/ResolveDuplicates` | Resolve one duplicate Jellyfin-user group with `{ "retainMappingId": "keeper-row", "removeMappingIds": ["sibling-row"], "expectedReportVersion": "..." }`. The request must name exactly one enabled, valid keeper and every other stable row in that group; stale reports, mixed/missing rows, invalid targets, incomplete device routes, invalid saved-scene/schedule dependencies, or persistence failures leave the complete mapping collection unchanged through one atomic transaction. The keeper's ID/name are canonicalized from Jellyfin, runtime and scene automation then resolve one deterministic row, and the credential-free response includes retained/removed summaries and a new report version. |
| `GET /HueSync/UserMappings/{userId}/Dependencies` | Inspect one mapping's credential-free scheduled-cue and saved-playlist target dependencies before disabling or deleting it. Supply the stable `mappingId` query value for an exact duplicate row; user-ID-only requests fail with `409` when multiple rows are present. Returns `canDisable`, `canDelete`, dependent cue and playlist counts, and bounded IDs/names/enabled state; bridge credentials and target details are never returned. |
| `DELETE /HueSync/UserMappings/{userId}` | Remove one per-user bridge mapping. Supply the stable `mappingId` query value to delete exactly the selected row; legacy user-ID-only deletion fails with `409` when multiple rows are present, and dependency or persistence failures leave every row unchanged. The response is credential-free. |
| `POST /HueSync/UserMappings/BulkDelete` | Atomically delete up to 50 selected mapping rows with `{ "mappingIds": ["..."] }`; legacy `{ "userIds": ["..."] }` remains supported only when every user resolves to one row and fails closed for ambiguous duplicates. Null/blank IDs, missing rows, scheduled-cue dependencies, or save failures leave the complete mapping collection unchanged. The credential-free response includes exact deleted summaries, remaining count, missing/ambiguous IDs, and blocked dependency details. |
| `POST /HueSync/UserMappings/BulkEnabled` | Atomically enable or disable up to 50 selected mapping rows with `{ "mappingIds": ["..."], "syncEnabled": true|false }`; legacy `{ "userIds": ["..."], "syncEnabled": true|false }` remains supported only when every user resolves to one row and fails closed for ambiguous duplicates. Missing rows, scheduled-cue references while disabling, incomplete custom targets while enabling, or save failure leave every selected mapping unchanged. Disabling clears custom bridge credentials/target fields and the credential-free response includes updated summaries, missing/ambiguous/invalid IDs, and blocked dependency details. |

Single-scene scheduled cues may carry nullable `red`, `green`, and `blue` channel overrides (0-255); omitted channels inherit the saved scene and effective RGB is included in scheduler status, occurrence JSON/CSV, iCalendar metadata, and retained run telemetry. Playlist cues continue to use their per-step color overrides.

### Generating Hue Credentials (Manual Fallback)
If the **Link Bridge** button doesn't work for you, you can generate keys manually:
1.  Use the administrator **Bridge Certificate** action (or `GET /HueSync/BridgeCertificate`) to read the SHA-256 fingerprint, verify it against your trusted bridge identity, and explicitly trust it with `POST /HueSync/BridgeCertificate/Trust`. Existing pins are listed in the configuration page; use **Forget** (or `DELETE /HueSync/BridgeCertificate/Trust?ipAddress=...`) when retiring a bridge or replacing its certificate, then explicitly trust the replacement before sending credentials.
2.  Go to `https://<BRIDGE_IP>/debug/clip.html`.
3.  Press the **Link Button** on your Hue Bridge.
4.  Post to `/api` with body: `{"devicetype":"jellyfin_plugin#server", "generateclientkey":true}`.
5.  Copy the `username` (App Key) and `clientkey` (Client Key) from the response; the plugin will now accept them only over the pinned bridge certificate.

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

Both helpers restore from the committed, content-hashed NuGet dependency graph with
`dotnet restore --locked-mode`; dependency drift stops the release before build or packaging.

#### Manual Build

```bash
cd Jellyfin.Plugin.Hue
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
```

The plugin DLL will be generated at:
`bin/Release/net8.0/Jellyfin.Plugin.Hue.dll`

> **Note**: The DLL path is useful for local build inspection. For an installation, use the release ZIP so the required `meta.json` manifest is installed alongside the plugin assembly.

#### Release Package Contents

The local release scripts produce `jellyfin-plugin-hue-v<version>.zip` and matching archive and manifest `.sha256` sidecars, while the GitHub release workflow publishes the canonical `jellyfin-plugin-hue-release.zip`, its checksum sidecar, and the matching `jellyfin-plugin-hue-release.manifest.json` plus `.sha256` sidecar. Each archive contains exactly:

* `BouncyCastle.Cryptography.dll` — the managed DTLS transport dependency
* `Jellyfin.Plugin.Hue.dll` — the plugin assembly, including the embedded configuration page
* `meta.json` — the Jellyfin plugin manifest and release version

The release manifest is deterministic and records the exact source commit, archive digest, each packaged
file's size and SHA-256 digest, and every resolved package/content hash from the plugin's committed NuGet
lock file. The source-commit binding lets an operator compare the published bytes with the reviewed
workflow commit before installation.

The local release helpers require a clean Git checkout so uncommitted source cannot be mislabeled with the
checked-out commit's provenance.

The version in `meta.json`, the project file, and the local archive name must match. Keep the
archive, both checksum sidecars, and the matching manifest together for auditability; install the
archive contents in a `HueSync` directory under the Jellyfin plugins directory, and verify the
published archive and manifest before extraction:

```bash
sha256sum --check --strict jellyfin-plugin-hue-v<version>.zip.sha256
sha256sum --check --strict jellyfin-plugin-hue-v<version>.manifest.json.sha256
```

If the .NET SDK is not installed on a Linux host, the same build can be run with Docker:

```bash
docker run --rm -v "$PWD:/src" -v /tmp/hue-nuget:/root/.nuget/packages \
  -w /src mcr.microsoft.com/dotnet/sdk:8.0 sh -lc \
  'apt-get update -qq && apt-get install -y -qq python3 zip unzip && ./build-release.sh'
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
# Verify the committed content-hashed dependency graph first
dotnet restore --locked-mode

# Run all tests
dotnet test --no-restore

# Run tests with detailed output
dotnet test --no-restore --verbosity detailed

# Run tests with code coverage
dotnet test --no-restore --collect:"XPlat Code Coverage"

# Run specific test class
dotnet test --no-restore --filter FullyQualifiedName~ColorProcessingTests
dotnet test --no-restore --filter FullyQualifiedName~HueClientTests
dotnet test --no-restore --filter FullyQualifiedName~HueStreamerTests
```

The test suite does not require a physical Hue bridge or FFmpeg installation; bridge calls,
packet construction, and playback lifecycle paths are exercised with deterministic test
doubles. A real bridge is only needed for an end-to-end playback check after installation.

#### Test Coverage

Tests are automatically run in CI/CD on:
- Trusted pushes to `main`

Full CI runs locked restores, build/tests, formatting, dependency auditing, Gitleaks, and split Semgrep scans on the named self-hosted runner. The Semgrep production-source/configuration (including PowerShell release helpers), repository-script, and embedded administrator-page JavaScript reports fail closed on findings, scanner errors, fixpoint-analysis timeouts, or a missing embedded scan target, and each reviewed ruleset snapshot is SHA-256 verified and configuration-validated before use. The embedded page scan keeps generic/browser rules blocking while explicitly documenting the non-applicable Express/React framework-rule exclusions in `.github/semgrep/config-page-excludes.txt`; its larger-than-default target-size allowance prevents the extracted page from being silently skipped. It also executes the documented Linux release helper in a separate bounded job and compares its verified archive and deterministic dependency manifest with the canonical package before publication. A repository-controlled weekly default-branch workflow reruns the blocking Gitleaks and Semgrep gates. The main persistent-runner workflow accepts `workflow_dispatch` only for an operator recovery run on `main`; manual recovery never publishes a release, while pull-request and non-main push events remain excluded and every self-hosted job has a `refs/heads/main` guard. The trusted workflow boundary validator (`scripts/validate-trusted-workflow.mjs`) parses job permissions and concrete-job timeouts in addition to trigger, runner, immutable-action, and version contracts; `scripts/test-workflow-contracts.mjs` runs executable negative fixtures for permission leakage and missing timeouts. Dependabot explicitly monitors the plugin, test, and benchmark NuGet directories, the GitHub Actions workflows, and the hash-locked Semgrep environment; `scripts/validate-dependabot.mjs` fails CI if a tracked project directory is missing. The repository Actions policy is selected-only with SHA pinning required; it allows only `actions/checkout`, `actions/setup-dotnet`, `actions/cache`, `actions/upload-artifact`, `actions/download-artifact`, `actions/github-script`, and `codecov/codecov-action` (plus local reusable workflows). The runner is configured under the dedicated, least-privileged `harmonize-runner` service account with an isolated home. Release publication uses a separate least-privileged self-hosted identity so its short-lived `contents:write` token is never exposed to the build/test account, verifies the release ZIP and dependency manifest sidecars plus exact four-asset set before publication, rechecks GitHub's recorded asset digests after publication, and resumes only a matching version-tag draft at the same workflow commit. See `.github/workflows/dotnet-ci.yml`, [SELF_HOSTED_RUNNERS.md](SELF_HOSTED_RUNNERS.md), and [.github/dependabot.yml](.github/dependabot.yml) for the full workflow, runner, and dependency-maintenance contracts.

Every trusted build also retains its Cobertura coverage artifact. Codecov publication is optional: when
`CODECOV_TOKEN` is not configured, CI records an explicit skip in the run summary; when it is configured,
an authenticated upload is required to succeed.

Dependabot update jobs use a third, dedicated `harmonize-dependabot-runner` identity with
rootless Docker, the `dependabot` label, isolated state, and bounded resources. Enable GitHub's
**Dependabot on self-hosted runners** repository setting only after that runner is online; the
setting is owner-controlled and is documented with the host contract in
[SELF_HOSTED_RUNNERS.md](SELF_HOSTED_RUNNERS.md).

### Troubleshooting

* **Registration fails:** press the physical Link button immediately before clicking
  **Link Bridge**. Hue registration and all v2 REST requests use the bridge's HTTPS API;
  current bridge firmware no longer supports the old HTTP endpoint. The configured target
  must be a private/local bridge address or a .local mDNS name.
* **Discovery finds no bridge:** the Jellyfin server must be able to reach the local network and
  allow mDNS/Bonjour traffic. Cloud discovery is tried first, then the plugin queries the local
  `_hue._tcp.local` service for private addresses. Link-local IPv6 results may include a `%`
  interface scope; retain that suffix when editing the address manually. If both paths are
  unavailable, use a private IP or `.local` host name manually.
* **No areas are listed:** verify the bridge IP and App Key, then click **Refresh
  Entertainment Areas**. The selected area must contain color-capable lights.
* **Lights stop updating:** run **System Diagnostics** first to confirm that `ffmpeg` and the managed DTLS transport are ready for the Jellyfin
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
  with the restored/failed/deadline count so the remaining lights can be recovered manually;
  diagnostic, preview, pause, startup-rollback, and playback cleanup all use an independent
  30-second deadline so a canceled request cannot strand the lifecycle lease indefinitely.
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

### Version 1.5.431 (Current)

- **Route-aware bridge linking**: nested playback-device routes can link their own Hue bridge from the administrator editor, with route-local credentials and area refreshes that never overwrite the parent mapping.

- **Import credential clearing**: clearing a replacement key after validation removes the stale in-memory value while blank fields continue to preserve matching server-side credentials.

- **Service-readable release archives**: canonical ZIP entries are non-executable `0644`, allowing Jellyfin to discover plugin metadata under a different service account.

- **Regression coverage**: route-link, import-clearing, release-permission, and existing full-suite contracts cover the new paths.

- **Jellyfin configuration serializer compatibility**: certificate pins persist through an XML-safe adapter while the runtime/API retain their credential-free case-insensitive dictionary, preventing plugin startup from being disabled by Jellyfin's serializer.

- **Fail-closed pin migration**: blank hosts are discarded and conflicting case/whitespace aliases force certificate re-pinning instead of silently selecting one entry.

- **Regression coverage**: plugin configuration XML round-trip and malformed/conflicting pin handling are covered by security tests.

- **Fail-closed entertainment-area identifiers**: malformed successful Hue bridge responses containing non-object areas or missing, null, numeric, or blank IDs are rejected before they can become selectable targets.

- **Regression coverage**: Hue response parsing covers malformed area entries while preserving valid empty and metadata-optional responses.

- **Duplicate scene-cue identity safety**: malformed case-variant persisted schedule IDs now fail closed across manual and bulk API actions, scheduler evaluation, runtime state, and deletion; ambiguous requests return conflict without mutation.

- **Diagnostics cancellation lifecycle**: retained configuration pages track cancellation requests, abort them on page teardown, and suppress stale completions.

- **Regression coverage**: service/API duplicate-ID and administrator lifecycle contracts cover these boundaries.

- **Entertainment-area payload validation**: malformed successful bridge responses with missing, null, non-array, or duplicate area data now fail closed instead of being treated as an empty or response-order-dependent target list.

- **Test Connection lifecycle ownership**: default and per-user mapping connection tests are now page- and target-owned, abort on teardown, and suppress stale results after bridge, area, credential, or channel edits.

- **Trusted-build evidence gate**: main CI now requires non-empty TRX and Cobertura reports and rejects missing evidence before release packaging.

- **Regression coverage**: Hue response parsing, administrator lifecycle races, and trusted workflow negative contracts cover the new boundaries.

- **Duplicate device-route playback arbitration**: concurrent playback now rejects duplicate nested device IDs before route lookup instead of falling back to another Hue target.

- **Credential preservation safety**: imports and mapping edits no longer retain arbitrary App/Client keys from ambiguous legacy device routes; explicit replacement credentials are required.

- **Preview cancellation ownership**: retained-page teardown cannot hide a newer page's loader when only an older preview cancellation remains pending.

- **Regression coverage**: API, configuration, and browser lifecycle contracts cover duplicate routes, credential retention, and deferred preview-cancellation races.

### Version 1.5.426

- **Credential preflight route arbitration**: credential-bearing entertainment-area requests now reject ambiguous duplicate nested device IDs before selecting stored keys or contacting Hue, matching schedule, capture, and diagnostics fail-closed behavior.

- **Global loader ownership**: configuration-page discovery, registration, mapping-edit/dependency, diagnostic, preview, and current-light operations now share owner-checked loader tokens with configuration operations, so stale or nested completions cannot hide a newer operation's indicator.

- **Regression coverage**: API and configuration-page contracts cover duplicate device-route credentials plus retained-page, cross-operation, and nested certificate-preflight loader races.

- **Configuration import loader ownership**: import validation and submit completions now release the shared global loader only when they still own it, so a late import request cannot hide a newer export or runtime-stop indicator.

- **Regression coverage**: configuration-page contracts cover import-validation/export and import-submit/export races alongside load/export and save/export ownership tests.

- **Duplicate device-route arbitration**: legacy or hand-edited mappings with duplicate nested device IDs now fail closed in schedule validation and execution, current-light capture, and target diagnostics instead of selecting the first route.

- **Regression coverage**: route validation, scheduler, capture, and diagnostics tests cover duplicate nested device IDs without bridge activity or credential leakage.

- **Server-only Semgrep taint boundary**: the Node/Express/Next `eval` taint rule is explicitly excluded from the vanilla browser-page scan because the extracted page has no server request objects or `eval` sinks; generic/browser dynamic-execution rules remain blocking.

- **Embedded scan regression coverage**: both blocking workflows pin the reviewed exclusion count and continue to require the extracted administrator target to be scanned with zero findings, scanner errors, or analysis timeouts.

### Version 1.5.423

- **Configuration loader ownership**: configuration load and save completions now release the shared global loader only when they still own it, so a late request cannot hide a newer export or runtime-stop indicator.

- **Regression coverage**: configuration-page contracts cover load/export and save/export races alongside the existing retained-page and cross-operation loader tests.

### Version 1.5.422

- **Target FPS validation parity**: the global administrator Target FPS field now enforces the server’s supported 1–60 range before submission.

- **Embedded JavaScript scan coverage**: the extracted administrator page is allowed its measured 1,096,321-byte size under a scoped 2 MB Semgrep target limit, and CI fails closed unless Semgrep reports that exact extracted file as scanned.

- **Regression coverage**: configuration-page and workflow-contract validators enforce the UI bounds and embedded-target coverage gate.

### Version 1.5.421

- **Global loader ownership**: retained configuration pages now share one latest-operation owner for runtime-stop and configuration-export indicators, so stale completions cannot hide an active global loader.

- **Accessible report tables**: scheduled-cue and session-history tables now expose captions and explicit column headers for assistive technology.

- **Regression coverage**: configuration-page contracts cover cross-operation retained-page loader races and accessible report-table semantics.

### Version 1.5.420

- **Runtime-stop loader ownership**: a retained hidden configuration page can no longer hide the global loader owned by a newer visible stop operation.

- **Partial-import opt-outs**: adding a new fully scrubbed disabled user mapping remains allowed during active playback as a policy-only change, while unrelated or enabled mapping changes stay blocked.

- **Regression coverage**: retained-page loader races and partial-import playback arbitration have focused tests.

### Version 1.5.419

- **Certificate target freshness**: direct trust prompts, credential preflights, and bridge registration re-check the current bridge address before trusting a fingerprint or sending a request; edited or hidden pages cannot mutate the old target or surface stale alerts.

- **Import playback arbitration**: imports that disable one user mapping while changing unrelated settings remain blocked during active Hue playback. Only policy-only disables may proceed and stop the affected sessions.

- **Rollback nullability**: failed mapping and configuration imports explicitly normalize legacy null mapping collections, removing the remaining nullable-assignment warning while preserving atomic rollback.

- **Regression coverage**: trust-prompt lifecycle races and mixed-import playback conflicts have focused regression tests.

### Version 1.5.418

- **Legacy row identity safety**: cleanup, duplicate resolution, delete, and bulk mapping operations no longer backfill legacy mapping IDs while validating a request, and failed mapping saves restore every prior row identity.

- **Reference-safe deletion**: single and bulk deletes remove the exact selected mapping objects, so blank legacy IDs cannot accidentally select or remove sibling rows.

- **Dependency-inspection lifecycle**: per-user mapping dependency requests are page-owned, canceled on page teardown, and prevented from raising stale alerts or leaving loading indicators active.

- **Regression coverage**: backend atomicity tests cover stale/unknown mapping operations and persistence rollback, while configuration-page contracts cover dependency inspection cancellation, stale completion suppression, current-page success, and control recovery.

- **Global configuration ownership**: bridge address, stored App Key/Client Key presence, and selected entertainment area snapshots are now retained per configuration page, so sibling retained pages cannot overwrite another page's credentials or selection.

- **Regression coverage**: two-page configuration-load and bridge-discovery races verify snapshot ownership, with static guards preventing the retired singleton reads from returning.

- **Mapping editor ownership**: retained administrator pages now keep mapping editor state on their owning page, preventing duplicate-page edits, credentials, and saves from crossing page boundaries.

- **Registration and discovery ownership**: page teardown only cancels the registration preflight it owns, and playback-device discovery cache reads remain restricted to the owning page while bridge registration stays serialized.

- **Regression coverage**: configuration-page contracts cover retained-page state isolation, sibling pagehide cancellation, and playback-device cache ownership.

- **Bounded FFmpeg cleanup test**: the stderr-reader regression now waits within the existing five-second deadline for inherited pipe descriptors to settle after bounded `Stop()` cleanup, removing scheduler-timing flakes without weakening the cleanup contract.

- **Regression coverage**: the full 1,391-test suite and repeated stderr-reader stress runs cover the bounded cleanup lifecycle.

- **Page-scoped retained configuration listeners**: all inline administrator controls now bind through the active plugin configuration page, so hidden retained pages cannot steal duplicate-ID handlers or leave current controls unbound.

- **Scheduled target snapshot parity**: scheduled cue snapshots now preserve the optional default target when selected user mappings are also targeted, ensuring execution and history represent and invoke every configured target.

- **Regression coverage**: configuration-page validation and scheduler tests cover retained-page listener scoping and selected-plus-default cue execution.

- **Page-scoped mapping route editors**: retained configuration pages now keep route selectors, editor fields, credentials, area/channel loads, previews, and mapping saves isolated to their owning page.

- **Weekly schedule boundary safety**: interval schedules anchored at the minimum supported date now compute week alignment without `DateTime` underflow.

- **Page-scoped playback-device routing**: discovery results and route refreshes stay on the configuration page that initiated the request, preventing retained pages from receiving another page's devices.

- **Solar boundary fail-closed behavior**: solar calculations return no event instead of throwing when date, offset, or local conversion arithmetic would overflow.

- **Runner isolation documentation**: the self-hosted runner runbook now distinguishes the build/release identities' lack of Docker access from Dependabot's separately confined rootless Docker socket.

- **Discovery lifecycle ownership**: global and per-mapping bridge discovery requests are page-scoped, abort on pagehide, and ignore stale target edits before applying candidates or status.

- **Release-helper safety**: Linux and PowerShell release helpers verify clean Git state before deleting previous artifacts, preserving existing outputs when a dirty checkout is rejected.

- **Administrator import file guard**: configuration restore rejects local files larger than 8 MiB before FileReader parsing and validates top-level collection shapes before rendering migration credential fields.

- **Bounded entertainment channels**: bridge entertainment-configuration responses are capped at 256 channels before downstream enumeration.

- **Bounded bridge discovery**: local mDNS and cloud discovery now share a 256-candidate ceiling across datagrams, address families, and merge stages, stopping local discovery once the cloud result is full.

### Version 1.5.408

- **Three-runner version monitoring**: the host's daily `actions/runner` version check now covers the isolated build, release, and rootless-Docker Dependabot installations, and the runbook documents all three update/reverification paths.

### Version 1.5.407

- **Trusted runner binding**: workflow validators now require every concrete build and security job to use the non-publishing `harmonizeproject-jellyfin` runner; only GitHub Release publication may use the isolated release runner, with executable negative fixtures for accidental reassignment.

- **Hue state sanitization**: captured XY coordinates and color-temperature Mirek values are range-checked, and restore payloads clamp brightness while omitting invalid color data instead of sending unsafe state to a bridge.

- **Dedicated Dependabot runner**: document the third rootless-Docker `dependabot` runner, its isolation/resource limits, and the owner-controlled GitHub setting required before Dependabot jobs leave `ubuntu-latest`.

### Version 1.5.406

- **Playback retry isolation**: each playback worker clones the injected Hue transport before applying its per-session retry policy, so API and diagnostic calls cannot inherit another session's mutable retry setting.

- **Playback-device discovery lifecycle**: discovery is scoped to its owning configuration page, rejects competing live-page ownership, aborts on cancellation/pagehide, clears only its own cache, and ignores stale user-target responses while restoring controls and loading state.

- **Regression coverage**: constructor isolation, retained-page ownership, selection-clear cancellation, pagehide abort, stale completion, retry, and current-success contracts cover the new boundaries.

### Version 1.5.405

- **Dependency inspection lifecycle**: saved-scene and color-preset dependency checks now share generation, selection, and request ownership; duplicate submissions are suppressed, page teardown aborts in-flight work, and stale responses cannot overwrite a reused administrator page.
- **Control recovery and regression coverage**: the inspect control recovers after success, failure, selection changes, and pagehide cancellation, with executable current-success, stale-selection, duplicate, and teardown contracts.
- **Playback pause race hardening**: pause events must still match the active playback session under the lifecycle lock before they can publish paused state or start cleanup, so a stale event from a stopped session cannot alter a newer handoff.

### Version 1.5.404

- **Pre-binding request-size hardening**: bulk actions, target-selection previews/captures, mapping reconciliation controls, bridge registration, certificate trust, connection tests, saved-scene controls, and schedule state changes reject bodies over 64 KiB before model binding traverses user-supplied collections or text.

- **Regression coverage**: reflection contracts cover every bounded selection/control endpoint, while valid maximum selections and oversized JSON bodies verify the pre-binding boundary.

### Version 1.5.403

- **Playlist dependency inspection lifecycle**: dependency checks are generation-owned, selection-aware, duplicate-suppressed, and canceled on page teardown; stale responses cannot overwrite a reused administrator page and the inspect control always recovers.

- **Regression coverage**: selection-change, pagehide abort, stale completion, current success, and duplicate-submit contracts cover the dependency-inspection lifecycle.

### Version 1.5.402

- **Scheduled-cue and playlist lifecycle ownership**: direct and bulk mutations are generation-owned, duplicate-suppressed, stale-safe, and canceled on teardown; controls remain locked through refresh and cancellation barriers.

- **Bulk-run configuration barrier**: sequential scheduled-cue runs hold scheduler evaluation across preflight and every cue so saved configuration cannot change between cues.

- **Regression coverage**: pending confirmations, pagehide aborts, stale completions, cross-action arbitration, cancellation barriers, reload leases, and bulk-run configuration barriers are covered.

### Version 1.5.401

- **Direct playlist mutation lock**: save, duplicate, rename, and delete now share one page-generation-owned lock, suppress opposite actions, and restore every mutation control safely on cancellation or teardown.

- **Selection ownership**: individual playlist mutations ignore stale completions when the administrator changes the selected playlist, preventing an older response from clearing or reloading a newer draft.

- **Regression coverage**: cross-action lock suppression, selection-change stale completions, pagehide release, and successful refresh contracts cover direct playlist mutations.

### Version 1.5.400

- **Saved-playlist mutation lifecycle**: individual playlist delete, duplicate, and rename confirmations/requests are page-generation-owned, canceled on teardown, stale-safe, duplicate-suppressed, and leave no stuck controls.

- **Scene mutation request bounds**: direct playlist and scheduled-cue saves reject bodies over 1 MiB before model binding traverses bounded step, target, and excluded-date collections.

- **Regression coverage**: individual playlist mutation lifecycle and maximum-valid/oversized playlist and schedule payload contracts cover the new boundaries.

### Version 1.5.399

- **History-clear lifecycle**: cue/session history clear confirmations and DELETE requests are page-generation-owned, canceled on teardown, stale-safe, duplicate-suppressed, and leave no stuck controls.

- **Ambiguous area configuration rejection**: duplicate case/whitespace-variant entertainment-area IDs fail closed instead of selecting a response-order-dependent channel layout.

- **Configuration request bound**: `POST /HueSync/Configuration` rejects bodies over 1 MiB before model binding traverses certificate pins and administrator text.

- **Regression coverage**: history-clear lifecycle, duplicate-area configuration, and maximum-valid configuration payload contracts cover the new boundaries.

### Version 1.5.398

- **Saved-playlist mutation lifecycle**: bulk duplicate/delete confirmations, selection, requests, and refreshes now share page-generation ownership, abort on teardown, suppress opposite actions, and ignore stale completion callbacks.

- **Bounded entertainment-area discovery**: bridge responses with more than 256 areas fail closed before list materialization, while the exact boundary remains accepted.

- **User-mapping request bound**: `POST /HueSync/UserMappings` rejects bodies over 1 MiB before recursive extension-data validation or re-serialization.

- **Regression coverage**: saved-playlist lifecycle, entertainment-area response, and maximum-valid user-mapping payload contracts cover the new boundaries.

### Version 1.5.397

- **Actionable bulk saved-scene errors**: administrator duplicate/delete failures now surface server-provided capacity, missing-scene, validation, and dependency details while retaining the selection for a safe retry.

- **Bounded light-state capture**: bridge resource identifiers are validated, case-insensitively deduplicated, and capped before any REST fan-out; malformed or excessive area data fails closed without activating the bridge.

- **Administrator import request bound**: configuration validation/import endpoints reject oversized JSON bodies before model binding, keeping credential-safe exports within a documented resource envelope.

- **Regression coverage**: UI rejection/retry, light-resource boundary, no-activation, and import request-size contracts cover these safety boundaries.

### Version 1.5.396

- **Bulk saved-scene mutation lock**: duplicate and delete operations now share one page-generation-owned lock across selection, bulk controls, confirmation, and requests, preventing opposite mutations and stale selection writes.

- **Teardown-safe confirmations**: pending bulk saved-scene confirmations settle when page teardown dismisses their dialog, while stale callbacks and in-flight responses remain unable to mutate a reused page.

- **Pull-request permission isolation**: workflow contracts now reject every top-level or job-level write permission, including `write-all`, `actions`, and `id-token`, with executable negative fixtures.

- **Regression coverage**: administrator lifecycle tests cover cross-action suppression, all five bulk controls, teardown promise settlement, stale completion, and successful refresh behavior.

### Version 1.5.395

- **Scheduled cleanup arbitration**: cleanup recovery now uses the canonical bridge/area lease, preventing a saved-scene recovery from mutating an area owned by active playback or diagnostics.

- **Pending cleanup fairness**: recovery scans past coordination-blocked entries while retaining a four-bridge-work cap, so blocked records cannot starve later due cleanups.

- **Bulk saved-scene deletion lifecycle**: confirmation and POST requests are page-generation-owned, canceled on teardown, duplicate-suppressed, and guarded against stale completion updates.

- **Job-aware workflow permissions and timeouts**: trusted workflow validation now isolates `contents: write` to release publication and requires every concrete job to use a positive timeout of at most 30 minutes.

- **Regression coverage**: scheduler arbitration/fairness, administrator bulk deletion, and executable negative workflow-contract fixtures cover the new boundaries.

- **Playback cleanup ownership**: failed restoration/deactivation retains the playback lease, blocks queued starts and same-target diagnostics, and retries with a fresh bounded cleanup token.

- **Pause deactivation recovery**: dim/keep-last pause cleanup retries failed or thrown deactivation without restoring the saved playback snapshot.

- **Bulk user-mapping mutation lock**: selection, bulk controls, and opposite mutations are locked to one page-generation-owned operation and recomputed safely after completion.

- **Regression coverage**: queued-start, diagnostic arbitration, pause retry/transport failure, and bulk mapping lifecycle contracts cover these boundaries.

- **Playback cleanup retry**: incomplete light restoration retains its snapshot and retries restoration/deactivation with a fresh bounded cleanup token before a new playback session is allowed.

- **Bulk user-mapping deletion lifecycle**: confirmations and requests are generation-owned, canceled on page teardown, and stale completions cannot mutate a reused page.

- **PowerShell release-helper strict mode**: the documented Windows helper now enables strict mode so missing properties and invalid command assumptions fail closed.

- **Regression coverage**: runtime restoration retry and configuration lifecycle contracts cover these boundaries.

- **Release-helper fail-closed shell**: the Linux release helper now enables `pipefail` alongside `errexit`, so pipeline failures stop packaging.

- **One-time cancellation recovery**: automatic one-time cues restore their enabled state when host cancellation interrupts bridge work, retain deferred occurrences, and remain eligible for a safe retry after restart.

- **Restoration persistence repair**: failed cancellation-restoration writes are retried on the next scheduler pass while ambiguous durable state fails closed against replay.

- **Stale mapping cleanup lifecycle**: reconciliation and destructive stale-mapping cleanup are generation-owned, canceled on page teardown, and prevented from applying stale confirmation, status, or reload callbacks.

- **Pinned SDK parity**: trusted and pull-request workflows now verify that their .NET SDK pin exactly matches `global.json` before building.

- **Regression coverage**: scheduler cancellation/restart, cleanup lifecycle, and SDK parity contracts cover the new boundaries.

- **Trusted-workflow parity**: pull-request validation now executes the trusted-workflow boundary contract, covering exact triggers, permissions, release provenance, immutable actions, and security-scan boundaries before merge.

- **Bridge certificate trust lifecycle**: approved certificate trust POSTs are owned by the active configuration-page generation, canceled on teardown, and prevented from applying stale pin-cache or status callbacks.

- **Regression coverage**: PR workflow and certificate-trust tests cover validator enforcement and in-flight pagehide cancellation.

- **HueStream packet budget**: packets now enforce the AES-GCM DTLS application payload limit before allocation or transport writes, and oversized playback/preview areas fail closed before bridge activation.

- **Repository ownership coverage**: CODEOWNERS now protects release, build, dependency-lock, security, project, and validation-script paths so changes receive the configured owner review.

- **Regression coverage**: packet tests cover the exact maximum channel boundary and oversized-area rejection; workflow contracts require every protected ownership entry.

- **Saved-scene deletion lifecycle**: confirmation and DELETE requests are now owned by the active configuration-page generation, canceled on teardown, guarded against duplicate submissions, and prevented from applying stale form, status, or reload callbacks.

- **Repository ownership coverage**: CODEOWNERS now protects release, build, dependency-lock, security, project, and validation-script paths so changes receive the configured owner review.

- **Regression coverage**: configuration lifecycle contracts cover stale confirmation/completion and duplicate saved-scene deletion; workflow contracts require every protected ownership entry.

- **Scheduled-cue deletion lifecycle**: confirmation and DELETE requests are owned by the active configuration-page generation, canceled on teardown, guarded against duplicate submissions, and prevented from applying stale status or reload callbacks.

- **Bounded FFmpeg diagnostics**: stderr is drained in fixed-size chunks, capped to 8 KiB per diagnostic line, and marked once when truncated while continuing to drain the process pipe.

- **Regression coverage**: configuration lifecycle contracts cover stale confirmation/completion and duplicate deletion; FFmpeg tests cover multi-megabyte unterminated stderr and bounded cleanup.

- **mDNS class validation**: discovery accepts only Internet-class DNS questions and records while preserving mDNS cache-flush/unicast-response flag bits, so unrelated class data cannot be treated as a Hue endpoint.

- **Regression coverage**: discovery tests reject non-Internet question and resource-record classes and verify cache-flush compatibility.

- **mDNS compression safety**: discovery rejects forward DNS compression pointers, which are invalid on the wire and could otherwise manufacture service/host associations from later records.

- **Regression coverage**: discovery tests verify forward-pointer responses fail closed without returning a bridge address.

- **mDNS RDATA boundary hardening**: PTR and SRV discovery names are now required to fit entirely within their declared DNS record data, preventing malformed local responses from borrowing bytes from adjacent records.

- **Regression coverage**: discovery tests verify truncated SRV records fail closed without returning a bridge address.

- **Run-scoped Semgrep isolation**: self-hosted and pull-request security scans isolate their hash-locked virtual environment, reviewed rule snapshots, extracted administrator JavaScript, and reports by workflow run/attempt, so canceled-run cleanup cannot invalidate a newer scan.

- **Security workflow contract**: the Semgrep validator requires isolated run-scoped paths and rejects the legacy shared `RUNNER_TEMP` scanner state while keeping every finding, scanner error, and timeout fail-closed.

- **Scoped IPv6 bridge transport**: mDNS-discovered link-local IPv6 addresses preserve their interface scope in HTTPS request URIs, allowing bridges on multi-interface hosts to remain reachable.

- **Certificate-pin parity**: URI-encoded IPv6 zone identifiers normalize back to the persisted bridge address before certificate-pin lookup, with regression coverage for both transport and identity handling.

- **Strict channel-profile bound**: the 4,096-character limit is applied before blank-value handling, including whitespace-only input; ordinary blank fields below the limit still preserve all-channel/inherited behavior.

- **Regression coverage**: parser tests cover oversized valid-token and whitespace-only values.

- **Bounded channel-profile input**: global, per-user, and device-route channel profile text is capped at 4,096 characters before tokenization; valid channel IDs and delimiter behavior remain unchanged.

- **Regression coverage**: the configuration suite verifies oversized channel text is rejected before parsing, and the administrator editors expose the same limit.

- **Cleanup resolver trust boundary**: durable cleanup recovery re-checks injected or host-provided bridge resolver results against the private/link-local/unique-local boundary before treating an alias as the same physical bridge.

- **Regression coverage**: scheduler recovery tests reject non-local resolver results without contacting a bridge.

- **Administrator delete lifecycle safety**: single user-mapping deletes now bind confirmation and DELETE callbacks to the active configuration page, cancel on page teardown, prevent duplicate submissions, and suppress stale reloads/resets.

- **Durable cleanup alias recovery**: scheduled cleanup journals now reconcile IP/.local bridge aliases through vetted local addresses or matching certificate pins, while mismatched/ambiguous targets remain fail-closed.

- **Regression coverage**: lifecycle and scheduler tests cover stale mapping-delete confirmation/completion, alias recovery, and unrelated-target rejection.

- **Reviewed Semgrep snapshots**: blocking self-hosted and PR scans now use SHA-256-verified local registry snapshots, preventing mutable aliases or CDN responses from changing policy mid-run.

- **Install-failure isolation**: sub-scans only run after a successful hash-locked Semgrep install, preventing stale runner-temp files from being mistaken for current scan inputs.

- **Regression coverage**: the Semgrep lock validator verifies snapshot paths, file digests, workflow parity, and safe scan gating.

- **Gitleaks false-positive boundary**: the default Gitleaks rules remain active while only public example fixtures in the reviewed Semgrep snapshot directory are allowlisted, with a contract test preventing broader exclusions.

- **Certificate trust alias safety**: bridge certificate pins now resolve IP and .local aliases against a single vetted local address, fail closed on conflicting fingerprints, and keep registration, credential-free probes, transport TLS validation, playback arbitration, and pin management aligned.

- **Regression coverage**: API and certificate-validation tests cover alias equivalence, conflict rejection, alias cleanup, unknown-target protection, and credential-free probe behavior.

- **User-mapping save lifecycle safety**: mapping saves are tracked against the active configuration page, duplicate submissions are bounded, teardown and edit cancellation abort in-flight writes, and stale callbacks cannot alert, reload mappings, or reset a newer draft.

- **Regression coverage**: configuration contracts cover complete mapping payloads, duplicate-submit protection, pagehide cancellation, stale callback suppression, success reloads, and button-state restoration.

- **Scheduled-run identity safety**: manual Run Now cancellation remains bound to the cue ID that started the run, even if the administrator changes the selection; stale run callbacks cannot mutate a reused page.

- **Preview/capture lifecycle safety**: page teardown cancels in-flight preview, capture, and cancellation requests; stale callbacks cannot hide loading UI or overwrite an active page.

- **Recurrence search hardening**: cached normalized exclusion dates avoid reparsing exclusions for every candidate, and a maximum `DateTime` boundary guard prevents overflow with stale skip markers.

- **Selector and accessibility hardening**: persisted entertainment-area IDs are matched without selector interpolation, and diagnostics/mapping tables expose captions and column scopes.

- **Regression coverage**: configuration contracts cover cancellation identity, page lifecycle races, unsafe area IDs, and scheduler boundary conditions.

- **Semgrep snapshot integrity**: the blocking default ruleset pin is refreshed after the registry rotation and continues to fail closed on any unreviewed snapshot change.

- **Long-interval recurrence visibility**: internal next-run and Skip Next resolution now search beyond the public 366-day preview horizon, so valid weekly, monthly, and yearly cues with large configured intervals remain actionable.

- **Stale certificate trust-prompt cancellation**: approving a bridge certificate prompt after the configuration page is hidden now fails closed without mutating the server trust store.

- **Semgrep snapshot integrity**: the blocking default ruleset pin is refreshed after the registry rotation and continues to fail closed on any unreviewed snapshot change.

- **Regression coverage**: scheduler tests cover long recurrence intervals and the administrator configuration contract covers stale certificate trust prompts.

- **Audio terminal status**: naturally completed audio playback now reports an audio stream ending after shared cleanup instead of the video terminal message.

- **Legacy schedule target preservation**: partial schedule edits and configuration imports retain an omitted legacy `targetUserId`; explicit target selectors, including an explicit empty value for the global bridge, remain mode switches.

- **Certificate pin lifecycle management**: administrators can review credential-free bridge certificate pins in the configuration page and explicitly forget a retired or rotated bridge pin; deletion is validated, atomic, and fail-closed until replacement trust is confirmed.

- **Semgrep snapshot integrity**: the blocking default ruleset pin is refreshed after the registry rotation and continues to fail closed on any unreviewed snapshot change.

- **Regression coverage**: lifecycle and API tests cover audio cleanup status, omitted legacy target retention, selected-target switching, explicit global targeting, and partial-import parity.

- **Nested device-route API parity**: the current-light capture documentation now shows single-target `targetDeviceId` and batch `targetRoutes` request shapes, including the owning Jellyfin user and exact playback-device ID.

- **Documentation regression coverage**: the API-doc contract requires the exact single and batch nested-route examples.

- **Explicit background-task ownership**: reconnect scheduling call sites now discard the returned observer task explicitly, removing compiler diagnostics while retaining cancellation-safe observation.

- **Regression coverage**: the Hue streamer suite continues to await the scheduled observer for unrelated callback cancellation and canceled-result safety.

- **Cancellation-safe reconnect observation**: background DTLS reconnect continuations now handle canceled tasks without reading `Task.Result`, preventing unobserved cancellation exceptions during callback and lifecycle races.

- **Regression coverage**: the Hue streamer suite exercises preparation cancellation from an unrelated token and verifies the scheduled reconnect observation completes cleanly.

- **Semgrep snapshot integrity**: the blocking default ruleset pin is refreshed after the registry rotation and continues to fail closed on any unreviewed snapshot change.

- **Staged device-route diagnostics**: Test Connection and color Preview now send the staged route App Key and Client Key from the matching editor, so a newly staged route can be verified before the parent mapping is saved while persisted routes keep server-side credential fallback.

- **Regression coverage**: the configuration lifecycle contract verifies staged route credentials in both connection-test and preview payloads.

- **Single-mapping saved-scene previews**: selecting exactly one mapped user now sends only the selected-target representation for both individual and bulk previews, so the API no longer rejects the request as an ambiguous legacy/selected-target mix.

- **Regression coverage**: the configuration lifecycle contract verifies the corrected payload for individual and bulk saved-scene previews.

- **Semgrep snapshot integrity**: the blocking default ruleset pin is refreshed after the registry rotation and continues to fail closed on any unreviewed snapshot change.

- **Process-tree-safe FFmpeg teardown**: serialized capture lifecycle transitions retain and observe stderr/health tasks, close redirected streams, terminate descendants, and bound cleanup so playback restarts cannot strand child processes or pipe readers.

- **Manual one-time cue completion**: successful Run Now executions now disable one-time schedules through the existing persistence-retry path, increment run state, and prevent later automatic replay.

- **Semgrep scanner freshness**: the hash-locked scanner is updated to Semgrep 1.175.0 with reviewed compatible dependency hashes, and both blocking workflows use the same pin.

- **Regression coverage**: FFmpeg process-tree cleanup and manual one-time schedule replay prevention are covered by focused tests.

- **Playback-device discovery resilience**: invalid user filters fail with `400`, session enumeration failures return a sanitized `503`, null session lists remain empty, and credential-free device routes stay deterministically sorted and bounded to 256 results.

- **Probe process cleanup hardening**: bounded FFmpeg/version probes now cancel, terminate the entire process tree, close redirected streams, observe reader failures, and drain cleanup with a one-second limit so cancellation and oversized-output paths cannot strand diagnostics.

- **Locked restore enforcement**: `Directory.Build.props` now enables `RestoreLockedMode` globally, and the release-doc validator fails closed if the committed NuGet lock-file contract is removed.

- **Stale queued-run runbook**: self-hosted runner operations now document read-only inspection, safe cancellation, and owner escalation for queued Actions runs with no assigned job, without deleting workflow history.

- **Regression coverage**: API tests cover invalid filters, sanitized session failures, null enumerations, and 256-route bounds; environment-probe tests cover bounded output and cancellation cleanup.

- **Semgrep snapshot integrity**: reviewed and refreshed the default ruleset SHA-256 pin after the registry rotated; the current 121-rule production/configuration scan remains fail-closed with zero findings, scanner errors, or analysis timeouts.

- **Null-safe playback events**: Jellyfin playback-start events with missing item metadata are logged safely and remain filtered as unsupported instead of throwing before lifecycle arbitration.

- **Bounded environment probes**: FFmpeg version, diagnostic stderr, and PCM capability output are capped; oversized output terminates the probe and fails closed without allowing a misbehaving executable to grow memory indefinitely.

- **Regression coverage**: lifecycle and environment-probe tests cover null playback items, unbounded version stdout/stderr, and unbounded PCM output.

- **Security-scan concurrency isolation**: scheduled scans and trusted main-push security gates use separate event-scoped lanes, so a weekly scan cannot cancel a release-critical gate.

- **Semgrep snapshot integrity**: refreshed the reviewed SHA-256 pin for the default ruleset after the registry rotated; blocking scans continue to fail closed on any unreviewed change.

- **Fail-closed entertainment-area identity**: Hue configuration responses reject present malformed area IDs instead of treating them as legacy ID-less responses, preventing an unverified area's channel layout from being used for the requested target while preserving the documented ID-less fallback.

- **Weighted multi-target brightness**: aggregate current-light capture now weights brightness by captured light count, including off lights, so rooms with different numbers of lights seed the scene editor accurately.

- **Regression coverage**: client and API tests cover malformed area identities, legacy ID-less responses, unequal target sizes, and off-light brightness weighting.

- **Semgrep snapshot integrity**: refreshed the reviewed SHA-256 pins for the current default and Python ruleset snapshots after the registry rotated, with local findings, scanner errors, and fixpoint timeouts at zero.

- **Semgrep snapshot integrity**: blocking Semgrep workflows vary each ruleset request per run and attempt while retaining SHA-256 fail-closed verification, so any changed snapshot requires an explicit pin review.

- **Cross-platform Git provenance parity**: the PowerShell release helper applies an explicit safe.directory override when binding the source commit, matching Linux helper behavior on hardened self-hosted runners.

- **Source-bound release provenance**: release manifests now carry the exact 40-character source commit that produced the tested package, and the isolated release runner verifies that binding before publication.

- **Manifest schema hardening**: advance the deterministic manifest contract to schema version 2 with a required source-commit field and regression coverage for malformed or missing provenance.

- **Cross-platform helper parity**: both documented release helpers pass their checked-out commit into the manifest generator so Linux helper output remains byte-for-byte identical to the canonical package manifest.

- **Semgrep snapshot integrity**: refreshed the SHA-256 pin for the reviewed default ruleset snapshot in both blocking workflows after the registry snapshot rotated.

- **Deterministic release manifest**: every release now publishes exact packaged-file sizes and SHA-256 digests, the deterministic archive digest, and the plugin's resolved hash-locked NuGet graph.

- **Manifest parity gate**: the documented Linux release helper must produce a byte-for-byte matching manifest before the canonical package can publish.

- **Release asset integrity**: the isolated release runner verifies the manifest and both checksum sidecars, then binds all four GitHub release asset digests to locally verified bytes.

- **Regression coverage**: a dependency-free manifest self-test and executable workflow/package markers protect the provenance contract.

- **Semgrep snapshot integrity**: refreshed the SHA-256 pin for the reviewed default ruleset snapshot in both blocking workflows after the registry snapshot rotated again.

- **Matrix scene effect**: add a deterministic green/cyan cascading data-rain pattern with smooth trails and independent channel phases across previews, saved scenes, playlists, scheduled cues, and credential-free portability.

- **Semgrep snapshot integrity**: refreshed the SHA-256 pin for the reviewed default ruleset snapshot in both blocking workflows after the registry snapshot rotated again.

- **Case-insensitive cleanup recovery**: pending scheduled cleanup snapshots now match Hue entertainment-area UUIDs without treating upper/lowercase spelling differences as a target change, keeping imported or hand-edited target casing consistent with playback arbitration.

- **Regression coverage**: scheduler recovery tests verify deactivation and light restoration when persisted and configured area-ID casing differs.

- **Semgrep snapshot integrity**: refreshed the SHA-256 pin for the reviewed default ruleset snapshot in both blocking workflows after the registry snapshot rotated.

- **Restart-safe recurring claims**: recurring automatic scene occurrences persist an exact UTC occurrence slot and credential-free timing-definition key before bridge activity, so a scheduler restart cannot replay the same-minute or recovered catch-up cue.

- **Fail-closed occurrence persistence**: failed claim writes leave the cue eligible without contacting Hue, while cancellation, playback conflicts, skipped occurrences, schedule edits, and deleted cues reconcile durable claims safely.

- **Semgrep ruleset integrity**: the blocking workflows verify the refreshed SHA-256 pin for the reviewed default ruleset snapshot before scanning.

- **Regression coverage**: scheduler contracts cover same-minute restart suppression, catch-up suppression, next-occurrence execution, timing-definition edits, and persistence-failure retry.

- **Playback-conflict durability**: Defer scheduled cues now preserve their exact occurrence slot when playback starts after the scheduler's initial check, without consuming a run; active playback remains authoritative across direct scenes and continuous playlists.

- **Mapping editor safety**: disabled per-user mappings now disable all persisted audio-profile overrides until sync is re-enabled.

- **Regression coverage**: lifecycle-gate, stream, scheduler, playlist, and configuration-page contracts cover playback arbitration and disabled mapping controls.

- **Playback-generation identity**: concurrent playback routing treats `PlaySessionId` as the authoritative generation key, refusing to attach unknown IDs to stale workers from the same Jellyfin client and allowing rapid same-client transitions to a new Hue target.

- **Bounded bridge responses**: bridge REST and cloud-discovery bodies use header-first streaming with a strict 1 MiB limit, rejecting oversized success or error payloads without logging their contents.

- **Regression coverage**: concurrent playback, bounded response, history lifecycle, API conflict, and serialized persistence tests cover the new runtime and transport boundaries.

- **Active-lifecycle history clearing**: `DELETE /HueSync/History` and `DELETE /HueSync/SceneSchedules/History` use an independent serialized history lease, so clearing retained telemetry does not stop active playback, diagnostics, or scheduler evaluation while configuration snapshots remain protected.

- **Cross-service persistence ordering**: session and scheduled-cue history writes share synchronization, preventing a clear or persistence repair from racing another telemetry update.

- **Regression coverage**: lifecycle-gate, mutation-filter, API, scheduler-history, and session-history tests cover active playback clears, configuration conflicts, and serialized persistence.

- **Immediate sync-policy shutdown**: saving a disabled global or per-user policy now stops affected active Hue sessions after the durable write, restores captured lights, and requires a fresh playback event after re-enabling.

- **Selective lifecycle reconciliation**: global, single-user, bulk-user, and imported policy changes filter concurrent sessions by effective user state; duplicate or ambiguous mappings remain fail-closed.

- **Security-reporting accuracy**: SECURITY.md now makes private reporting conditional on the repository capability and directs reporters to an established private owner channel when that capability is unavailable.

- **Regression coverage**: lifecycle, API rollback, duplicate-mapping, import, gate, and toggle contracts cover immediate policy shutdown and safe restoration.

- **Shutdown-durable manual runs**: service shutdown now rejects new manual cues, cancels active runs outside the ownership lock, and awaits their bounded restorative cleanup before the hosted service stops.

- **Cancellation-safe automatic occurrences**: host-canceled transport failures retain scheduler slots and deferred markers across direct scenes and fallback playlists, while natural stream failures keep their ordinary failure accounting.

- **Saved-scene duplication lifecycle safety**: duplicate requests are tracked, bounded to one submission, canceled on page teardown, and prevented from updating a hidden or reused administrator page.

- **Regression coverage**: scheduler tests cover shutdown awaiting, transport-failure wording, fallback playlist retry, deferred-occurrence retention, and the duplicate saved-scene page lifecycle contract.

- **Restart-durable one-time claims**: automatic one-time cues persist their disabled state before bridge activity or deferred-expiry handling; failed claims fail closed, release the occurrence slot, and retry without replaying a cue after restart.

- **Deferred expiry durability**: expired deferred one-time occurrences retain their marker until the disabled state and skipped outcome are safely processed, preserving retry behavior across persistence failures.

- **Saved-scene lifecycle safety**: color-preset saves now cancel on page teardown, reject duplicate submissions, and ignore stale responses before updating a hidden or reused administrator page.

- **Regression coverage**: serializer-backed scheduler tests cover preclaim failure, deferred retry, reconstructed restart suppression, and the administrator color-preset lifecycle contract.

- **Lifecycle-safe scene saves**: saved playlists and scheduled cues now cancel on page teardown, reject duplicate submissions, and ignore stale responses so a hidden or reused administrator page cannot overwrite current state.

- **One-time completion repair**: a successful one-time cue remains disabled in memory and retries its completion write on the next scheduler pass when the initial persistence fails, without replaying bridge activity.

- **Repository governance**: CODEOWNERS now covers workflow, Dependabot, and Semgrep policy files for owner review while the owner-only direct-main bypass remains available for the trusted release path.

- **Regression coverage**: configuration-page lifecycle tests and serializer-backed scheduler tests cover stale saves, teardown cancellation, one-time repair, and no-duplicate execution.

- **Finite-schedule persistence retry**: failed finite run-count and auto-disable writes remain dirty and retry on the next scheduler pass even when retained schedule history is disabled.

- **Regression coverage**: serializer-backed scheduler tests verify authoritative finite state survives transient persistence failures without duplicate bridge activity.

- **Weekday-mask update parity**: partial schedule updates preserve an existing valid weekly weekday mask when the field is omitted, while new cues retain the all-days default and malformed legacy masks fall back safely.

- **History-repair persistence retry**: failed normalization, save, and empty-history clears retain a dirty marker and retry the authoritative in-memory history on a later read or write.

- **Paginated draft discovery**: trusted release publication searches all GitHub release pages so older drafts remain resumable after the repository exceeds 100 releases.

- **Regression coverage**: controller schedule updates, serializer-backed history retries, and paginated trusted-workflow fixtures cover the new boundaries.

- **User-mapping reconciliation lifecycle safety**: stale report and apply callbacks are cancelled or ignored after page teardown, so hidden pages cannot show obsolete confirmations, status, or mapping reloads.

- **Retry-preserving shutdown cleanup**: failed entertainment-area deactivation retains its target snapshot so deferred cleanup can retry safely with a fresh cancellation token.

- **Post-publication asset integrity**: the trusted release gate rechecks GitHub-recorded ZIP and checksum digests after publication, closing replacement races after pre-publication verification.

- **Regression coverage**: configuration lifecycle, shutdown deactivation retry, and final release-digest contracts cover the new safety boundaries.

- **Null-safe draft lookup**: trusted release publication treats GitHub CLI's empty no-release response as an explicit null and does not confuse a missing draft with a published release.

- **Release-ID integrity gate**: draft discovery, asset digest verification, and publication remain bound to one verified release record and workflow commit.

- **Regression coverage**: the trusted-workflow contract covers empty lookup normalization, draft state, target identity, asset digests, and API publication.

- **Runner-stable Semgrep script gate**: repository-validator scans explicitly exclude only the non-applicable Express raw-HTML rule; all other JavaScript findings, scanner errors, and fixpoint timeouts remain blocking.

- **Draft-safe release publication**: trusted automation resolves draft releases through GitHub's release API and publishes by verified release ID, so the digest gate works before and after a draft becomes public.

- **Idempotent asset handling**: release retries remain bound to the exact workflow commit and verified ZIP/checksum assets without creating duplicate releases.

- **Regression coverage**: the trusted-workflow contract requires draft lookup, release-ID digest verification, and API-based publication markers.

- **Cancellation-safe shutdown restoration**: if host shutdown cancellation interrupts light restoration, the bridge snapshot is retained and deferred cleanup retries restoration and entertainment-area deactivation with a fresh token.

- **Page lifecycle-safe runtime stops**: stale Stop and per-session Stop callbacks no longer restart polling or show alerts after navigation, while the explicit server-side stop request is preserved.

- **Release asset digest verification**: trusted publication checks GitHub's recorded ZIP and checksum-asset SHA-256 digests against the locally verified bytes before making the release public.

- **Regression coverage**: host-cancellation cleanup retry, runtime Stop page teardown, release digest markers, and the complete configuration/security contracts are exercised before release.

- **Bridge response identity validation**: state capture rejects malformed or mismatched Hue light resource IDs rather than labeling another light as the requested resource, preventing unsafe restoration.

- **Workflow inventory hardening**: every tracked workflow remains in the reviewed set, uses immutable selected actions, and keeps persistent self-hosted jobs behind per-job `main` guards; pull-request jobs stay on fixed ephemeral runners without secrets or write permissions.

- **Disabled mapping preview safety**: the administrator's Preview Current Color control follows the selected mapping's Sync Enabled state and remains correct after metadata, lifecycle, and busy-state updates.

- **Regression coverage**: malformed/mismatched bridge IDs, disabled-mapping preview state, and workflow-boundary contracts are exercised before release.

- **Deferred scheduler durability**: expired deferred one-time cues now clear stale Skip Next markers together with their completed disabled state, and deferred queue/removal/expiry/normalization writes retry from the authoritative runtime snapshot after transient persistence failures.

- **Regression coverage**: scheduler tests verify expired-marker cleanup, queue and removal persistence retries, no duplicate bridge activity, and no deferred replay after a failed write.

- **Deferred one-time cue cancellation**: mark a one-time cue that is waiting behind active playback with **Skip Next Cue**; the scheduler consumes the pending occurrence without touching the bridge, disables the cue, clears its durable defer state, and records skipped/deferred telemetry. The atomic bulk Skip Next action has the same bounded behavior while retaining active-run, execution-limit, and stale-state protections.

- **Regression coverage**: single and bulk deferred-cue tests verify marker persistence, scheduler consumption, one-time disablement, deferred cleanup, and credential-free history/status parity.

- **Complete Semgrep target coverage**: extract and scan the DLL-embedded administrator JavaScript and include the PowerShell release helper in the blocking, hash-pinned ruleset gates.

- **Regression coverage**: trusted and pull-request workflow contracts require every extracted-script, target-extension, report, and timeout gate.

- **History-only scheduler clears**: clearing retained cue history preserves pending deferred occurrences, their status/waiting message, and retry behavior.

- **Cancellation-safe host shutdown cleanup**: interrupted sync-loop waits defer bridge restoration until the predecessor exits and then retry deactivation with a fresh non-canceled cleanup token.

- **Fail-closed Semgrep analysis**: split production/non-JavaScript and repository-script scans, block fixpoint timeouts, and verify reviewed SHA-256-pinned rule snapshots before use.

- **Regression coverage**: scheduler history, host-shutdown cleanup, and Semgrep workflow contracts cover the new safety boundaries.

- **Cancellation-safe deferred schedules**: host-shutdown cancellation retains deferred occurrences, releases occurrence slots, and leaves finite run counters unchanged until a cue completes.

- **Snapshot-consistent scheduler conflicts**: matching-target playback arbitration uses the evaluated configuration snapshot's certificate identity.

- **Regression coverage**: cancellation/retry and snapshot-identity tests protect deferred persistence and target arbitration.

- **.NET 8 ABI guard**: Dependabot ignores major `System.Text.Json` and `System.Text.Encodings.Web` updates because the plugin does not ship framework assemblies; security and minor/patch monitoring remain enabled.

- **Executable Dependabot policy contract**: every tracked NuGet project block is required to carry the ABI guard so unsupported major PRs do not reopen unattended.

- **Complete NuGet Dependabot coverage**: monitor the plugin, test, and benchmark project directories explicitly so nested package manifests cannot be skipped by a repository-root configuration.

- **Configuration regression contract**: validate every tracked NuGet project directory plus the GitHub Actions and hash-locked Semgrep update blocks in both trusted and pull-request workflows.

- **Release verification**: keep locked restore, vulnerability scanning, immutable-action checks, and the self-hosted release gates authoritative for the expanded dependency configuration.

- **Alias-safe target fan-out**: scheduler and current-light capture deduplicate IP and `.local` target aliases with the configured certificate identity and channel profile, preventing duplicate work against one physical Hue area.

- **Target ID contract**: selected target fields use Jellyfin user IDs, while stable `mappingId` values remain reserved for exact-row administration; API/UI examples and regression coverage enforce the distinction.

- **Reconciliation persistence**: generated legacy mapping-row identities now save atomically for healthy users and roll back on unavailable directories or persistence failures.

- **Alias-safe playback arbitration**: trusted bridge certificate fingerprints provide a stable physical-bridge identity, so an IP address and its `.local` alias cannot acquire concurrent playback leases while separate pinned bridges remain independent.

- **Manual scheduler snapshot barrier**: manual Run now acquires scheduler evaluation before resolving configuration and holds it through the tracked lifecycle, preventing edits or deletes from racing a stale schedule snapshot.

- **Regression coverage**: lifecycle, scheduler, and import-validation tests cover certificate-identity aliasing and barrier lifetime.

- **Captured playback scope**: Live Sync Status now reports the effective media scope captured when playback starts, even if global or per-user settings change while the session is active; seek restarts retain the same policy.

- **Same-target playback handoff**: a replacement session for the same bridge and entertainment area now waits for the predecessor stream, reuses its lifecycle lease, and preserves the original light-state snapshot for safe restoration.

- **Bracketed IPv6 bridge support**: pinned private and link-local IPv6 bridge URLs now pass local-host certificate validation when written in the standard bracketed URI form.

- **Regression coverage**: lifecycle and certificate-validation tests cover scope mutation, same-target replacement, saved-state retention, and bracketed IPv6 pins.

- **Credential-bearing administrator preflight**: bridge area/channel loading, connection tests, and single-target previews now require an explicit certificate pin before sending App Keys, with stale page lifecycle guards that cannot start a request after navigation.

- **Strict mapping credential input**: non-string App Key and Client Key JSON values are rejected atomically, and device-route stored-credential flags must prove the required key pair before reuse.

- **Fail-closed release retries**: the trusted release job resumes only a matching draft and refuses to overwrite an already-published release.

- **Regression coverage**: API, configuration-page lifecycle, route credential, and trusted-workflow contracts cover the new safety boundaries.

### Version 1.5.330

- **Local bridge certificate pinning**: credential-free probes expose a SHA-256 fingerprint and explicit administrator trust/re-pin is required before registration or any App Key request; unpinned, changed, malformed, or conflicting certificates fail closed.

- **Per-target retry isolation**: scheduled scene and playlist runs clone Hue transport state so mapping retry overrides cover discovery, capture, activation, reconnect, deactivation, and restoration without cross-room races.

- **Scheduler clock correctness**: long-running cues advance an absolute UTC logical clock and derive due slots from the advanced evaluation instant across midnight and DST transitions.

- **Media-scope documentation parity**: administrator copy and README now accurately describe supported video/audio sync and certificate trust setup.

- **Regression coverage**: API, configuration, certificate-validation, mutation-filter, and static documentation contracts cover the new safety boundaries.

- **Concurrent playback transport**: the shared HTTP timeout is configured once by the typed client factory, so creating a concurrent playback client never mutates a client after request startup.

- **Route-specific channel profiles**: administrators can load channel IDs directly into the selected device route; route Test Connection and Preview use route channels and fall back to outer/global profiles when blank.

- **Release-helper parity**: trusted self-hosted CI executes the documented Linux release helper and verifies its archive and checksum against the canonical package contract.

- **Regression coverage**: transport and configuration-page tests cover shared-client concurrency and route/outer channel isolation.

- **Manual/scheduled cue slot recovery**: when a scheduler evaluation loses a race with a manually running cue, its claimed occurrence slot is released so the same occurrence can run after the manual lifecycle completes instead of being silently suppressed.

- **Scoped device-route credential caching**: temporary administrator route credentials are now isolated by normalized user/mapping, bridge, and case-sensitive device identity, preventing a reused device ID on another bridge or mapping from inheriting the wrong keys.

- **Exact configuration-action classification**: configuration mutation arbitration now matches route segments and HTTP methods exactly, so saved scenes, playlists, and scheduled cues whose names contain `Preview`, `Run`, or `Cancel` cannot bypass the writer lease; only the documented preview/run/cancel actions remain pass-through.

- **Stale-progress stop safety**: natural playback-stop cleanup now marks the exact session as in-flight and rejects delayed progress/resume events until cleanup completes, preventing lights from restarting after playback has ended while preserving newer-session queueing.

- **Lifecycle-safe configuration imports**: pending `FileReader` operations are aborted on page teardown or replacement, and success/error callbacks require both the current page generation and reader identity before changing import state.

- **Selected Actions policy**: repository Actions now permits only the seven action repositories required by the workflows and the pinned Codecov composite dependency, keeps broad owner/verified allowances disabled, and requires full commit-SHA references.

- **Exact-session stop safety**: explicit `playSessionId` stop requests now fail closed when a listed worker is stale or unknown, preventing an unrelated primary playback session from being stopped; matching primary IDs remain supported.

- **Lifecycle-safe duplicate resolution**: administrator duplicate-mapping confirmation now participates in page-generation cancellation, suppresses late callbacks after navigation, and cannot start a destructive request after `pagehide`.

- **Explicit coverage publication**: trusted CI retains Cobertura artifacts, records when optional Codecov publication is skipped because no token is configured, and fails closed when a configured upload cannot complete.

- **Optimistic configuration-import concurrency**: `ValidateImport` now returns a credential-safe snapshot token, and `Import` requires the matching token so stale administrator tabs cannot overwrite newer settings, credentials, mappings, scenes, playlists, or cues; runtime and retained telemetry changes do not invalidate an approval.

- **Fail-closed release publication**: the trusted self-hosted release job verifies the exact ZIP/checksum asset set before publishing, retries final release reads, and only resumes a draft whose tag points at the current workflow commit.

- **Resumable release publication**: the trusted self-hosted release job resumes partial drafts, retries transient GitHub API failures, and verifies the published tag, commit, ZIP, and checksum assets before succeeding.

- **Stable multi-target previews**: raw, saved-scene, and saved-playlist previews now snapshot validated target credentials and saved-scene values under a short configuration read lease before releasing the lease for bridge I/O. Bulk previews retain one detached target/preset snapshot across every item, return a retryable conflict during active configuration mutation, and cannot be redirected by later edits.

### Version 1.5.319
- **Schedule target integrity**: explicit all-enabled broadcast requests that also select users, device routes, or the default target are rejected as ambiguous, while partial imports and edits switch away from inherited broadcast mode safely before validation.
- **Security gate**: the committed lifecycle test sentinel is explicitly documented as non-secret historical data and excluded by a fingerprint-only Gitleaks rule; the active fixture no longer resembles a credential.
- **Regression coverage**: direct-save and import tests cover mixed-target rejection, partial-target conversion, and atomic rollback.

### Version 1.5.318
- **Restoration fidelity**: current-light snapshots preserve writable Hue v2 gradient, effects/effects_v2, timed-effects, alert, and effect parameters with canonical bounded payloads while excluding read-only bridge metadata.
- **Lifecycle and targeting safety**: diagnostics cancel all active target operations, stream generations prevent stale reconnect races, duplicate user mappings are blocked in target controls, and partial scheduled-cue target edits correctly switch away from inherited broadcast mode with atomic rollback.
- **Regression coverage**: focused tests cover advanced state capture/restore, lifecycle races, duplicate mapping controls, and schedule target-mode transitions.

### Version 1.5.317
- **Credential-resolution lifecycle isolation**: bridge-area, channel, and connection-test routes take a short shared configuration read lease while resolving persisted credentials, fail closed during configuration mutation, and release it before network I/O.
- **Regression coverage**: API tests verify mutation contention produces no bridge calls across all three routes.

### Version 1.5.316
- **Raw preview credential isolation**: direct single-target previews take a short shared configuration read lease while resolving stored bridge credentials, fail closed with a retryable conflict during configuration mutation, and release the lease before bridge I/O.
- **Regression coverage**: API tests verify mutation contention produces no bridge or stream calls.

### Version 1.5.315
- **Capture lifecycle isolation**: single and batch current-light capture routes reserve the shared diagnostic lifecycle before resolving persisted mappings and credentials, returning a retryable conflict instead of observing a concurrent configuration mutation.
- **Regression coverage**: API tests verify capture target resolution is blocked before any bridge request while configuration changes are active.

### Version 1.5.314
- **Configuration read isolation**: all credential-free scene, playlist, status, diagnostics, and user-mapping projections now take a short shared read lease, rejecting requests while an administrator mutation or scheduler evaluation is applying.
- **Export conflict fidelity**: JSON, CSV, and iCalendar schedule exports preserve the retryable 409 response instead of being misreported as a 404 or empty report during a configuration mutation.
- **Regression coverage**: API tests cover every protected projection under configuration mutation and scheduler-evaluation barriers, plus diagnostics after its environment probe.

### Version 1.5.313
- **Shutdown cancellation resilience**: host cancellation during a lifecycle-lock wait now schedules an observed deferred cleanup pass, preserving bridge deactivation, sync-state clearing, and playback-lease release.
- **Concurrent worker retention**: isolated playback workers remain tracked until stop completes so cancellation can be retried instead of orphaning a target.
- **Restart safety**: service startup waits for deferred cleanup before accepting a new session, preventing stale shutdown work from clobbering restarted playback.
- **Regression coverage**: lifecycle tests cover canceled lock waits and eventual cleanup/lease release.
- **Pause cleanup retry safety**: failed entertainment-area deactivation now remains eligible for the shutdown retry and surfaces a credential-free cleanup warning.
- **Read-only administrator projections**: playlist, user-mapping, reconciliation, dependency, and target-diagnostics reads no longer initialize collections or manufacture mapping IDs in live configuration.
- **Scheduler read isolation**: status/history API reads do not persist lazy repairs, and scheduler evaluation cannot overlap a configuration snapshot.
- **Regression coverage**: lifecycle, scheduler, API, and gate tests cover failed pause deactivation, shutdown retry, non-mutating reads, and scheduler/read exclusion.
- **Consistent configuration snapshots**: configuration reads and import validation now use a shared short-lived read lease, preventing hybrid responses while an administrator save or import is applying.
- **Save lifecycle protection**: administrator configuration saves cancel stale loads, suppress hidden or superseded callbacks, and bound duplicate submissions with recoverable controls.
- **Playback-stop fault observation**: playback-stop event work now flows through the service task observer so asynchronous cleanup faults are logged instead of escaping as `async void` exceptions.
- **Regression coverage**: lifecycle, endpoint contention, configuration-page, and playback-stop handler contracts cover the new consistency and cancellation behavior.

### Version 1.5.310
- **Fail-closed channel frames**: malformed, null, or short RGB16 payloads are rejected before threshold comparison, transport health checks, or reconnect scheduling.
- **Cancellation-aware startup**: entertainment-area activation waits now stop immediately when the linked sync lifecycle is cancelled.
- **Regression coverage**: streaming tests verify malformed frames never emit packets or trigger reconnects after a valid stream has started.

### Version 1.5.309
- **DST-safe deferred cues**: persisted waits now retain a UTC timestamp, migrate legacy local-only entries safely, and expire from elapsed instants rather than wall-clock arithmetic.
- **Global target integrity**: partially populated default bridge targets are validated even when custom user/device mappings exist, while intentionally blank global targets remain valid for custom-only configurations.
- **Configuration navigation**: the administrator page now provides a keyboard-friendly section index with focusable anchors for status, diagnostics, bridge targets, scenes, scheduling, mappings, and advanced settings.
- **Regression coverage**: scheduler, configuration validation, and static administrator-page contracts cover UTC migration, incomplete targets, navigation links, and focusable sections.

### Version 1.5.308
- **Area-target correctness**: entertainment configuration responses now select the requested resource by ID and fail closed when identified responses describe a different area, while preserving legacy responses without resource IDs.
- **Transport recovery**: disposed and I/O-failed DTLS connections are invalidated only when still active and enter the existing serialized, bounded reconnect flow without closing a replacement stream.
- **Accessible diagnostics**: administrator action results expose atomic live status/alert regions while high-frequency scheduler telemetry remains quiet for screen readers.
- **Regression coverage**: Hue client, streaming lifecycle, and configuration-page contracts cover target selection, transport disposal, stale-connection races, and live-region semantics.

### Version 1.5.307
- **Fail-closed channel validation**: connection diagnostics ignore malformed, non-numeric, negative, and out-of-range entertainment channel IDs and skip DTLS probing when no controllable channels remain.
- **Reconnect resilience**: failed internal DTLS startup attempts retain the active target and lifecycle state so later frames can consume the remaining bounded retry budget; public stops still clear stale reconnect state.
- **Playlist duration visibility**: saved playlist selectors and schedule status, upcoming-occurrence, and history tables render bounded total durations alongside their step and pass metadata.
- **Regression coverage**: API, streaming lifecycle, and administrator configuration-page contracts cover channel validation, reconnect retry preservation, and playlist telemetry rendering.

### Version 1.5.306
- **Lifecycle-safe configuration import**: validation and submission cancel superseded or hidden-page requests, suppress stale responses, clear approval state on teardown, and block duplicate in-flight imports.
- **Malformed mapping protection**: invalid nested device-target values return a sanitized 400 response without mutating persisted mappings.
- **Deferred-cue correctness**: persisted playback-deferred occurrences are checked against the current schedule definition before replay, preventing stale timing after edits or imports.
- **Regression coverage**: API, scheduler, and configuration-page contracts cover malformed input, stale lifecycle responses, page teardown, and edited deferred cues.

### Version 1.5.305
- **Stale mapping-edit protection**: administrator mapping edits cancel superseded requests and ignore late success, failure, and cleanup callbacks after navigation or pagehide.
- **Fail-closed schedule routes**: malformed null target routes are rejected before direct-save or import mutation.
- **Partial schedule state preservation**: omitted `Enabled` and `SkipNextOccurrence` fields retain existing values during partial updates, while explicit values override them.
- **Lifecycle and import regression coverage**: API and configuration-page contracts cover stale responses, malformed routes, and omitted-versus-explicit schedule state.

### Version 1.5.304
- **Credential entry protection**: global and per-user Hue keys are masked password fields with `new-password` autocomplete hints.
- **Ambiguous mapping rejection**: direct mapping writes fail closed on case-variant duplicate properties recursively, before nested targets or credential fields are materialized.
- **DST fall-back scheduling**: due checks now match the resolved UTC occurrence minute, avoiding early or duplicate runs for ambiguous local times.

### Version 1.5.303
- **Ambiguous mapping input rejection**: direct user-mapping writes fail closed on duplicate property names that differ only by case, preventing inconsistent JSON binding before nested targets are materialized.
- **Security regression coverage**: oversized case-variant device-target input is rejected without changing persisted mappings.

### Version 1.5.302
- **Export teardown recovery**: tracked administrator export cancellation restores button state after navigation, pagehide, or bfcache restore and prevents stale downloads.
- **Export lifecycle harness**: CI gates execute deterministic VM coverage for all JSON, CSV, and iCalendar administrator exports.
- **Bounded backend inputs and retries**: nested device-target imports and direct mapping payloads are rejected before materialization when oversized, and Hue retry values are clamped to a finite 0-10 range.

### Version 1.5.301
- **Lifecycle-safe exports**: configuration, scheduled-cue JSON/CSV/iCalendar, and session-history exports are bound to the active page and query scope; stale work is cancelled or ignored and controls recover after filter changes or navigation.
- **Export regression contracts**: validator coverage protects request records, filter/horizon cancellation, credential-free downloads, and teardown cleanup.

### Version 1.5.300
- **Run/cancel lifecycle safety**: bulk scheduled-cue Run and Cancel actions no longer contend with the configuration-writer lease, so active runs execute and remain cancellable.
- **Stale-safe occurrence exports**: occurrence JSON exports cancel superseded requests and ignore stale filter or page responses, including safe teardown reset.
- **Dependabot and PR gate**: untrusted changes run read-only build, test, format, vulnerability, Gitleaks, and Semgrep checks on ephemeral `ubuntu-24.04`; trusted main packaging and release remain on the named self-hosted runners.

### Version 1.5.299
- **Bounded mapping saves**: public user-mapping saves reject a new 101st row before assignment or persistence while exact-limit edits remain supported.
- **Scheduled-cue lifecycle safety**: runtime status, conflicts, occurrences, history, and conflict-report export requests cancel superseded work and ignore stale responses or filter scopes.
- **Regression contracts**: mapping capacity and administrator report lifecycle checks preserve no-mutation and credential-free behavior.

### Version 1.5.298
- **Reproducible releases**: CI and both local release helpers use the same deterministic packager with canonical entry order, fixed timestamps/metadata, and fixed DEFLATE settings.
- **Package determinism gate**: release validation proves identical staged bytes produce identical archive hashes despite source order and mtimes, then verifies the strict checksum sidecar.

### Version 1.5.297
- **Import capacity hardening**: preset, playlist, and schedule collections, playlist parallel step arrays and target selections, and schedule target/excluded-date collections are rejected before normalization when they exceed persisted limits.
- **Mapping-area lifecycle safety**: entertainment-area requests now use page generations, target fingerprints, cancellation, and stale-response guards so bridge responses cannot overwrite a newer mapping draft or reopened page.

### Version 1.5.296
- **Target-scoped mapping safety**: entertainment-area, channel-ID, route-area, and playback-device discovery responses now carry page generations and target fingerprints, so a stale bridge response cannot overwrite a newer mapping draft.
- **Cancelable mapping requests**: newer target requests and pagehide now abort the previous mapping request when the client supports it.

### Version 1.5.295
- **Bounded mapping storage**: persisted user mappings are capped at 100 rows, and oversized configuration imports are rejected before normalization or mutation.
- **Capacity contract coverage**: exact-limit and limit-plus-one validation tests protect both runtime configuration and import preflight behavior.

### Version 1.5.294
- **Page lifecycle safety**: administrator configuration and metadata loaders now use page-scoped generations, tracked aborts, and stale-response guards so navigation or reopen cannot overwrite current edits with late responses.
- **Administrator accessibility**: manual entertainment-area, scheduled weekday, and playback-device route controls now expose explicit labels and ARIA names.

### Version 1.5.293
- **Bridge-safe diagnostics**: target diagnostics and support bundles now hold the shared diagnostic lifecycle lease for their complete live-bridge snapshot and return `409 Conflict` while playback or another diagnostic owns the bridge.
- **Cross-platform package verification**: the PowerShell release helper now parses and independently verifies the generated SHA-256 sidecar filename and digest.

### Version 1.5.292
- **Runtime observability**: Live Sync Status now shows the localized sync start timestamp alongside playback freshness and duration.
- **Administrator accessibility**: runtime errors announce through live regions, and device-route/import credential controls now expose explicit accessible names.

### Version 1.5.291
- **Fail-closed runtime status**: failed or superseded status requests now clear stale playback, target, health, and session telemetry; hidden-page polling is invalidated and aborted when supported.
- **API contract parity**: the documented `GET /HueSync/Status` contract now names `playbackObservedAtUtc`, matching the live API and administrator panel.

### Version 1.5.290
- **Resolved-peer security**: `.local` bridge names are resolved once and every answer must be private, link-local, or unique-local before the exact address is used for REST or managed DTLS credentials; mixed/public answers fail closed.
- **Playback freshness**: Live Sync Status shows the localized timestamp of the latest Jellyfin playback observation and clears it when no timeline is active.
- **Lifecycle and interoperability coverage**: public replacement starts serialize with reconnects so stale cleanup cannot close a new stream, while deterministic loopback DTLS tests negotiate Hue's PSK contract and bound cancellation without hardware.

### Version 1.5.288
- **Self-hosted runner resilience**: the diagnostics FFmpeg version probe now tolerates bounded process-start contention, and CI preflights the exact short PCM capture required for audio playback before running tests.

### Version 1.5.287
- **Reproducible release tooling**: Linux and PowerShell release helpers now enforce locked NuGet restores, matching CI and failing closed on dependency-graph drift; the release-documentation validator protects that contract.

### Version 1.5.286
- **Per-target scheduled-cue observability**: administrator scheduler status and retained history now render each credential-free target's label, success/skip/failure state, message, channel counts, and cleanup warning, so partial multi-room outcomes remain visible beside the aggregate result.

### Version 1.5.285
- **Host-cancellation-aware shutdown**: service stop propagates Jellyfin's host shutdown token through workers, paused cleanup, lifecycle acquisition, sync-loop waits, and bridge cleanup so a blocked capture or DTLS loop cannot indefinitely delay application termination; interrupted cleanup remains visible as a credential-free warning.

### Version 1.5.284
- **Side-effect-free import planning**: configuration imports now deep-clone existing mapping rows, including nested device-target credentials and profile overrides, before legacy mapping-ID normalization; invalid documents and persistence failures cannot mutate live row identities or credentials.

### Version 1.5.283
- **Stable-row configuration import**: credential-safe imports match per-user mappings by exact `mappingId`, fall back to a unique user row for cross-server migrations, reject ambiguous duplicate groups, preserve exact-row credentials, and replace only the selected row during partial merges.

### Version 1.5.282
- **Honest target diagnostics**: saved-target validation now reports credential-free duplicate mapping groups, blocks affected rows before bridge contact, and prevents the administrator UI from displaying an all-ready result while any duplicate group remains unresolved.

### Version 1.5.281
- **Fail-closed current-light capture**: direct, selected-route, and all-target diagnostic capture rejects ambiguous duplicate Jellyfin-user mappings before resolving credentials or contacting a bridge.

### Version 1.5.280
- **Deterministic duplicate mapping resolution**: administrators can choose one exact stable row to retain from a duplicate Jellyfin-user group; an optimistic reconciliation version, complete sibling selection, enabled/valid keeper checks, candidate validation, and atomic rollback protect saved scenes, device routes, and credentials.
- **Fail-closed runtime targeting**: playback credentials, scene validation, and scheduled target resolution no longer select the first arbitrary duplicate row; unresolved duplicates remain blocked until explicitly resolved.

### Version 1.5.279
- **Exact-row mapping state changes**: bulk enable/disable carries stable mapping IDs so duplicate rows cannot update or scrub the wrong credentials; legacy user-ID-only state changes fail closed with `409 Conflict` when ambiguous.

### Version 1.5.278
- **Exact-row mapping deletion**: single-row, bulk, and dependency actions carry stable mapping IDs so deleting one duplicate cannot remove its siblings; legacy user-ID-only deletion fails closed with `409 Conflict` when ambiguous.

### Version 1.5.277
- **Duplicate-safe mapping edits**: mapping editors carry stable row IDs, exact-row edits preserve sibling duplicates, and ambiguous user-ID-only edits fail closed before mutation.

### Version 1.5.276
- **Safe stale mapping cleanup**: administrators can remove exact missing or malformed mapping rows only after an optimistic reconciliation-report check and dependency preflight; duplicate, referenced, and healthy rows remain protected without exposing credentials.

### Version 1.5.275
- **User-mapping lifecycle reconciliation**: elevated administrators can compare persisted mapping IDs and names with Jellyfin's live user directory, safely refresh existing unique users atomically, and review missing, malformed, or duplicate mappings without exposing Hue credentials.

### Version 1.5.274
- **Credential-free playback progress**: Live Sync Status, `GET /HueSync/Status`, and support bundles now show active media position, duration, bounded progress percentage, pause state, and observation time without exposing playback tokens or bridge credentials.
- **Release-documentation guard**: validation now requires exactly one current release heading matching `meta.json`.

### Version 1.5.273
- **Accurate release requirements**: release instructions now state that only FFmpeg is an external prerequisite; the managed Hue DTLS transport is included in the package and OpenSSL is not required.

### Version 1.5.272
- **Credential-safe DTLS**: replace the OpenSSL child process with managed Bouncy Castle DTLS 1.2 PSK, keeping Hue App/Client Keys out of process arguments and `/proc` command-line inspection.
- **Dependency-complete packaging**: release archives include `BouncyCastle.Cryptography.dll`; managed DTLS startup is cancellation-bounded and no longer depends on an OpenSSL executable.
- **Regression coverage**: verify the Hue cipher contract, connected UDP transport, and nonresponsive-handshake cancellation cleanup.

### Version 1.5.271
- **Bounded cancellation cleanup**: diagnostic, preview, pause, startup rollback, and playback restoration use an independent 30-second budget that survives page/request cancellation while preventing an unreachable bridge from holding the lifecycle lease indefinitely.
- **Partial restoration telemetry**: timed-out cleanup retains credential-free attempted/restored/failed counts and surfaces the existing cleanup warning instead of claiming success.

### Version 1.5.270
- **Credential-free cue metadata parity**: valid GUID mapping IDs now serialize as canonical D-format text across scheduler status, upcoming occurrences, playlist/scene preview results, skip/failure telemetry, and restored history while malformed opaque legacy IDs remain unchanged and fail closed.
- **Legacy route editor matching**: the administrator cue editor compares brace/N-format route and mapping IDs by GUID value, so legacy persisted targets remain selectable instead of appearing unavailable.

### Version 1.5.269
- **GUID-equivalent target references**: schedule and playlist target user IDs now canonicalize on save/import, and runtime/mapping dependency resolution treats valid brace/N-format legacy IDs as the same Jellyfin user without broadening malformed-ID matching.
- **Import target warning**: the migration confirmation now explains that stored bridge keys are preserved only for an unchanged target and that changed targets require replacement keys or an explicit credential clear.
- **Legacy route coverage**: API and scheduler tests cover brace/N-format device routes plus legacy mapping update/delete behavior.

### Version 1.5.268
- **Global credential target binding**: omitted bridge credentials are preserved only when the configured global bridge target is unchanged; changing targets requires replacement keys or an explicit clear operation and fails closed before mutation.
- **Import refresh parity**: successful configuration imports now refresh scheduled cues along with settings, mappings, saved scenes, and playlists.

### Version 1.5.267
- **Import-safe canonical mapping IDs**: configuration backup imports now require valid Jellyfin user GUIDs, normalize brace/N-format values before merge and credential preservation, and reject malformed or canonical-equivalent duplicate mappings without mutation.
- **Import rollback coverage**: malformed import, credential-preserving normalization, and duplicate detection paths are covered by API regression tests and static documentation contracts.

### Version 1.5.266
- **Canonical Jellyfin mapping IDs**: public per-user mapping saves now require valid Jellyfin user GUIDs and normalize brace/N-format inputs to the canonical form used by runtime lookups.
- **Fail-before-mutation mapping validation**: malformed mapping IDs return a clear `400` before persisted mappings can change, with API/documentation regression coverage.

### Version 1.5.265
- **Portable scheduled time zones**: scheduled-cue exports and API results include a canonical IANA `timeZoneIanaId` alongside the host `timeZoneId`; Windows and IANA aliases resolve on either supported OS, and imports normalize legacy values without falling back to server-local time.
- **Timezone migration coverage**: the administrator selector submits the canonical value, upcoming occurrences/runtime status expose it, and tests cover cross-platform aliases plus fail-closed unmappable IDs.

### Version 1.5.264
- **Setup-complete device-route editor**: add, update, remove, and discover areas for nested playback-device routes without hand-editing JSON; route credentials remain write-only and blank existing keys preserve server-side secrets.
- **Scoped discovery and preview metadata**: device discovery filters by valid user ID server-side, and direct previews return credential-free user/device target metadata.

### Version 1.5.263
- **Direct preview route parity**: Preview Current Color carries the selected nested `deviceId` through the direct preview API, keeping bridge credentials and route resolution consistent with areas, channels, and connection tests.
- **Nested route regression coverage**: blank redacted keys resolve only the exact configured playback-device target.

### Version 1.5.262
- **Playback-device route discovery**: the administrator mapping editor discovers recent Jellyfin device IDs, selects exact configured routes, and keeps nested credentials server-side while loading areas/channels or testing a route.
- **Route-aware API and security coverage**: area, channel, and connection diagnostics accept an explicit device ID and fail closed on wrong-case, wrong-user, or wrong-bridge routes; bounded discovery returns identity/activity metadata only.

### Version 1.5.261
- **Health checks before threshold suppression**: static scenes now verify DTLS stream health and attempt reconnection before color-change threshold skips, preventing a dead managed DTLS session from remaining broken indefinitely.
- **Static-scene recovery coverage**: verifies an unhealthy stream is not reported as a successful threshold skip.

### Version 1.5.260
- **Scoped IPv6 bridge discovery**: mDNS responses now retain the receiving interface scope on link-local AAAA addresses, so discovered `fe80::` bridges remain routable on the correct local interface.
- **Scoped discovery coverage**: parser and bridge-registration URI regressions verify link-local zones are preserved without scoping unique-local addresses.

### Version 1.5.259
- **Restarted finite-cue accounting**: persisted skipped schedule occurrences no longer consume finite cue run limits when scheduler state is rehydrated after a restart.
- **Restart history coverage**: verifies skipped persisted history remains auditable without exhausting the restored finite schedule.

### Version 1.5.258
- **Skip retry safety**: failed `SkipNextOccurrence` persistence releases the in-memory occurrence slot and preserves deferred state for a safe retry.
- **Mapping credential writes**: JSON user-mapping POSTs preserve explicitly entered top-level and nested device bridge keys while read models remain credential-free.
- **Structured dependency gate**: the self-hosted NuGet check validates JSON output and blocks on top-level or transitive vulnerability entries.
- **Failure-path coverage**: scheduler retry, mapping credential round-trip/redaction, and structured vulnerability-report contracts are covered.

### Version 1.5.257
- **Session restart isolation**: canceled video/audio capture loops are awaited before shared FFmpeg and Hue streamers are reused, preventing stale predecessor colors from crossing a playback restart.
- **IPv6 bridge discovery**: local mDNS discovery queries scoped IPv6 multicast on each multicast-capable interface, alongside the existing IPv4 path.
- **Lifecycle and discovery coverage**: gated restart and ULA/link-local AAAA contracts cover the new runtime and discovery boundaries.

### Version 1.5.256
- **Self-hosted checkout isolation**: all CI/security/release checkouts disable persisted GitHub credentials; only the release tag step receives an explicit scoped auth header for its tag operation.
- **Tested-artifact release integrity**: release packaging consumes the exact publish artifact produced by the tested build job instead of rebuilding a separate DLL.
- **Reproducible SDK**: CI and repository tooling use the pinned .NET 8.0.424 SDK from `global.json`.
- **Read-only scheduler telemetry**: schedule status/history GETs defer persisted history and deferred-run repairs whenever a configuration mutation or scheduler lifecycle owns the barrier.

### Version 1.5.255
- **Generic configuration-route isolation**: the built-in Jellyfin plugin-configuration JSON omits global, per-user, and nested device-route Hue credentials.
- **Guarded configuration writes**: the generic plugin configuration update route is rejected; use the authenticated `/HueSync/Configuration` endpoint for normalized, lifecycle-safe updates.

### Version 1.5.254
- **Configuration writer serialization**: administrator configuration writers share the lifecycle gate with imports, while scheduler evaluation and manual cue lifecycles block stale writes and post-run persistence races.
- **Import readiness contract**: validation exposes explicit scheduler/lifecycle, diagnostic, and concurrent-import flags while keeping responses credential-free.

### Version 1.5.253 (withdrawn; superseded)
- **Configuration lifecycle barrier**: imports coordinate with the shared bridge lifecycle gate and scheduler evaluation barrier, closing playback-start and stale-scheduler snapshot races before replacing configuration.
- **Lifecycle blocker telemetry**: validation exposes explicit diagnostic and concurrent-import flags while keeping responses credential-free.

### Version 1.5.252 (withdrawn; superseded)
- **Active-cue import guard**: configuration validation reports scheduled-run blocking and imports return `409 Conflict` without replacing a cue that is actively running.
- **Import lifecycle coverage**: blocking-stream regression coverage verifies validation, conflict responses, configuration preservation, and successful import after completion.

### Version 1.5.251
- **Upcoming-target UI parity**: administrator occurrence rows now distinguish all-enabled targets, default-bridge inclusion, selected mapping IDs, and nested user/device routes while preserving legacy labels.
- **Occurrence target contract**: safe rendering and exact target-selection metadata are covered without exposing bridge credentials.

### Version 1.5.250
- **Active-cue update guard**: existing scheduled cues cannot be replaced while their restorative run is active; the API returns `409 Conflict` and preserves the original definition.
- **Schedule lifecycle coverage**: in-flight update rejection and configuration preservation are covered through the controller path.

### Version 1.5.249
- **Pause telemetry status parity**: `/HueSync/Status` and support-bundle runtime diagnostics now expose effective pause behavior and pause brightness while keeping the response credential-free.
- **Hosted status coverage**: live-service projection, support-bundle inheritance, safe serialization, and null/no-service behavior are covered by regression tests.

### Version 1.5.248
- **History target-mode UI parity**: the administrator cue-history table now distinguishes all-enabled targets, default-bridge inclusion, selected mapping IDs, and nested user/device routes while retaining legacy labels.
- **Credential-free accessible rendering**: target details use text-only cells and the history status region announces updates without exposing bridge secrets.

### Version 1.5.247
- **Scheduled-history target-mode parity**: retained cue history now preserves `targetAllEnabledMappings` through in-memory results, persisted reloads, JSON exports, and credential-free CSV rows.
- **History target-mode coverage**: all-enabled mode, legacy false defaults, route metadata, and secret absence are covered by regression tests.

### Version 1.5.246
- **Active-cue deletion guard**: single scheduled-cue deletion now shares the transactional active-run protection used by bulk deletion and returns `409 Conflict` without mutating the cue.
- **Deletion lifecycle coverage**: active-run rejection and configuration preservation are covered through the authenticated controller path.

### Version 1.5.245
- **RFC 5545-safe calendar folding**: iCalendar exports now fold by UTF-8 octets without splitting Unicode scalars, preserving valid calendar interoperability for multibyte schedule and target metadata.
- **Multibyte calendar coverage**: physical-line limits, continuation semantics, unfolded metadata, and credential absence are covered by regression tests.

### Version 1.5.244
- **iCalendar target parity**: upcoming cue calendars now preserve all-enabled mapping mode, exact selected user IDs, nested userId/deviceId routes, and default-target inclusion as credential-free VEVENT properties.
- **Calendar export coverage**: escaped route JSON and credential absence are covered alongside the existing UTC/timing metadata.

### Version 1.5.243
- **Duplicate registration prevention**: global and per-user Link Bridge controls disable after confirmation, reject duplicate in-flight requests, and always recover their enabled state after failures.
- **Credential-safe registration UX**: both registration surfaces keep status and error messages generic without logging bridge addresses, response bodies, or credentials.

### Version 1.5.242
- **Bridge registration retry hardening**: Link Button registration is single-attempt, preventing generic network retries from repeating credential-creation requests after transient transport failures.
- **Registration retry coverage**: transient registration failures are verified to make exactly one request even when normal Hue retry attempts are enabled.

### Version 1.5.241
- **Scheduled-occurrence CSV target parity**: upcoming-occurrence exports now preserve exact selected user IDs, nested `userId`/`deviceId` routes, and default-target inclusion as credential-free JSON fields.

### Version 1.5.240
- **Scheduled-history CSV target parity**: history exports now preserve exact selected user IDs, nested `userId`/`deviceId` routes, and default-target inclusion as credential-free JSON fields.
- **Bridge registration API contract**: authenticated Link Button registration is documented with private/local target validation and explicit App Key/Client Key secret-handling guidance.

### Version 1.5.239
- **Audio capability probe resilience**: the real FFmpeg PCM startup check keeps a bounded 10-second budget, reducing false unavailable diagnostics during busy self-hosted runner startup.

### Version 1.5.238
- **Preview target safety**: ambiguous legacy `userId` plus broadcast/selected-target requests fail closed before bridge activity, direct and bulk preview failures retain credential-free route metadata, and malformed/incomplete nested device routes stay unavailable in the administrator selectors.

### Version 1.5.237
- **Scheduled-route telemetry labels**: status, occurrence, history, and preview surfaces now show exact credential-free `userId / deviceId` route identifiers for nested playback-device selections.

### Version 1.5.236
- **Preview route failure parity**: raw, saved-scene, and saved-playlist preview endpoints document exact nested device routes, while failure telemetry retains normalized user/device IDs without credentials.
- **Preview API contract validation**: the trusted main build checks route fields in every preview endpoint row and confirms the corresponding request/source support.

### Version 1.5.235
- **Scheduled-route dependency protection**: mapping dependency audits, disable/delete guards, and device-target edits now protect exact nested device routes referenced by scheduled cues; partial imports preserve omitted routes and direct run telemetry retains route IDs.
- **Scheduled device-route targeting**: scheduled scene cues can select exact nested Jellyfin playback-device routes, retain those credential-free IDs through API/status/history/occurrence/backup round-trips, and fail closed when a route is missing or disabled.
- **Release installation parity**: installation instructions now download the published ZIP and SHA-256 sidecar, verify the archive, and install both the plugin DLL and required `meta.json` manifest together.
- **Release documentation contract**: the trusted main build validates that README package guidance, workflow asset names, archive contents, and checksum verification remain synchronized.

### Version 1.5.234
- **Fail-closed preview target metadata**: target selectors and preview, capture, and playlist controls remain disabled while saved-scene, playlist, or mapping metadata is unavailable; failed target lists show an explicit reload message instead of silently falling back to the default bridge, and direct preview entry points are guarded before any POST.
- **Configuration-page contracts**: static validation covers separate scene/playlist metadata readiness, failure-state controls, unavailable-target messaging, and direct/bulk/saved-scene/playlist/capture/mapping preview guards.

### Version 1.5.232
- **Fail-closed bulk mapping deletion**: null or blank user mapping IDs are rejected before normalization or configuration mutation, preserving atomic bulk-delete behavior.
- **Release artifact verification**: the release ZIP and deterministic dependency manifest sidecars are verified between the package and release runners and published for downstream integrity and provenance checks.

### Version 1.5.231
- **Fail-closed preview target selection**: direct, saved-scene, bulk saved-scene, playlist, and bulk playlist previews reject blank target user IDs before default-bridge fallback or bridge activity.
- **Test runner refresh**: xUnit Visual Studio adapter 4.0.0 is now locked for the test project only; production dependencies remain on the .NET 8-compatible set.

### Version 1.5.230
- **Hash-locked Semgrep**: blocking static analysis installs a reviewed Python 3.12/x86_64 dependency lock with SHA-256 hashes, platform/version validation, and `pip check`; current rulesets are separately reviewed and SHA-256-pinned before use.
- **Import preflight enforcement**: Review and Import stays disabled until successful validation and is invalidated when migration credentials change; submission also fails closed.
- **Actions policy enforcement**: repository policy now requires immutable commit-SHA action references.
- **Scanner dependency maintenance**: Dependabot now monitors the hash-locked Semgrep environment and its transitive packages.

### Version 1.5.229
- **Credential-safe bridge registration**: registration no longer logs response credentials or raw errors in the administrator browser console, and recovery guidance is credential-free.
- **Fail-closed capture selection**: current-light capture rejects blank target user IDs before default-target fallback, without contacting a bridge.

### Version 1.5.228
- **Malformed capture routes**: batch current-light capture rejects null or blank-user routes before default-target fallback, with no bridge activity.
- **Bulk preview telemetry**: saved-scene and playlist bulk previews now show per-target route outcomes instead of only generic completion text.

### Version 1.5.227
- **Case-sensitive device routes**: current-light capture and selected scene cues require exact Jellyfin `DeviceId` matching; batch selection keeps distinct case-sensitive routes separate while preserving case-insensitive user mapping IDs and avoiding delimiter-key collisions.

### Version 1.5.226
- **Fail-closed release verification**: tag creation waits for package/changelog checks, remote lookup failures stop publication, and the created tag is verified against the workflow commit.

### Version 1.5.225
- **Release provenance**: release tags are created and verified at the exact workflow commit before publication, preventing serialized runs from attaching a package to a newer moving `main` tip.

### Version 1.5.224
- **Disabled target safety**: preview selectors disable disabled user mappings and discard restored selections that are no longer eligible, avoiding predictable preview failures.

### Version 1.5.223
- **Device-route profile preservation**: selected previews keep same-area device routes distinct when their channel profiles differ, preventing a requested route from being silently dropped.

### Version 1.5.222
- **Diagnostic cancellation recovery**: the administrator diagnostics Cancel control now re-enables for a safe retry when cancellation fails or reports no active operation while diagnostics remain active.

### Version 1.5.221
- **Preview route telemetry**: normal, saved-scene, and playlist preview results now return sanitized explicit `{userId, deviceId}` selections so device-specific outcomes remain identifiable without exposing credentials. The administrator Cancel control recovers for safe retries when cancellation fails or reports no active operation.
- **Malformed playlist resilience**: color-preset rename validates null saved-scene references and returns a safe validation response without mutating configuration.

### Version 1.5.220
- **Device-route preview parity**: normal, saved-scene, bulk saved-scene, and playlist previews accept credential-free `{userId, deviceId}` routes and resolve nested device bridge profiles without exposing secrets or changing persisted target selections. Preview selectors expose nested device routes while preserving legacy default, user-mapping, and all-target behavior.
- **Fail-closed validation**: malformed route payloads are rejected before credential-bearing fallback, device IDs remain case-sensitive, and single-playlist previews preflight selected routes.

### Version 1.5.219
- **Malformed nested-target resilience**: configuration-import matching ignores null persisted device targets, validation runs even when global sync is disabled, and mapping identity comparisons normalize whitespace before replacement.

### Version 1.5.218
- **Credential lifecycle control**: clear stored global Hue credentials from the administrator page behind an explicit confirmation, without touching per-user or device-route secrets.
- **Malformed mapping resilience**: runtime bridge, playback, override, and summary lookups ignore null mapping entries safely.

### Version 1.5.217
- **Device-route current-light capture**: select an explicit per-user device route from the dedicated capture target control, capture device-only mappings, and review redacted device identity in single or batch results without exposing bridge credentials.

### Version 1.5.216
- **Device-route migration and diagnostics**: enter replacement App/Client keys for each exported nested device route, and review explicit device identity, route labels, and readiness in target diagnostics and support bundles.
- **Fail-safe configuration validation**: malformed null user mappings are reported safely, and complete device-only routes no longer require an unused global bridge target.

### Version 1.5.215
- **Live device-route telemetry**: expose the active device identity and route-match state through the top-level status API and administrator live panel.

### Version 1.5.214
- **Import nullability hardening**: disabled or legacy mappings with absent nested route collections now normalize safely without compiler warnings.

### Version 1.5.213
- **Automatic playback device routing**: configure bounded, exact-match per-user device routes with private bridge validation, device-over-user-over-global channel precedence, credential-safe summaries/imports, runtime/session telemetry, rollback-safe bulk mapping changes, and administrator JSON controls.

### Version 1.5.212
- **Redirect-safe bridge transport**: automatic HTTP redirects are disabled for Hue bridge clients, preventing bridge requests from being followed to an unintended host while retaining scoped local-certificate validation.
- **Bridge response privacy**: registration, entertainment-area start, and stop failures retain only operation/status telemetry; raw bridge response bodies are never written to Jellyfin logs.

### Version 1.5.211
- **Bridge target validation**: loopback, unspecified, multicast, and broadcast addresses are rejected before bridge requests; private, link-local, unique-local, and `.local` Hue targets remain supported.

### Version 1.5.210
- **FFmpeg capability boundary**: custom flags are limited to decoder, thread, and hardware-tuning options with bounded values; alternate inputs/outputs, protocols, headers, filters, scripts, arbitrary paths, duplicates, option smuggling, and oversized text fail closed before playback.

### Version 1.5.209
- **Support-bundle FFmpeg redaction**: support documents omit global and per-user custom FFmpeg flag values while exposing only configured-state telemetry; intentional backup exports remain available for migration.
- **POST-only entertainment-area loading**: the secret-bearing legacy GET route is removed so Hue app keys are not sent in URLs or access logs.

### Version 1.5.208
- **In-process schedule overlap recovery**: long restorative cues no longer make later scheduled cues disappear when their minute passes; the scheduler re-evaluates elapsed occurrences once without changing the configured restart catch-up window.
- **Safe one-time semantics**: recovered one-time cues retain stable run-slot claims, catch-up telemetry, and automatic disable behavior.
- **Administrator UI hardening**: user-mapping action attributes encode quotes and backticks, keeping imported identifiers confined to their data attributes.

### Version 1.5.207
- **Complete non-success telemetry**: skipped and failed direct-scene cues retain effective brightness in runtime/history results and CSV exports.
- **Stable schedule request API shape**: omitted duration fields preserve existing cues without changing the public integer request property; explicit zero clears a duration override.

### Version 1.5.206
- **Scheduled-scene brightness overrides**: optionally override brightness per single-scene cue from 0-100%; blank values inherit the saved scene and playlist cues retain per-step brightness.
- **Credential-free brightness parity**: expose effective brightness in scheduler status, runtime/history telemetry, upcoming occurrence JSON/CSV/iCalendar exports, backup/restore, and the administrator editor.
- **Safe partial schedule edits**: omitted duration or brightness fields preserve existing cue values while explicit null clears an override.

### Version 1.5.205
- **Credential-free playlist rename**: atomically migrate scheduled-cue references when renaming a saved-scene playlist, with collision validation, rollback-safe persistence, and administrator controls
- **Scheduled-scene RGB overrides**: optionally override red, green, and blue channels per single-scene cue from 0-255; blank channels inherit the saved scene and playlist cues retain per-step colors
- **Operational hardening**: bound every self-hosted build, security, package, and release job with a documented timeout budget
- **Regression coverage**: validate rename migration, RGB inheritance/range rules, explicit null clearing, runtime payloads, status/history/occurrence exports, and credential-free API/UI contracts

### Version 1.5.204
- **Per-step playlist RGB colors**: nullable `stepRed`, `stepGreen`, and `stepBlue` values override saved-scene channel colors within 0-255; blank/null values inherit the referenced scene
- **Complete color telemetry parity**: effective RGB travels through non-continuous and continuous previews, scheduled occurrence plans, runtime/history, API/UI, CSV/iCalendar metadata, duplication, and credential-free backup/restore
- **Regression coverage**: color validation/inheritance, stream payloads, continuous multi-target execution, API/export round trips, and configuration-page contracts

### Version 1.5.203
- **Per-step playlist effects**: nullable `stepEffects` values select any canonical effect per step; blank/null inherits the referenced scene, and effective effects appear in preview, occurrence, and retained-history plans
- **Single-lifecycle playlist streaming**: validate the expanded plan before bridge mutation and render every step per target through one capture, activation, continuous DTLS stream, deactivation, and restoration
- **Unambiguous target outcomes**: preserve independent result telemetry for distinct resolved targets even when their administrator-facing labels are identical
- **Reproducible and isolated CI**: enforce content-hashed NuGet lock files, locked-mode restores, an unused release tag, and the dedicated least-privileged `harmonize-runner` account

### Version 1.5.202
- **Per-step playlist effect speeds**: add nullable `stepEffectSpeedPercent` overrides bounded to 25-400%; omitted/null values inherit each referenced scene's animation rate
- **End-to-end speed parity**: apply effective per-step speeds to non-solid previews, scheduled runs, expanded plans, runtime/history telemetry, API/UI, duplication, and credential-free backup/restore
- **Regression coverage**: verify speed validation/inheritance, stream propagation, scheduler/history telemetry, API round trips, backup portability, and configuration-page contracts
- **Fail-closed self-hosted release security**: require build/tests, formatting, dependency vulnerability checks, full-history Gitleaks, and Semgrep before packaging or publishing; pin both scanners, checksum-verify the official Gitleaks manifest/archive, and run them from isolated temporary paths without privileged host installation

### Version 1.5.201
- **Per-step playlist fade curves**: add nullable `stepTransitionCurves` overrides for Linear, SmoothStep, EaseIn, EaseOut, and EaseInOut; omitted/null values inherit each referenced scene's curve
- **End-to-end curve parity**: apply effective per-step curves to previews, easing-capable stream calls, scheduled plans, runtime/history telemetry, API/UI, occurrence displays, duplication, and credential-free backup/restore
- **Regression coverage**: verify curve validation/inheritance, runtime propagation, scheduler/history telemetry, API round trips, backup portability, and configuration-page contracts

### Version 1.5.200
- **Per-step playlist transitions**: add nullable `stepTransitionSeconds` and `stepTransitionOutSeconds` overrides bounded to 0-30 seconds; blank/null values inherit each saved scene's fade settings and short holds clamp safely at runtime
- **End-to-end transition/offset parity**: carry effective per-step fades and cumulative start offsets through previews, scheduled cues, runtime/status/history telemetry, occurrence JSON/CSV/iCalendar plans, API/UI, duplication, and credential-safe backup/restore
- **Regression coverage**: verify transition validation/inheritance/clamping, API and backup round trips, preview/scheduler payloads, persisted history, and configuration-page contracts

### Version 1.5.199
- **Expanded upcoming playlist plans**: expose the exact credential-free per-occurrence playlist sequence, including stable shuffle/repeat order, saved positions, effective brightness, hold duration, transitions, and cumulative offsets
- **Export and administrator parity**: carry the expanded plan through occurrence JSON/CSV/iCalendar responses and the upcoming-cue administrator table without exposing credentials
- **Regression coverage**: verify override inheritance, repeat offsets, shuffle stability, export metadata, and configuration-page contracts

### Version 1.5.198
- **Per-step playlist brightness**: add optional credential-free `stepBrightnessPercent` overrides in parallel with `presetNames`; `null` or an omitted list inherits each saved scene's brightness while explicit 0-100% values apply only to that step
- **End-to-end brightness/history parity**: carry effective per-step brightness through previews, scheduled cues, repeat passes, runtime/status/history telemetry, API/UI, duplication, and backup/restore, including persisted credential-free playlist step results across restart
- **Regression coverage**: verify validation, inheritance, API/UI round trips, preview/scheduler payloads, persistence, duplication, and credential-safe portability

### Version 1.5.197
- **Per-step playlist timing**: add credential-free `stepDurationSeconds` overrides for saved playlist steps, with `0` inheriting each saved scene's duration and bounded 1-30 second holds
- **End-to-end duration parity**: carry effective step timing through previews, scheduled cues, repeat totals, runtime/history/status/occurrence telemetry, API/UI, duplication, and backup/restore while preserving legacy playlists
- **Regression coverage**: verify validation, inheritance, API/UI round trips, preview timing, scheduler totals, and credential-safe portability

### Version 1.5.196
- **Configurable transition curves**: add Linear, SmoothStep, EaseIn, EaseOut, and EaseInOut easing for saved-scene and direct-preview fade-in/fade-out transitions, with Linear preserved for older configurations
- **End-to-end transition parity**: carry the normalized curve through playlists, scheduled execution, runtime/history/status/occurrence metadata, API/UI responses, CSV/iCalendar exports, duplication, and credential-free backup/restore
- **Regression coverage**: verify curve normalization/validation, deterministic easing math, legacy linear behavior, and configuration-page/API contracts

### Version 1.5.195
- **Deterministic playlist shuffle**: add saved `Sequential` or date-seeded `Shuffle` playback order per repeat pass, with stable retry behavior and original saved-position telemetry
- **End-to-end playlist parity**: carry playback order through previews, scheduled cues, runtime/history/status/occurrence metadata, duplication, backup/restore, API contracts, and the administrator editor
- **Regression coverage**: verify legacy sequential defaults, canonical shuffle normalization, stable pass ordering, saved-position reporting, API round trips, and configuration-page contracts

### Version 1.5.194
- **Starlight scene effect**: add deterministic cool white/blue twinkles with sharp glints and independent channel phases to previews, saved scenes, playlists, and scheduled cues
- **Portable effect support**: carry Starlight through validation, API/UI, backup/restore, occurrence metadata, and administrator controls
- **Regression coverage**: verify deterministic bounded frames, seed intensity, channel phase, canonical API persistence, and configuration-page contracts

### Version 1.5.193
- **Solar-noon scheduled cues**: schedule saved scenes and playlists at NOAA solar noon with bounded offsets, portable decimal coordinates, and the selected cue time zone
- **Scheduler parity**: carry SolarNoon through due/upcoming/catch-up behavior, API/status/CSV/iCalendar metadata, backup/restore, and administrator controls; solar noon remains available on polar day/night dates
- **Regression coverage**: verify NOAA equation-of-time math, timezone-aware scheduling, offset behavior, canonical API persistence, and configuration-page contracts

### Version 1.5.192
- **Complete twilight scheduling**: add NauticalDawn/NauticalDusk and AstronomicalDawn/AstronomicalDusk using NOAA's 12° and 18° below-horizon events
- **Scheduler parity**: carry all twilight bands through due/upcoming/catch-up behavior, API/status/CSV/iCalendar metadata, backup/restore, and administrator controls
- **Regression coverage**: verify chronological dawn/dusk ordering, timezone-aware scheduling, canonical API persistence, and configuration-page contracts

### Version 1.5.191
- **Civil twilight scheduled cues**: schedule saved scenes and playlists at CivilDawn or CivilDusk using the sun's 6°-below-horizon events in each cue's time zone
- **Scheduler parity**: carry civil dawn/dusk through validation, due/upcoming/catch-up behavior, API/status/CSV/iCalendar metadata, backup/restore, and administrator controls
- **Regression coverage**: verify timezone-aware civil twilight calculations, canonical API persistence, and configuration-page contracts

### Version 1.5.190
- **Lightning scene effect**: add deterministic electric blue/white storm flashes with independent channel phases to previews, saved scenes, playlists, and scheduled cues
- **Portable effect support**: carry Lightning through validation, API/UI, backup/restore, occurrence metadata, and administrator controls
- **Regression coverage**: verify deterministic animation, bounded RGB16 output, per-channel phase, and API/config contracts

### Version 1.5.189
- **Cross-midnight solar offsets**: keep the base solar date as the recurrence anchor while allowing ±12-hour sunrise/sunset offsets to resolve into the adjacent local date
- **Scheduler correctness**: match shifted solar events across neighboring base dates for due checks, stable run-slot de-duplication, next-run previews, and missed-cue recovery
- **Regression coverage**: verify cross-date UTC/local resolution, upcoming previews, due detection, and catch-up behavior

### Version 1.5.188
- **Solar-aware scheduled cues**: schedule saved scenes and playlists at Fixed, Sunrise, or Sunset in the selected cue time zone with bounded offsets and decimal coordinates; polar no-event dates are skipped safely
- **Portable schedule telemetry**: carry timing mode, solar metadata, and resolved UTC/local occurrences through status, API CRUD, occurrence previews, CSV, iCalendar, backup/restore, and the administrator editor
- **Regression coverage**: verify NOAA solar calculations, midnight-crossing events, validation, normalization, DST-safe occurrence resolution, and polar handling

### Version 1.5.187
- **Fire and Ocean scene effects**: add deterministic high-energy red/amber/yellow Fire flicker and rolling blue/cyan Ocean waves to previews, saved scenes, playlists, and scheduled cues
- **Portable effect compatibility**: carry both effects through configuration normalization, API validation, backup/restore, administrator controls, and scheduler telemetry
- **Regression coverage**: verify bounded deterministic frames, independent channel phases, effect normalization, and UI/API contracts

### Version 1.5.186
- **Quarter-turn orientation calibration**: correct sideways-mounted entertainment-area layouts with clockwise or counterclockwise 90-degree transforms
- **Video and audio consistency**: apply quarter-turn orientation to video sampling coordinates and audio spatial/stereo routing, including per-user inheritance
- **Portable controls and telemetry**: expose both modes through validation, configuration portability, administrator controls, and regression coverage

### Version 1.5.185
- **Spatial orientation calibration**: correct mirrored, vertically flipped, or 180-degree-rotated entertainment-area layouts with a Normal default
- **Video and audio consistency**: apply orientation to video sampling coordinates and audio spatial/stereo routing, including per-user inheritance
- **Portable controls and telemetry**: expose orientation in runtime status, configuration backup/import, mapping summaries, administrator controls, and regression coverage

### Version 1.5.184
- **Color temperature correction**: tune global or per-user white balance from 1000-20000 K with a neutral 6500 K daylight default, applied consistently to video and audio streams
- **Portable profiles and telemetry**: carry color temperature through configuration save/load, backup/import, mapping summaries, runtime status, and administrator controls
- **Validation and regression coverage**: normalize warm/cool profiles safely and cover color-temperature math, bounds, inheritance, and API/UI round trips

### Version 1.5.183
- **Contrast correction**: tune global or per-user contrast around mid-gray from 50-200% with the neutral 100% default, applied consistently to video and audio streams
- **Portable profiles and telemetry**: carry contrast through configuration save/load, backup/import, mapping summaries, runtime status, and administrator controls
- **Validation and regression coverage**: clamp runtime values safely and cover contrast math, bounds, inheritance, and API/UI round trips

### Version 1.5.182
- **Gamma color correction**: tune global or per-user mid-tone brightness from 0.5-2.5 with the neutral 1.0 default, applied consistently to video and audio streams
- **Portable profiles and telemetry**: carry gamma through configuration save/load, backup/import, mapping summaries, runtime status, and the administrator controls
- **Validation and regression coverage**: clamp malformed runtime values safely and cover gamma math, bounds, inheritance, and API/UI round trips

### Version 1.5.181
- **Configurable dark-scene behavior**: choose Blackout or KeepLastColors when sampled frames fall below the blackout threshold, with consistent video/audio handling and no redundant Hue writes for preserved colors
- **Per-user dark-scene profiles**: inherit the global policy or override it per mapped user; effective policy is visible in runtime status and credential-safe mapping summaries
- **Portable administrator controls**: carry the policy through configuration save/load, backup/import, and the administrator page with validation and regression coverage

### Version 1.5.180
- **Blocking security checks**: Gitleaks and Semgrep now fail CI on detected secrets, static-analysis findings, missing reports, or scanner errors while retaining redacted JSON artifacts
- **Reliable SAST configuration**: Semgrep uses the explicit `p/default` ruleset with metrics disabled, preventing the previous auto-configuration failure from being hidden
- **Action supply-chain hardening**: all third-party workflow actions are pinned to immutable commit SHAs and every job uses the named self-hosted runner
- **Dependency and action refresh**: update Microsoft.NET.Test.Sdk, coverlet.collector, setup-dotnet, and Codecov to current stable major versions

### Version 1.5.179
- **Pause-time dimming**: choose KeepLastColors, RestoreLightState, or DimToCinemaLevel so paused playback can dim captured lights to the effective cinema level without resetting their streamed colors
- **Safe pause lifecycle**: capture the required light snapshot even when final restoration is disabled, apply brightness-only updates with retries, preserve resume ownership, and clear snapshots after playback cleanup
- **Pause telemetry and coverage**: expose the effective pause policy and dim level in runtime status, add per-user override support, and cover brightness payloads, validation, and pause/resume lifecycle behavior

### Version 1.5.178
- **Target-aware scheduled playback conflicts**: choose the historical process-wide conflict scope or allow scheduled cues to continue when active playback owns a different bridge/entertainment-area target
- **Scoped lifecycle arbitration**: matching-target scheduled previews reserve only their resolved Hue resource, preserving independent multi-room playback and credential-safe status telemetry
- **Portable controls and regression coverage**: carry the conflict scope through configuration save/load, backup/import, administrator controls, and target-specific scheduler tests

### Version 1.5.177
- **Per-cue playback conflict overrides**: each scheduled cue can inherit the global Skip/Defer policy or explicitly choose Skip or bounded Defer without changing other cues
- **Effective policy telemetry**: schedule API results, runtime status, credential-safe backup/import, and the administrator editor expose the saved override and the policy actually applied
- **Policy-aware cleanup and regression coverage**: stale persisted deferred waits are removed when a cue is no longer effectively deferred; global inheritance and both per-cue overrides are covered by tests

### Version 1.5.176
- **Restart-safe deferred cues**: persist one credential-free deferred occurrence per scheduled cue so a Jellyfin restart cannot silently lose a cue that is still inside its playback wait window
- **Restored-cue telemetry**: report pending occurrences restored after scheduler startup and mark their eventual success or safe expiry in status, history, JSON, CSV, and administrator controls
- **Recovery cleanup**: discard malformed, duplicate, disabled, and deleted-cue deferred state safely while preserving the existing bounded wait and one-occurrence semantics

### Version 1.5.175
- **Playback-aware scheduled cues**: choose Skip to preserve the historical immediate attempt or Defer to hold one due occurrence while playback owns the bridge and retry it after playback ends without consuming a finite execution limit
- **Bounded deferral and telemetry**: configure a 1-120 minute wait window; pending, expired, deferred, and playback-active states remain visible in credential-free status/history and expire safely as skipped outcomes
- **Portable controls and regression coverage**: validation, API settings, backup/import, administrator UI, and focused tests cover conflict policy round trips and lifecycle behavior

### Version 1.5.174
- **Configurable beat-pulse onset threshold**: require a bounded minimum normalized energy rise before beat flashes attack, while 0% preserves the existing response
- **Per-user threshold profiles**: inherit or override threshold with active telemetry, administrator controls, credential-safe backup/import, validation, and regression coverage

### Version 1.5.173
- **Configurable audio response smoothing**: blend each analysis window with the previous window from 0-90% to reduce spectral flicker while preserving the immediate default at 0%
- **Per-user response profiles**: inherit or override smoothing with bounded runtime clamping, active status telemetry, administrator controls, and credential-safe backup/import support
- **Regression coverage**: verify smoothing math across mixed and source-channel energy, bounds, inheritance, and configuration/API/UI wiring

### Version 1.5.172
- **Configurable audio band gains**: independently scale low, mid, and high audio energy from 0-200%; neutral 100% values preserve the original analyzer balance while zero mutes a band
- **Per-user band-balance profiles**: inherit or override each gain with bounded runtime clamping, live telemetry, administrator controls, and credential-safe backup/import support
- **Regression coverage**: verify band-gain math, defaults, bounds, inheritance, and configuration/API/UI wiring

### Version 1.5.171
- **Configurable audio noise gate**: suppress sub-threshold mixed RMS windows with a bounded 0-100% threshold; zero preserves the existing analyzer behavior
- **Per-user noise-floor profiles**: inherit or override the global gate with active status telemetry, validation, administrator UI, configuration API, and credential-safe backup/import support
- **Regression coverage**: verify gate math, bounds, defaults, per-user inheritance, and API/UI wiring

### Version 1.5.170
- **Configurable beat-pulse release**: carry enabled audio beat flashes into subsequent frames with a bounded 0-100% tail; zero preserves the instant transient default
- **Per-user release profiles**: inherit or override release independently, with live status telemetry, validation, administrator UI, configuration API, and credential-safe backup/import support
- **Regression coverage**: verify release math, bounds, defaults, per-user inheritance, and runtime resolution

### Version 1.5.169
- **Configurable history retention**: choose 1-25 completed playback sessions and 1-100 scheduled-cue runs to retain in memory and optionally across Jellyfin restarts, with legacy defaults preserved
- **Safe runtime trimming**: reducing a retention window trims newest-first sanitized history immediately on save without affecting active playback, credentials, or finite cue counters
- **Portable configuration**: carry retention preferences through the settings API, credential-safe backup/import, administrator UI, validation, and lifecycle regression coverage

### Version 1.5.168
- **Selectable audio source channels**: choose a backward-compatible Mono mix, Stereo left/right spatial blending, or isolate the Left or Right PCM source channel for Spatial routing
- **Per-user source-channel profiles**: inherit or override Mono/Stereo/Left/Right with active runtime telemetry and credential-free backup/import support
- **Regression coverage**: verify source-channel isolation, symmetric non-Spatial behavior, validation, API/UI markers, and mapping/configuration round trips

### Version 1.5.167
- **Stereo audio source routing**: optionally preserve left/right PCM energy for physical Spatial placement; Mono remains the backward-compatible default and Uniform/Mirror stay symmetric
- **Per-user source-channel profiles**: inherit or override Mono/Stereo with active runtime telemetry and credential-free backup/import support
- **Regression coverage**: verify stereo analysis, source-aware spatial output, symmetric routing, validation, API/UI markers, and mapping/configuration round trips

### Version 1.5.166
- **Audio spatial routing**: choose Spatial (backward-compatible position-aware routing), Uniform (one mixed response for every channel), or Mirror (symmetric center-to-edge routing) for Audio and All-media playback
- **Per-user routing profiles**: inherit or override the global mode with validation, safe runtime fallback, active status telemetry, and credential-free backup/import support
- **Regression coverage**: verify spatial, uniform, and mirrored channel output plus configuration, API, UI, and mapping round trips

### Version 1.5.165
- **Audio visualizer palettes**: choose Spectrum (the backward-compatible default), Band RGB, Warm, Cool, or Monochrome rendering for Audio and All-media playback
- **Per-user palette profiles**: inherit or override the global palette with active runtime telemetry and credential-free backup/import support
- **Regression coverage**: verify palette determinism, safe fallback, validation, API/UI markers, and mapping/configuration round trips

### Version 1.5.164
- **Audio Beat Pulse**: add an opt-in 0-100% transient brightness response to rising audio energy so beats and attacks create visible flashes without changing steady loudness behavior
- **Per-user pulse profiles**: inherit or override the global beat response, expose the effective setting in live status, and preserve it through configuration, mapping, and backup/import flows
- **Regression coverage**: verify onset math, bounded resolution, validation, UI markers, status wiring, and credential-safe round trips

### Version 1.5.163
- **Configurable audio-band spread**: average each low/mid/high spectral center across a bounded 0-100% neighborhood so between-center content remains reactive while 0% preserves exact-center analysis
- **Per-user audio spread profiles**: inherit or override spread independently, with bounded validation and effective runtime telemetry
- **Portable profile coverage**: carry spread through configuration save/load, backup/export, import mappings, administrator controls, status, and regression tests

### Version 1.5.162
- **Configurable audio band centers**: tune low/mid/high spectral-analysis centers from 20-3900 Hz globally or per user while retaining 90/420/1600 Hz defaults
- **Live band telemetry**: show the effective audio sensitivity and band centers in sanitized runtime status and the configuration page
- **Portable profile coverage**: carry audio centers through configuration save/load, backup/export, import mappings, validation, and regression tests

### Version 1.5.161
- **Audio capability diagnostics**: verify that the configured FFmpeg can emit a bounded PCM s16le 8 kHz stereo capture, not just report a version string
- **Audio readiness gating**: require the capture probe only for global or per-user Audio-only/All-media scopes, and expose the result through diagnostics, the configuration page, and support bundles
- **Credential-safe coverage**: keep probe output bounded and tokenized, avoid returning process output, and cover missing-tool and status propagation paths

### Version 1.5.160
- **Audio visualizer controls**: tune low/mid/high audio-reactive intensity from 25-400% globally or per user; the sensitivity control is independent from final brightness boost and is shown in live status

### Version 1.5.159
- **Audio-reactive playback**: choose Audio-only or All-media playback scopes to decode a bounded PCM window and drive low/mid/high spectral energy through spatial Hue colors, with existing bridge leases, light restoration, retries, seek recovery, per-user scopes, and sanitized telemetry preserved
- **Safe audio capture**: emit tokenized FFmpeg `s16le` output without shell parsing, use cancellation/stall cleanup identical to video, and keep the default AllVideo behavior unchanged

### Version 1.5.158
- **Aurora scene effect**: use a deterministic drifting green/cyan/blue/violet Aurora effect in manual previews, saved scenes, playlists, and scheduled cues; its RGB seed controls output level while effect speed controls the wave

### Version 1.5.157
- **Multi-room current-light capture**: capture one, selected, or all distinct enabled targets from the scene editor; each result is credential-free and honors the target's saved channel profile
- **Weighted scene seeding**: successful room samples are combined into aggregate RGB/brightness values, with partial bridge failures and inherited-target deduplication reported explicitly

### Version 1.5.156
- **Temperature scene effect**: use the new deterministic warm-to-cool Temperature effect in manual previews, saved scenes, playlists, and scheduled cues; its RGB seed controls output level while the effect sweeps Hue-compatible white balance
- **Effect compatibility**: Temperature is accepted through the configuration validator, API, stream tester, administrator editor, backup/restore metadata, and scheduler alongside Solid, Pulse, Rainbow, and Candle

### Version 1.5.155
- **Current-light scene seeding**: capture one selected Hue target's live color and brightness into the administrator preview editor, then save it as a reusable scene without sending bridge credentials from the browser
- **Hue color conversion**: convert xy chromaticity and mirek color-temperature states to averaged sRGB while keeping off-light brightness and partial capture counts explicit
- **Safe lifecycle and coverage**: serialize capture with playback/diagnostics, support cancellation and stale-channel validation, and cover conversions, target resolution, lifecycle contention, and credential-safe API responses

### Version 1.5.154
- **Early FFmpeg flag validation**: global and per-user custom FFmpeg flags now use the playback parser during configuration validation, so malformed quoted values are rejected when saved instead of failing at playback startup
- **Actionable configuration feedback**: unterminated quotes return a clear administrator-facing validation error while preserving safe tokenized process arguments
- **Regression coverage**: verify invalid global and per-user execution profiles fail validation consistently

### Version 1.5.153
- **Safe FFmpeg process arguments**: playback uses tokenized process arguments so media paths containing spaces or quotes remain reliable across platforms
- **Custom FFmpeg flag parsing**: quoted values and escaped quotes/backslashes are preserved without shell interpretation; malformed quotes fail clearly before startup
- **Cross-platform playback parity**: FFmpeg follows the safe process-launch model used by environment diagnostics while Hue streaming uses the managed DTLS transport

### Version 1.5.152
- **Service-level target override normalization**: empty or whitespace-only target lists preserve each playlist's saved target mode across direct service execution and API calls
- **Credential-safe playlist parity**: direct playlist previews normalize, trim, and deduplicate target IDs just like the administrator endpoints
- **Regression coverage**: verify empty service overrides preserve target telemetry and never expose persisted bridge credentials

### Version 1.5.151
- **Empty target override normalization**: empty target ID arrays no longer turn explicit all-target playlist previews into an invalid selected-target request; saved targets remain unchanged when no target is selected
- **Playlist preview parity**: individual and bulk playlist endpoints now normalize target-selection metadata consistently before server-side validation and execution
- **Regression coverage**: verify empty target selections still fan out to every enabled target without exposing persisted bridge credentials

### Version 1.5.150
- **One-off playlist target overrides**: preview an individual saved playlist on its saved target, the default bridge, every enabled target, or a deliberate subset of enabled mappings without changing the saved playlist
- **Credential-free individual playlist previews**: the browser sends only nullable target-selection metadata; server-side credentials, channel profiles, validation, and restorative execution remain authoritative
- **Administrator preview target picker**: use the saved-target/default/all options or combine selected mappings beside Preview Playlist, while Preview Playlist on All Targets remains an explicit broadcast override

### Version 1.5.149
- **Target-aware bulk playlist previews**: preview selected saved playlists using each playlist's saved target by default, or choose the default bridge, every enabled target, or a deliberate subset of enabled mappings with optional default-bridge inclusion
- **Credential-free playlist target override**: the browser sends only selected IDs and mode flags; persisted credentials, channel profiles, validation, and restorative execution remain server-side
- **Administrator bulk target picker**: use the exclusive saved-target/default/all options or combine selected mappings for Preview Selected, while Preview Selected on All Targets remains an explicit broadcast action

### Version 1.5.148
- **Raw preview target parity**: run administrator color previews on the default bridge, every enabled target, or a deliberate subset of enabled mappings with optional default-bridge inclusion
- **Credential-free raw preview execution**: selected-target previews resolve persisted credentials and channel profiles server-side and return selected IDs, default inclusion, and per-target outcomes without secrets
- **Administrator target picker**: add a multi-select target control to the raw color preview while retaining explicit all-target broadcast controls

### Version 1.5.147
- **Saved-scene preview target parity**: preview one or many saved scenes on the default bridge, every enabled target, or a deliberate subset of enabled mappings with optional default-bridge inclusion
- **Credential-free target telemetry**: saved-scene preview results expose selected mapping IDs, default-target inclusion, and per-target outcomes without bridge credentials
- **Administrator target selector**: choose saved-scene preview targets from the shared default/all/mapping multi-select; the explicit All Targets action remains a broadcast override

### Version 1.5.146
- **Persisted saved-playlist targets**: save the global bridge, every enabled mapping, or a deliberate subset of enabled mappings with optional default-bridge inclusion; the selected mode survives CRUD, previews, duplication, scheduled playlist cues, and backup/restore
- **Playlist target lifecycle safety**: validate selected playlist mappings atomically and include saved-playlist target references in mapping dependency reports and disable/delete protection
- **Credential-free administrator workflow**: edit playlist targets with an exclusive All enabled option or a deliberate multi-selection, while normal previews preserve each playlist's saved target mode

### Version 1.5.145
- **Selected scheduled-cue targets**: choose a credential-free subset of enabled mappings with optional default-bridge inclusion for scene and playlist cues
- **Target-safe telemetry and migration**: carry selected IDs and default inclusion through status, occurrences, history, backup/restore, and per-target run results without credentials
- **Administrator multi-target editor**: use an exclusive All enabled option or a deliberate multi-selection with atomic validation and mapping dependency protection

### Version 1.5.144
- **Per-cue run serialization**: automatic and manual executions of the same scheduled cue refuse to overlap, protecting bridge state, counters, and retained history
- **Navigation-safe bulk runs**: leaving the administrator page requests cleanup-aware cancellation of an in-flight bulk sequence before it can start another cue

### Version 1.5.143
- **Atomic bulk scheduled-cue Run Now**: run up to 50 selected cues sequentially with full reference/target preflight and restorative cleanup between cues
- **Per-cue outcomes and cancellation**: retain credential-free success/failure telemetry, continue after runtime failures, and stop remaining cues safely after cancellation
- **Administrator workflow**: add Run Selected and Cancel Selected Runs controls with aggregate progress and per-cue status summaries

### Version 1.5.142
- **Bulk saved-scene previews**: preview up to 50 selected scenes sequentially on the default target or every enabled target with restorative state cleanup between scenes
- **Bulk playlist previews**: preview selected playlists sequentially while preserving saved default, selected-subset, or all-target modes, or applying an explicit target override
- **Preflight and cancellation safety**: resolve all selected scenes/playlists, references, and targets before bridge calls; preserve per-item failures, continue safely, and stop remaining work on cancellation
- **Administrator workflow**: add credential-free aggregate/per-item status and Preview Selected controls beside existing bulk duplicate/delete actions

### Version 1.5.141
- **Atomic bulk scene duplication**: create independent copies of up to 50 selected saved scenes with preserved visual/effect metadata and bounded unique names
- **Atomic bulk playlist duplication**: create independent copies of up to 50 selected playlists with fresh IDs, preserved ordered scenes, repeat passes, and target mode
- **Capacity and rollback safety**: missing sources, collection limits, validation failures, and persistence failures leave originals, references, and complete collections unchanged
- **Administrator duplication workflow**: add Duplicate Selected controls beside saved-scene and playlist dependency-safe deletion

### Version 1.5.140
- **Atomic bulk cue duplication**: create disabled copies of up to 50 selected scheduled cues with fresh IDs, unique bounded names, reset counters, and cleared Skip Next markers
- **All-or-nothing capacity safety**: missing IDs, schedule-capacity limits, validation failures, and persistence failures leave the original cue collection unchanged
- **Administrator cue variants**: add Duplicate Selected beside bulk enable/disable, skip, counter-reset, and deletion controls

### Version 1.5.139
- **Atomic bulk mapping enable/disable**: change up to 50 selected per-user mappings together with all IDs resolved before any mutation
- **Lifecycle-safe state changes**: scheduled-cue dependencies block disabling, incomplete custom targets block enabling, and persistence failures restore the complete selected state
- **Administrator mapping controls**: add Enable Selected and Disable Selected actions; disabling clears stored custom bridge targets and never returns credentials

### Version 1.5.138
- **Atomic bulk counter reset**: reset and re-enable up to 50 selected scheduled cues while clearing pending Skip Next markers in one persistence transaction
- **Lifecycle-safe recovery**: active cues, missing IDs, and persistence failures leave every selected cue unchanged and retained cue history intact
- **Administrator recovery workflow**: add Reset Counters beside bulk enable/disable, skip, and delete actions

### Version 1.5.137
- **Atomic bulk mapping deletion**: remove up to 50 selected per-user bridge mappings by user ID while preserving every dependent scheduled cue when references block the operation
- **All-or-nothing dependency safety**: missing IDs, scheduled-cue references, and persistence failures leave the full mapping collection unchanged with sanitized details
- **Administrator mapping cleanup**: add select-all, clear-selection, and Delete Selected controls with credential-free refresh

### Version 1.5.136
- **Atomic bulk scene deletion**: remove up to 50 selected saved scenes by normalized name while preserving every dependent playlist and cue when references block the operation
- **All-or-nothing dependency safety**: missing names, direct or playlist-backed scheduled-cue references, and persistence failures leave the full scene collection unchanged with sanitized details
- **Administrator scene cleanup**: add select-all, clear-selection, and Delete Selected controls with post-action playlist and schedule refresh

### Version 1.5.135
- **Atomic bulk playlist deletion**: remove up to 50 selected saved-scene playlists by stable ID while preserving every dependent schedule when references block the operation
- **All-or-nothing dependency safety**: missing IDs, scheduled-cue references, and persistence failures leave the full playlist collection unchanged and return sanitized blocking details
- **Administrator playlist cleanup**: add select-all, clear-selection, and Delete Selected controls with post-action playlist and schedule refresh

### Version 1.5.134
- **Credential-safe import diff**: Validate Import now compares the finalized normalized candidate against the live configuration and reports added, removed, changed, and unchanged counts for mappings, scenes, playlists, and scheduled cues
- **Migration safety**: global settings and App Key/Client Key changes are visible as booleans while secret values remain absent from the API and administrator summary
- **Administrator review**: show the exact server-calculated change summary in the Backup and Restore wizard before import

### Version 1.5.133
- **Atomic bulk scheduled-cue deletion**: remove up to 50 selected scheduled cues while preserving retained cue history and leaving active lifecycles protected
- **All-or-nothing safeguards**: active, missing, and persistence-blocked selections restore the complete previous cue collection
- **Administrator cleanup workflow**: add a confirmed Delete Selected action beside bulk enable/disable and skipped-occurrence controls

### Version 1.5.132
- **Atomic bulk skipped-occurrence administration**: mark or clear the next automatic occurrence for up to 50 selected cues without changing recurrence definitions, targets, scenes, or finite-run counters
- **All-or-nothing safeguards**: active, disabled, exhausted, futureless, missing, and persistence-blocked cues leave the complete selection unchanged
- **Administrator multi-action workflow**: add Skip Next Selected and Clear Selected Skips alongside bulk enable/disable controls

### Version 1.5.131
- **Spreadsheet-ready telemetry**: export upcoming occurrences, duration-aware conflicts, scheduled-cue history, and completed playback history as credential-free CSV
- **Filter-preserving reports**: CSV downloads retain selected cue, outcome, and 7/31/90/366-day horizon filters from the administrator views
- **Safe deterministic format**: UTF-8 CSV uses invariant values, explicit local/UTC columns, RFC quoting, and spreadsheet formula-marker protection for labels

### Version 1.5.130
- **Atomic bulk scheduled-cue administration**: select up to 50 cues and enable or disable them together without changing their timing, target, scene, or execution counters
- **All-or-nothing safeguards**: active cues, exhausted finite cues, missing IDs, and persistence failures leave the complete selected set unchanged
- **Administrator selection workflow**: add select-all, clear-selection, and post-action credential-free status refresh controls

### Version 1.5.129
- **Per-user playback media scope**: inherit the global scope or override it per user mapping for all video, movies, TV episodes, other video, audio-only music, or all media while retaining safe cleanup for active sessions
- **Effective status and portability**: expose the active effective scope and carry credential-free overrides through mapping summaries and backup/restore

### Version 1.5.128
- **Playback media scope**: choose all video, movies, TV episodes, other video, audio-only music, or all media as the global start policy; existing sessions still receive cleanup lifecycle events when the policy changes
- **Visible policy telemetry**: expose the selected scope through configuration, Live Sync Status, diagnostics, and credential-safe exports

### Version 1.5.127
- **Credential-safe support bundle**: export local and bridge-target diagnostics, runtime state, playback and scheduler history, scheduler status, and redacted configuration metadata in one administrator JSON download while keeping bridge credentials and playback tokens out of the file

### Version 1.5.126
- **Configurable schedule report horizon**: choose 7, 31, 90, or 366 days and apply the same server-bounded scope to upcoming occurrence/conflict tables, JSON diagnostics, and calendar export

### Version 1.5.125
- **Exportable schedule diagnostics**: download the filtered upcoming-occurrence and duration-aware conflict reports as credential-free JSON for support, automation verification, and offline troubleshooting

### Version 1.5.124
- **Cue-scoped schedule telemetry**: focus the upcoming occurrence preview, iCalendar download, retained cue history, and JSON export on one configured cue while preserving an all-cues view
- **Filter continuity**: retain selected cue and outcome filters across schedule refreshes, label disabled cues, and keep all filtering credential-free through existing `scheduleId` query parameters

### Version 1.5.123
- **Configuration import preflight**: validate backup normalization, saved-object dependencies, complete settings, planned totals, matching-key preservation, and active-playback readiness without mutating the live configuration
- **Administrator migration safety**: add a Validate Import action before the atomic Backup and Restore confirmation

### Version 1.5.122
- **Outcome-filtered scheduled-cue history**: inspect only succeeded, failed, skipped, or recovered cue runs in the administrator monitor and credential-free export

### Version 1.5.121
- **Cue-scoped conflict diagnostics**: filter the upcoming overlap report to one enabled cue while preserving the opposing schedule and add a focused administrator conflict selector

### Version 1.5.120
- **Scheduled-cue conflict diagnostics**: detect upcoming duration overlaps with time-zone-aware instants, priorities, target labels, overlap seconds, and serialized-execution guidance before a cue can delay another

### Version 1.5.119
- **Fail-safe scheduled lifecycle**: failed Skip Next persistence never falls through into an unintended cue run, and failed one-time completion saves restore the enabled state

### Version 1.5.118
- **Transactional global settings**: failed administrator configuration saves restore all prior settings and retained session/scheduled-cue history instead of leaking serializer errors

### Version 1.5.117
- **Transactional scene persistence**: failed saved-scene updates restore the prior in-memory scene collection and return sanitized persistence errors
- **Transactional scheduled-cue lifecycle**: failed save/delete/direct counter-reset operations roll back instead of rethrowing raw persistence exceptions

### Version 1.5.116
- **Per-user mapping dependency audit**: inspect every scheduled cue that targets a user mapping through a credential-free API before disabling or deleting it
- **Transactional mapping lifecycle**: roll back in-memory mapping changes and return sanitized persistence errors for failed save/delete operations
- **Administrator reference inspection**: use View References beside each per-user mapping to understand scheduled-cue and saved-playlist target protections without trial-and-error

### Version 1.5.115
- **Saved-playlist dependency audit**: inspect every scheduled cue that references a playlist through a credential-free API before deletion
- **Administrator cue-reference inspection**: use View Cue References in the Saved Scene Playlists editor to understand why deletion is blocked without trial-and-error

### Version 1.5.114
- **Complete saved-scene dependency graph**: show scheduled cues that reach a scene through a dependent playlist, including direct-versus-playlist reference type
- **Transactional saved-scene deletion**: roll back the in-memory scene collection and return a sanitized 500 response if persistence fails

### Version 1.5.113
- **Saved-scene dependency audit**: inspect dependent playlists, repeated scene-step counts, and direct scheduled cues before changing or deleting a scene through a credential-free API
- **Administrator reference inspection**: use View References in the Saved Scene editor to understand why deletion is blocked without trial-and-error

### Version 1.5.112
- **Reference-safe saved-scene rename**: preserve all visual/effect metadata while atomically migrating playlist and direct scheduled-cue references to the new scene name
- **Administrator rename workflow**: enter a replacement scene name and use Rename Scene to avoid creating an unreferenced duplicate or breaking existing automation

### Version 1.5.111
- **Reference-safe saved-scene deletion**: reject deletion while a playlist or scheduled cue references the scene, with sanitized dependency counts
- **Administrator dependency feedback**: explain the protected scene dependency in the Saved Scene editor

### Version 1.5.110
- **Reference-safe playlist lifecycle**: migrate scheduled-cue references atomically when a playlist is renamed and reject deletion while dependent cues still use it
- **Administrator dependency feedback**: refresh scheduled-cue selectors after playlist saves and report dependent cue counts when deletion is blocked

### Version 1.5.109
- **Repeatable saved playlists**: repeat a saved sequence for up to 10 passes while enforcing the bounded 10-minute aggregate duration cap
- **Pass-aware observability**: expose repeat counts and expanded per-pass outcomes through previews, scheduled cue telemetry, iCalendar metadata, history, and backup/restore

### Version 1.5.108
- **Scheduled playlist cues**: schedule saved scenes or playlists with target overrides, saved-order or stable-shuffle restorative execution, per-step saved-position telemetry, total-duration metadata, and credential-safe status/history/calendar/backup behavior

### Version 1.5.107
- **Saved scene playlists**: compose up to 20 existing saved scenes into reusable sequences with bounded repeat passes, credential-free CRUD, duplicate/delete actions, and default-target, selected-subset, or all-enabled-target previews
- **Sequential restorative execution**: preview each playlist step through the existing cancellation and light-state restoration lifecycle, with per-step results, aggregate target outcomes, channel counts, and cleanup warnings
- **Portable definitions**: include saved-scene order, repeat count, playback order, selected mapping IDs/default inclusion, target mode, and duration metadata in credential-safe backup/restore without exposing bridge credentials

### Version 1.5.106
- **Saved-scene preview execution**: preview any saved scene directly from the administrator page or `POST /HueSync/ColorPresets/{name}/Preview`, with default-target, selected-mapping, and all-enabled-target modes using server-side credentials and channel profiles
- **Credential-free saved-scene actions**: return sanitized per-target results, aggregate channel counts, transition metadata, and cleanup warnings while never sending bridge credentials to the browser

### Version 1.5.105
- **Channel-profile target diagnostics**: validate effective global and per-user channel selections against each bridge area's live channel list, with selected counts and stale IDs shown before playback
- **Credential-safe preflight**: malformed or stale profiles are reported without opening DTLS streams or changing bridge state

### Version 1.5.104
- **Immediate all-enabled-target preview**: preview the selected effect sequentially on the default bridge and every distinct enabled mapped target, using each target's saved channel profile and showing sanitized per-target outcomes and channel counts
- **Credential-safe broadcast API**: `POST /HueSync/Preview` accepts `targetAllEnabledMappings: true` without direct target fields and rejects incomplete enabled targets before any bridge state is changed

### Version 1.5.103
- **All-enabled-target scheduled cues**: run one saved scene sequentially on the default bridge and every distinct enabled mapped bridge/area, with sanitized per-target success, failure, and cleanup telemetry
- **Portable broadcast mode**: preserve the target mode through schedule CRUD, duplicate cues, status, upcoming occurrences, iCalendar metadata, and credential-safe backup/restore; incomplete enabled targets are rejected before execution

### Version 1.5.102
- **Saved-scene duplication**: create a uniquely named editable copy from any saved effect scene while preserving its effect, animation speed, color, brightness, duration, and fade transitions
- **Credential-free copy workflow**: duplicate scenes from the administrator page or API without changing source schedules or exposing bridge credentials

### Version 1.5.101
- **Deterministic scheduled-cue priorities**: assign each cue a bounded 0-100 priority so higher-priority cues run first when multiple automatic cues are due together, while equal priorities retain saved order
- **Priority-aware administration**: edit, duplicate, back up, restore, preview, and inspect cue priority through the administrator UI and credential-free API/status surfaces

### Version 1.5.100
- **Missed-cue recovery**: optionally recover the most recent automatic scheduled cue missed during a short Jellyfin restart or outage using a bounded 0-120 minute global window; older missed cues are never replayed in a burst
- **Recovery observability**: recovered executions, including recovered skips, are labeled in scheduler status and sanitized history and preserve existing recurrence, run-limit, restoration, and one-time semantics

### Version 1.5.99
- **Skip Next Cue**: omit one upcoming automatic occurrence without deleting or editing a recurring schedule; future occurrences remain intact
- **Restart-safe skip state**: pending skips are persisted, reversible, reflected in upcoming previews/status, and recorded as sanitized history; one-time cues disable after a skipped event and manual Run Now is unaffected

### Version 1.5.98
- **Per-cue enable/disable control**: pause or resume one scheduled rule without changing its timing, target, scene, or recurrence definition
- **Guarded state transitions**: active cues cannot be changed, and exhausted finite cues require Reset Run Counter before re-enabling

### Version 1.5.97
- **Safe scheduled-cue duplication**: create a fresh disabled copy with a unique stable ID, copied timing/target/recurrence/effect metadata, and a reset execution counter
- **Credential-free administrator API**: expose duplication through `POST /HueSync/SceneSchedules/{id}/Duplicate` with full configuration validation before saving

### Version 1.5.96
- **Resettable finite cues**: reset a persisted execution counter and re-enable an exhausted scheduled cue without deleting its retained audit history
- **Safe administrator action**: refuse resets while a cue is active and expose the same behavior through `POST /HueSync/SceneSchedules/{id}/ResetRunCount`

### Version 1.5.95
- **Finite scheduled-cue limits**: stop recurring scene cues after a bounded 1-365 execution count, or keep the default `0` for unlimited runs
- **Restart-safe counters**: persist finite-cue run counts, disable cues automatically at the limit, and retain the state through credential-safe backup/restore
- **Limit-aware administration**: configure maximum executions in the cue editor and inspect current, maximum, and remaining runs in scheduler telemetry

### Version 1.5.94
- **Configurable animated speed**: set a bounded 25-400% rate for Pulse, Rainbow, and Candle previews and saved scenes while Solid remains unchanged
- **Portable speed metadata**: preserve effect speed through scene CRUD, scheduled execution, status, upcoming occurrences, iCalendar, history, and credential-safe backup/restore
- **Speed-aware editor and telemetry**: edit, apply, and inspect the selected rate in preview controls, cue lists, runtime status, upcoming runs, and retained history

### Version 1.5.93
- **Saved-scene effects**: add bounded Solid, breathing Pulse, hue-cycling Rainbow, and warm flickering Candle effects to manual previews and reusable scenes
- **Scheduled effect playback**: preserve effect metadata through cue execution, status, occurrences, iCalendar export, history, and credential-safe backup/restore
- **Effect-aware editor**: select, save, apply, and preview scene effects on the default or current mapping target while legacy scenes remain Solid

### Version 1.5.92
- **Saved-scene effects**: add bounded Solid, breathing Pulse, and hue-cycling Rainbow effects to manual previews and reusable scenes
- **Scheduled effect playback**: preserve effect metadata through cue execution, status, occurrences, iCalendar export, history, and credential-safe backup/restore
- **Effect-aware editor**: select, save, apply, and preview scene effects on the default or current mapping target while legacy scenes remain Solid

### Version 1.5.91
- **Cancellable system diagnostics**: show **Cancel Active Diagnostics** while prerequisite or saved-target checks run, with cancellation status and page-leave cleanup
- **Shared diagnostics cancellation**: cancel every active non-mutating check through a bounded server-side gate without exposing credentials or changing bridge state

### Version 1.5.90
- **Cancellable connection diagnostics**: show **Cancel Active Diagnostic** while default or per-user mapping Test Connection runs, with completion/error status and page-leave cleanup
- **Shared restorative cancellation**: reuse the credential-free preview cancellation endpoint so diagnostics stop safely and restore captured light state before ending

### Version 1.5.89
- **Cancellable administrator previews**: add visible cancellation for default and mapping previews, with page-leave cleanup and serialized-operation guards
- **Cancellable manual scene cues**: add a Run Now cancellation button and `POST /HueSync/SceneSchedules/{id}/Cancel` endpoint with linked token cleanup
- **Sanitized cancellation responses**: expose only bounded status/message results while preserving bridge deactivation and captured-light restoration

### Version 1.5.88
- **Saved-scene fade-out transitions**: optionally ramp previews and scheduled cues from their target scene back to dark at the end of the configured hold time
- **Bookended transition timing**: validate and clamp combined fade-in/fade-out durations so shorter per-cue overrides preserve the full restorative lifecycle
- **Portable fade metadata**: carry `transitionSeconds` and `transitionOutSeconds` through scene CRUD, scheduler status/occurrences, iCalendar export, and credential-safe backup/restore

### Version 1.5.87
- **Saved-scene fade-in transitions**: ramp previews and scheduled cues from dark to a target scene over a bounded optional duration within the configured hold time
- **Portable transition metadata**: carry `transitionSeconds` through scene CRUD, scheduler status/occurrences, iCalendar export, and credential-safe backup/restore

### Version 1.5.86
- **Bounded scheduled-cue intervals**: run daily, weekly, monthly, monthly-weekday, or yearly cues every N calendar units from 1 through 365
- **Portable cadence anchors**: preserve `recurrenceInterval` and the recurring `startDate` anchor through CRUD, status/preview, calendars, and credential-safe backup/restore

### Version 1.5.85
- **Yearly scheduled cues**: run scenes on a selected month and calendar day each year for birthdays, anniversaries, and holidays, with short-month clamping
- **Portable yearly recurrence**: preserve `monthOfYear` and yearly date rules through CRUD, status/preview, calendars, and credential-safe backup/restore

### Version 1.5.84
- **Monthly weekday scheduled cues**: run scenes on the first through fifth or last matching weekday of each month, such as first Monday or last Friday
- **Portable monthly weekday recurrence**: preserve ordinal-weekday fields through CRUD, status/preview, calendars, and credential-safe backup/restore

### Version 1.5.83
- **Daily scheduled cues**: run scenes every calendar day at a selected local time, with date windows and exclusions
- **Portable daily recurrence**: preserve the new daily mode through CRUD, status/preview, calendars, and credential-safe backup/restore

### Version 1.5.82
- **Monthly scheduled cues**: run scenes on a calendar day each month, with day 31 clamped to the final day in shorter months
- **Portable monthly recurrence controls**: preserve monthly mode and day-of-month through CRUD, status/preview, calendars, and credential-safe backup/restore

### Version 1.5.81
- **Per-cue hold duration overrides**: reuse one saved scene at different event lengths, inheriting the scene duration with blank/`0` or overriding it with a bounded 1-30 second cue value
- **Portable duration telemetry**: preserve overrides through CRUD, effective status/occurrence previews, iCalendar event lengths, and credential-safe backup/restore

### Version 1.5.80
- **One-time scheduled cues**: schedule a saved scene for one exact date in its selected time zone without weekday-mask workarounds
- **Portable one-time automation**: preserve one-time dates through CRUD, status/preview, iCalendar export, and credential-safe backup/restore

### Version 1.5.79
- **Calendar interoperability**: download the bounded upcoming scene-cue preview as a credential-free iCalendar feed with UTC event times and cue timezone metadata
- **Administrator calendar action**: add a one-click `.ics` download beside the existing upcoming-occurrence preview

### Version 1.5.78
- **Global scheduled-automation pause**: pause or resume recurring scene cues without changing individual cue definitions while keeping **Run Now** available for manual checks
- **Pause-aware status**: expose the automation state through scheduler telemetry and the administrator configuration summary

### Version 1.5.77
- **Upcoming cue preview**: show the next 31 days of credential-free cue-local and UTC occurrences after applying timezone, DST, weekday, date-window, and exclusion rules
- **Occurrence API**: add bounded `GET /HueSync/SceneSchedules/Occurrences` previews with cue filtering for automation verification and troubleshooting

### Version 1.5.76
- **Scheduled cue exclusions**: skip up to 100 explicit `yyyy-MM-dd` holidays or blackout dates in each cue's selected timezone
- **Portable scheduler exceptions**: preserve normalized exclusions through the admin editor, status/API, and credential-safe backup/restore

### Version 1.5.75
- **Bounded scheduled cues**: add optional inclusive `yyyy-MM-dd` start/end dates evaluated in each cue's selected time zone; blank values remain ongoing
- **Date-window observability and portability**: expose ranges in the admin editor/status/API and preserve them through credential-safe backup and restore

### Version 1.5.74
- **Per-cue time zones**: schedule each recurring scene in a validated host time zone while preserving server-local behavior for existing cues
- **DST-aware scheduler telemetry**: expose cue-local and UTC next-run values, selected zone labels, and deterministic handling for nonexistent spring-forward times

### Version 1.5.73
- **Persistent scheduled-cue history**: optionally retain the newest 100 sanitized cue runs across Jellyfin restarts and restore last outcomes/run counts into the scheduler monitor
- **Cue history controls**: add `GET /HueSync/SceneSchedules/History`, export JSON, clear history, and a credential-free administrator history table

### Version 1.5.72
- **Scheduled cue readiness diagnostics**: identify missing saved scenes, disabled mappings, invalid times/days, missing credentials/areas, and invalid channel profiles before a cue runs
- **Preflight scheduler monitor**: display a credential-free ready/not-ready reason beside each cue's next run and execution history

### Version 1.5.71
- **Scheduled cue observability**: inspect each cue's next server-local run, active state, run count, last outcome/message, cleanup warnings, and restart-safe pending cleanup records through the scheduler monitor and `GET /HueSync/SceneSchedules/Status`
- **Resilient automation loop**: isolated bridge/network failures become sanitized cue failures instead of terminating the hosted scheduler

### Version 1.5.70
- **Scheduled scene cues**: run saved color scenes automatically on selected days and server-local times, with a one-click Run Now action
- **Target-aware safe automation**: resolve the global target or enabled user mapping at execution time and reuse the restorative preview lifecycle while yielding to playback
- **Portable scene automation**: include credential-free recurring cues in configuration backup and restore

### Version 1.5.69
- **Opt-in persistent session history**: retain the bounded 25-entry sanitized playback history across Jellyfin restarts and restore it into Live Sync Status when the service starts
- **Privacy-aware retention control**: add a Retain history across Jellyfin restarts setting; disabling it or clearing history removes stored entries without stopping playback

### Version 1.5.68
- **Session history operations**: filter recent summaries by outcome, export a credential-free JSON troubleshooting document, and clear retained history without stopping active playback
- **Administrator history controls**: add outcome filtering, Export JSON, and Clear History actions to the Recent Hue Sessions panel

### Version 1.5.67
- **Completed-session history**: retain the 25 most recent sanitized playback summaries, including concurrent multi-room worker sessions, and expose them through `GET /HueSync/History` plus a refreshable Recent Hue Sessions administrator table
- **Credential-safe diagnostics**: history remains in memory only and includes aggregate telemetry/target labels without bridge credentials or Jellyfin playback tokens

### Version 1.5.66
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
- **Cancellation-safe DTLS lifecycle**: Playback and diagnostics cancel managed DTLS startup, color writes, delayed reconnects, and area reactivation; stopped streams cannot resurrect a background tunnel

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
- **System Diagnostics**: Add a non-mutating setup and runtime report for configuration validity, FFmpeg availability/version, managed DTLS readiness, lifecycle contention, and playback/diagnostic readiness
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
- **Reusable color scenes**: Save, apply, update, duplicate, and delete up to 50 named effect presets from the preview controls; duplication preserves the complete visual definition while assigning a bounded unique name, and scenes remain global, credential-free, and available for default or mapping previews
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
- Health monitoring for FFmpeg and the managed DTLS transport
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
