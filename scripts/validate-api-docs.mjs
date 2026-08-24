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

console.log("Preview API documentation and target-route contracts passed");
