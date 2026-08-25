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
for (const marker of ["targetAllEnabledMappings", "targetUserIds", "targetRoutes", "userId", "deviceId", "includeDefaultTarget"]) {
    if (!occurrenceCsvRow.includes(marker)) {
        throw new Error(`README.md scheduled-occurrence CSV contract is missing route field: ${marker}`);
    }
}
for (const marker of ["targetAllEnabledMappings", "targetUserIds", "targetRoutes", "userId", "deviceId", "includeDefaultTarget"]) {
    if (!historyCsvRow.includes(marker)) {
        throw new Error(`README.md scheduled-history CSV contract is missing route field: ${marker}`);
    }
}

const calendarRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `GET /HueSync/SceneSchedules/Calendar?"));
if (!calendarRow) {
    throw new Error("README.md is missing the scheduled-calendar endpoint contract");
}
for (const marker of [
    "X-HUE-TARGET-ALL-ENABLED-MAPPINGS",
    "X-HUE-TARGET-USER-IDS",
    "X-HUE-TARGET-ROUTES",
    "userId",
    "deviceId",
    "X-HUE-INCLUDE-DEFAULT-TARGET"
]) {
    if (!calendarRow.includes(marker)) {
        throw new Error(`README.md scheduled-calendar contract is missing target metadata: ${marker}`);
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

const userMappingsRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `GET/POST /HueSync/UserMappings` |"));
if (!userMappingsRow || !userMappingsRow.includes("valid Jellyfin user GUID") || !userMappingsRow.includes("before configuration mutation")) {
    throw new Error("README.md user-mapping contract is missing GUID validation and fail-before-mutation markers");
}

const configurationImportRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `POST /HueSync/Configuration/Import` |"));
if (!configurationImportRow ||
    !configurationImportRow.includes("valid Jellyfin user GUIDs") ||
    !configurationImportRow.includes("canonical D-format") ||
    !configurationImportRow.includes("targetRoutes.userId") ||
    !configurationImportRow.includes("legacy brace/N-format routes resolve") ||
    !configurationImportRow.includes("invalid documents leave the current configuration unchanged")) {
    throw new Error("README.md configuration-import contract is missing GUID target normalization and atomic rejection markers");
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

if (!controller.includes("Guid.TryParse(mapping.UserId?.Trim(), out var parsedUserId)") ||
    !controller.includes("mapping.UserId = parsedUserId.ToString(\"D\")") ||
    !controller.includes("userId must be a valid Jellyfin user ID.")) {
    throw new Error("Hue API user-mapping endpoint is missing Jellyfin GUID validation or canonicalization");
}

for (const marker of [
    "var hasValidUserId = Guid.TryParse(sourceUserId, out var parsedUserId)",
    "var normalizedUserId = hasValidUserId",
    "PluginConfiguration.AreSameJellyfinUserId(candidate.UserId, normalizedUserId)",
    "TargetUserId = PluginConfiguration.NormalizeJellyfinUserId(TargetUserId)",
    "duplicates another imported user mapping",
    "user ID must be a valid Jellyfin user ID."
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API configuration import is missing GUID normalization marker: ${marker}`);
    }
}

for (const marker of [
    "public async Task<ActionResult<HueRegistrationResult>> RegisterBridge(",
    "HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress)",
    "JsonSerializer.Serialize(run.TargetUserIds",
    "JsonSerializer.Serialize(run.TargetRoutes",
    "run.TargetAllEnabledMappings",
    "run.IncludeDefaultTarget",
    "JsonSerializer.Serialize(occurrence.TargetUserIds",
    "JsonSerializer.Serialize((occurrence.TargetRoutes",
    "occurrence.IncludeDefaultTarget",
    "X-HUE-TARGET-ALL-ENABLED-MAPPINGS",
    "X-HUE-TARGET-USER-IDS",
    "X-HUE-TARGET-ROUTES",
    "X-HUE-INCLUDE-DEFAULT-TARGET"
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API bridge registration support is missing source marker: ${marker}`);
    }
}

console.log("Preview and bridge-registration API documentation contracts passed");
