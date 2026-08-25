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
    'id="registerBtn"',
    'id="mappingLinkBridgeBtn"',
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
    'id="runtimePlaybackProgress"',
    'id="runtimePlaybackObservedAt"',
    'id="runtimeSyncStartedAt"',
    'id="runtimeStatusMessage" class="fieldDescription" role="status" aria-live="polite"',
    'id="runtimeStatusError" role="alert" aria-live="assertive"',
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
    'id="captureColorTarget"',
    'id="clearStoredCredentials"',
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
    'timeZoneIanaId',
    'data-time-zone-iana-id',
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
    'id="sceneScheduleRed"',
    'id="sceneScheduleGreen"',
    'id="sceneScheduleBlue"',
    'id="sceneScheduleBulkSelect"',
    'id="sceneScheduleTarget"',
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
    'id="reconcileUserMappingsBtn"',
    'id="resolveDuplicateUserMappingsBtn"',
    'id="userMappingReconcileStatus"',
    'id="userMappingDuplicateResolutionControls"',
    'id="targetDiagnosticsDuplicateMappings"',
    'id="scenePlaylistBulkSelect"',
    'id="scenePlaylistPlaybackOrder"',
    'id="renameScenePlaylistBtn"',
    'id="scenePlaylistItems"',
    'value="Sequential">Saved order',
    'value="Shuffle">Stable daily shuffle',
    'id="scenePlaylistPreviewTarget"',
    'id="scenePlaylistBulkPreviewTarget"',
    'id="previewTargetMetadataStatus"',
    'id="applyConfigurationImportBtn" disabled',
    'id="previewSelectedScenePlaylistsBtn"',
    'id="previewSelectedScenePlaylistsAllBtn"',
    'id="duplicateSelectedScenePlaylistsBtn"',
    'id="deleteSelectedScenePlaylistsBtn"',
    'id="configurationImportDiff"',
    'id="configurationImportCredentialFields"',
    'id="exportSessionHistoryCsvBtn"',
    'id="sessionHistoryRetentionCount"',
    'id="sceneScheduleHistoryRetentionCount"',
    'id="sceneScheduleHistoryStatus" class="fieldDescription" role="status" aria-live="polite"',
    'id="sceneScheduleColorContainer"',
    'id="sceneScheduleBrightnessContainer"',
    'id="sceneScheduleBrightness"',
    'id="exportSceneScheduleConflictsCsvBtn"',
    'id="exportSceneScheduleOccurrencesCsvBtn"',
    'id="exportSceneScheduleHistoryCsvBtn"',
    'id="mappingDiscoverDevicesBtn"',
    'id="mappingDeviceRouteSelect"',
    'id="mappingDeviceDiscoveryStatus"',
    'id="mappingDeviceRouteEditor"',
    'id="mappingDeviceRouteId"',
    'id="mappingDeviceRouteId" type="text" is="emby-input" aria-label="Device ID (case-sensitive)"',
    'id="mappingDeviceRouteName" type="text" is="emby-input" aria-label="Device name (optional)"',
    'id="mappingDeviceRouteBridge"',
    'id="mappingDeviceRouteBridge" type="text" is="emby-input" aria-label="Route bridge address"',
    'id="mappingDeviceRouteAppKey"',
    'id="mappingDeviceRouteAppKey" type="password" is="emby-input" aria-label="Route App Key (new/replacement only)"',
    'id="mappingDeviceRouteClientKey"',
    'id="mappingDeviceRouteClientKey" type="password" is="emby-input" aria-label="Route Client Key (new/replacement only)"',
    'id="mappingDeviceRouteAreaSelect"',
    'id="mappingDeviceRouteAreaSelect" is="emby-select" aria-label="Route entertainment area"',
    'id="mappingDeviceRouteAreaId"',
    'id="mappingDeviceRouteAreaId" type="text" is="emby-input" aria-label="Entertainment area ID"',
    'id="mappingDeviceRouteChannels"',
    'id="mappingDeviceRouteChannels" type="text" is="emby-input" aria-label="Channel IDs (optional, comma separated)"',
    'id="mappingAddDeviceRouteBtn"',
    'id="mappingRemoveDeviceRouteBtn"',
    'id="entertainmentAreaManual" name="entertainmentAreaManual" type="text" is="emby-input" aria-label="Manual entertainment area ID"',
    'id="sceneScheduleDayOfWeek" is="emby-select" aria-label="Monthly weekday day of week"',
    'id="mappingAreaManual" type="text" is="emby-input" aria-label="Manual entertainment area ID"',
    'id="mappingDeviceRouteSelect" is="emby-select" aria-label="Playback device route"'
];

for (const marker of requiredMarkup) {
    if (!html.includes(marker)) {
        throw new Error(`${file} is missing required markup: ${marker}`);
    }
}

const requiredScript = [
    "function escapeAttribute(str)",
    "normalizeJellyfinUserId: function",
    "areSameJellyfinUserId: function",
    "HueConfigurationPage.areSameJellyfinUserId(candidateRoute.userId, route.userId)",
    "userId: HueConfigurationPage.normalizeJellyfinUserId(route.userId)",
    "data-userid=\"' + escapeAttribute(userId)",
    "setDiagnosticBusy: function",
    "setDiagnosticsBusy: function",
    "cancelButton.disabled = !!page._hueDiagnosticsCancellationRequest",
    "exportSupportBundle: function",
    "HueConfigurationPage.cancelDiagnostics(e.target)",
    "targetOption.disabled = !enabled",
    "option.disabled = !enabled",
    "option.value === value && !option.disabled",
    "option.selected = !option.disabled && targetDefinition.previousValues.indexOf(option.value) >= 0",
    'url: ApiClient.getUrl("HueSync/Preview/Cancel")',
    'url: ApiClient.getUrl("HueSync/Diagnostics/Cancel")',
    'ApiClient.getUrl("HueSync/Diagnostics/SupportBundle")',
    'data-hue-import-scope="device"',
    'data-hue-import-device-index',
    'DeviceTargetCredentials',
    'device-route migration keys',
    'scope === "UserDevice"',
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
    "PlaybackPositionSeconds",
    "PlaybackDurationSeconds",
    "PlaybackProgressPercent",
    "PlaybackIsPaused",
    "PlaybackObservedAtUtc",
    "SyncStartedAtUtc",
    "formatRuntimeTimestamp: function",
    "ensurePageLifecycle: function",
    "beginPageLifecycle: function",
    "isPageLifecycleCurrent: function",
    "isPageLifecycleRequestCurrent: function",
    "abortPageLifecycleRequest: function",
    "invalidatePageLifecycle: function",
    "getPageLifecycleRequest: function",
    "page._huePageGeneration",
    "page._huePageActive = false",
    "page._huePageRequests",
    "typeof AbortController === 'function'",
    "controller.abort()",
    "record.request.abort()",
    "HueConfigurationPage.beginPageLifecycle(e.target)",
    "HueConfigurationPage.invalidatePageLifecycle(e.target)",
    'aria-label="Replacement App Key for global target"',
    'aria-label="Replacement Client Key for global target"',
    "aria-label=\"Replacement App Key for mapping ' + (index + 1)",
    "aria-label=\"Replacement Client Key for mapping ' + (index + 1)",
    "aria-label=\"Replacement Device App Key for mapping ' + (index + 1)",
    "aria-label=\"Replacement Device Client Key for mapping ' + (index + 1)",
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
    "reconcileUserMappings: function",
    "renderDuplicateResolutionControls: function",
    "HueSync/UserMappings/ResolveDuplicates",
    "retainMappingId",
    "removeMappingIds",
    "expectedReportVersion",
    "DuplicateMappingGroups",
    "targetDiagnosticsDuplicateMappings",
    "Duplicate user mappings are blocked from bridge validation",
    'ApiClient.getJSON(ApiClient.getUrl("HueSync/UserMappings/Reconcile"))',
    'url: ApiClient.getUrl("HueSync/UserMappings/Reconcile")',
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
    "HueConfigurationPage.stopRuntimeStatusPolling(e.target)",
    "var activeCancelButton = page.querySelector('#cancelPreviewBtn')",
    "ClearStoredCredentials: !!(clearCredentialsCheckbox && clearCredentialsCheckbox.checked)",
    "Clear Stored Credentials",
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
    "targetDeviceId: targetSelection.targetDeviceId || \"\"",
    "HueSync/Preview/CaptureCurrentColor",
    "fetchCurrentLightColors: function",
    "targetRoutes: Array.isArray(targetSelection.targetRoutes) ? targetSelection.targetRoutes : []",
    "HueSync/Preview/CaptureCurrentColors",
    "captureCurrentColor: function",
    "initializePreviewTargetMetadata: function",
    "hasPreviewTargetMetadata: function",
    "setPreviewTargetMetadataState: function",
    "markPreviewTargetMetadataUnavailable: function",
    "updatePreviewTargetMetadataControls: function",
    "requirePreviewTargetMetadata: function",
    "page._huePreviewTargetMetadataReady",
    "initializeSceneScheduleMetadata: function",
    "hasSceneScheduleMetadata: function",
    "setSceneScheduleMetadataState: function",
    "isSceneScheduleMappingMetadataValid: function",
    "isConfiguredDeviceRouteReady: function",
    "markSceneScheduleMetadataUnavailable: function",
    "updateSceneScheduleMetadataControls: function",
    "requireSceneScheduleMetadata: function",
    "page._hueSceneScheduleMetadataReady",
    "state.schedules && state.colorPresets && state.scenePlaylists && state.mappings",
    "state.colorPresets && state.scenePlaylists",
    "Preview targets unavailable; reload this page.",
    "if (!HueConfigurationPage.requirePreviewTargetMetadata(page)) return;",
    "if (!HueConfigurationPage.requireSceneScheduleMetadata(page)) return;",
    "targetMetadataReady",
    "fetchColorPreview: function",
    "deviceId: multiTarget ? \"\" : (deviceId || \"\")",
    "target.deviceId",
    "discoveryUrl += \"?userId=\" + encodeURIComponent(userId)",
    "upsertMappingDeviceRoute: function",
    "removeMappingDeviceRoute: function",
    "loadMappingDeviceRouteAreas: function",
    "getMappingDeviceRouteTarget: function",
    "getMappingTargetFingerprint: function",
    "isPageLifecycleTargetRequestCurrent: function",
    "cancelPageLifecycleRequest: function",
    "targetFingerprint",
    "'mappingAreas'",
    "'mappingChannels'",
    "'mappingDeviceRouteAreas'",
    "'mappingPlaybackDevices'",
    "_hueDeviceRouteCredentials",
    "delete route.HueAppKey",
    "mappingDeviceRouteAreaSelect",
    "var selectedTargetRoutes = Array.isArray(targetSelection.targetRoutes) ? targetSelection.targetRoutes : []",
    "payload.targetRoutes = selectedTargetRoutes",
    "fetchSavedColorPreview: function",
    "normalizeSavedPresetTargetSelection: function",
    "normalizeColorPreviewTargetSelection: function",
    "targetRoutes: targetRoutes",
    "includeDeviceRoutes: true",
    "page._hueCaptureRequest",
    "getColorPreviewTargetSelection(page)",
    "getCurrentLightCaptureTargetSelection: function",
    "encodeCurrentLightDeviceTarget: function",
    "DeviceTargets",
    "populateSceneScheduleCueFilters: function",
    "page._hueSceneScheduleMappings = mappings",
    "normalizeSceneScheduleTargetSelection: function",
    "isSceneScheduleDeviceRouteAvailable: function",
    "getSceneScheduleTargetSelection: function",
    "get('TargetRoutes', 'targetRoutes', [])",
    "targetRoutes: targetSelection.targetRoutes",
    "encodeCurrentLightDeviceTarget(userId, deviceId)",
    "This cue references an unavailable or disabled device route.",
    "getSceneScheduleReportHorizon: function",
    "updateSceneScheduleTimeMode: function",
    "['SolarNoon', 'Sunrise', 'Sunset', 'CivilDawn', 'CivilDusk', 'NauticalDawn', 'NauticalDusk', 'AstronomicalDawn', 'AstronomicalDusk']",
    "timeMode: timeMode",
    "solarOffsetMinutes: solarOffsetMinutes",
    "solarLatitude: solarLatitude",
    "solarLongitude: solarLongitude",
    "red: red",
    "green: green",
    "blue: blue",
    "brightnessPercent: brightness",
    "get('BrightnessPercent', 'brightnessPercent', '')",
    "var brightness = page.querySelector('#sceneScheduleBrightness');",
    "brightness.disabled = !!isPlaylist;",
    "if (isPlaylist) brightness.value = '';",
    "Number.isInteger(brightness)",
    'readStatusField(schedule, "BrightnessPercent", null)',
    'readStatusField(occurrence, "BrightnessPercent", null)',
    'readStatusField(occurrence, "TargetAllEnabledMappings", false)',
    'readStatusField(occurrence, "TargetUserIds", [])',
    'readStatusField(occurrence, "TargetRoutes", [])',
    'readStatusField(occurrence, "IncludeDefaultTarget", false)',
    'occurrenceTargetDetails.push("All enabled targets")',
    'occurrenceTargetDetails.push("Mappings: " + occurrenceTargetUserIds.join(", "))',
    'occurrenceTargetDetails.push("Routes: " + occurrenceTargetRoutes.join(", "))',
    'var occurrenceTargetSummary = occurrenceTargetDetails.length ? occurrenceTargetDetails.join(" · ") : occurrenceTargetLabel',
    'addCell(occurrenceTargetSummary)',
    'readStatusField(run, "BrightnessPercent", null)',
    'readStatusField(run, "TargetAllEnabledMappings", false)',
    'readStatusField(run, "TargetUserIds", [])',
    'readStatusField(run, "TargetRoutes", [])',
    'readStatusField(run, "IncludeDefaultTarget", false)',
    'targetDetails.push("All enabled targets")',
    'targetDetails.push("Mappings: " + targetUserIds.join(", "))',
    'targetDetails.push("Routes: " + targetRoutes.join(", "))',
    'var targetSummary = targetDetails.length ? targetDetails.join(" · ") : targetLabel',
    'addCell(targetSummary)',
    "formatSceneScheduleTargetResults: function",
    'readStatusField(target, "TargetLabel", "Target")',
    'readStatusField(target, "Succeeded", false)',
    'readStatusField(target, "Skipped", false)',
    'readStatusField(target, "CleanupWarning", "")',
    'readStatusField(schedule, "LastTargetResults", [])',
    'readStatusField(run, "TargetResults", [])',
    "lastTargetOutcomeText",
    "targetOutcomeText",
    "downloadJsonDocument: function",
    "exportSceneScheduleConflicts: function",
    "sceneScheduleConflictsExport",
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
    "normalizeScenePlaylistTargetOverrideSelection: function",
    "var targetRoutes = saved || all ? null : selected",
    "fetchScenePlaylistPreview: function",
    "payload.targetRoutes = Array.isArray(targetSelection.targetRoutes) ? targetSelection.targetRoutes : []",
    "playbackOrder: playbackOrder",
    "Playback order: stable daily shuffle.",
    "previewScenePlaylistsBulk: function",
    "HueSync/ScenePlaylists/BulkPreview",
    "HueConfigurationPage.previewMessage(result, true)",
    "registerBridge: function",
    "registerMappingBridge: function",
    "Registration returned without both required credentials.",
    "Registration failed. Verify the bridge address and link-button prompt, then try again.",
    "renameScenePlaylist: function",
    'url: ApiClient.getUrl("HueSync/ScenePlaylists/" + encodeURIComponent(name) + "/Rename")',
    "deleteColorPresetsBulk: function",
    "duplicateColorPresetsBulk: function",
    "previewColorPresetsBulk: function",
    "HueSync/ColorPresets/BulkPreview",
    "HueConfigurationPage.previewMessage(preview, true)",
    "HueSync/ColorPresets/BulkDuplicate",
    "HueSync/ColorPresets/BulkDelete",
    "getSelectedColorPresetBulkNames: function",
    "updateColorPresetBulkButtons: function",
    "deleteUserMappingsBulk: function",
    "HueSync/UserMappings/BulkDelete",
    "getSelectedUserMappingBulkMappingIds: function",
    "mappingIds: mappingIds",
    "?mappingId=\" + encodeURIComponent(mappingId)",
    "selection.syncEnabled = syncEnabled",
    "data: JSON.stringify(selection)",
    "inspectUserMappingDependencies(this.closest(\\'.page\\'), this.dataset.userid, this.dataset.mappingid)",
    "deleteUserMapping(this.closest(\\'.page\\'), this.dataset.userid, this.dataset.mappingid)",
    "cleanupStaleUserMappings: function",
    "HueSync/UserMappings/Cleanup",
    "cleanupStaleUserMappingsBtn",
    "mappingEditingMappingId",
    "data-mappingid",
    "MappingId: HueConfigurationPage.mappingEditingMappingId",
    "setUserMappingsEnabledBulk: function",
    "HueSync/UserMappings/BulkEnabled",
    "getSelectedUserMappingBulkIds: function",
    "updateUserMappingBulkButtons: function",
    "renderConfigurationImportDiff: function",
    "Import change summary (credential-safe):",
    "setConfigurationImportValidated: function",
    "invalidateConfigurationImport: function",
    "HueConfigurationPage.setConfigurationImportValidated(page, false)",
    "page._hueImportValidationGeneration",
    "input.addEventListener('input', function ()",
    "if (!page._hueImportValidated)",
    "Validate Import successfully before importing.",
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
    "History/ExportCsv",
    'ApiClient.getUrl("HueSync/PlaybackDevices")',
    'deviceId: deviceId || ""',
    'discoverMappingDevices: function',
    'canUseStoredDeviceRouteCredentials: function',
    'Selected route " + target.deviceId'
];

for (const marker of requiredScript) {
    if (!scriptMatch[1].includes(marker)) {
        throw new Error(`${file} is missing required behavior: ${marker}`);
    }
}

for (const [functionName, markers] of [
    ["loadMappingAreas", [
        "var page",
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'mappingAreas')",
        "getPageLifecycleRequest",
        "isPageLifecycleTargetRequestCurrent"
    ]],
    ["loadMappingDeviceRouteAreas", [
        "var page",
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'mappingDeviceRouteAreas')",
        "getMappingDeviceRouteTarget",
        "getPageLifecycleRequest",
        "isPageLifecycleTargetRequestCurrent"
    ]],
    ["discoverMappingDevices", [
        "var page",
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'mappingPlaybackDevices')",
        "getPageLifecycleRequest",
        "isPageLifecycleTargetRequestCurrent",
        "HueConfigurationPage.refreshMappingDeviceRoutes()"
    ]],
    ["loadChannelIds", [
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, requestKey)",
        "getPageLifecycleRequest",
        "isPageLifecycleTargetRequestCurrent",
        "channelInput.value = channelIds.join(', ')",
        "if (!isCurrent()) return;"
    ]],
    ["loadEntertainmentAreas", [
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'entertainmentAreas')",
        "isPageLifecycleCurrent(page, pageGeneration)",
        "getPageLifecycleRequest",
        "isPageLifecycleTargetRequestCurrent",
        "type: 'POST'",
        "HueSync/EntertainmentAreas",
        "if (!isCurrent()) return;"
    ]]
]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of markers) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} is missing target-scoped stale-request protection: ${marker}`);
        }
    }
}

{
    const start = scriptMatch[1].indexOf("loadEntertainmentAreas: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    const staleGuards = functionBody.match(/if \(!isCurrent\(\)\) return;/g) || [];
    if (staleGuards.length < 3 ||
        functionBody.indexOf("selectEl.innerHTML = ''") < functionBody.indexOf("if (!isCurrent()) return;") ||
        functionBody.indexOf("statusEl.textContent = \"Unable to load areas:") < functionBody.indexOf("if (!isCurrent()) return;")) {
        throw new Error(`${file} loadEntertainmentAreas must guard area-list writes and terminal callbacks against stale targets`);
    }
}

{
    const start = scriptMatch[1].indexOf("loadChannelIds: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    const staleGuards = functionBody.match(/if \(!isCurrent\(\)\) return;/g) || [];
    const writeIndex = functionBody.indexOf("channelInput.value = channelIds.join(', ')");
    const firstGuardIndex = functionBody.indexOf("if (!isCurrent()) return;");
    if (staleGuards.length < 3 || firstGuardIndex < 0 || writeIndex < 0 || firstGuardIndex > writeIndex) {
        throw new Error(`${file} loadChannelIds must guard channel writes and terminal callbacks against stale targets`);
    }
}

for (const functionName of [
    "loadConfiguration",
    "loadUsers",
    "loadUserMappings",
    "loadColorPresets",
    "loadScenePlaylists",
    "loadSceneSchedules",
    "loadSessionHistory",
    "loadEnvironmentDiagnostics",
    "loadTargetDiagnostics",
    "exportSupportBundle"
]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var pageGeneration",
        "getPageLifecycleRequest",
        "isPageLifecycleRequestCurrent(page, pageGeneration"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} is missing page-lifecycle stale-write protection: ${marker}`);
        }
    }
}

for (const [functionName, requestKey, loadingProperty, queryMarker] of [
    ["loadSceneScheduleRuntimeStatus", "sceneScheduleRuntimeStatus", "_hueSceneScheduleRuntimeStatusLoading", ""],
    ["loadSceneScheduleConflicts", "sceneScheduleConflicts", "_hueSceneScheduleConflictsLoading", "getSceneScheduleConflictQuery(page)"],
    ["loadSceneScheduleOccurrences", "sceneScheduleOccurrences", "_hueSceneScheduleOccurrencesLoading", "getSceneScheduleOccurrenceQuery(page)"],
    ["loadSceneScheduleHistory", "sceneScheduleHistory", "_hueSceneScheduleHistoryLoading", "historyUrl"]
]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var pageGeneration",
        `cancelPageLifecycleRequest(page, '${requestKey}')`,
        "isPageLifecycleCurrent(page, pageGeneration)",
        "getPageLifecycleRequest",
        "isPageLifecycleRequestCurrent(page, pageGeneration",
        `${loadingProperty} = true`,
        `${loadingProperty} = false`,
        "if (!isCurrent()) return;"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} is missing scheduled-cue lifecycle protection: ${marker}`);
        }
    }
    if (queryMarker && !functionBody.includes(queryMarker)) {
        throw new Error(`${file} ${functionName} must preserve its report filter scope`);
    }
    const staleGuards = functionBody.match(/if \(!isCurrent\(\)\) return;/g) || [];
    if (staleGuards.length < 3) {
        throw new Error(`${file} ${functionName} must guard success, catch, and finally callbacks`);
    }
}

{
    const start = scriptMatch[1].indexOf("exportSceneScheduleConflicts: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'sceneScheduleConflictsExport')",
        "getSceneScheduleConflictQuery(page)",
        "getPageLifecycleRequest",
        "isPageLifecycleRequestCurrent(page, pageGeneration, request)",
        "currentQuery.url === scheduleQuery.url",
        "if (!isCurrent()) return;",
        "downloadJsonDocument(report, \"jellyfin-hue-scene-schedule-conflicts.json\")",
        "if (!HueConfigurationPage.isPageLifecycleRequestCurrent(page, pageGeneration, request)) return;"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} exportSceneScheduleConflicts is missing query-scoped lifecycle protection: ${marker}`);
        }
    }
    const staleGuards = functionBody.match(/if \(!isCurrent\(\)/g) || [];
    if (staleGuards.length < 3) {
        throw new Error(`${file} exportSceneScheduleConflicts must guard success, catch, and finally callbacks`);
    }
}

{
    const start = scriptMatch[1].indexOf("invalidatePageLifecycle: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "page._huePageGeneration += 1",
        "page._huePageActive = false",
        "page._huePageRequests = {}",
        "abortPageLifecycleRequest(requests[key])",
        "page._hueSessionHistoryLoading = false",
        "page._hueSceneScheduleRuntimeStatusLoading = false",
        "page._hueSceneScheduleConflictsLoading = false",
        "page._hueSceneScheduleConflictsExporting = false",
        "page._hueSceneScheduleOccurrencesLoading = false",
        "page._hueSceneScheduleHistoryLoading = false",
        "page._hueDiagnosticsLoading = false",
        "page._hueTargetDiagnosticsLoading = false",
        "page._hueDiagnosticsRequest = null",
        "page._hueTargetDiagnosticsRequest = null",
        "page._hueSupportBundleRequest = null"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} invalidatePageLifecycle is missing cleanup contract: ${marker}`);
        }
    }
}

{
    const runtimeStatusStart = scriptMatch[1].indexOf("loadRuntimeStatus: function");
    const runtimeStatusEnd = scriptMatch[1].indexOf("\n                },", runtimeStatusStart);
    const runtimeStatusBody = runtimeStatusStart >= 0 && runtimeStatusEnd > runtimeStatusStart
        ? scriptMatch[1].slice(runtimeStatusStart, runtimeStatusEnd)
        : "";
    const resetStatusStart = scriptMatch[1].indexOf("resetRuntimeStatus: function");
    const resetStatusEnd = scriptMatch[1].indexOf("\n                },", resetStatusStart);
    const resetStatusBody = resetStatusStart >= 0 && resetStatusEnd > resetStatusStart
        ? scriptMatch[1].slice(resetStatusStart, resetStatusEnd)
        : "";
    for (const marker of [
        "runtimeItem",
        "runtimeTarget",
        "runtimeHealth",
        "runtimeQuality",
        "runtimePlaybackProgress",
        "runtimePlaybackObservedAt",
        "runtimeSessions"
    ]) {
        if (!resetStatusBody.includes(`\"${marker}\"`)) {
            throw new Error(`${file} resetRuntimeStatus must clear runtime field: ${marker}`);
        }
    }
    for (const marker of [
        "page._hueRuntimeStatusGeneration",
        "page._hueRuntimeStatusRequest",
        "var isCurrentRequest = function ()",
        "if (!isCurrentRequest()) return;",
        "HueConfigurationPage.resetRuntimeStatus(page"
    ]) {
        if (!runtimeStatusBody.includes(marker)) {
            throw new Error(`${file} loadRuntimeStatus is missing stale-request protection: ${marker}`);
        }
    }

    const pollingStart = scriptMatch[1].indexOf("stopRuntimeStatusPolling: function");
    const pollingEnd = scriptMatch[1].indexOf("\n                },", pollingStart);
    const pollingBody = pollingStart >= 0 && pollingEnd > pollingStart
        ? scriptMatch[1].slice(pollingStart, pollingEnd)
        : "";
    for (const marker of [
        "page._hueRuntimeStatusLoading = false",
        "page._hueRuntimeStatusRequest = null",
        "typeof runtimeRequest.abort === \"function\""
    ]) {
        if (!pollingBody.includes(marker)) {
            throw new Error(`${file} stopRuntimeStatusPolling must release runtime requests: ${marker}`);
        }
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

for (const contract of [
    {
        functionName: "registerBridge",
        buttonMarker: "page.querySelector('#registerBtn')",
        inFlightMarker: "page._hueRegistrationRequest"
    },
    {
        functionName: "registerMappingBridge",
        buttonMarker: "document.getElementById('mappingLinkBridgeBtn')",
        inFlightMarker: "HueConfigurationPage._hueMappingRegistrationRequest"
    }
]) {
    const start = scriptMatch[1].indexOf(`${contract.functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody || /console\.(?:log|warn|error)\s*\(/.test(functionBody) ||
        /Check console|Registration response|Keys not found in response/.test(functionBody)) {
        throw new Error(`${file} ${contract.functionName} must not log or direct administrators to raw registration responses`);
    }
    for (const marker of [
        contract.buttonMarker,
        contract.inFlightMarker,
        `if (!result || ${contract.inFlightMarker}) return;`,
        "if (button) button.disabled = true;",
        "if (button) button.disabled = false;",
        "Promise.resolve(ApiClient.ajax({",
        ".finally(function ()",
        "try {",
        "catch {"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${contract.functionName} is missing registration lifecycle guard: ${marker}`);
        }
    }
}

{
    const functionName = "validateConfigurationImport";
    const start = scriptMatch[1].indexOf(`\n                ${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("HueConfigurationPage.setConfigurationImportValidated(page, valid && canImport)") ||
        !functionBody.includes("applyButton.disabled = !page._hueImportValidated") ||
        !functionBody.includes("validationGeneration !== page._hueImportValidationGeneration") ||
        !functionBody.includes("validationGeneration === page._hueImportValidationGeneration") ||
        !functionBody.includes("Dashboard.hideLoadingMsg();")) {
        throw new Error(`${file} ${functionName} must only enable import after successful validation`);
    }
}

{
    const functionName = "submitConfigurationImport";
    const start = scriptMatch[1].indexOf(`\n                ${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("if (!page._hueImportValidated)") ||
        !functionBody.includes("Validate Import successfully before importing.") ||
        !functionBody.includes("HueConfigurationPage.applyConfigurationImportCredentials(page)") ||
        !functionBody.includes("changing the bridge requires replacement App/Client keys or explicit credential clearing") ||
        !functionBody.includes("HueConfigurationPage.loadSceneSchedules(page)")) {
        throw new Error(`${file} ${functionName} must fail closed until import preflight succeeds`);
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
    if ((!functionBody.includes("ApiClient.ajax") && !functionBody.includes("getPageLifecycleRequest")) ||
        !functionBody.includes("page._hue") ||
        !functionBody.includes("setDiagnosticsBusy(page)")) {
        throw new Error(`${file} ${functionName} is missing cancellable diagnostics lifecycle wiring`);
    }
}

for (const functionName of ["exportSceneScheduleOccurrences"]) {
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
        !functionBody.includes("_hueSupportBundleRequest") ||
        !functionBody.includes("var pageGeneration") ||
        !functionBody.includes("getPageLifecycleRequest") ||
        !functionBody.includes("isPageLifecycleRequestCurrent(page, pageGeneration")) {
        throw new Error(`${file} ${functionName} is missing support bundle export wiring`);
    }
}

{
    const start = scriptMatch[1].indexOf("updatePreviewTargetMetadataControls: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("hasPreviewTargetMetadata(page)") ||
        !functionBody.includes("select.disabled = true") ||
        !functionBody.includes("button.disabled = true") ||
        !functionBody.includes("previewTargetMetadataStatus")) {
        throw new Error(`${file} updatePreviewTargetMetadataControls must keep target controls disabled until metadata is ready`);
    }
}

{
    const start = scriptMatch[1].indexOf("updateSceneScheduleMetadataControls: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("hasSceneScheduleMetadata(page)") ||
        !functionBody.includes("control.disabled = !ready") ||
        !functionBody.includes("saveButton.disabled = !ready") ||
        !functionBody.includes("sceneScheduleStatus")) {
        throw new Error(`${file} updateSceneScheduleMetadataControls must keep scheduled-cue metadata controls disabled until metadata is ready`);
    }
}

{
    const start = scriptMatch[1].indexOf("isSceneScheduleMappingMetadataValid: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("if (!Array.isArray(mappings)) return false") ||
        !functionBody.includes("if (!Array.isArray(deviceTargets)) return false") ||
        !functionBody.includes("String(read(deviceTarget, 'deviceId', 'DeviceId') || '').trim()")) {
        throw new Error(`${file} isSceneScheduleMappingMetadataValid must reject malformed mapping and device metadata`);
    }
}

{
    const start = scriptMatch[1].indexOf("isConfiguredDeviceRouteReady: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("deviceBridgeIp") ||
        !functionBody.includes("deviceAreaId") ||
        !functionBody.includes("deviceHasAppKey === true") ||
        !functionBody.includes("deviceHasClientKey === true") ||
        !functionBody.includes("!!String(deviceBridgeIp || '').trim()")) {
        throw new Error(`${file} isConfiguredDeviceRouteReady must keep the scheduled-cue device readiness contract`);
    }
}

for (const [functionName, source] of [["loadColorPresets", "colorPresets"], ["loadScenePlaylists", "scenePlaylists"]]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes(`setPreviewTargetMetadataState(page, "${source}", false`) ||
        !functionBody.includes(`setPreviewTargetMetadataState(page, "${source}", true)`) ||
        !functionBody.includes("markPreviewTargetMetadataUnavailable(page)") ||
        !functionBody.includes("updatePreviewTargetMetadataControls(page)")) {
        throw new Error(`${file} ${functionName} must fail closed when target metadata loading fails`);
    }
}

for (const [functionName, markers] of [
    ["loadColorPresets", [
        "if (!Array.isArray(presets) || !Array.isArray(mappings) ||",
        "!HueConfigurationPage.isSceneScheduleMappingMetadataValid(mappings)",
        "var deviceReady = HueConfigurationPage.isConfiguredDeviceRouteReady(deviceTarget)",
        "deviceOption.disabled = !enabled || !deviceReady",
        "(deviceReady ? \"\" : \" (unavailable)\")"
    ]],
    ["loadScenePlaylists", [
        "var playlists = Array.isArray(responses[0]) ? responses[0] : null;",
        "if (!Array.isArray(playlists) || !Array.isArray(presets) || !Array.isArray(mappings) ||",
        "!HueConfigurationPage.isSceneScheduleMappingMetadataValid(mappings)",
        "var deviceReady = HueConfigurationPage.isConfiguredDeviceRouteReady(deviceTarget)",
        "deviceOption.disabled = !enabled || !deviceReady",
        "(deviceReady ? \"\" : \" (unavailable)\")"
    ]]
]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of markers) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} is missing fail-closed preview target metadata contract: ${marker}`);
        }
    }
}

for (const functionName of [
    "previewDefaultColor",
    "previewAllEnabledTargets",
    "captureCurrentColor",
    "previewSavedPreset",
    "previewColorPresetsBulk",
    "previewScenePlaylist",
    "previewScenePlaylistsBulk",
    "previewMappingColor"
]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("requirePreviewTargetMetadata(page)")) {
        throw new Error(`${file} ${functionName} must reject preview requests while target metadata is unavailable`);
    }
}

for (const functionName of ["updateColorPresetBulkButtons", "updateScenePlaylistButtons", "updateScenePlaylistBulkButtons"]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("hasPreviewTargetMetadata(page)") ||
        !functionBody.includes("targetMetadataReady")) {
        throw new Error(`${file} ${functionName} must gate target-dependent controls on metadata readiness`);
    }
}

for (const [functionName, markers] of [
    ["loadSceneSchedules", [
        "HueConfigurationPage.initializeSceneScheduleMetadata(page)",
        "HueConfigurationPage.isSceneScheduleMappingMetadataValid(responses[3])",
        "page._hueSceneScheduleMappings = mappings",
        "data-time-zone-iana-id",
        "option.value = ianaId || id",
        "deviceOption.value = HueConfigurationPage.encodeCurrentLightDeviceTarget(userId, deviceId)",
        "HueConfigurationPage.isConfiguredDeviceRouteReady(deviceTarget)",
        "deviceOption.disabled = !enabled || !deviceReady",
        "HueConfigurationPage.setSceneScheduleMetadataState(page, source, true)",
        "HueConfigurationPage.markSceneScheduleMetadataUnavailable(page"
    ]],
    ["applySceneSchedule", [
        "get('TargetRoutes', 'targetRoutes', [])",
        "get('TimeZoneIanaId', 'timeZoneIanaId', '')",
        "data-host-time-zone-id",
        "isSceneScheduleDeviceRouteAvailable(page, route)",
        "page._hueSceneScheduleUnavailableTargetRoutes = unavailableTargetRoutes"
    ]],
    ["saveSceneSchedule", [
        "if (!HueConfigurationPage.requireSceneScheduleMetadata(page)) return;",
        "var timeZoneIanaId = selectedTimeZoneOption",
        "timeZoneIanaId: timeZoneIanaId",
        "var targetSelection = HueConfigurationPage.getSceneScheduleTargetSelection(page)",
        "if (!targetSelection.valid)",
        "targetRoutes: targetSelection.targetRoutes"
    ]],
    ["getSceneScheduleTargetSelection", [
        "isSceneScheduleDeviceRouteAvailable(page, normalizedRoute)",
        "targetRoutes.push(normalizedRoute)",
        "targetRoutes: all ? [] : targetRoutes"
    ]]
]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of markers) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} is missing scheduled device-route contract: ${marker}`);
        }
    }
}

console.log(`${file}: JavaScript syntax and cancellable diagnostic contracts passed`);
