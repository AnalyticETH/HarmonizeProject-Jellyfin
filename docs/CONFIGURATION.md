# Configuration reference

[Installation and initial setup](../README.md#initial-setup) | [Administrator API](API.md)

[Stream protocol, color conversion, and evidence](HUE_STREAM_PROTOCOL.md)

## Overview
The plugin synchronizes Hue lights with supported video and audio media through
the Hue Entertainment API. FFmpeg analyzes video frames or bounded audio windows;
video scopes map sampled colors, while audio scopes map low/mid/high energy to
the configured visualizer. Cinema-mode dimming and state restoration are separate
playback-lifecycle settings.

## How It Works
1.  **Playback Detection**: The plugin monitors Jellyfin for supported video and audio playback events.
2.  **Media Analysis**: When playback starts, it uses a bounded FFmpeg pipeline to extract video colors or audio energy in real-time, according to the selected media scope.
3.  **Light Mapping**: It maps video colors or audio-reactive bands to your specific Hue Entertainment Area configuration (Left, Right, Center, etc.).
4.  **DTLS Streaming**: Color updates are streamed securely and instantly to your Hue Bridge via DTLS (Datagram Transport Layer Security).

## Configuration
Go to **Dashboard -> Plugins -> Philips Hue Sync** to configure the plugin.

Each playback lifecycle captures its bridge address, entertainment area, App Key,
Client Key, and effective channel profile when it starts. The same playback keeps
that captured target through pause, resume, and seek. Edits to those fields in
global settings, user mappings, or device routes apply to the next playback;
resuming or seeking the current playback does not retarget it or replace its
captured credentials or channel selection.

Certificate trust is not frozen with the target: current bridge-certificate pins
remain authoritative, and an untrusted or changed certificate still fails closed.
Master and per-user disable controls remain effective for active or paused
playback. They stop the affected synchronization and clean up its captured target;
failed cleanup remains visible and retryable.

State capture resolves each selected channel's entertainment service to its
`renderer_reference` light resource. Repeated segments and shared renderers are
deduplicated. Missing, ambiguous, or mismatched service/renderer references stop
capture before light-state reads or output mutation.

Same-target replacement re-resolves the selected renderers; resume and seek do so
within the captured channel selection. Previously controlled lights keep their
original snapshots; newly resolved renderer lights are captured before activation.
The combined snapshot remains capped at 256 lights, including previously selected
lights that still require final restoration.
Incomplete supplemental capture aborts startup and preserves the old snapshot for
cleanup. Restore-on-pause captures originals even when final-stop restoration is
disabled; failed pause restoration remains an obligation for later cleanup retries.

If concurrent playback cleanup still fails after its bounded retry, the worker
remains visible with its lifecycle ID, cleanup warning, and a Stop control. Retry
Stop after the bridge recovers; the target stays reserved until cleanup succeeds.
Other targets remain independent. A configuration-disable or host-stop request
can also retry the retained cleanup.

Each REST attempt applies the transport timeout (10 seconds in Jellyfin) across
connection setup, headers, and body consumption; bodies remain limited to 1 MiB.
Caller cancellation prevents retries and discovery fallback, while a cloud
request timeout still permits local discovery. Activation, deactivation,
brightness updates, and restoration require an empty Hue `errors` array and a
typed acknowledgement of the requested resource. HTTP 2xx responses with errors,
missing acknowledgements, or malformed bodies count as failures; rejected light
mutations remain failed in aggregate cleanup results.

Live streams resend their last successful colors after five seconds without a
packet (checked once per second), including `KeepLastColors` producer pauses.
No colors are synthesized before the first frame. Eight seconds of known silence
requires area reactivation and DTLS recovery before the next write, including
after an initial blackout or delayed timer. This uses the configured retry budget;
zero retries fails closed instead of writing to the expired session. Stop,
replacement, and cancellation retire the heartbeat with its owning lifecycle.

Entertainment channel IDs are `0..255` and must exist in the selected area's
configuration. Corrected RGB8-to-RGB16 conversion uses the full component range;
unchanged brightness settings produce higher numeric output than the previous
halved conversion. Explicit brightness, tint, and dimming policies remain in place,
and saved settings are not rewritten. See the protocol reference for exact bytes
and the limits of offline interoperability evidence.

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
| **Startup recovery** | If the plugin or Jellyfin service starts while an unpaused video or audio item is already playing, recover the active session at Jellyfin's current position so viewers do not need to stop and restart playback. |
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
| **Saved Scene and Target Lifecycle** | Capture current light color from a default, selected, or all-enabled target set; weighted aggregate capture seeds the editor without exposing credentials. Preview saved scenes on the default target, a selected subset, or all enabled mappings with sequential state restoration and per-target outcomes. Compose up to 20 scenes into playlists with 1-10 repeat passes, saved-order or stable UTC-date-seeded Shuffle playback, optional per-step RGB channel, effect, bounded hold, brightness, effect-speed, fade-in/fade-out, and fade-curve overrides; blank/null values inherit the saved scene and `0` hold inherits its duration. Saved playlists can retain exact playback-device routes as credential-free `userId`/case-sensitive `deviceId` pairs; current mapping credentials and channel profiles are resolved only when the playlist runs, and removed, disabled, incomplete, or ambiguous routes fail closed. A playlist preview validates the complete expanded plan before bridge mutation, then renders every step for each target in one capture/activate/DTLS/deactivate/restore lifecycle. Rename and duplicate scenes/playlists with reference migration, inspect credential-free dependencies, and use atomic bulk duplicate/delete actions that block unsafe referenced changes. Cancel active previews safely; active playback still blocks overlap. |
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
| **Dark Scene Behavior** | When a frame is below the blackout threshold, either **Blackout** lights (legacy default) or **KeepLastColors** to preserve the last streamed colors. Periodic liveness frames continue after the first successful frame. |
| **Color Change Threshold** | Suppress color updates unless a component's high-byte difference exceeds the 0-255 threshold (default: 10), except for periodic liveness frames. Zero disables suppression. Lower values follow subtle changes; higher values reduce network traffic. |
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
Those target edits take effect for the next playback, not a resume or seek of the current playback.
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
server and are not displayed in the browser. Leave those fields blank to keep the stored keys only
when the normalized bridge address is unchanged, or enter replacement keys to rotate them.
Changing the bridge address requires both replacement keys; neither key is copied from the old
target. The mapping list reports credential presence without
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

Audio frequency overrides are validated together with inherited global values. The effective
low, mid, and high centers must remain strictly ordered and within 20-3900 Hz; an invalid partial
override is rejected before the mapping changes. Unrelated invalid legacy rows do not prevent
saving a valid repair to the selected mapping.

Mappings can also override Cinema Mode, its dim level, and pause behavior. Choose **Inherit global
setting** to keep the defaults, or enable/disable cinema mode, set a separate 0-100% dim level, and
choose whether pausing that user's playback keeps the last colors, restores the captured light state,
or dims to that user's effective cinema level.

## Generating Hue Credentials (Manual Fallback)
If the **Link Bridge** button doesn't work for you, you can generate keys manually:
1.  Use the administrator **Bridge Certificate** action (or `GET /HueSync/BridgeCertificate`) to read the SHA-256 fingerprint, verify it against your trusted bridge identity, and explicitly trust it with `POST /HueSync/BridgeCertificate/Trust`. Existing pins are listed in the configuration page; use **Forget** (or `DELETE /HueSync/BridgeCertificate/Trust?ipAddress=...`) when retiring a bridge or replacing its certificate, then explicitly trust the replacement before sending credentials.
2.  Go to `https://<BRIDGE_IP>/debug/clip.html`.
3.  Press the **Link Button** on your Hue Bridge.
4.  Post to `/api` with body: `{"devicetype":"jellyfin_plugin#server", "generateclientkey":true}`.
5.  Copy the `username` (App Key) and `clientkey` (Client Key) from the response; the plugin will now accept them only over the pinned bridge certificate.
