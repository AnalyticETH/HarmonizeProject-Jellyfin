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
    'id="sceneScheduleHistoryCueFilter"'
];

for (const marker of requiredMarkup) {
    if (!html.includes(marker)) {
        throw new Error(`${file} is missing required markup: ${marker}`);
    }
}

const requiredScript = [
    "setDiagnosticBusy: function",
    "setDiagnosticsBusy: function",
    "HueConfigurationPage.cancelDiagnostics(e.target)",
    'url: ApiClient.getUrl("HueSync/Preview/Cancel")',
    'url: ApiClient.getUrl("HueSync/Diagnostics/Cancel")',
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
    "exportUrl += \"&scheduleId=\""
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

console.log(`${file}: JavaScript syntax and cancellable diagnostic contracts passed`);
