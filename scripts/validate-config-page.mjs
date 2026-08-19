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
    'id="mappingPlaybackMediaFilterOverride"',
    'id="runtimePlaybackMediaFilter"',
    'id="previewEffect"',
    'id="previewEffectSpeed"',
    'id="exportSceneScheduleConflictsBtn"',
    'id="exportSceneScheduleOccurrencesBtn"',
    'id="sceneScheduleReportHorizon"',
    'value="7">Next 7 days',
    'value="31" selected>Next 31 days',
    'value="90">Next 90 days',
    'value="366">Next 366 days',
    'id="sceneScheduleOccurrenceFilter"',
    'id="sceneScheduleHistoryCueFilter"',
    'id="sceneScheduleBulkSelect"',
    'id="enableSelectedSceneSchedulesBtn"',
    'id="disableSelectedSceneSchedulesBtn"',
    'id="skipSelectedSceneSchedulesBtn"',
    'id="clearSelectedSceneScheduleSkipsBtn"',
    'id="deleteSelectedSceneSchedulesBtn"',
    'id="configurationImportDiff"',
    'id="exportSessionHistoryCsvBtn"',
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
    "getConfigValue('PlaybackMediaFilter', 'AllVideo')",
    "ConfiguredPlaybackMediaFilter",
    "ActivePlaybackMediaFilter",
    "PlaybackMediaFilterOverride: readOptionalString('mappingPlaybackMediaFilterOverride')",
    "getMappingValue('PlaybackMediaFilterOverride', 'playbackMediaFilterOverride')",
    "HueConfigurationPage.cancelPreview(e.target)",
    '["Solid", "Pulse", "Rainbow", "Candle"]',
    'effect: effect || "Solid"',
    'effectSpeedPercent: effectSpeedPercent || 100',
    'effectSpeedPercent: values.effectSpeedPercent',
    "effect: values.effect",
    "populateSceneScheduleCueFilters: function",
    "getSceneScheduleReportHorizon: function",
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
    "deleteSceneSchedulesBulk: function",
    "HueSync/SceneSchedules/BulkDelete",
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
