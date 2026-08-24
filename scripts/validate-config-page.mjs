import fs from "node:fs";
import vm from "node:vm";

const file = "Jellyfin.Plugin.Hue/Configuration/configPage.html";
const html = fs.readFileSync(file, "utf8");
const scriptMatch = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/);

if (!scriptMatch) {
    throw new Error(`${file} does not contain the configuration script`);
}

new vm.Script(scriptMatch[1], { filename: file });

const requiredMarkup = [
    'id="cancelConnectionTestBtn"',
    'id="cancelMappingTestConnectionBtn"',
    'id="cancelPreviewBtn"',
    'id="cancelDiagnosticsBtn"',
    'id="exportSupportBundleBtn"',
    'id="playbackMediaFilter"',
    'id="audioSensitivityPercent"',
    'id="audioLowFrequencyHz"',
    'id="audioMidFrequencyHz"',
    'id="audioHighFrequencyHz"',
    'id="audioBandSpreadPercent"',
    'id="audioBeatPulsePercent"',
    'id="audioColorPalette"',
    'id="audioSpatialMode"',
    'id="audioChannelMode"',
    'id="mappingPlaybackMediaFilterOverride"',
    'id="mappingAudioSensitivityPercentOverride"',
    'id="mappingAudioLowFrequencyHzOverride"',
    'id="mappingAudioMidFrequencyHzOverride"',
    'id="mappingAudioHighFrequencyHzOverride"',
    'id="mappingAudioBandSpreadPercentOverride"',
    'id="mappingAudioBeatPulsePercentOverride"',
    'id="mappingAudioColorPaletteOverride"',
    'id="mappingAudioSpatialModeOverride"',
    'id="mappingAudioChannelModeOverride"',
    'value="Left">Left source channel only',
    'value="Right">Right source channel only',
    'value="Audio">Audio only (music)',
    'value="AllMedia">All video and audio',
    'id="runtimePlaybackMediaFilter"',
    'id="runtimeAudioProfile"',
    'id="contrastPercent"',
    'id="colorTemperatureKelvin"',
    'id="spatialOrientation"',
    'id="mappingContrastPercentOverride"',
    'id="mappingColorTemperatureKelvinOverride"',
    'id="mappingSpatialOrientationOverride"',
    'value="Rotate90Clockwise">Rotate 90° clockwise',
    'value="Rotate90Counterclockwise">Rotate 90° counterclockwise',
    'id="diagnosticsAudioCapture"',
    'id="previewEffect"',
    'id="previewEffectSpeed"',
    'id="previewTransitionCurve"',
    'value="EaseInOut">Ease in/out',
    'value="Fire">Fire',
    'value="Ocean">Ocean',
    'value="Lightning">Lightning',
    'value="Starlight">Starlight',
    'id="captureCurrentColorBtn"',
    'id="exportSceneScheduleConflictsBtn"',
    'id="exportSceneScheduleOccurrencesBtn"',
    'id="sceneScheduleReportHorizon"',
    'value="7">Next 7 days',
    'value="31" selected>Next 31 days',
    'value="90">Next 90 days',
    'value="366">Next 366 days',
    'id="sceneScheduleOccurrenceFilter"',
    'id="sceneScheduleHistoryCueFilter"',
    'id="sceneScheduleTimeMode"',
    'value="SolarNoon">Solar noon',
    'value="CivilDawn">Civil dawn',
    'value="CivilDusk">Civil dusk',
    'value="NauticalDawn">Nautical dawn',
    'value="NauticalDusk">Nautical dusk',
    'value="AstronomicalDawn">Astronomical dawn',
    'value="AstronomicalDusk">Astronomical dusk',
    'id="sceneScheduleSolarOffset"',
    'id="sceneScheduleSolarLatitude"',
    'id="sceneScheduleSolarLongitude"',
    'id="sceneScheduleBulkSelect"',
    'id="enableSelectedUserMappingsBtn"',
    'id="disableSelectedUserMappingsBtn"',
    'id="enableSelectedSceneSchedulesBtn"',
    'id="disableSelectedSceneSchedulesBtn"',
    'id="skipSelectedSceneSchedulesBtn"',
    'id="clearSelectedSceneScheduleSkipsBtn"',
    'id="resetSelectedSceneScheduleRunCountsBtn"',
    'id="duplicateSelectedSceneSchedulesBtn"',
    'id="deleteSelectedSceneSchedulesBtn"',
    'id="runSelectedSceneSchedulesBtn"',
    'id="cancelSelectedSceneSchedulesBtn"',
    'id="previewPresetBulkSelect"',
    'id="previewSelectedPreviewPresetsBtn"',
    'id="previewSelectedPreviewPresetsAllBtn"',
    'id="duplicateSelectedPreviewPresetsBtn"',
    'id="deleteSelectedPreviewPresetsBtn"',
    'id="userMappingBulkSelect"',
    'id="deleteSelectedUserMappingsBtn"',
    'id="scenePlaylistBulkSelect"',
    'id="scenePlaylistPlaybackOrder"',
    'id="scenePlaylistItems"',
    'value="Sequential">Saved order',
    'value="Shuffle">Stable daily shuffle',
    'id="scenePlaylistPreviewTarget"',
    'id="scenePlaylistBulkPreviewTarget"',
    'id="previewSelectedScenePlaylistsBtn"',
    'id="previewSelectedScenePlaylistsAllBtn"',
    'id="duplicateSelectedScenePlaylistsBtn"',
    'id="deleteSelectedScenePlaylistsBtn"',
    'id="configurationImportDiff"',
    'id="exportSessionHistoryCsvBtn"',
    'id="sessionHistoryRetentionCount"',
    'id="sceneScheduleHistoryRetentionCount"',
    'id="exportSceneScheduleConflictsCsvBtn"',
    'id="exportSceneScheduleOccurrencesCsvBtn"',
    'id="exportSceneScheduleHistoryCsvBtn"'
];

for (const marker of requiredMarkup) {
    if (!html.includes(marker)) {
        throw new Error(`${file} is missing required markup: ${marker}`);
    }
}

const requiredScript = [
    "setDiagnosticBusy: function",
    "setDiagnosticsBusy: function",
    "exportSupportBundle: function",
    "HueConfigurationPage.cancelDiagnostics(e.target)",
    'url: ApiClient.getUrl("HueSync/Preview/Cancel")',
    'url: ApiClient.getUrl("HueSync/Diagnostics/Cancel")',
    'ApiClient.getUrl("HueSync/Diagnostics/SupportBundle")',
    "PlaybackMediaFilter: page.querySelector('#playbackMediaFilter').value",
    "ContrastPercent: parseNumberOrDefault('#contrastPercent', 100)",
    "ColorTemperatureKelvin: parseNumberOrDefault('#colorTemperatureKelvin', 6500)",
    "SpatialOrientation: page.querySelector('#spatialOrientation').value || 'Normal'",
    "AudioSensitivityPercent: parseNumberOrDefault('#audioSensitivityPercent', 100)",
    "AudioLowFrequencyHz: parseNumberOrDefault('#audioLowFrequencyHz', 90)",
    "AudioMidFrequencyHz: parseNumberOrDefault('#audioMidFrequencyHz', 420)",
    "AudioHighFrequencyHz: parseNumberOrDefault('#audioHighFrequencyHz', 1600)",
    "AudioBandSpreadPercent: parseNumberOrDefault('#audioBandSpreadPercent', 0)",
    "AudioBeatPulsePercent: parseNumberOrDefault('#audioBeatPulsePercent', 0)",
    "AudioColorPalette: page.querySelector('#audioColorPalette').value || 'Spectrum'",
    "AudioSpatialMode: page.querySelector('#audioSpatialMode').value || 'Spatial'",
    "AudioChannelMode: page.querySelector('#audioChannelMode').value || 'Mono'",
    "SessionHistoryRetentionCount: parseNumberOrDefault('#sessionHistoryRetentionCount', 25)",
    "SceneScheduleHistoryRetentionCount: parseNumberOrDefault('#sceneScheduleHistoryRetentionCount', 100)",
    "getConfigValue('PlaybackMediaFilter', 'AllVideo')",
    "getConfigValue('ContrastPercent', 100)",
    "getConfigValue('ColorTemperatureKelvin', 6500)",
    "getConfigValue('SpatialOrientation', 'Normal')",
    "getConfigValue('AudioSensitivityPercent', 100)",
    "getConfigValue('AudioLowFrequencyHz', 90)",
    "getConfigValue('AudioMidFrequencyHz', 420)",
    "getConfigValue('AudioHighFrequencyHz', 1600)",
    "getConfigValue('AudioBandSpreadPercent', 0)",
    "getConfigValue('AudioBeatPulsePercent', 0)",
    "getConfigValue('AudioColorPalette', 'Spectrum')",
    "getConfigValue('AudioSpatialMode', 'Spatial')",
    "getConfigValue('AudioChannelMode', 'Mono')",
    "getConfigValue('SessionHistoryRetentionCount', 25)",
    "getConfigValue('SceneScheduleHistoryRetentionCount', 100)",
    "ConfiguredPlaybackMediaFilter",
    "ActivePlaybackMediaFilter",
    "ActiveAudioSensitivityPercent",
    "ActiveAudioLowFrequencyHz",
    "ActiveAudioMidFrequencyHz",
    "ActiveAudioHighFrequencyHz",
    "ActiveAudioBandSpreadPercent",
    "ActiveAudioBeatPulsePercent",
    "ActiveAudioColorPalette",
    "ActiveAudioSpatialMode",
    "ActiveAudioChannelMode",
    "ActiveContrastPercent",
    "ActiveColorTemperatureKelvin",
    "ActiveSpatialOrientation",
    "AudioCaptureRequired",
    "HueConfigurationPage.formatToolDiagnostics(audioCapture)",
    "PlaybackMediaFilterOverride: readOptionalString('mappingPlaybackMediaFilterOverride')",
    "ContrastPercentOverride: readOptionalNumber('mappingContrastPercentOverride')",
    "ColorTemperatureKelvinOverride: readOptionalNumber('mappingColorTemperatureKelvinOverride')",
    "SpatialOrientationOverride: readOptionalString('mappingSpatialOrientationOverride')",
    "AudioSensitivityPercentOverride: readOptionalNumber('mappingAudioSensitivityPercentOverride')",
    "AudioLowFrequencyHzOverride: readOptionalNumber('mappingAudioLowFrequencyHzOverride')",
    "AudioMidFrequencyHzOverride: readOptionalNumber('mappingAudioMidFrequencyHzOverride')",
    "AudioHighFrequencyHzOverride: readOptionalNumber('mappingAudioHighFrequencyHzOverride')",
    "AudioBandSpreadPercentOverride: readOptionalNumber('mappingAudioBandSpreadPercentOverride')",
    "AudioBeatPulsePercentOverride: readOptionalNumber('mappingAudioBeatPulsePercentOverride')",
    "AudioColorPaletteOverride: readOptionalString('mappingAudioColorPaletteOverride')",
    "AudioSpatialModeOverride: readOptionalString('mappingAudioSpatialModeOverride')",
    "AudioChannelModeOverride: readOptionalString('mappingAudioChannelModeOverride')",
    "getMappingValue('PlaybackMediaFilterOverride', 'playbackMediaFilterOverride')",
    "getMappingValue('ContrastPercentOverride', 'contrastPercentOverride')",
    "getMappingValue('ColorTemperatureKelvinOverride', 'colorTemperatureKelvinOverride')",
    "getMappingValue('SpatialOrientationOverride', 'spatialOrientationOverride')",
    "getMappingValue('AudioLowFrequencyHzOverride', 'audioLowFrequencyHzOverride')",
    "getMappingValue('AudioMidFrequencyHzOverride', 'audioMidFrequencyHzOverride')",
    "getMappingValue('AudioHighFrequencyHzOverride', 'audioHighFrequencyHzOverride')",
    "getMappingValue('AudioBandSpreadPercentOverride', 'audioBandSpreadPercentOverride')",
    "getMappingValue('AudioBeatPulsePercentOverride', 'audioBeatPulsePercentOverride')",
    "getMappingValue('AudioColorPaletteOverride', 'audioColorPaletteOverride')",
    "getMappingValue('AudioSpatialModeOverride', 'audioSpatialModeOverride')",
    "getMappingValue('AudioChannelModeOverride', 'audioChannelModeOverride')",
    "HueConfigurationPage.cancelPreview(e.target)",
    '["Solid", "Pulse", "Rainbow", "Candle", "Temperature", "Aurora", "Fire", "Ocean", "Lightning", "Starlight"]',
    'effect: effect || "Solid"',
    'effectSpeedPercent: effectSpeedPercent || 100',
    'transitionCurve: transitionCurve || "Linear"',
    'effectSpeedPercent: values.effectSpeedPercent',
    'transitionCurve: values.transitionCurve',
    'var stepEffects = playlist.stepEffects',
    'page._hueScenePlaylistEffects = stepEffects',
    'page._hueScenePlaylistEffects = []',
    'page._hueScenePlaylistEffects.push(null)',
    'effects[index] = effects[target]',
    'page._hueScenePlaylistEffects.splice(index, 1)',
    'stepEffects: effects',
    'Effect override for ',
    'var stepEffectSpeeds = playlist.stepEffectSpeedPercent',
    'page._hueScenePlaylistEffectSpeeds = stepEffectSpeeds',
    'page._hueScenePlaylistEffectSpeeds = []',
    'page._hueScenePlaylistEffectSpeeds.push(null)',
    'effectSpeeds[index] = effectSpeeds[target]',
    'page._hueScenePlaylistEffectSpeeds.splice(index, 1)',
    'stepEffectSpeedPercent: effectSpeeds',
    'Effect speed override for ',
    "effect: values.effect",
    "fetchCurrentLightColor: function",
    "HueSync/Preview/CaptureCurrentColor",
    "fetchCurrentLightColors: function",
    "HueSync/Preview/CaptureCurrentColors",
    "captureCurrentColor: function",
    "page._hueCaptureRequest",
    "getColorPreviewTargetSelection(page)",
    "populateSceneScheduleCueFilters: function",
    "getSceneScheduleReportHorizon: function",
    "updateSceneScheduleTimeMode: function",
    "['SolarNoon', 'Sunrise', 'Sunset', 'CivilDawn', 'CivilDusk', 'NauticalDawn', 'NauticalDusk', 'AstronomicalDawn', 'AstronomicalDusk']",
    "timeMode: timeMode",
    "solarOffsetMinutes: solarOffsetMinutes",
    "solarLatitude: solarLatitude",
    "solarLongitude: solarLongitude",
    "downloadJsonDocument: function",
    "exportSceneScheduleConflicts: function",
    "exportSceneScheduleOccurrences: function",
    "url += \"&scheduleId=\"",
    "days=\" + String(horizonDays)",
    "occurrenceQuery.horizonDays",
    "calendarUrl += \"&scheduleId=\"",
    "historyUrl += \"&scheduleId=\"",
    "exportUrl += \"&scheduleId=\"",
    "setSceneSchedulesEnabledBulk: function",
    "HueSync/SceneSchedules/BulkEnabled",
    "setSceneSchedulesSkipNextBulk: function",
    "HueSync/SceneSchedules/BulkSkipNext",
    "resetSceneSchedulesRunCountsBulk: function",
    "HueSync/SceneSchedules/BulkResetRunCount",
    "duplicateSceneSchedulesBulk: function",
    "HueSync/SceneSchedules/BulkDuplicate",
    "deleteSceneSchedulesBulk: function",
    "HueSync/SceneSchedules/BulkDelete",
    "runSceneSchedulesBulk: function",
    "HueSync/SceneSchedules/BulkRun",
    "cancelSceneSchedulesBulk: function",
    "HueSync/SceneSchedules/BulkCancel",
    "HueConfigurationPage.cancelSceneSchedulesBulk(e.target)",
    "deleteScenePlaylistsBulk: function",
    "duplicateScenePlaylistsBulk: function",
    "HueSync/ScenePlaylists/BulkDuplicate",
    "HueSync/ScenePlaylists/BulkDelete",
    "getSelectedScenePlaylistBulkIds: function",
    "updateScenePlaylistBulkButtons: function",
    "normalizeScenePlaylistPreviewTargetSelection: function",
    "_hueScenePlaylistDurations",
    "_hueScenePlaylistBrightness",
    "_hueScenePlaylistTransitions",
    "_hueScenePlaylistTransitionOuts",
    "_hueScenePlaylistTransitionCurves",
    "stepDurationSeconds: durations",
    "stepRed: reds",
    "stepGreen: greens",
    "stepBlue: blues",
    "stepBrightnessPercent: brightnesses",
    "stepTransitionSeconds: transitions",
    "stepTransitionOutSeconds: transitionOuts",
    "stepTransitionCurves: transitionCurves",
    "Fade curve (inherit):",
    "var stepReds = playlist.stepRed",
    "var stepGreens = playlist.stepGreen",
    "var stepBlues = playlist.stepBlue",
    "page._hueScenePlaylistReds",
    "page._hueScenePlaylistGreens",
    "page._hueScenePlaylistBlues",
    "channel + ' color channel override for '",
    'readStatusField(occurrence, "PlaylistSteps", [])',
    'readStatusField(step, "StartOffsetSeconds", 0)',
    "getScenePlaylistPreviewTargetSelection: function",
    "normalizeScenePlaylistBulkPreviewTargetSelection: function",
    "getScenePlaylistBulkPreviewTargetSelection: function",
    "playbackOrder: playbackOrder",
    "Playback order: stable daily shuffle.",
    "previewScenePlaylistsBulk: function",
    "HueSync/ScenePlaylists/BulkPreview",
    "deleteColorPresetsBulk: function",
    "duplicateColorPresetsBulk: function",
    "previewColorPresetsBulk: function",
    "HueSync/ColorPresets/BulkPreview",
    "HueSync/ColorPresets/BulkDuplicate",
    "HueSync/ColorPresets/BulkDelete",
    "getSelectedColorPresetBulkNames: function",
    "updateColorPresetBulkButtons: function",
    "deleteUserMappingsBulk: function",
    "HueSync/UserMappings/BulkDelete",
    "setUserMappingsEnabledBulk: function",
    "HueSync/UserMappings/BulkEnabled",
    "getSelectedUserMappingBulkIds: function",
    "updateUserMappingBulkButtons: function",
    "renderConfigurationImportDiff: function",
    "Import change summary (credential-safe):",
    "getSelectedSceneScheduleBulkIds: function",
    "updateSceneScheduleBulkButtons: function",
    "downloadCsvDocument: function",
    "downloadCsvFromApi: function",
    "exportSceneScheduleConflictsCsv: function",
    "exportSceneScheduleOccurrencesCsv: function",
    "exportSceneScheduleHistoryCsv: function",
    "exportSessionHistoryCsv: function",
    "SceneSchedules/Conflicts/ExportCsv",
    "SceneSchedules/Occurrences/ExportCsv",
    "SceneSchedules/History/ExportCsv",
    "History/ExportCsv"
];

for (const marker of requiredScript) {
    if (!scriptMatch[1].includes(marker)) {
        throw new Error(`${file} is missing required behavior: ${marker}`);
    }
}

const playlistStepSpeedTelemetryReads = html.match(/readStatusField\(step, "EffectSpeedPercent", 100\)/g) || [];
if (playlistStepSpeedTelemetryReads.length < 3) {
    throw new Error(`${file} must render per-step effect speed in playlist preview, occurrence, and history status`);
}

const playlistStepEffectTelemetryReads = html.match(/readStatusField\(step, "Effect", "Solid"\)/g) || [];
if (playlistStepEffectTelemetryReads.length < 3) {
    throw new Error(`${file} must render the effective per-step effect in playlist preview, occurrence, and history status`);
}

for (const channel of ["Red", "Green", "Blue"]) {
    const telemetryReads = html.match(new RegExp(`readStatusField\\(step, "${channel}", 0\\)`, "g")) || [];
    if (telemetryReads.length < 2) {
        throw new Error(`${file} must render per-step ${channel.toLowerCase()} channel in occurrence and history status`);
    }
}

{
    const start = scriptMatch[1].indexOf("renderScenePlaylistItems: function");
    const end = scriptMatch[1].indexOf("updateScenePlaylistButtons: function", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const effect of ["Solid", "Pulse", "Rainbow", "Candle", "Temperature", "Aurora", "Fire", "Ocean", "Lightning", "Starlight"]) {
        if (!functionBody.includes(`['${effect}', '${effect}']`)) {
            throw new Error(`${file} playlist step effect selector is missing canonical effect: ${effect}`);
        }
    }
    if (!functionBody.includes("['', 'Inherited scene']") ||
        !functionBody.includes("page._hueScenePlaylistEffects[index] = effectSelect.value || null")) {
        throw new Error(`${file} playlist step effect selector is missing null-as-inherit wiring`);
    }
}

for (const functionName of ["testDefaultConnection", "testMappingConnection"]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("page._huePreviewRequest = request;") ||
        !functionBody.includes("setDiagnosticBusy(page, true)") ||
        !functionBody.includes("setDiagnosticBusy(page, false)")) {
        throw new Error(`${file} ${functionName} is missing cancellable diagnostic lifecycle wiring`);
    }
}

for (const functionName of ["loadEnvironmentDiagnostics", "loadTargetDiagnostics"]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("ApiClient.ajax") ||
        !functionBody.includes("page._hue") ||
        !functionBody.includes("setDiagnosticsBusy(page)")) {
        throw new Error(`${file} ${functionName} is missing cancellable diagnostics lifecycle wiring`);
    }
}

for (const functionName of ["exportSceneScheduleConflicts", "exportSceneScheduleOccurrences"]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("ApiClient.getJSON") ||
        !functionBody.includes("downloadJsonDocument") ||
        !functionBody.includes("scheduleQuery")) {
        throw new Error(`${file} ${functionName} is missing credential-free schedule report export wiring`);
    }
}

{
    const functionName = "exportSupportBundle";
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("Diagnostics/SupportBundle") ||
        !functionBody.includes("downloadJsonDocument") ||
        !functionBody.includes("_hueSupportBundleRequest")) {
        throw new Error(`${file} ${functionName} is missing support bundle export wiring`);
    }
}

console.log(`${file}: JavaScript syntax and cancellable diagnostic contracts passed`);
