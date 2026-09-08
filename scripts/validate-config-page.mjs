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
    'id="channelIds" name="channelIds" type="text" is="emby-input" maxlength="4096"',
    'id="mappingChannelIdsOverride" type="text" maxlength="4096" is="emby-input"',
    'Hue entertainment channel IDs (0-255; maximum 4,096 characters)',
    'id="configurationSectionNav" aria-label="Configuration sections"',
    'href="#runtimeStatusSection"',
    'href="#sessionHistorySection"',
    'href="#environmentDiagnosticsSection"',
    'href="#configurationPortabilitySection"',
    'href="#bridgeConnectionSection"',
    'href="#lightingTargetSection"',
    'href="#globalChannelProfileSection"',
    'href="#sceneEffectPreviewSection"',
    'href="#savedScenePlaylistsSection"',
    'href="#scheduledSceneCuesSection"',
    'href="#syncPerformanceSection"',
    'href="#cinemaModeSection"',
    'href="#playbackPauseBehaviorSection"',
    'href="#perUserMappingsSection"',
    'href="#advancedSettingsSection"',
    'id="runtimeStatusSection" tabindex="-1"',
    'id="sessionHistorySection" tabindex="-1"',
    'id="environmentDiagnosticsSection" tabindex="-1"',
    'id="configurationPortabilitySection" tabindex="-1"',
    'id="bridgeConnectionSection" tabindex="-1"',
    'id="lightingTargetSection" tabindex="-1"',
    'id="globalChannelProfileSection" tabindex="-1"',
    'id="sceneEffectPreviewSection" tabindex="-1"',
    'id="savedScenePlaylistsSection" tabindex="-1"',
    'id="scheduledSceneCuesSection" tabindex="-1"',
    'id="syncPerformanceSection" tabindex="-1"',
    'id="cinemaModeSection" tabindex="-1"',
    'id="playbackPauseBehaviorSection" tabindex="-1"',
    'id="perUserMappingsSection" tabindex="-1"',
    'id="advancedSettingsSection" tabindex="-1"',
    'id="registerBtn"',
    'id="bridgeCertificateBtn"',
    'id="bridgeCertificatePins"',
    'id="bridgeCertificatePinsList"',
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
    'id="targetFps" name="targetFps" type="number" min="1" max="60" is="emby-input"',
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
    'id="customFfmpegFlags" name="customFfmpegFlags" type="text" is="emby-input" maxlength="768"',
    'id="mappingCustomFfmpegFlagsOverride" type="text" is="emby-input" maxlength="768" placeholder="Leave blank to inherit"',
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
    'id="hueAppKey" name="hueAppKey" type="password" is="emby-input" autocomplete="new-password"',
    'id="hueClientKey" name="hueClientKey" type="password" is="emby-input" autocomplete="new-password"',
    'id="mappingAppKey" type="password" class="emby-input" autocomplete="new-password"',
    'id="mappingClientKey" type="password" class="emby-input" autocomplete="new-password"',
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
    'value="Matrix">Matrix',
    'id="captureCurrentColorBtn"',
    'id="captureColorTarget"',
    'id="clearStoredCredentials"',
    'id="saveConfigurationBtn"',
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
    'id="sceneScheduleDuplicateTargetStatus" class="fieldDescription" role="status" aria-live="polite" aria-atomic="true"',
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
    'id="mappingDeviceRouteBridge" type="text" is="emby-input" list="mappingDeviceRouteBridgeCandidates" aria-label="Route bridge address"',
    'id="mappingDeviceRouteBridge" type="text" is="emby-input" list="mappingDeviceRouteBridgeCandidates"',
    'id="mappingDiscoverDeviceRouteBridgeBtn"',
    'id="mappingDeviceRouteBridgeCandidates"',
    'id="mappingDeviceRouteAppKey"',
    'id="mappingDeviceRouteAppKey" type="password" is="emby-input" aria-label="Route App Key (new/replacement only)"',
    'id="mappingDeviceRouteClientKey"',
    'id="mappingDeviceRouteClientKey" type="password" is="emby-input" aria-label="Route Client Key (new/replacement only)"',
    'id="mappingDeviceRouteAreaSelect"',
    'id="mappingDeviceRouteAreaSelect" is="emby-select" aria-label="Route entertainment area"',
    'id="mappingDeviceRouteAreaId"',
    'id="mappingDeviceRouteAreaId" type="text" is="emby-input" aria-label="Entertainment area ID"',
    'id="mappingDeviceRouteChannels"',
    'id="mappingDeviceRouteChannels" type="text" is="emby-input" aria-label="Channel IDs (optional, comma separated)" maxlength="4096"',
    'id="mappingAddDeviceRouteBtn"',
    'id="mappingRemoveDeviceRouteBtn"',
    'id="mappingLoadDeviceRouteChannelsBtn"',
    'id="entertainmentAreaManual" name="entertainmentAreaManual" type="text" is="emby-input" aria-label="Manual entertainment area ID"',
    'id="sceneScheduleDayOfWeek" is="emby-select" aria-label="Monthly weekday day of week"',
    'id="mappingAreaManual" type="text" is="emby-input" aria-label="Manual entertainment area ID"',
    'id="mappingDeviceRouteSelect" is="emby-select" aria-label="Playback device route"'
];

for (const marker of [
    "Enable Real-time Media Sync",
    "supported video and audio playback"
]) {
    if (!html.includes(marker)) {
        throw new Error(`${file} is missing media-sync copy marker: ${marker}`);
    }
}
if (html.includes("Enable Real-time Video Sync") ||
    html.includes("lights will sync to the video in real-time")) {
    throw new Error(`${file} still contains stale video-only sync wording`);
}

for (const marker of requiredMarkup) {
    if (!html.includes(marker)) {
        throw new Error(`${file} is missing required markup: ${marker}`);
    }
}

for (const id of ["customFfmpegFlags", "mappingCustomFfmpegFlagsOverride"]) {
    const inputIndex = html.indexOf(`id="${id}"`);
    const containerEnd = html.indexOf("</div>", inputIndex);
    const fieldMarkup = inputIndex >= 0 && containerEnd > inputIndex
        ? html.slice(inputIndex, containerEnd)
        : "";
    for (const marker of ['maxlength="768"', "maximum 768 characters"]) {
        if (!fieldMarkup.includes(marker)) {
            throw new Error(`${file} ${id} must expose the FFmpeg flag limit: ${marker}`);
        }
    }
}

const sectionNavMatch = html.match(/<nav\b[^>]*\bid="configurationSectionNav"[^>]*>([\s\S]*?)<\/nav>/);
if (!sectionNavMatch) {
    throw new Error(`${file} is missing the configuration section navigation body`);
}

const sectionNavigationTargets = [
    "runtimeStatusSection",
    "sessionHistorySection",
    "environmentDiagnosticsSection",
    "configurationPortabilitySection",
    "bridgeConnectionSection",
    "lightingTargetSection",
    "globalChannelProfileSection",
    "sceneEffectPreviewSection",
    "savedScenePlaylistsSection",
    "scheduledSceneCuesSection",
    "syncPerformanceSection",
    "cinemaModeSection",
    "playbackPauseBehaviorSection",
    "perUserMappingsSection",
    "advancedSettingsSection"
];
for (const targetId of sectionNavigationTargets) {
    if (!sectionNavMatch[1].includes(`href="#${targetId}"`)) {
        throw new Error(`${file} section navigation is missing #${targetId}`);
    }
    const targetMatch = html.match(new RegExp(`<[^>]*\\bid="${targetId}"[^>]*>`));
    if (!targetMatch || !targetMatch[0].includes('tabindex="-1"')) {
        throw new Error(`${file} section navigation target ${targetId} must be programmatically focusable`);
    }
}

// Action results must be exposed to assistive technology as complete, atomic
// announcements. Keep frequently refreshed scheduler telemetry quiet so it can
// still be inspected on demand without interrupting a screen-reader user.
const requiredLiveRegions = {
    status: [
        "runtimeStatusMessage",
        "runtimeCleanupWarning",
        "sessionHistoryStatus",
        "diagnosticsMessage",
        "targetDiagnosticsMessage",
        "targetDiagnosticsDuplicateMappings",
        "configurationPortabilityStatus",
        "bridgeStatus",
        "entertainmentAreaStatus",
        "channelIdsStatus",
        "previewColorStatus",
        "previewTargetMetadataStatus",
        "previewPresetBulkStatus",
        "previewPresetStatus",
        "scenePlaylistBulkStatus",
        "scenePlaylistStatus",
        "sceneScheduleBulkStatus",
        "sceneScheduleStatus",
        "sceneScheduleRuntimeStatusAnnouncement",
        "sceneScheduleDuplicateTargetStatus",
        "sceneScheduleConflictsSummary",
        "sceneScheduleOccurrencesSummary",
        "sceneScheduleHistoryStatus",
        "userMappingBulkStatus",
        "userMappingReconcileStatus",
        "mappingChannelIdsStatus",
        "mappingBridgeStatus",
        "mappingDeviceDiscoveryStatus",
        "mappingDeviceRouteEditorStatus"
    ],
    alert: [
        "runtimeStatusError",
        "diagnosticsErrors"
    ]
};

for (const [role, ids] of Object.entries(requiredLiveRegions)) {
    for (const id of ids) {
        const openingTagMatch = html.match(new RegExp(`<[^>]*\\bid="${id}"[^>]*>`));
        if (!openingTagMatch) {
            throw new Error(`${file} is missing a live-region element: ${id}`);
        }
        const openingTag = openingTagMatch[0];
        for (const attribute of [`role="${role}"`, `aria-live="${role === "alert" ? "assertive" : "polite"}"`, 'aria-atomic="true"']) {
            if (!openingTag.includes(attribute)) {
                throw new Error(`${file} ${id} must include ${attribute}`);
            }
        }
    }
}

{
    const openingTagMatch = html.match(/<[^>]*\bid="sceneScheduleRuntimeStatusSummary"[^>]*>/);
    if (!openingTagMatch ||
        !openingTagMatch[0].includes('role="status"') ||
        !openingTagMatch[0].includes('aria-live="off"') ||
        !openingTagMatch[0].includes('aria-atomic="true"')) {
        throw new Error(`${file} scheduler telemetry summary must remain a quiet atomic status region`);
    }
}

{
    const runtimeStart = scriptMatch[1].indexOf("loadSceneScheduleRuntimeStatus: function");
    const runtimeEnd = scriptMatch[1].indexOf("\n                },", runtimeStart);
    const runtimeBody = runtimeStart >= 0 && runtimeEnd > runtimeStart
        ? scriptMatch[1].slice(runtimeStart, runtimeEnd)
        : "";
    for (const marker of [
        "loadSceneScheduleRuntimeStatus: function (page, announce)",
        "var shouldAnnounce = announce === true;",
        "previousRuntimeStatusRecord.announce === true",
        "var announcement = page.querySelector('#sceneScheduleRuntimeStatusAnnouncement');",
        "announceStatus(\"Loading scheduler status...\");",
        "request._huePageRequestRecord.announce = shouldAnnounce;",
        "announceStatus(\"Scheduler status updated: \" + summaryText);",
        "announceStatus(\"Scheduler status unavailable. \" + unavailableText);",
        "announceStatus(\"Scheduler status error: \" + errorText);",
        "requestRecord.announce = false"
    ]) {
        if (!runtimeBody.includes(marker)) {
            throw new Error(`${file} scheduler manual refresh announcement contract is missing: ${marker}`);
        }
    }
    if (!scriptMatch[1].includes("HueConfigurationPage.loadSceneScheduleRuntimeStatus(this.closest('.page'), true);")) {
        throw new Error(`${file} scheduler refresh button must request a one-shot announcement`);
    }
    if (!scriptMatch[1].includes("runtimeStatusAnnouncement.textContent = \"\";")) {
        throw new Error(`${file} page lifecycle invalidation must clear scheduler refresh announcements`);
    }
}

for (const [functionName, captionText] of [
    ["loadSceneScheduleRuntimeStatus", "Scheduled cue runtime status"],
    ["loadSceneScheduleConflicts", "Scheduled cue conflicts"],
    ["loadSceneScheduleOccurrences", "Upcoming scheduled cue occurrences"],
    ["loadSceneScheduleHistory", "Scheduled cue history"],
    ["loadSessionHistory", "Recent Hue session history"]
]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var caption = document.createElement('caption');",
        `caption.textContent = '${captionText}';`,
        "header.setAttribute('scope', 'col');"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} must expose accessible table context: ${marker}`);
        }
    }
}

const requiredScript = [
    "function escapeAttribute(str)",
    "getGlobalCredentialState: function",
    "page._hueGlobalCredentialState",
    "getPageSelectedAreaId: function",
    "page._hueSelectedAreaId",
    "normalizeJellyfinUserId: function",
    "areSameJellyfinUserId: function",
    "getTargetMappingDuplicateKey: function",
    "getTargetMappingDuplicateState: function",
    "setTargetMappingDuplicateMetadata: function",
    "hasEnabledDuplicateTargetMappings: function",
    "getDuplicateTargetMessage: function",
    "annotateTargetOptions: function",
    "applyDuplicateTargetRestrictions: function",
    "blockDuplicateTargetBroadcast: function",
    "refreshTargetMetadata: function",
    "Duplicate enabled Jellyfin user mappings are configured.",
    "All enabled targets (resolve duplicate mappings first)",
    "(duplicate mapping; resolve mappings)",
    "HueConfigurationPage.setTargetMappingDuplicateMetadata(page, mappings)",
    "HueConfigurationPage.annotateTargetOptions(previewTargetSelect, duplicateTargetState)",
    "HueConfigurationPage.annotateTargetOptions(select, duplicateTargetState)",
    "HueConfigurationPage.blockDuplicateTargetBroadcast(page, statusEl)",
    "HueConfigurationPage.blockDuplicateTargetBroadcast(page, status)",
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
    "bridgeDiscovery",
    "mappingBridgeDiscovery",
    "mappingDeviceRouteBridgeDiscovery",
    "page._hueBridgeDiscoveryLoading",
    "page._hueMappingBridgeDiscoveryLoading",
    "page._hueMappingDeviceRouteBridgeDiscoveryLoading",
    "HueConfigurationPage.discoverMappingBridge(this.closest('.page'))",
    "HueConfigurationPage.discoverMappingDeviceRouteBridge(this.closest('.page'))",
    "getBridgeCertificatePins: function",
    "renderBridgeCertificatePins: function",
    "forgetBridgeCertificatePin: function",
    "ensureBridgeCertificate: function",
    "runWithBridgeCertificate: function",
    "getCredentialLifecycleRequest: function",
    "var pageGeneration = HueConfigurationPage.ensurePageLifecycle(page);",
    "page._hueConfigurationSaving",
    "HueConfigurationPage.cancelPageLifecycleRequest(page, 'configuration')",
    "'configurationSave'",
    "isPageLifecycleRequestCurrent(page, pageGeneration, request)",
    "page._hueConfigurationSaving = false;",
    "saveButton.disabled = false",
    "page._huePageGeneration",
    "page._huePageActive = false",
    "page._huePageRequests",
    "bridgeCertificatePinDelete",
    "type: 'DELETE'",
    "dataType: 'text'",
    "Certificate pins are intentionally omitted from this form save.",
    "typeof AbortController === 'function'",
    "controller.abort()",
    "record.request.abort()",
    "HueConfigurationPage.beginPageLifecycle(e.target)",
    "HueConfigurationPage.invalidatePageLifecycle(e.target)",
    "maxConfigurationImportFileBytes: 8 * 1024 * 1024",
    "isConfigurationImportDocument: function",
    "The selected configuration file is too large. Choose a file no larger than 8 MiB.",
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
    "configurationVersion",
    "expectedConfigurationVersion",
    "_hueImportConfigurationVersion",
    "Configuration changed after validation. Validate Import again before retrying.",
    "DuplicateMappingGroups",
    "targetDiagnosticsDuplicateMappings",
    "Duplicate user mappings are blocked from bridge validation",
    'ApiClient.getUrl("HueSync/UserMappings/Reconcile")',
    "userMappingCleanupReport",
    "userMappingReconciliation",
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
    '["Solid", "Pulse", "Rainbow", "Candle", "Temperature", "Aurora", "Fire", "Ocean", "Lightning", "Starlight", "Matrix"]',
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
    'var totalDurationSeconds = playlist.totalDurationSeconds',
    "formatRuntimeDuration(totalDurationSeconds)",
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
    "loadMappingDeviceRouteChannels: function",
    "getEffectiveMappingChannelIds: function",
    "getEffectiveMappingTarget(page, false)",
    "mappingDeviceRouteChannels",
    "getMappingDeviceRouteTarget: function",
    "getMappingDeviceRouteCredentialContext: function",
    "getMappingDeviceRouteCredentialKey: function",
    "forgetMappingDeviceRouteCredentials: function",
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
    "sceneScheduleOccurrencesExport",
    "exportSceneScheduleCalendar: function",
    "sceneScheduleCalendarExport",
    "url += \"&scheduleId=\"",
    "days=\" + String(horizonDays)",
    "occurrenceQuery.horizonDays",
    "calendarUrl += \"&scheduleId=\"",
    "historyUrl += \"&scheduleId=\"",
    "currentExportUrl += \"&scheduleId=\"",
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
    'readStatusField(occurrence, "PlaylistTotalDurationSeconds", 0)',
    'formatRuntimeDuration(occurrencePlaylistTotalDuration)',
    'readStatusField(step, "StartOffsetSeconds", 0)',
    'readStatusField(schedule, "PlaylistTotalDurationSeconds", 0)',
    'formatRuntimeDuration(playlistTotalDuration)',
    'readStatusField(run, "PlaylistTotalDurationSeconds", 0)',
    'formatRuntimeDuration(historyPlaylistTotalDuration)',
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
    'ApiClient.getUrl("HueSync/ScenePlaylists/" + encodeURIComponent(name) + "/Rename")',
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
    "getUserMappingActionAriaLabel: function",
    "var userDisplayName = String(mapping.UserName || mapping.userName || userId || 'Unknown');",
    "aria-label=\"' + escapeAttribute(HueConfigurationPage.getUserMappingActionAriaLabel('Edit mapping', userDisplayName)) + '\"",
    "aria-label=\"' + escapeAttribute(HueConfigurationPage.getUserMappingActionAriaLabel('View references', userDisplayName)) + '\"",
    "aria-label=\"' + escapeAttribute(HueConfigurationPage.getUserMappingActionAriaLabel('Delete mapping', userDisplayName)) + '\"",
    "inspectUserMappingDependencies(this.closest(\\'.page\\'), this.dataset.userid, this.dataset.mappingid)",
    "deleteUserMapping(this.closest(\\'.page\\'), this.dataset.userid, this.dataset.mappingid, this)",
    "cleanupStaleUserMappings: function",
    "HueSync/UserMappings/Cleanup",
    "cleanupStaleUserMappingsBtn",
    "getMappingEditingState: function",
    "page._hueMappingEditingState",
    "data-mappingid",
    "MappingId: mappingState.mappingId || ''",
    "setUserMappingsEnabledBulk: function",
    "HueSync/UserMappings/BulkEnabled",
    "getSelectedUserMappingBulkIds: function",
    "updateUserMappingBulkButtons: function",
    "renderConfigurationImportDiff: function",
    "Import change summary (credential-safe):",
    "setConfigurationImportValidated: function",
    "invalidateConfigurationImport: function",
    "cancelPageLifecycleRequest(page, 'configurationImportValidation')",
    "HueConfigurationPage.setConfigurationImportValidated(page, false)",
    "page._hueImportValidationGeneration",
    "configurationImportValidation",
    "configurationImport",
    "clearConfigurationImport(page)",
    "input.addEventListener('input', function ()",
    "if (!page._hueImportValidated)",
    "Validate Import successfully before importing.",
    "getSelectedSceneScheduleBulkIds: function",
    "updateSceneScheduleBulkButtons: function",
    "downloadCsvDocument: function",
    "downloadCsvFromApi: function",
    "var exportStates = {",
    "exportButton.disabled = false",
    "exportSceneScheduleConflictsCsv: function",
    "exportSceneScheduleOccurrencesCsv: function",
    "exportSceneScheduleHistoryCsv: function",
    "exportSessionHistoryCsv: function",
    "exportSceneScheduleHistory: function",
    "exportSessionHistory: function",
    "exportConfiguration: function",
    "SceneSchedules/Conflicts/ExportCsv",
    "SceneSchedules/Occurrences/ExportCsv",
    "SceneSchedules/History/ExportCsv",
    "History/ExportCsv",
    'ApiClient.getUrl("HueSync/PlaybackDevices")',
    'deviceId: deviceId || ""',
    'discoverMappingDevices: function',
    "discoverMappingDevices(this.closest('.page'))",
    'canUseStoredDeviceRouteCredentials: function',
    'isMappingPlaybackDevicesOwner: function',
    'getMappingConfigurationPage: function',
    'getMappingPageElement: function',
    'getMappingRegistrationTarget: function (page)',
    'getMappingDeviceRouteTarget: function (page)',
    'getSelectedMappingDeviceRoute: function (page)',
    'updateMappingLinkBridgeButton: function (page)',
    'loadMappingDeviceRouteAreas(this.closest(\'.page\'))',
    'loadMappingDeviceRouteChannels(this.closest(\'.page\'))',
    'upsertMappingDeviceRoute(this.closest(\'.page\'))',
    'removeMappingDeviceRoute(this.closest(\'.page\'))',
    'testMappingConnection(this.closest(\'.page\'))',
    'registerMappingBridge(this.closest(\'.page\'))',
    'addUserMapping(this.closest(\'.page\'))',
    'Selected route " + target.deviceId'
];

for (const marker of requiredScript) {
    if (!scriptMatch[1].includes(marker)) {
        throw new Error(`${file} is missing required behavior: ${marker}`);
    }
}

// Jellyfin can retain a hidden configuration page while another instance is
// being shown. Mapping editors must therefore resolve every mutable control
// through the page that owns the event/request; document-level ID lookups can
// otherwise read or overwrite the hidden sibling with the same IDs.
for (const forbidden of [
    "document.getElementById('mapping",
    'document.getElementById("mapping',
    "getSelectedMappingDeviceRoute()",
    "getMappingDeviceRouteTarget()",
    "readMappingDeviceTargets()",
    "HueConfigurationPage.mappingEditingUserId",
    "HueConfigurationPage.mappingEditingMappingId",
    "HueConfigurationPage.mappingEditingHasAppKey",
    "HueConfigurationPage.mappingEditingHasClientKey",
    "HueConfigurationPage.selectedAreaId",
    "HueConfigurationPage.globalBridgeIp",
    "HueConfigurationPage.globalHasAppKey",
    "HueConfigurationPage.globalHasClientKey"
]) {
    if (scriptMatch[1].includes(forbidden)) {
        throw new Error(`${file} contains an unscoped mapping-page lookup: ${forbidden}`);
    }
}

// The inline control bindings must use the page that contains this script. A
// global document query would resolve duplicate IDs from the first retained
// Jellyfin page and leave the newly shown page's controls unbound or wired to
// the wrong lifecycle owner.
{
    const scopeStart = scriptMatch[1].indexOf("(function (document) {");
    const firstBinding = scriptMatch[1].indexOf("document.querySelector('.huePluginConfigurationForm').addEventListener", scopeStart);
    const scopeEnd = scriptMatch[1].indexOf("})(typeof document !== 'undefined'", scopeStart);
    const pageShowBinding = scriptMatch[1].indexOf("document.addEventListener('pageshow'");
    if (scopeStart < 0 || firstBinding < scopeStart || scopeEnd < firstBinding || pageShowBinding < scopeEnd) {
        throw new Error(`${file} control listeners must be enclosed by a page-scoped registration block`);
    }
    const scopeBody = scriptMatch[1].slice(scopeStart, scopeEnd);
    for (const marker of [
        "document.currentScript",
        "document.currentScript.closest('.pluginConfigurationPage')",
        "this.closest('.page')"
    ]) {
        if (!scriptMatch[1].includes(marker)) {
            throw new Error(`${file} page-scoped listener registration is missing: ${marker}`);
        }
    }
    if (!scopeBody.includes("document.querySelector('#mappingDiscoverDevicesBtn').addEventListener")) {
        throw new Error(`${file} mapping controls must remain inside the page-scoped listener registration block`);
    }
    if (!scopeBody.includes('HueConfigurationPage.bindSectionNavigation(sectionNavigationPage);')) {
        throw new Error(`${file} section navigation must bind against the current page-scoped root`);
    }

    const lifecycleGuard = "if (!document.__hueConfigurationPageLifecycleHandlersInstalled)";
    const lifecycleStart = scriptMatch[1].indexOf(lifecycleGuard);
    const lifecycleEnd = scriptMatch[1].indexOf("\n            }", lifecycleStart);
    const lifecycleBody = lifecycleStart >= 0 && lifecycleEnd > lifecycleStart
        ? scriptMatch[1].slice(lifecycleStart, lifecycleEnd)
        : "";
    if (lifecycleStart < 0 || lifecycleEnd < 0 ||
        !lifecycleBody.includes("document.__hueConfigurationPageLifecycleHandlersInstalled = true;") ||
        !lifecycleBody.includes("document.addEventListener('pageshow'") ||
        !lifecycleBody.includes("document.addEventListener('pagehide'")) {
        throw new Error(`${file} retained-page lifecycle handlers must be installed once per document`);
    }
    const sectionNavigationStart = scriptMatch[1].indexOf("bindSectionNavigation: function (page)");
    const sectionNavigationEnd = scriptMatch[1].indexOf("\n                },", sectionNavigationStart);
    const sectionNavigationBody = sectionNavigationStart >= 0 && sectionNavigationEnd > sectionNavigationStart
        ? scriptMatch[1].slice(sectionNavigationStart, sectionNavigationEnd)
        : "";
    for (const marker of [
        "var sectionNav = page.querySelector('#configurationSectionNav')",
        "page.querySelector('#' + targetId)",
        "target.focus({ preventScroll: true })",
        "target.scrollIntoView({ behavior: 'smooth', block: 'start' })"
    ]) {
        if (!sectionNavigationBody.includes(marker)) {
            throw new Error(`${file} section navigation is missing page-scoped focus/scroll behavior: ${marker}`);
        }
    }
    if (!scopeBody.includes("sectionNavigationPage.querySelector('#configurationSectionNav')") ||
        !scopeBody.includes("sectionNavigation.closest('.pluginConfigurationPage')")) {
        throw new Error(`${file} section navigation registration must resolve its owning configuration page`);
    }
}

{
    const start = scriptMatch[1].indexOf("refreshMappingDeviceRoutes: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var discovered = HueConfigurationPage.isMappingPlaybackDevicesOwner(page) &&",
        "Array.isArray(HueConfigurationPage._huePlaybackDevices)"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} refreshMappingDeviceRoutes must not render shared discovery rows on a non-owner page: ${marker}`);
        }
    }
}

{
    const functionName = "getMappingDeviceRouteCredentialKey";
    const start = scriptMatch[1].indexOf(`\n                ${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "getMappingDeviceRouteCredentialContext(userId, mappingId, page)",
        "normalizedDeviceId",
        "normalizedBridgeIp",
        "normalizedUserId",
        "normalizedMappingId",
        "if (!normalizedDeviceId || !normalizedBridgeIp || (!normalizedUserId && !normalizedMappingId)) return '';"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} must scope ephemeral route credentials by route identity and owner`);
        }
    }
}

{
    const functionName = "rememberMappingDeviceRouteCredentials";
    const start = scriptMatch[1].indexOf(`\n                ${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "bridgeIp, userId, mappingId",
        "getMappingDeviceRouteCredentialKey(deviceId, bridgeIp, userId, mappingId, page)",
        "if (!key || !page) return;",
        "page._hueDeviceRouteCredentials"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} must refuse unscoped route credential caching`);
        }
    }
}

{
    const start = scriptMatch[1].indexOf("getPageLifecycleRequest: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var request;",
        "try {",
        "request = ApiClient.ajax(options);",
        "} catch (err) {",
        "request = Promise.reject(err);",
        "var record = {",
        "request: request,"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} getPageLifecycleRequest must preserve lifecycle cleanup when request construction fails: ${marker}`);
        }
    }
}

for (const [functionName, markers] of [
    ["reconcileUserMappings", [
        "var pageGeneration = HueConfigurationPage.ensurePageLifecycle(page);",
        "getPageLifecycleRequest",
        "'userMappingReconciliation'",
        "isPageLifecycleRequestCurrent(page, pageGeneration, request)",
        "if (!isCurrent()) return;",
        "type: \"POST\"",
        "if (button) button.disabled = true;",
        "request === reportRequest"
    ]],
    ["editUserMapping", [
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'editUserMapping')",
        "getPageLifecycleRequest",
        "isPageLifecycleRequestCurrent(page, pageGeneration, request)"
    ]],
    ["loadMappingAreas", [
        "var page",
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'mappingAreas')",
        "getCredentialLifecycleRequest",
        "isPageLifecycleTargetRequestCurrent"
    ]],
    ["loadMappingDeviceRouteAreas", [
        "var page",
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'mappingDeviceRouteAreas')",
        "getMappingDeviceRouteTarget",
        "getCredentialLifecycleRequest",
        "isPageLifecycleTargetRequestCurrent"
    ]],
    ["discoverMappingDevices", [
        "var page",
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'mappingPlaybackDevices')",
        "getPageLifecycleRequest",
        "isPageLifecycleTargetRequestCurrent",
        "HueConfigurationPage.refreshMappingDeviceRoutes(page, undefined, true)"
    ]],
    ["loadChannelIds", [
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, requestKey)",
        "getCredentialLifecycleRequest",
        "isPageLifecycleTargetRequestCurrent",
        "channelInput.value = channelIds.join(', ')",
        "if (!isCurrent()) return;"
    ]],
    ["loadEntertainmentAreas", [
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'entertainmentAreas')",
        "isPageLifecycleCurrent(page, pageGeneration)",
        "getCredentialLifecycleRequest",
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

for (const [functionName, markers] of [
    ["testDefaultConnection", ["getCredentialLifecycleRequest", "getConnectionTestRequestOptions", "isPageLifecycleTargetRequestCurrent", "isCredentialInputCurrent"]],
    ["testMappingConnection", ["getCredentialLifecycleRequest", "getConnectionTestRequestOptions", "isPageLifecycleTargetRequestCurrent", "isCredentialInputCurrent"]],
    ["previewDefaultColor", ["runWithBridgeCertificate", "fetchColorPreview", "usesMultiTargetSelection", "isPageLifecycleCurrent", "isCredentialInputCurrent"]],
    ["previewMappingColor", ["runWithBridgeCertificate", "fetchColorPreview", "isPageLifecycleCurrent", "isCredentialInputCurrent"]],
    ["registerBridge", ["_hueRegistrationPreflight", "isPageLifecycleCurrent", "isCurrentRegistrationTarget"]],
    ["registerMappingBridge", [
        "_hueMappingRegistrationPreflight",
        "getMappingRegistrationTarget(page)",
        "var preflight = { page: page, generation: pageGeneration, canceled: false }",
        "request._hueMappingRegistrationPage = page",
        "rememberMappingDeviceRouteCredentials",
        "loadMappingDeviceRouteAreas(page)",
        "isPageLifecycleCurrent",
        "isCurrentRegistrationTarget"
    ]]
]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of markers) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} must protect credential-bearing requests with the page lifecycle: ${marker}`);
        }
    }
}

{
    const start = scriptMatch[1].indexOf("invalidatePageLifecycle: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var mappingRegistrationPreflight = HueConfigurationPage._hueMappingRegistrationPreflight;",
        "mappingRegistrationPreflight && mappingRegistrationPreflight.page === page",
        "HueConfigurationPage._hueMappingRegistrationPreflight = null;"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} invalidatePageLifecycle must only cancel the mapping registration preflight owned by the invalidated page: ${marker}`);
        }
    }
}

{
    const start = scriptMatch[1].indexOf("runWithBridgeCertificate: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var pageGeneration = HueConfigurationPage.ensurePageLifecycle(page);",
        "isPageLifecycleCurrent(page, pageGeneration)",
        "huePageLifecycleStale",
        "preflightRecord.canceled",
        "typeof preflightGuard === 'function'"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} runWithBridgeCertificate must suppress stale credential-bearing actions: ${marker}`);
        }
    }
}

{
    const claimStart = scriptMatch[1].indexOf("claimRuntimeStopLoading: function");
    const claimEnd = scriptMatch[1].indexOf("\n                },", claimStart);
    const claimBody = claimStart >= 0 && claimEnd > claimStart ? scriptMatch[1].slice(claimStart, claimEnd) : "";
    if (!claimBody.includes("HueConfigurationPage._hueGlobalLoadingOwner = owner;")) {
        throw new Error(`${file} claimRuntimeStopLoading must use the shared global loader owner`);
    }
    const helperStart = scriptMatch[1].indexOf("releaseRuntimeStopLoading: function");
    const helperEnd = scriptMatch[1].indexOf("\n                },", helperStart);
    const helperBody = helperStart >= 0 && helperEnd > helperStart ? scriptMatch[1].slice(helperStart, helperEnd) : "";
    for (const marker of [
        "if (HueConfigurationPage._hueGlobalLoadingOwner !== owner) return false;",
        "HueConfigurationPage._hueGlobalLoadingOwner = null;",
        "Dashboard.hideLoadingMsg();"
    ]) {
        if (!helperBody.includes(marker)) {
            throw new Error(`${file} releaseRuntimeStopLoading must only hide the loader for its current owner: ${marker}`);
        }
    }
    for (const functionName of ["stopRuntimeSync", "stopRuntimeSession"]) {
        const start = scriptMatch[1].indexOf(`${functionName}: function`);
        const end = scriptMatch[1].indexOf("\n                },", start);
        const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
        for (const marker of [
            "var loadingOwner = HueConfigurationPage.claimRuntimeStopLoading(page, null);",
            "HueConfigurationPage.releaseRuntimeStopLoading(loadingOwner);"
        ]) {
            if (!functionBody.includes(marker)) {
                throw new Error(`${file} ${functionName} must release only its owned loader: ${marker}`);
            }
        }
    }
}

{
    const claimStart = scriptMatch[1].indexOf("claimConfigurationExportLoading: function");
    const claimEnd = scriptMatch[1].indexOf("\n                },", claimStart);
    const claimBody = claimStart >= 0 && claimEnd > claimStart ? scriptMatch[1].slice(claimStart, claimEnd) : "";
    if (!claimBody.includes("HueConfigurationPage._hueGlobalLoadingOwner = owner;")) {
        throw new Error(`${file} claimConfigurationExportLoading must use the shared global loader owner`);
    }
    const helperStart = scriptMatch[1].indexOf("releaseConfigurationExportLoading: function");
    const helperEnd = scriptMatch[1].indexOf("\n                },", helperStart);
    const helperBody = helperStart >= 0 && helperEnd > helperStart ? scriptMatch[1].slice(helperStart, helperEnd) : "";
    for (const marker of [
        "if (HueConfigurationPage._hueGlobalLoadingOwner !== owner) return false;",
        "HueConfigurationPage._hueGlobalLoadingOwner = null;",
        "Dashboard.hideLoadingMsg();"
    ]) {
        if (!helperBody.includes(marker)) {
            throw new Error(`${file} releaseConfigurationExportLoading must only hide the loader for its current owner: ${marker}`);
        }
    }
    const start = scriptMatch[1].indexOf("exportConfiguration: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var loadingOwner = HueConfigurationPage.claimConfigurationExportLoading(page, request);",
        "HueConfigurationPage.releaseConfigurationExportLoading(loadingOwner);"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} exportConfiguration must release only its owned loader: ${marker}`);
        }
    }
}

{
    const claimStart = scriptMatch[1].indexOf("claimConfigurationOperationLoading: function");
    const claimEnd = scriptMatch[1].indexOf("\n                },", claimStart);
    const claimBody = claimStart >= 0 && claimEnd > claimStart ? scriptMatch[1].slice(claimStart, claimEnd) : "";
    if (!claimBody.includes("HueConfigurationPage._hueGlobalLoadingOwner = owner;")) {
        throw new Error(`${file} claimConfigurationOperationLoading must use the shared global loader owner`);
    }
    const helperStart = scriptMatch[1].indexOf("releaseConfigurationOperationLoading: function");
    const helperEnd = scriptMatch[1].indexOf("\n                },", helperStart);
    const helperBody = helperStart >= 0 && helperEnd > helperStart ? scriptMatch[1].slice(helperStart, helperEnd) : "";
    for (const marker of [
        "if (HueConfigurationPage._hueGlobalLoadingOwner !== owner) return false;",
        "HueConfigurationPage._hueGlobalLoadingOwner = null;",
        "Dashboard.hideLoadingMsg();"
    ]) {
        if (!helperBody.includes(marker)) {
            throw new Error(`${file} releaseConfigurationOperationLoading must only hide the loader for its current owner: ${marker}`);
        }
    }
    for (const functionName of ["loadConfiguration", "saveConfiguration", "validateConfigurationImport", "submitConfigurationImport"]) {
        const start = scriptMatch[1].indexOf(`\n                ${functionName}: function`);
        const end = scriptMatch[1].indexOf("\n                },", start);
        const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
        for (const marker of [
            "var loadingOwner = HueConfigurationPage.claimConfigurationOperationLoading(page, request);",
            "HueConfigurationPage.releaseConfigurationOperationLoading(loadingOwner);"
        ]) {
            if (!functionBody.includes(marker)) {
                throw new Error(`${file} ${functionName} must release only its owned global loader: ${marker}`);
            }
        }
        if (functionBody.includes("Dashboard.hideLoadingMsg();")) {
            throw new Error(`${file} ${functionName} must not hide a newer operation's global loader directly`);
        }
    }
}

{
    const claimStart = scriptMatch[1].indexOf("claimDiagnosticLoading: function");
    const claimEnd = scriptMatch[1].indexOf("\n                },", claimStart);
    const claimBody = claimStart >= 0 && claimEnd > claimStart ? scriptMatch[1].slice(claimStart, claimEnd) : "";
    if (!claimBody.includes("HueConfigurationPage._hueGlobalLoadingOwner = owner;")) {
        throw new Error(`${file} claimDiagnosticLoading must use the shared global loader owner`);
    }
    const helperStart = scriptMatch[1].indexOf("releaseDiagnosticLoading: function");
    const helperEnd = scriptMatch[1].indexOf("\n                },", helperStart);
    const helperBody = helperStart >= 0 && helperEnd > helperStart ? scriptMatch[1].slice(helperStart, helperEnd) : "";
    for (const marker of [
        "if (HueConfigurationPage._hueGlobalLoadingOwner !== owner) return false;",
        "HueConfigurationPage._hueGlobalLoadingOwner = null;",
        "Dashboard.hideLoadingMsg();"
    ]) {
        if (!helperBody.includes(marker)) {
            throw new Error(`${file} releaseDiagnosticLoading must only hide the loader for its current owner: ${marker}`);
        }
    }
    for (const functionName of ["testDefaultConnection", "testMappingConnection"]) {
        const start = scriptMatch[1].indexOf(`${functionName}: function`);
        const end = scriptMatch[1].indexOf("\n                },", start);
        const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
        for (const marker of [
            "var loadingOwner = HueConfigurationPage.claimDiagnosticLoading(page, request);",
            "page._hueDiagnosticLoadingOwner = loadingOwner;",
            "HueConfigurationPage.releaseDiagnosticLoading(loadingOwner);"
        ]) {
            if (!functionBody.includes(marker)) {
                throw new Error(`${file} ${functionName} must release only its owned diagnostic loader: ${marker}`);
            }
        }
        if (functionBody.includes("Dashboard.hideLoadingMsg();")) {
            throw new Error(`${file} ${functionName} must not hide a newer operation's global loader directly`);
        }
    }
}

{
    const claimStart = scriptMatch[1].indexOf("claimPreviewLoading: function");
    const claimEnd = scriptMatch[1].indexOf("\n                },", claimStart);
    const claimBody = claimStart >= 0 && claimEnd > claimStart ? scriptMatch[1].slice(claimStart, claimEnd) : "";
    if (!claimBody.includes("HueConfigurationPage._hueGlobalLoadingOwner = owner;")) {
        throw new Error(`${file} claimPreviewLoading must use the shared global loader owner`);
    }
    const helperStart = scriptMatch[1].indexOf("releasePreviewLoading: function");
    const helperEnd = scriptMatch[1].indexOf("\n                },", helperStart);
    const helperBody = helperStart >= 0 && helperEnd > helperStart ? scriptMatch[1].slice(helperStart, helperEnd) : "";
    for (const marker of [
        "if (HueConfigurationPage._hueGlobalLoadingOwner !== owner) return false;",
        "HueConfigurationPage._hueGlobalLoadingOwner = null;",
        "Dashboard.hideLoadingMsg();"
    ]) {
        if (!helperBody.includes(marker)) {
            throw new Error(`${file} releasePreviewLoading must only hide the loader for its current owner: ${marker}`);
        }
    }

    const trackStart = scriptMatch[1].indexOf("trackPreviewRequest: function");
    const trackEnd = scriptMatch[1].indexOf("\n                },", trackStart);
    const trackBody = trackStart >= 0 && trackEnd > trackStart ? scriptMatch[1].slice(trackStart, trackEnd) : "";
    for (const marker of [
        "page._huePreviewLoadingOwner ||",
        "HueConfigurationPage.claimPreviewLoading(page, trackedRequest);",
        "page._huePreviewLoadingOwner = loadingOwner;"
    ]) {
        if (!trackBody.includes(marker)) {
            throw new Error(`${file} trackPreviewRequest must retain one loader owner across nested preview promises: ${marker}`);
        }
    }

    const finishStart = scriptMatch[1].indexOf("finishPreviewRequest: function");
    const finishEnd = scriptMatch[1].indexOf("\n                },", finishStart);
    const finishBody = finishStart >= 0 && finishEnd > finishStart ? scriptMatch[1].slice(finishStart, finishEnd) : "";
    for (const marker of [
        "var loadingOwner = page && page._huePreviewLoadingOwner;",
        "var ownsLoading = !!loadingOwner && loadingOwner.request === request;",
        "HueConfigurationPage.releasePreviewLoading(loadingOwner);"
    ]) {
        if (!finishBody.includes(marker)) {
            throw new Error(`${file} finishPreviewRequest must release only its owned loader: ${marker}`);
        }
    }
    if (finishBody.includes("Dashboard.hideLoadingMsg();")) {
        throw new Error(`${file} finishPreviewRequest must not hide a newer operation's global loader directly`);
    }
}

{
    const claimStart = scriptMatch[1].indexOf("claimPageLoading: function");
    const claimEnd = scriptMatch[1].indexOf("\n                },", claimStart);
    const claimBody = claimStart >= 0 && claimEnd > claimStart ? scriptMatch[1].slice(claimStart, claimEnd) : "";
    for (const marker of [
        "var owner = { page: page, request: request, slot: slot || '' };",
        "HueConfigurationPage._hueGlobalLoadingOwner = owner;",
        "page[owner.slot] = owner;"
    ]) {
        if (!claimBody.includes(marker)) {
            throw new Error(`${file} claimPageLoading must register a page-scoped owner: ${marker}`);
        }
    }
    const releaseStart = scriptMatch[1].indexOf("releasePageLoading: function");
    const releaseEnd = scriptMatch[1].indexOf("\n                },", releaseStart);
    const releaseBody = releaseStart >= 0 && releaseEnd > releaseStart ? scriptMatch[1].slice(releaseStart, releaseEnd) : "";
    for (const marker of [
        "if (page && owner.slot && page[owner.slot] === owner) page[owner.slot] = null;",
        "if (HueConfigurationPage._hueGlobalLoadingOwner !== owner) return false;",
        "HueConfigurationPage._hueGlobalLoadingOwner = null;",
        "Dashboard.hideLoadingMsg();"
    ]) {
        if (!releaseBody.includes(marker)) {
            throw new Error(`${file} releasePageLoading must hide only for the current page owner: ${marker}`);
        }
    }
    for (const functionName of [
        "discoverBridge",
        "discoverMappingBridge",
        "discoverMappingDeviceRouteBridge",
        "discoverMappingDevices",
        "registerBridge",
        "editUserMapping",
        "inspectUserMappingDependencies"
    ]) {
        const start = scriptMatch[1].indexOf(`${functionName}: function`);
        const end = scriptMatch[1].indexOf("\n                },", start);
        const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
        if (!functionBody.includes("HueConfigurationPage.claimPageLoading(")) {
            throw new Error(`${file} ${functionName} must claim its page-scoped loader owner`);
        }
        if (!functionBody.includes("HueConfigurationPage.releasePageLoading(")) {
            throw new Error(`${file} ${functionName} must release its page-scoped loader owner`);
        }
        if (functionBody.includes("Dashboard.hideLoadingMsg();")) {
            throw new Error(`${file} ${functionName} must not hide a newer operation's global loader directly`);
        }
    }
    const invalidateStart = scriptMatch[1].indexOf("invalidatePageLifecycle: function");
    const invalidateEnd = scriptMatch[1].indexOf("\n                },", invalidateStart);
    const invalidateBody = invalidateStart >= 0 && invalidateEnd > invalidateStart
        ? scriptMatch[1].slice(invalidateStart, invalidateEnd)
        : "";
    for (const marker of [
        "var hadMappingPlaybackDeviceLoadingOwner = !!page._hueMappingPlaybackDevicesLoadingOwner;",
        "!hadMappingPlaybackDeviceLoadingOwner &&",
        "!HueConfigurationPage._hueGlobalLoadingOwner"
    ]) {
        if (!invalidateBody.includes(marker)) {
            throw new Error(`${file} invalidatePageLifecycle must guard legacy discovery cleanup from newer global loaders: ${marker}`);
        }
    }
}

{
    const start = scriptMatch[1].indexOf("ensureBridgeCertificate: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "targetGuard",
        "typeof targetGuard !== 'function' || targetGuard()",
        "stalePageError",
        "if (!canWritePage())"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ensureBridgeCertificate must reject stale target approvals: ${marker}`);
        }
    }
}

{
    const start = scriptMatch[1].indexOf("verifyBridgeCertificate: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var isCurrentTarget = function ()",
        "HueConfigurationPage.isSameBridgeTarget(currentInput.value, bridgeIp)",
        "huePageLifecycleStale",
        "HueConfigurationPage.isPageLifecycleCurrent(page, pageGeneration)"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} verifyBridgeCertificate must suppress stale target and lifecycle alerts: ${marker}`);
        }
    }
}

{
    const start = scriptMatch[1].indexOf("inspectUserMappingDependencies: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "getMappingConfigurationPage(page)",
        "var pageGeneration = HueConfigurationPage.ensurePageLifecycle(page);",
        "HueConfigurationPage.isPageLifecycleCurrent(page, pageGeneration)",
        "'userMappingDependencies'",
        "page._hueUserMappingDependenciesRequest",
        "if (!isCurrent()) return;",
        "if (tracked) HueConfigurationPage.cancelPageLifecycleRequest(page, 'userMappingDependencies')"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} inspectUserMappingDependencies must be page-lifecycle scoped: ${marker}`);
        }
    }
}

{
    const start = scriptMatch[1].indexOf("getCredentialLifecycleRequest: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "preflightGuard",
        "}, key, preflightGuard);"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} getCredentialLifecycleRequest must carry target freshness into the certificate preflight: ${marker}`);
        }
    }
}

{
    const start = scriptMatch[1].indexOf("cancelPreview: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("_hueCertificatePreflights") || !functionBody.includes("preflights.preview.canceled = true")) {
        throw new Error(`${file} cancelPreview must cancel a pending certificate preflight before requesting server cleanup`);
    }
}

{
    const start = scriptMatch[1].indexOf("canUseStoredDeviceRouteCredentials: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "hasStoredCredential('HasAppKey', 'hasAppKey')",
        "hasStoredCredential('HasClientKey', 'hasClientKey')",
        "return !requireClientKey || hasStoredCredential('HasClientKey', 'hasClientKey');"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} canUseStoredDeviceRouteCredentials must require stored credential presence flags: ${marker}`);
        }
    }
}

{
    const start = scriptMatch[1].indexOf("loadEntertainmentAreas: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    const staleGuards = functionBody.match(/if \(!isCurrent\(\)\) return;/g) || [];
    if (staleGuards.length < 2 ||
        functionBody.indexOf("selectEl.innerHTML = ''") < functionBody.indexOf("if (!isCurrent()) return;") ||
        functionBody.indexOf("statusEl.textContent = \"Unable to load areas:") < functionBody.indexOf("if (!isCurrent()) return;")) {
        throw new Error(`${file} loadEntertainmentAreas must guard area-list writes and error callbacks against stale targets`);
    }
}

{
    const start = scriptMatch[1].indexOf("loadChannelIds: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    const staleGuards = functionBody.match(/if \(!isCurrent\(\)\) return;/g) || [];
    const writeIndex = functionBody.indexOf("channelInput.value = channelIds.join(', ')");
    const firstGuardIndex = functionBody.indexOf("if (!isCurrent()) return;");
    if (!functionBody.includes("Number.isInteger(channelId) && channelId >= 0 && channelId <= 255")) {
        throw new Error(`${file} loadChannelIds must bound v2 channel IDs to unsigned bytes`);
    }
    if (staleGuards.length < 2 || firstGuardIndex < 0 || writeIndex < 0 || firstGuardIndex > writeIndex) {
        throw new Error(`${file} loadChannelIds must guard channel writes and error callbacks against stale targets`);
    }
}

for (const functionName of ["loadEntertainmentAreas", "loadMappingAreas", "loadMappingDeviceRouteAreas", "loadChannelIds"]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!/\.finally\(function \(\) \{\s*if \(!HueConfigurationPage\.isPageLifecycleRequestCurrent\(page, pageGeneration, request\)\) return;/.test(functionBody)) {
        throw new Error(`${file} ${functionName} must release controls by request ownership, independently of result target identity`);
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

{
    const functionName = "renderDuplicateResolutionControls";
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var pageGeneration = HueConfigurationPage.ensurePageLifecycle(page);",
        "cancelPageLifecycleRequest(page, 'userMappingDuplicateResolution')",
        "if (!confirmed || !HueConfigurationPage.isPageLifecycleCurrent(page, pageGeneration)) return;",
        "getPageLifecycleRequest(",
        "'userMappingDuplicateResolution'",
        "isPageLifecycleRequestCurrent(page, pageGeneration, request)",
        "if (!isCurrent()) return;",
        ".finally(function ()",
        "HueConfigurationPage.cancelPageLifecycleRequest(page, 'userMappingDuplicateResolution')"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} is missing duplicate-resolution lifecycle protection: ${marker}`);
        }
    }
    const staleGuards = functionBody.match(/if \(!isCurrent\(\)\) return;/g) || [];
    if (staleGuards.length < 2) {
        throw new Error(`${file} ${functionName} must guard success and failure callbacks`);
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
    const start = scriptMatch[1].indexOf("exportSceneScheduleOccurrences: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'sceneScheduleOccurrencesExport')",
        "getSceneScheduleOccurrenceQuery(page)",
        "getPageLifecycleRequest",
        "isPageLifecycleRequestCurrent(page, pageGeneration, request)",
        "currentQuery.url === scheduleQuery.url",
        "if (!isCurrent()) return;",
        "downloadJsonDocument(report, \"jellyfin-hue-scene-schedule-occurrences.json\")",
        "if (!HueConfigurationPage.isPageLifecycleRequestCurrent(page, pageGeneration, request)) return;"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} exportSceneScheduleOccurrences is missing query-scoped lifecycle protection: ${marker}`);
        }
    }
    const staleGuards = functionBody.match(/if \(!isCurrent\(\)/g) || [];
    if (staleGuards.length < 3) {
        throw new Error(`${file} exportSceneScheduleOccurrences must guard success, catch, and finally callbacks`);
    }
}

{
    const start = scriptMatch[1].indexOf("downloadCsvFromApi: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "downloadCsvFromApi: function (page, pageGeneration, requestKey, url, fileName, queryGuard)",
        "getPageLifecycleRequest",
        "{ dataType: \"text\" }",
        "isPageLifecycleRequestCurrent(page, pageGeneration, request)",
        "(!queryGuard || queryGuard())",
        "if (!isCurrent()) return;",
        "HueConfigurationPage.downloadCsvDocument(csv, fileName)",
        "trackedRequest._huePageRequestRecord = request._huePageRequestRecord"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} downloadCsvFromApi is missing lifecycle-aware CSV handling: ${marker}`);
        }
    }
}

for (const [functionName, requestKey, routeMarker, queryMarker, downloadMarker] of [
    ["exportSceneScheduleHistory", "sceneScheduleHistoryExport", "SceneSchedules/History/Export", "getExportUrl() === exportUrl", "JSON.stringify(historyDocument, null, 2)"],
    ["exportSessionHistory", "sessionHistoryExport", "getSessionHistoryUrl(page, \"History/Export\", 25)", "getSessionHistoryUrl(page, \"History/Export\", 25) === exportUrl", "JSON.stringify(historyDocument, null, 2)"]
]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var pageGeneration",
        `cancelPageLifecycleRequest(page, '${requestKey}')`,
        `pageGeneration,\n                        '${requestKey}'`,
        "getPageLifecycleRequest",
        "isPageLifecycleRequestCurrent(page, pageGeneration, request)",
        queryMarker,
        routeMarker,
        "if (!isCurrent()) return;",
        "download",
        downloadMarker
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} is missing JSON export lifecycle/query protection: ${marker}`);
        }
    }
    const staleGuards = functionBody.match(/if \(!isCurrent\(\)/g) || [];
    if (staleGuards.length < 3) {
        throw new Error(`${file} ${functionName} must guard success, catch, and finally callbacks`);
    }
}

{
    const functionName = "exportConfiguration";
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'configurationExport')",
        "pageGeneration,\n                        'configurationExport'",
        "HueSync/Configuration/Export",
        "getPageLifecycleRequest",
        "isPageLifecycleRequestCurrent(page, pageGeneration, request)",
        "if (!isCurrent()) return;",
        "JSON.stringify(exportDocument, null, 2)",
        "var requestRecord = request && request._huePageRequestRecord",
        "page._huePageRequests.configurationExport === requestRecord",
        "HueConfigurationPage.cancelPageLifecycleRequest(page, 'configurationExport')"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} is missing configuration export lifecycle protection: ${marker}`);
        }
    }
    if (!functionBody.includes("var current = isCurrent()") ||
        !functionBody.includes("if (current || tracked)") ||
        !functionBody.includes("page._hueConfigurationExporting = false")) {
        throw new Error(`${file} ${functionName} must guard terminal cleanup against stale page lifecycle state`);
    }
}

for (const [functionName, requestKey, queryMarker] of [
    ["exportSceneScheduleConflictsCsv", "sceneScheduleConflictsCsvExport", "getSceneScheduleConflictQuery(page).url === scheduleQuery.url"],
    ["exportSceneScheduleOccurrencesCsv", "sceneScheduleOccurrencesCsvExport", "getSceneScheduleOccurrenceQuery(page).url === scheduleQuery.url"],
    ["exportSceneScheduleHistoryCsv", "sceneScheduleHistoryCsvExport", "getExportUrl() === exportUrl"],
    ["exportSessionHistoryCsv", "sessionHistoryCsvExport", "getSessionHistoryUrl(page, \"History/ExportCsv\", 25) === csvUrl"]
]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var pageGeneration",
        `cancelPageLifecycleRequest(page, '${requestKey}')`,
        `pageGeneration,\n                        '${requestKey}'`,
        "downloadCsvFromApi",
        "isPageLifecycleRequestCurrent(page, pageGeneration, request)",
        queryMarker,
        "if (!isCurrent()) return;"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} is missing CSV lifecycle/query protection: ${marker}`);
        }
    }
    const staleGuards = functionBody.match(/if \(!isCurrent\(\)/g) || [];
    if (staleGuards.length < 3) {
        throw new Error(`${file} ${functionName} must guard success, catch, and finally callbacks`);
    }
}

{
    const start = scriptMatch[1].indexOf("exportSceneScheduleCalendar: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var pageGeneration",
        "cancelPageLifecycleRequest(page, 'sceneScheduleCalendarExport')",
        "getSceneScheduleOccurrenceQuery(page)",
        "getPageLifecycleRequest",
        "isPageLifecycleRequestCurrent(page, pageGeneration, request)",
        "currentQuery.url === occurrenceQuery.url",
        "if (!isCurrent()) return;",
        "new Blob([String(calendar || \"\")], { type: \"text/calendar;charset=utf-8\" })",
        "if (!HueConfigurationPage.isPageLifecycleRequestCurrent(page, pageGeneration, request)) return;"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} exportSceneScheduleCalendar is missing query-scoped lifecycle protection: ${marker}`);
        }
    }
    const staleGuards = functionBody.match(/if \(!isCurrent\(\)/g) || [];
    if (staleGuards.length < 3) {
        throw new Error(`${file} exportSceneScheduleCalendar must guard success, catch, and finally callbacks`);
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
        "cancelPageLifecycleRequest(page, key)",
        "page._hueSessionHistoryLoading = false",
        "page._hueSceneScheduleRuntimeStatusLoading = false",
        "page._hueSceneScheduleConflictsLoading = false",
        "page._hueSceneScheduleConflictsExporting = false",
        "page._hueSceneScheduleConflictsCsvExporting = false",
        "page._hueSceneScheduleOccurrencesLoading = false",
        "page._hueSceneScheduleOccurrencesExporting = false",
        "page._hueSceneScheduleOccurrencesCsvExporting = false",
        "page._hueSceneScheduleCalendarExporting = false",
        "page._hueSceneScheduleHistoryLoading = false",
        "page._hueSceneScheduleHistoryExporting = false",
        "page._hueSceneScheduleHistoryCsvExporting = false",
        "page._hueSessionHistoryExporting = false",
        "page._hueSessionHistoryCsvExporting = false",
        "page._hueConfigurationExporting = false",
        "page._hueDiagnosticsLoading = false",
        "page._hueTargetDiagnosticsLoading = false",
        "page._hueDiagnosticsRequest = null",
        "page._hueTargetDiagnosticsRequest = null",
        "page._hueSupportBundleRequest = null",
        "page._hueDiagnosticLoadingOwner = null",
        "releaseDiagnosticLoading(diagnosticLoadingOwner)",
        "var previewLoadingOwner = page._huePreviewLoadingOwner",
        "page._huePreviewLoadingOwner = null",
        "releasePreviewLoading(previewLoadingOwner)",
        "if (!diagnosticLoadingOwner && !previewLoadingOwner && !HueConfigurationPage._hueGlobalLoadingOwner) Dashboard.hideLoadingMsg();"
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

    const activeSessionStart = runtimeStatusBody.indexOf("activeSessions.forEach(function (session) {");
    const activeSessionEnd = runtimeStatusBody.indexOf("sessionsElement.appendChild(sessionLine);", activeSessionStart);
    const activeSessionBody = activeSessionStart >= 0 && activeSessionEnd > activeSessionStart
        ? runtimeStatusBody.slice(activeSessionStart, activeSessionEnd)
        : "";
    for (const marker of [
        "var sessionLabel = item +",
        "sessionLine.textContent = sessionLabel;",
        "stopSessionButton.setAttribute('aria-label', 'Stop sync for ' + sessionLabel);"
    ]) {
        if (!activeSessionBody.includes(marker)) {
            throw new Error(`${file} active runtime-session stop controls are missing accessible row context: ${marker}`);
        }
    }
    const ariaLabelStart = activeSessionBody.indexOf("stopSessionButton.setAttribute('aria-label'");
    const ariaLabelEnd = activeSessionBody.indexOf(");", ariaLabelStart);
    const ariaLabelStatement = ariaLabelStart >= 0 && ariaLabelEnd > ariaLabelStart
        ? activeSessionBody.slice(ariaLabelStart, ariaLabelEnd)
        : "";
    if (ariaLabelStatement.includes("sessionId") ||
        ariaLabelStatement.includes("AppKey") ||
        ariaLabelStatement.includes("ClientKey")) {
        throw new Error(`${file} active runtime-session stop labels must not expose identifiers or credentials`);
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

    for (const methodName of ["stopRuntimeSync", "stopRuntimeSession"]) {
        const stopStart = scriptMatch[1].indexOf(`${methodName}: function`);
        const stopEnd = scriptMatch[1].indexOf("\n                },", stopStart);
        const stopBody = stopStart >= 0 && stopEnd > stopStart
            ? scriptMatch[1].slice(stopStart, stopEnd)
            : "";
        for (const marker of [
            "var loadingOwner = HueConfigurationPage.claimRuntimeStopLoading(page, null);",
            "var request;",
            "try {",
            "request = Promise.resolve(ApiClient.ajax({",
            "} catch (err) {",
            "page._hueRuntimeStopRequest = null;",
            "page._hueRuntimeStopInFlight = false;",
            "HueConfigurationPage.releaseRuntimeStopLoading(loadingOwner);",
            "return Promise.resolve();",
            "loadingOwner.request = request;"
        ]) {
            if (!stopBody.includes(marker)) {
                throw new Error(`${file} ${methodName} must recover from synchronous request construction failure: ${marker}`);
            }
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
    for (const effect of ["Solid", "Pulse", "Rainbow", "Candle", "Temperature", "Aurora", "Fire", "Ocean", "Lightning", "Starlight", "Matrix"]) {
        if (!functionBody.includes(`['${effect}', '${effect}']`)) {
            throw new Error(`${file} playlist step effect selector is missing canonical effect: ${effect}`);
        }
    }
    if (!functionBody.includes("['', 'Inherited scene']") ||
        !functionBody.includes("page._hueScenePlaylistEffects[index] = effectSelect.value || null")) {
        throw new Error(`${file} playlist step effect selector is missing null-as-inherit wiring`);
    }
    for (const marker of [
        "up.setAttribute('aria-label', 'Move Step ' + (index + 1) + ': ' + name + ' up');",
        "down.setAttribute('aria-label', 'Move Step ' + (index + 1) + ': ' + name + ' down');",
        "remove.setAttribute('aria-label', 'Remove Step ' + (index + 1) + ': ' + name + ' from playlist');"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} playlist step controls are missing row-specific accessible labeling: ${marker}`);
        }
    }
}

for (const contract of [
    {
        functionName: "registerBridge",
        buttonMarker: "page.querySelector('#registerBtn')",
        inFlightMarker: "page._hueRegistrationRequest",
        preflightMarker: "page._hueRegistrationPreflight"
    },
    {
        functionName: "registerMappingBridge",
        buttonMarker: "getMappingPageElement(page, 'mappingLinkBridgeBtn')",
        inFlightMarker: "HueConfigurationPage._hueMappingRegistrationRequest",
        preflightMarker: "HueConfigurationPage._hueMappingRegistrationPreflight"
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
        contract.preflightMarker,
        "isCurrentRegistrationTarget()",
        "if (!result || preflight.canceled",
        "var pageGeneration = HueConfigurationPage.ensurePageLifecycle(page);",
        "isPageLifecycleCurrent(page, pageGeneration)",
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
    if (!functionBody.includes("HueConfigurationPage.setConfigurationImportValidated(") ||
        !functionBody.includes("valid && canImport && !!page._hueImportConfigurationVersion") ||
        !functionBody.includes("applyButton.disabled = !page._hueImportValidated") ||
        !functionBody.includes("getPageLifecycleRequest") ||
        !functionBody.includes("'configurationImportValidation'") ||
        !functionBody.includes("isPageLifecycleRequestCurrent(page, pageGeneration, request)") ||
        !functionBody.includes("return request.then") ||
        !functionBody.includes("if (isCurrent())") ||
        !functionBody.includes("var loadingOwner = HueConfigurationPage.claimConfigurationOperationLoading(page, request);") ||
        !functionBody.includes("HueConfigurationPage.releaseConfigurationOperationLoading(loadingOwner);")) {
        throw new Error(`${file} ${functionName} must only enable import after a current, successful validation`);
    }
}

{
    const functionName = "clearConfigurationImport";
    const start = scriptMatch[1].indexOf(`\n                ${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var importReader = page._hueImportReader;",
        "page._hueImportReader = null;",
        "typeof importReader.abort === 'function'",
        "importReader.abort()"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} must abort and clear pending file readers`);
        }
    }
}

{
    const functionName = "importConfigurationFile";
    const start = scriptMatch[1].indexOf(`\n                ${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "var pageGeneration = HueConfigurationPage.ensurePageLifecycle(page);",
        "if (!HueConfigurationPage.isPageLifecycleCurrent(page, pageGeneration)) return;",
        "HueConfigurationPage.clearConfigurationImport(page);",
        "page._hueImportReader = reader;",
        "var isCurrent = function ()",
        "HueConfigurationPage.isPageLifecycleCurrent(page, pageGeneration)",
        "page._hueImportReader === reader",
        "reader.onload = function ()",
        "reader.onerror = function ()",
        "if (!isCurrent()) return;",
        "reader.readAsText(file);"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} must guard file-reader callbacks with the page lifecycle`);
        }
    }
    const onloadStart = functionBody.indexOf("reader.onload = function ()");
    const onerrorStart = functionBody.indexOf("reader.onerror = function ()");
    const onloadGuard = functionBody.indexOf("if (!isCurrent()) return;", onloadStart);
    const onerrorGuard = functionBody.indexOf("if (!isCurrent()) return;", onerrorStart);
    if (onloadGuard < onloadStart || onerrorGuard < onerrorStart) {
        throw new Error(`${file} ${functionName} must guard both reader callbacks before parsing or status writes`);
    }
}

{
    const functionName = "submitConfigurationImport";
    const start = scriptMatch[1].indexOf(`\n                ${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("if (!page._hueImportValidated || !page._hueImportConfigurationVersion)") ||
        !functionBody.includes("Validate Import successfully before importing.") ||
        !functionBody.includes("HueConfigurationPage.applyConfigurationImportCredentials(page)") ||
        !functionBody.includes("changing the bridge requires replacement App/Client keys or explicit credential clearing") ||
        !functionBody.includes("getPageLifecycleRequest") ||
        !functionBody.includes("'configurationImport'") ||
        !functionBody.includes("isPageLifecycleRequestCurrent(page, pageGeneration, request)") ||
        !functionBody.includes("if (!isCurrent()) return;") ||
        !functionBody.includes("HueConfigurationPage.loadSceneSchedules(page)")) {
        throw new Error(`${file} ${functionName} must fail closed until import preflight succeeds and its page remains current`);
    }
}

{
    const functionName = "applyConfigurationImportCredentials";
    const start = scriptMatch[1].indexOf(`\n                ${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "writeConfigurationValue(configuration, 'HueAppKey', globalAppValue);",
        "writeConfigurationValue(configuration, 'HueClientKey', globalClientValue);",
        "writeConfigurationValue(mappings[index], 'HueAppKey', value);",
        "writeConfigurationValue(mappings[index], 'HueClientKey', value);",
        "if (!replacement && value)",
        "if (!replacement) return;",
        "writeConfigurationValue(replacement, 'HueAppKey', value);",
        "writeConfigurationValue(replacement, 'HueClientKey', value);"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} must rebuild blank replacement fields instead of retaining prior credentials: ${marker}`);
        }
    }
}

for (const functionName of ["testDefaultConnection", "testMappingConnection"]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!functionBody.includes("page._huePreviewRequest = request;") ||
        !functionBody.includes("getCredentialLifecycleRequest") ||
        !functionBody.includes("isPageLifecycleTargetRequestCurrent") ||
        !functionBody.includes("cancelPageLifecycleRequest(page, 'preview')") ||
        !functionBody.includes("setDiagnosticBusy(page, true)") ||
        !functionBody.includes("setDiagnosticBusy(page, false)")) {
        throw new Error(`${file} ${functionName} is missing target-scoped cancellable diagnostic lifecycle wiring`);
    }
}

{
    const functionName = "cancelDiagnostics";
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "getPageLifecycleRequest",
        "'diagnosticsCancellation'",
        "isPageLifecycleRequestCurrent(page, pageGeneration, cancellationRequest)",
        "page._hueDiagnosticsCancellationRequest !== cancellationRequestPromise",
        "cancelPageLifecycleRequest(page, 'diagnosticsCancellation')"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} must track and invalidate its cancellation request with the page lifecycle: ${marker}`);
        }
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
    if (!functionBody.includes("getPageLifecycleRequest") ||
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

{
    const start = scriptMatch[1].indexOf("getCredentialLifecycleRequest: function");
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "typeof requestOptions === 'function' ? requestOptions() : requestOptions",
        "getPageLifecycleRequest",
        "request._huePageRequestRecord = preflightRecord",
        "page._huePageRequests[key] = preflightRecord"
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} getCredentialLifecycleRequest must retain the abortable page-owned request record: ${marker}`);
        }
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
        "page._hueScenePlaylistMappings = mappings",
        "page._hueScenePlaylistUnavailableTargetRoutes = []",
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

for (const [functionName, markers] of [
    ["applyScenePlaylist", [
        "playlist.targetRoutes !== undefined ? playlist.targetRoutes : (playlist.TargetRoutes || [])",
        "HueConfigurationPage.isScenePlaylistDeviceRouteAvailable(page, route)",
        "page._hueScenePlaylistUnavailableTargetRoutes = unavailableTargetRoutes",
        "This playlist references an unavailable or disabled device route."
    ]],
    ["getScenePlaylistTargetSelection", [
        "HueConfigurationPage.parseCurrentLightDeviceTarget(value)",
        "HueConfigurationPage.isScenePlaylistDeviceRouteAvailable(page, normalizedRoute)",
        "page._hueScenePlaylistUnavailableTargetRoutes",
        "targetRoutes: all ? [] : targetRoutes",
        "valid: !error"
    ]],
    ["saveScenePlaylist", [
        "if (!targetSelection.valid)",
        "targetRoutes: targetSelection.targetRoutes"
    ]]
]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of markers) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} is missing saved-playlist device-route contract: ${marker}`);
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

for (const [selector, keys] of [
    ["#sceneScheduleConflictFilter", [
        "sceneScheduleConflictsExport",
        "sceneScheduleConflictsCsvExport"
    ]],
    ["#sceneScheduleReportHorizon", [
        "sceneScheduleConflictsExport",
        "sceneScheduleConflictsCsvExport",
        "sceneScheduleOccurrencesExport",
        "sceneScheduleOccurrencesCsvExport",
        "sceneScheduleCalendarExport",
        "sceneScheduleHistoryExport",
        "sceneScheduleHistoryCsvExport"
    ]],
    ["#sceneScheduleOccurrenceFilter", [
        "sceneScheduleOccurrencesExport",
        "sceneScheduleOccurrencesCsvExport",
        "sceneScheduleCalendarExport"
    ]],
    ["#sceneScheduleHistoryOutcome", [
        "sceneScheduleHistoryExport",
        "sceneScheduleHistoryCsvExport"
    ]],
    ["#sceneScheduleHistoryCueFilter", [
        "sceneScheduleHistoryExport",
        "sceneScheduleHistoryCsvExport"
    ]],
    ["#sessionHistoryOutcome", [
        "sessionHistoryExport",
        "sessionHistoryCsvExport"
    ]]
]) {
    const start = scriptMatch[1].indexOf(`document.querySelector('${selector}').addEventListener('change'`);
    const end = scriptMatch[1].indexOf("\n            });", start);
    const handlerBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    if (!handlerBody.includes("var page = this.closest('.page');")) {
        throw new Error(`${file} ${selector} filter handler must resolve its page before cancelling exports`);
    }
    for (const key of keys) {
        if (!handlerBody.includes(`cancelPageLifecycleRequest(page, '${key}')`)) {
            throw new Error(`${file} ${selector} filter handler must cancel ${key}`);
        }
    }
}

for (const [functionName, requestKey, loadingProperty, targetInputId] of [
    ["discoverBridge", "bridgeDiscovery", "_hueBridgeDiscoveryLoading", "hueBridgeIp"],
    ["discoverMappingBridge", "mappingBridgeDiscovery", "_hueMappingBridgeDiscoveryLoading", "mappingBridgeIp"],
    ["discoverMappingDeviceRouteBridge", "mappingDeviceRouteBridgeDiscovery", "_hueMappingDeviceRouteBridgeDiscoveryLoading", "mappingDeviceRouteBridge"]
]) {
    const start = scriptMatch[1].indexOf(`${functionName}: function`);
    const end = scriptMatch[1].indexOf("\n                },", start);
    const functionBody = start >= 0 && end > start ? scriptMatch[1].slice(start, end) : "";
    for (const marker of [
        "ensurePageLifecycle",
        "isPageLifecycleCurrent",
        `cancelPageLifecycleRequest(page, '${requestKey}')`,
        `getPageLifecycleRequest(`,
        `'${requestKey}'`,
        "isPageLifecycleTargetRequestCurrent",
        `page.${loadingProperty}`,
        `delete page._huePageRequests.${requestKey}`,
        `page.querySelector('#${targetInputId}')`
    ]) {
        if (!functionBody.includes(marker)) {
            throw new Error(`${file} ${functionName} is missing lifecycle guard: ${marker}`);
        }
    }
}

{
    const routeStart = scriptMatch[1].indexOf("discoverMappingDeviceRouteBridge: function");
    const routeEnd = scriptMatch[1].indexOf("\n                },", routeStart);
    const routeBody = routeStart >= 0 && routeEnd > routeStart
        ? scriptMatch[1].slice(routeStart, routeEnd)
        : "";
    for (const marker of [
        "var routeTarget = HueConfigurationPage.getMappingDeviceRouteTarget(page);",
        "'mappingDeviceRouteBridgeDiscovery'",
        "page.querySelector('#mappingDeviceRouteBridge')",
        "page.querySelector('#mappingDeviceRouteBridgeCandidates')",
        "page.querySelector('#mappingDeviceRouteEditorStatus')",
        "var hasGlobalLoadingOwner = !!HueConfigurationPage._hueGlobalLoadingOwner;",
        "if (!hasGlobalLoadingOwner) Dashboard.showLoadingMsg();"
    ]) {
        if (!routeBody.includes(marker)) {
            throw new Error(`${file} route bridge discovery must remain scoped to its own request and controls: ${marker}`);
        }
    }
    if (routeBody.includes("'mappingBridgeDiscovery'") || routeBody.includes("#mappingBridgeIp")) {
        throw new Error(`${file} route bridge discovery must not reuse outer mapping bridge state`);
    }
    if (!scriptMatch[1].includes("HueConfigurationPage.discoverMappingDeviceRouteBridge(this.closest('.page'))")) {
        throw new Error(`${file} route bridge discovery button must resolve its owning page`);
    }
}

console.log(`${file}: JavaScript syntax and cancellable diagnostic contracts passed`);
