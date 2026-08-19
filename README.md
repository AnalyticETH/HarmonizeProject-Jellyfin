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
| **Test Connection** | Verify bridge credentials and, when selected, that the entertainment area has controllable channels. If a Client Key is present, also run a short DTLS stream probe that captures a complete light-state snapshot before activation and restores it afterward. While a probe is running, the page exposes **Cancel Active Diagnostic**; disconnecting, canceling, or leaving the page stops the diagnostic lifecycle safely. |
| **System Diagnostics** | Run a non-mutating local health check for saved configuration validity, FFmpeg/OpenSSL availability and versions, active bridge lifecycle contention, and playback/diagnostic readiness. **Validate Saved Targets** additionally checks every enabled default/inherited/custom bridge mapping for reachability, selected-area presence, controllable channels, and stale IDs in the effective global/per-user channel profile without opening a DTLS stream. **Export Support Bundle** collects these results, credential-free configuration metadata, runtime telemetry, bounded playback/scheduler history, and scheduler status into one reviewable JSON document. **Cancel Active Diagnostics** safely stops either long-running check or support-bundle target validation, and leaving the page requests the same cancellation. |
| **Backup and Restore** | Export global settings, per-user profiles, saved scene effects, ordered saved scene playlists with repeat passes, and scheduled scene cues—including Solid/Pulse/Rainbow/Candle metadata, bounded 25-400% rates, optional fade-in/fade-out transitions, one-time dates, optional per-cue hold durations, priorities, and recurring date windows—as a credential-safe JSON document. **Validate Import** runs the same normalization, dependency, and full-configuration preflight without changing the server, including planned totals and active-playback readiness. Import is atomic, preserves matching stored keys on the same server, and includes an in-page password-field wizard for explicit replacement keys during migrations. |
| **Live Sync Status** | Show the active Jellyfin user, effective playback media scope, selected bridge/area, captured profiles, effective FPS, sent/skipped/failed stream updates, reconnect attempts, seek-recovery restarts, frame health, cleanup warnings, and safe per-session stop controls while playback is running. Distinct mapped bridges/areas can be streamed concurrently. |
| **Recent Hue Sessions** | Review and filter the 25 most recent completed sync sessions, including outcome, target, duration, quality counters, and cleanup/error warnings. Export a credential-free JSON troubleshooting document, clear history without stopping playback, or optionally retain the sanitized window across Jellyfin restarts. |
| **Startup recovery** | If the plugin or Jellyfin service starts while an unpaused video is already playing, recover the active session at Jellyfin's current position so viewers do not need to stop and restart playback. |
| **Completed-session summary** | Keep the most recent video session's outcome, duration, frame/packet telemetry, reconnects, seek recoveries, and cleanup warnings visible after playback ends; summaries never contain bridge credentials or playback tokens. Audio-only and other non-video playback is ignored safely. |
| **Hue App Key** | "Username" for the REST API. The key is stored server-side and is never returned by the configuration endpoint; leave the field blank to keep it, or use Link Bridge to replace it. |
| **Hue Client Key** | "ClientKey" for the streaming API. The key is stored server-side and is never returned by the configuration endpoint; leave the field blank to keep it, or use Link Bridge to replace it. |
| **Entertainment Area ID** | UUID of the specific area to sync. |
| **Playback Media Scope** | Choose whether Hue Sync starts for all video items (default), movies only, TV episodes only, or other video such as music/home videos. Changing the scope does not interrupt an active session; normal progress and stop events still perform cleanup. |
| **Per-User Playback Media Scope** | Each per-user mapping can inherit the global scope or override it for that user's playback, so a room or family profile can restrict sync independently. Existing sessions still clean up normally when the effective scope changes. |
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
| **Scene Effect Preview** | Choose a color, **Solid**, breathing **Pulse**, hue-cycling **Rainbow**, or warm flickering **Candle** effect, bounded 25-400% animation speed, brightness, 1-30 second duration, and optional 0-30 second Fade in and Fade out to preview the default target (or the current mapping target). **Preview All Enabled Targets** runs the same scene sequentially on the default bridge and every distinct enabled mapped bridge/area, using each target's saved channel profile and returning independent sanitized outcomes. **Preview Saved Scene** and **Preview Saved Scene on All Targets** run a selected saved scene through those same server-side target profiles without sending credentials from the browser. The **Saved Scene Playlists** panel composes up to 20 saved scenes in a chosen order, repeats the complete sequence for 1-10 passes when the aggregate duration remains within the 10-minute safety cap, and previews it on the default target or every enabled target with expanded per-pass step and aggregate status. Renaming a playlist automatically migrates scheduled-cue references, while deletion is blocked until dependent cues are changed or removed. Saved-scene rename now preserves every visual setting and atomically migrates playlist and direct scheduled-cue references; **View References** lists those credential-free dependencies before edits or deletion; saved-scene deletion is likewise blocked while a playlist or scheduled cue references that scene. The plugin ramps from dark to the selected effect, refreshes animated frames at a bounded cadence, and can ramp back to dark within the same duration; captures and restores the selected lights automatically. Save scenes for reuse, use **Duplicate Scene** to create a uniquely named editable variant, or use **Rename Scene** after editing the name field when existing references must follow the scene. **Cancel Active Preview** requests a safe stop for any preview button (and page navigation does the same best-effort cleanup), while active playback still blocks overlap. |
| **Cleanup diagnostics** | Light capture and restoration retry each light using the captured network policy. Playback, probes, and previews refuse to activate when the snapshot is incomplete; one shared bridge lease also prevents playback and diagnostics from overlapping. Partial restoration or failed entertainment-area deactivation remains visible as a sanitized warning in Live Sync Status and probe/preview results. DTLS startup, writes, and reconnects stop with playback or diagnostic cancellation. |
| **Scheduled Scene Cues** | Run either one saved Solid, Pulse, Rainbow, or Candle scene or an ordered saved-scene playlist automatically at a chosen time and time zone against the global target or an enabled user mapping. Playlist cues run each saved scene in order for the configured repeat passes, preserve each scene's duration/effect, and report pass count, expanded total duration, and ordered per-step outcomes. Each single-scene cue inherits the saved scene's bounded 25-400% animation speed. Choose daily recurrence, weekly weekday recurrence, monthly calendar-day recurrence, monthly ordinal-weekday recurrence such as first Monday or last Friday, or yearly calendar-date recurrence for birthdays and holidays; day 31 is clamped to the final day in shorter months. Set `Every` to 1 for the normal cadence or to a higher value for every-N-day/week/month/year schedules; values above 1 require the recurring cue's start date as the portable cadence anchor. Use a one-time calendar date for an exact event, or optional inclusive start/end windows and up to 100 excluded dates for recurring holidays and blackout days. Leave a cue's hold duration blank or `0` to inherit the saved scene duration; a playlist always uses its scenes' saved durations and repeat count, while a single-scene cue may set a bounded 1-30 second override. Inherited effect, speed, fade-in, and fade-out transitions run within the effective duration, with fade-out clamped after fade-in when a cue is shorter. Set a finite maximum of 1-365 executions when a recurring cue should stop automatically; its persisted run counter survives Jellyfin restarts, while `0` remains unlimited. Use **Reset Run Counter** to clear the counter and re-enable an exhausted cue without deleting its retained audit history. Use **Enable/Disable Cue** to pause one rule without editing its schedule, **Skip Next Cue** to omit exactly one upcoming automatic occurrence while preserving future recurrence, and **Duplicate Cue** to create a fresh, disabled copy with the same rules for safe variations. Skipped occurrences are persisted, reversible before the cue is due, recorded in sanitized history, and never suppress manual **Run Now**; if clearing the skip marker cannot be persisted, the cue is left untouched rather than running unexpectedly. One-time cues disable after their skipped occurrence, and failed one-time completion persistence restores their in-memory enabled state. The scheduler monitor and credential-free conflict report flag upcoming duration overlaps before they can delay a later cue, including effective durations, priorities, time-zone-aware instants, and ordering guidance. An optional global missed-cue recovery window (0-120 minutes) can recover the most recent occurrence missed during a short restart/outage without replaying an old burst; recovered runs are labeled in status and history. Successful one-time runs disable themselves so a restart cannot repeat the event. Pause or resume recurring automation without deleting cues; individual **Run Now** remains available while paused and exposes **Cancel Running Cue** until the restorative run ends. Every cue is a short restorative preview, active playback wins, duplicate polling within a selected-zone minute is suppressed, and DST transitions are handled deterministically. The scheduler monitor reports readiness, effect, speed, playlist step count/repeat count/total duration, automation state, recovery-window state, recurrence details and interval, finite execution limits, cue-local/UTC next run, effective hold duration and fade, pending skip state, a selectable 7/31/90/366-day report horizon, cue-scoped occurrence and calendar filters, downloadable credential-free occurrence and conflict JSON reports, conflict warnings, a credential-free iCalendar download, active state, run count, last outcome (including recovered/skipped runs), cleanup warnings, and an optional bounded cue-run history that can survive Jellyfin restarts, with cue- and outcome-filtered administrator and JSON-export views. |
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
Use **View Cue References** beside a mapping to inspect the credential-free scheduled-cue list
before disabling or deleting it; referenced mappings remain protected until those cues are changed
or removed. Mapping saves and deletes are transactional, so a persistence failure restores the
previous in-memory mapping collection.
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
| `POST /HueSync/Preview` | Display a bounded Solid, Pulse, Rainbow, or Candle scene effect and restore the selected lights. Request fields include `ipAddress`, `appKey`, `clientKey`, optional `userId`, `entertainmentAreaId`, optional `channelIds`, `effect` (`Solid`, `Pulse`, `Rainbow`, or `Candle`; blank/omitted preserves Solid), `effectSpeedPercent` (25-400; 100 is neutral and Solid ignores it), `red`, `green`, `blue` (0-255 seed color), `brightnessPercent` (0-100), `durationSeconds` (1-30), and optional `transitionSeconds`/`transitionOutSeconds` (0-30, with their sum no greater than the duration). Set `targetAllEnabledMappings: true` to omit direct target fields and run sequentially on the valid global target plus every distinct enabled custom mapping; each target uses its configured channel profile and the response includes credential-free `targetResults`, aggregate channel counts, and independent cleanup warnings. Active playback must be stopped first; matching stored mapping credentials are resolved server-side and never returned to the browser. |
| `POST /HueSync/Preview/Cancel` | Request cancellation of the active administrator preview or DTLS diagnostic. The configuration page exposes this action for previews and default/per-user Test Connection probes. The response is credential-free; the linked operation deactivates the entertainment area and restores captured light state before ending. |
| `GET /HueSync/ColorPresets` | List saved, credential-free color scenes sorted by name. |
| `GET /HueSync/ColorPresets/{name}/Dependencies` | Inspect one saved scene's credential-free lifecycle dependency graph before editing or deleting it. Returns `canDelete`, dependent playlist and scheduled-cue counts, playlist IDs/names with repeated reference counts, and cue IDs/names/enabled state plus `referenceType` (`DirectScene` or `Playlist`) and the dependent `playlistName`; bridge credentials and target details are never returned. |
| `POST /HueSync/ColorPresets` | Save or update a named scene with `name`, `effect` (`Solid`, `Pulse`, `Rainbow`, or `Candle`), `effectSpeedPercent` (25-400; 100 is neutral), RGB seed values, `brightnessPercent`, `durationSeconds`, and optional `transitionSeconds`/`transitionOutSeconds` (0-30, with their sum no greater than the scene duration); names are case-insensitive, omitted effects and speed remain Solid/100%, values are validated, and persistence failures leave the previous scene collection unchanged. |
| `POST /HueSync/ColorPresets/{name}/Rename` | Rename one saved scene with `{ "newName": "..." }`, preserving all visual/effect metadata and atomically migrating matching playlist `presetNames` and direct scheduled-cue `presetName` references. The new name is validated, must not collide with another scene, and invalid or failed saves leave the existing configuration unchanged. |
| `POST /HueSync/ColorPresets/{name}/Preview` | Preview one saved scene using server-side credentials and channel profiles. Send `{ "targetUserId": "..." }` for the default target (blank) or one enabled mapping, or `{ "targetAllEnabledMappings": true }` to run sequentially across every distinct enabled target. The response includes the saved visual metadata, credential-free per-target outcomes, channel counts, and cleanup warnings; incomplete targets, invalid saved scenes, and active playback are rejected before execution. |
| `POST /HueSync/ColorPresets/{name}/Duplicate` | Create a uniquely named copy of one saved scene, preserving effect, animation speed, RGB/brightness, duration, and fade transitions. The copy is independently editable, leaves scheduled references to the source unchanged, and never returns bridge credentials. |
| `DELETE /HueSync/ColorPresets/{name}` | Delete one saved color scene by name; returns a conflict with dependent playlist and scheduled-cue counts while any reference remains. |
| `GET /HueSync/ScenePlaylists` | List credential-free ordered saved-scene playlists, including scene references, repeat passes, target mode/label, and bounded total duration. |
| `GET /HueSync/ScenePlaylists/{name}/Dependencies` | Inspect one saved playlist's credential-free scheduled-cue dependencies before deletion. Returns `canDelete`, the dependent cue count, and cue IDs/names/enabled state; bridge credentials and target details are never returned. |
| `POST /HueSync/ScenePlaylists` | Save or update a playlist with an optional stable `id`, bounded `name`, up to 20 ordered `presetNames`, bounded `repeatCount` (1-10, default 1), and either the default target, one enabled `targetUserId`, or `targetAllEnabledMappings: true`; all references, repeated duration, and targets are validated before persistence. Renaming an existing playlist atomically updates scheduled cues that reference its old name. |
| `POST /HueSync/ScenePlaylists/{name}/Preview` | Preview a saved playlist sequentially through the restorative scene lifecycle for its configured repeat passes. An optional `{ "targetUserId": "..." }` or `{ "targetAllEnabledMappings": true }` overrides the saved target for that run; the response includes repeat count, expanded per-pass steps, credential-free aggregate target outcomes, channel counts, and cleanup warnings. |
| `POST /HueSync/ScenePlaylists/{name}/Duplicate` | Create a uniquely named copy of a playlist with a new stable ID while preserving its ordered scenes and target mode. |
| `DELETE /HueSync/ScenePlaylists/{name}` | Delete one saved-scene playlist by name; returns a conflict with the dependent cue count while any scheduled cue references it. |
| **Scheduled cue target modes** | `targetAllEnabledMappings: true` selects **All enabled targets**. The scheduler includes the valid global target and distinct enabled custom mappings, runs them sequentially, and returns credential-free `targetResults` with each target's outcome. The flag is carried by schedule CRUD, status, occurrences, history, duplication, and backup/restore; it cannot be combined with `targetUserId`. |
| `GET /HueSync/SceneSchedules` | List credential-free scene cues with exactly one saved-scene `presetName` or saved-playlist `playlistName`, `playlistStepCount`/`playlistRepeatCount`/`playlistTotalDurationSeconds` for playlist cues, `priority` (0-100; higher values run first when cues are due together and equal values retain saved order), effective `effect` (`Solid`, `Pulse`, `Rainbow`, `Candle`, or `Playlist`) and inherited `effectSpeedPercent` (25-400 for scenes; 100 for playlists), target label, cue time/time zone, effective `transitionSeconds` and `transitionOutSeconds` inherited from the saved scene (zero for playlists), `recurrence` (`Daily`, `Weekly`, `Monthly`, `MonthlyWeekday`, or `Yearly`), `recurrenceInterval` (1-365 calendar units; values above 1 use `startDate` as the recurring cadence anchor), `maxRuns` (`0` for unlimited or `1-365` finite attempts), persisted `runCount`, optional `dayOfMonth` (1-31 for monthly-day/yearly cues; shorter months clamp to their final day), optional `monthOfYear` (1-12 for yearly cues), optional `weekOfMonth` (1-5 or `-1` for last) and `dayOfWeek` (Sunday=0 through Saturday=6) for monthly-weekday cues, optional `durationSeconds` override (`0` inherits the saved scene and must remain zero for playlists), optional one-time run date, recurring date rules, day mask, and enabled state. |
| `POST /HueSync/SceneSchedules` | Save or update a cue with `id` (optional for new cues), `name`, exactly one of `presetName` or `playlistName`, optional `priority` (`0-100`; higher values run first when cues are due together, equal values retain saved order, and omitted priority preserves an existing cue's value), optional `targetUserId`, `timeOfDay` (`HH:mm` in the selected zone), optional `timeZoneId` (blank means server local), `recurrence` (`Daily`, `Weekly`, `Monthly`, `MonthlyWeekday`, or `Yearly`, default `Weekly`), `recurrenceInterval` (`1-365`; values above 1 require `startDate` and mean every N days, weeks, months, or years), `maxRuns` (`0` for unlimited or `1-365` finite attempts), optional `runCount` for credential-safe backup/restore (ordinary edits preserve the existing counter), `dayOfMonth` (`1-31` for monthly-day/yearly cues), `monthOfYear` (`1-12` for yearly cues), `weekOfMonth` (`1-5` or `-1` for last) and `dayOfWeek` (`0-6`) for monthly-weekday cues, `durationSeconds` (`0` to inherit the saved scene or `1-30` for a single-scene cue; playlist cues must use `0`), optional `runDate` (`yyyy-MM-dd` for one exact cue occurrence; when set, recurring date rules and recurrence selection are ignored), optional `startDate`/`endDate` (`yyyy-MM-dd`, inclusive in the selected zone; blank means unbounded for recurring cues), optional `excludedDates` (up to 100 `yyyy-MM-dd` values skipped in the selected zone), `daysOfWeekMask` (Sunday bit 1 through Saturday bit 64 for weekly cues; ignored for daily/monthly-day/monthly-weekday/yearly cues), and `enabled`. The cue inherits its saved scene's optional fade-in and fade-out transitions; playlists run each scene for their saved repeat count and transitions. Bridge credentials are resolved server-side from the selected target, and failed persistence leaves the previous cue collection unchanged. |
| `POST /HueSync/SceneSchedules/{id}/Duplicate` | Create a disabled copy of one cue with a unique name, a new stable ID, the same scene/target/timing/recurrence/limit metadata, and a reset execution counter. The copy is safe to edit before enabling and never returns bridge credentials. |
| `DELETE /HueSync/SceneSchedules/{id}` | Delete one scene cue by its stable ID; persistence failures roll back the in-memory cue collection and return a sanitized error. |
| `POST /HueSync/SceneSchedules/{id}/Run` | Run one cue immediately through the serialized, state-restoring preview lifecycle; active playback or another diagnostic safely blocks the run. |
| `POST /HueSync/SceneSchedules/{id}/Cancel` | Request cancellation of an active manual Run Now cue. The response reports whether a run was found; cleanup still deactivates the area and restores captured light state. |
| `POST /HueSync/SceneSchedules/{id}/ResetRunCount` | Reset a cue's persisted execution counter to zero and re-enable it. Active cues cannot be reset; retained history remains available as an audit trail, and direct persistence failures restore the prior counter/state. |
| `POST /HueSync/SceneSchedules/{id}/Enabled` | Enable or disable one cue without changing its schedule definition. Active cues cannot be changed, and an exhausted finite cue must be reset before it can be enabled; the response remains credential-free. |
| `POST /HueSync/SceneSchedules/BulkEnabled` | Atomically enable or disable up to 50 selected cue IDs with `{ "scheduleIds": ["..."], "enabled": true|false }`. Every ID is normalized and validated before persistence; active cues, exhausted finite cues, unknown IDs, and save failures leave the entire selection unchanged. The response includes updated credential-free cue summaries and counts. |
| `POST /HueSync/SceneSchedules/{id}/SkipNext` | Mark exactly one upcoming automatic occurrence to be skipped without changing recurrence, limits, or the saved scene. Active, disabled, exhausted, or futureless cues are rejected; manual Run Now remains available. |
| `DELETE /HueSync/SceneSchedules/{id}/SkipNext` | Clear a pending skip so the next eligible automatic occurrence runs normally. |
| `GET /HueSync/SceneSchedules/TimeZones` | List the Jellyfin host's available system time zones for schedule selection, including stable IDs, display names, and base UTC offsets. |
| `GET /HueSync/SceneSchedules/Status` | Read credential-free scheduler telemetry for the global automation state and every configured cue: saved-scene or playlist identity, playlist step count/repeat count/total duration, priority/execution ordering, preflight readiness and reason, effect and effect speed, recurrence/interval/month/day/ordinal-weekday details, selected time zone, finite execution limit and remaining runs, effective hold duration, fade-in, and fade-out transitions, optional one-time date, recurring date window and exclusions, cue-local and UTC next run, active state, pending skip, run count, last run/outcome/message (including skipped occurrences), cleanup warning, and bounded upcoming overlap warnings. Readiness validates saved configuration locally without contacting the bridge. |
| `GET /HueSync/SceneSchedules/Occurrences?limit=50&days=31&scheduleId=...` | Preview bounded upcoming cue occurrences in UTC and the cue-local wall clock, including each saved scene or playlist identity, playlist step count/repeat count/total duration, priority, effect, inherited effect speed, effective hold duration, fade-in, and fade-out transition and applying one-time dates, daily/weekly/monthly-day/monthly-weekday/yearly recurrence, bounded recurrence intervals anchored by `startDate`, finite execution limits, ordinal weekday matching, short-month clamping, timezone/DST rules, weekday masks, date windows, and exclusions without contacting the bridge. `limit` is capped at 50 per response and `days` at 366. |
| `GET /HueSync/SceneSchedules/Conflicts?limit=50&days=31&scheduleId=...` | Report bounded credential-free overlaps between enabled cue execution windows before they run. The report uses effective scene/playlist durations, selected time zones and DST conversion, recurrence intervals, exclusions, pending skips, finite-run limits, and priorities; it returns both occurrence instants, target labels, overlap seconds, and ordering guidance without contacting a bridge. When supplied, `scheduleId` returns only conflicts involving that cue while retaining the opposing cue for context. `limit` is capped at 200 and `days` at 366. |
| `GET /HueSync/SceneSchedules/Conflicts/ExportCsv?limit=50&days=31&scheduleId=...` | Download the same bounded conflict report as UTF-8 CSV with explicit UTC/local occurrence columns, priorities, durations, overlap seconds, and resolution guidance; cue filters and report horizons are preserved and bridge credentials are omitted. |
| `GET /HueSync/SceneSchedules/Calendar?limit=50&days=31&scheduleId=...` | Download the same bounded upcoming occurrences as an RFC 5545 iCalendar feed with UTC event times, cue timezone metadata, recurrence, interval, priority, finite-limit filtering, effect, effect speed, and transition metadata, target/scene-or-playlist descriptions, playlist step count/repeat count, and effective cue durations; `limit` is capped at 50 and `days` at 366, and no bridge credentials are included. |
| `GET /HueSync/SceneSchedules/Occurrences/ExportCsv?limit=50&days=31&scheduleId=...` | Download the same bounded upcoming occurrences as UTF-8 CSV with scene/playlist metadata, target labels, recurrence, duration/fade, and explicit cue-local/UTC timestamps; `limit` and `days` remain capped and no bridge credentials are included. |
| `GET /HueSync/SceneSchedules/History?limit=100&scheduleId=...&outcome=...` | Read the newest sanitized scheduled-cue runs, optionally filtered by stable cue ID and outcome (`Succeeded`, `Failed`, `Skipped`, or `Recovered`; recovered runs also match their underlying result); results include cue/scene-or-playlist/effect/speed/target labels, ordered playlist step results for in-memory runs, outcome, message, cleanup warning, timestamp, and retained run count. |
| `GET /HueSync/SceneSchedules/History/Export?limit=100&scheduleId=...&outcome=...` | Download the same credential-free scheduled-cue history document used by the administrator Export JSON action, including the selected cue and outcome filters. |
| `GET /HueSync/SceneSchedules/History/ExportCsv?limit=100&scheduleId=...&outcome=...` | Download the same filtered scheduled-cue history as one credential-free UTF-8 CSV row per run, including outcome, recovery state, run count, bounded nested-result counts, messages, and cleanup warnings. |
| `DELETE /HueSync/SceneSchedules/History` | Clear retained scheduled-cue run summaries and reset last-run pointers without stopping an active cue. |
| `GET /HueSync/Status` | Read sanitized runtime state, active Jellyfin user and target, active performance/color/execution/channel/restoration profile, frame count, effective FPS, stream packet counters, reconnect attempts, seek-recovery restart count and last seek position, FFmpeg/DTLS health, cleanup warnings, the credential-free `lastSession` summary, whether the current sync can be stopped safely, and a `sessions` array for concurrent playback workers. |
| `GET /HueSync/History?limit=20&outcome=Error` | Read the newest completed Hue session summaries (up to 25), optionally filtered by outcome, including target labels and aggregate playback quality/cleanup telemetry. Results are bounded in memory and never include bridge credentials or playback tokens. |
| `GET /HueSync/History/Export?limit=25&outcome=Error` | Download the same sanitized session-history document used by the administrator Export JSON action for troubleshooting; bridge credentials and playback tokens are omitted. |
| `GET /HueSync/History/ExportCsv?limit=25&outcome=Error` | Download the same filtered completed-session history as credential-free UTF-8 CSV with playback quality counters, timestamps, target metadata, errors, and cleanup warnings. |
| `DELETE /HueSync/History` | Clear retained completed-session summaries and the status API's last-session pointer without stopping active playback. |
| `GET /HueSync/Diagnostics` | Run a non-mutating, cancellation-aware local prerequisite check for configuration validity, FFmpeg/OpenSSL versions, bridge lifecycle contention, and playback/diagnostic readiness. No bridge credentials are returned. |
| `GET /HueSync/TargetDiagnostics` | Validate every saved default, inherited, and enabled custom bridge target without mutating bridge state; reports reachability, selected-area presence, available and selected channel counts, stale channel-profile IDs, credential presence, and sanitized readiness messages. |
| `GET /HueSync/Diagnostics/SupportBundle` | Collect a consolidated credential-safe support document containing local diagnostics, saved-target validation, runtime status, bounded playback and scheduled-cue history, scheduler status, and the redacted configuration export. Bridge keys and playback tokens are omitted; private labels and media metadata may remain. The operation is cancellation-aware while target validation is running. |
| `POST /HueSync/Diagnostics/Cancel` | Request cancellation of active non-mutating System Diagnostics or saved-target validation checks. The bounded response reports whether any operation was found; the canceled request still owns its normal process/network cleanup. |
| `GET /HueSync/Configuration/Export` | Download a credential-safe JSON backup containing global settings, per-user profile fields, target labels, credential-presence flags, the session/cue-history retention preferences, the recurring-automation pause preference, saved scene effects and effect speeds, ordered saved scene playlists with repeat counts, and recurring or one-time scene cues. Secret values and persisted history entries are never included. |
| `POST /HueSync/Configuration/ValidateImport` | Preflight a configuration import without mutating settings or contacting Hue: apply schema normalization, saved-scene/playlist/cue dependency checks, complete configuration validation, and matching-key preservation analysis. The credential-free result includes validation errors, planned object totals, and `canImport`, which is false while playback is active. |
| `POST /HueSync/Configuration/Import` | Atomically restore an export document, including saved scene playlists and scene cues. Matching stored global/mapping keys are preserved when omitted; explicit global or mapping keys may be supplied for migration, playlist renames migrate matching cue references by stable playlist ID, and invalid documents leave the current configuration unchanged. The configuration page keeps replacement keys in memory only and sends them once in this request. Active playback must be stopped first. |
| `POST /HueSync/Stop` | Stop Hue output for the current playback session, restore lights, and leave Jellyfin playback running. Pass `playSessionId` to stop one listed concurrent session. |
| `GET/POST /HueSync/Configuration` | Read or update default plugin settings, including the global playback media scope, global channel profile, recurring scene-automation pause preference, bounded missed-cue recovery window, and opt-in persistent session/cue-history preferences, without serializing per-user mappings or global credentials to the configuration page. Responses expose `hasAppKey`/`hasClientKey` presence flags; blank key fields preserve stored values and `clearStoredCredentials` explicitly removes both global keys. Failed persistence restores the complete prior settings and retained history and returns a sanitized server error. |
| `GET /HueSync/EntertainmentAreas` | Legacy query-string-compatible area loading for existing clients; `userId` can select a matching stored custom mapping, but POST is preferred so keys do not appear in URLs. |
| `GET/POST /HueSync/UserMappings` | List or save per-user bridge mappings, sync enable flags, optional playback-media-scope/color-threshold/performance/execution/channel/restoration-profile overrides; GET responses redact stored credentials and report `InheritsDefaultBridge`. |
| `GET /HueSync/UserMappings/{userId}/Dependencies` | Inspect one mapping's credential-free scheduled-cue dependencies before disabling or deleting it. Returns `canDisable`, `canDelete`, the dependent cue count, and cue IDs/names/enabled state; bridge credentials and target details are never returned. |
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

### Version 1.5.131 (Current)
- **Spreadsheet-ready telemetry**: export upcoming occurrences, duration-aware conflicts, scheduled-cue history, and completed playback history as credential-free CSV
- **Filter-preserving reports**: CSV downloads retain selected cue, outcome, and 7/31/90/366-day horizon filters from the administrator views
- **Safe deterministic format**: UTF-8 CSV uses invariant values, explicit local/UTC columns, RFC quoting, and spreadsheet formula-marker protection for labels

### Version 1.5.130
- **Atomic bulk scheduled-cue administration**: select up to 50 cues and enable or disable them together without changing their timing, target, scene, or execution counters
- **All-or-nothing safeguards**: active cues, exhausted finite cues, missing IDs, and persistence failures leave the complete selected set unchanged
- **Administrator selection workflow**: add select-all, clear-selection, and post-action credential-free status refresh controls

### Version 1.5.129
- **Per-user playback media scope**: inherit the global scope or override it per user mapping for all video, movies, TV episodes, or other video while retaining safe cleanup for active sessions
- **Effective status and portability**: expose the active effective scope and carry credential-free overrides through mapping summaries and backup/restore

### Version 1.5.128
- **Playback media scope**: choose all video, movies, TV episodes, or other video as the global start policy; existing sessions still receive cleanup lifecycle events when the policy changes
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
- **Administrator reference inspection**: use View Cue References beside each per-user mapping to understand dependency protections without trial-and-error

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
- **Repeatable saved playlists**: repeat an ordered sequence for up to 10 passes while enforcing the bounded 10-minute aggregate duration cap
- **Pass-aware observability**: expose repeat counts and expanded per-pass outcomes through previews, scheduled cue telemetry, iCalendar metadata, history, and backup/restore

### Version 1.5.108
- **Scheduled playlist cues**: schedule saved scenes or ordered playlists with target overrides, restorative execution, ordered step telemetry, total-duration metadata, and credential-safe status/history/calendar/backup behavior

### Version 1.5.107
- **Saved scene playlists**: compose up to 20 existing saved scenes into ordered, reusable sequences with bounded repeat passes, credential-free CRUD, duplicate/delete actions, and default-target or all-enabled-target previews
- **Sequential restorative execution**: preview each playlist step through the existing cancellation and light-state restoration lifecycle, with per-step results, aggregate target outcomes, channel counts, and cleanup warnings
- **Portable definitions**: include playlist order, repeat count, saved-scene references, target mode, and duration metadata in credential-safe backup/restore without exposing bridge credentials

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
- **Scheduled cue observability**: inspect each cue's next server-local run, active state, run count, last outcome/message, and cleanup warnings through the scheduler monitor and `GET /HueSync/SceneSchedules/Status`
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
