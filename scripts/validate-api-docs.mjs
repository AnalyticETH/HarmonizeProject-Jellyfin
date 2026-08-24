import fs from "node:fs";

const readme = fs.readFileSync("README.md", "utf8");
const controller = fs.readFileSync("Jellyfin.Plugin.Hue/Api/HueApiController.cs", "utf8");

const previewEndpoints = [
    "POST /HueSync/Preview",
    "POST /HueSync/ColorPresets/{name}/Preview",
    "POST /HueSync/ColorPresets/BulkPreview",
    "POST /HueSync/ScenePlaylists/{name}/Preview",
    "POST /HueSync/ScenePlaylists/BulkPreview"
];

const registerRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `POST /HueSync/Register` |"));
if (!registerRow) {
    throw new Error("README.md is missing the bridge registration endpoint contract");
}
for (const marker of ["Link Button", "private", "App Key", "Client Key", "secrets"]) {
    if (!registerRow.includes(marker)) {
        throw new Error(`README.md bridge registration contract is missing safety marker: ${marker}`);
    }
}

const historyCsvRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `GET /HueSync/SceneSchedules/History/ExportCsv?"));
if (!historyCsvRow) {
    throw new Error("README.md is missing the scheduled-history CSV endpoint contract");
}

const occurrenceCsvRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `GET /HueSync/SceneSchedules/Occurrences/ExportCsv?"));
if (!occurrenceCsvRow) {
    throw new Error("README.md is missing the scheduled-occurrence CSV endpoint contract");
}
for (const marker of ["targetUserIds", "targetRoutes", "userId", "deviceId", "includeDefaultTarget"]) {
    if (!occurrenceCsvRow.includes(marker)) {
        throw new Error(`README.md scheduled-occurrence CSV contract is missing route field: ${marker}`);
    }
}
for (const marker of ["targetUserIds", "targetRoutes", "userId", "deviceId", "includeDefaultTarget"]) {
    if (!historyCsvRow.includes(marker)) {
        throw new Error(`README.md scheduled-history CSV contract is missing route field: ${marker}`);
    }
}

for (const endpoint of previewEndpoints) {
    const row = readme.split("\n").find(line => line.startsWith("|") && line.includes(`| \`${endpoint}\` |`));
    if (!row) {
        throw new Error(`README.md is missing the preview endpoint contract: ${endpoint}`);
    }
    if (!row.includes("targetRoutes") || !row.includes("userId") || !row.includes("deviceId")) {
        throw new Error(`README.md preview contract is missing exact device-route fields: ${endpoint}`);
    }
}

for (const marker of [
    "public List<HueCurrentLightColorTargetRoute>? TargetRoutes",
    "public sealed class HueSavedColorPresetPreviewRequest",
    "public sealed class HueScenePlaylistPreviewRequest",
    "public sealed class HueScenePlaylistBulkPreviewRequest",
    "NormalizeSceneAutomationTargetRoutes(request.TargetRoutes)"
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API preview route support is missing source marker: ${marker}`);
    }
}

for (const marker of [
    "public async Task<ActionResult<HueRegistrationResult>> RegisterBridge(",
    "HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress)",
    "JsonSerializer.Serialize(run.TargetUserIds",
    "JsonSerializer.Serialize(run.TargetRoutes",
    "run.IncludeDefaultTarget",
    "JsonSerializer.Serialize(occurrence.TargetUserIds",
    "JsonSerializer.Serialize((occurrence.TargetRoutes",
    "occurrence.IncludeDefaultTarget"
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API bridge registration support is missing source marker: ${marker}`);
    }
}

console.log("Preview and bridge-registration API documentation contracts passed");
