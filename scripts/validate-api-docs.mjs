import fs from "node:fs";

const readme = fs.readFileSync("README.md", "utf8");
const controller = fs.readFileSync("Jellyfin.Plugin.Hue/Api/HueApiController.cs", "utf8");
const pluginConfiguration = fs.readFileSync("Jellyfin.Plugin.Hue/Configuration/PluginConfiguration.cs", "utf8");
const automationService = fs.readFileSync("Jellyfin.Plugin.Hue/Service/HueSceneAutomationService.cs", "utf8");
const serviceRegistrator = fs.readFileSync("Jellyfin.Plugin.Hue/Service/PluginServiceRegistrator.cs", "utf8");

for (const marker of [
    "supported video and audio media",
    "bounded audio windows",
    "supported video and audio playback events",
    "Audio and AllMedia scopes process supported audio playback"
]) {
    if (!readme.includes(marker)) {
        throw new Error(`README.md media-sync documentation is missing marker: ${marker}`);
    }
}
if (readme.includes("Audio-only and other non-video playback is ignored safely.")) {
    throw new Error("README.md still contains stale audio-only playback guidance");
}

const previewEndpoints = [
    "POST /HueSync/Preview",
    "POST /HueSync/ColorPresets/{name}/Preview",
    "POST /HueSync/ColorPresets/BulkPreview",
    "POST /HueSync/ScenePlaylists/{name}/Preview",
    "POST /HueSync/ScenePlaylists/BulkPreview"
];

for (const marker of [
    '"targetUserIds": ["jellyfin-user-id-1"',
    "Target IDs are Jellyfin user IDs",
    "stable `mappingId` values are used only by exact-row mapping administration",
    "stable `mappingId` values are reserved for exact-row mapping operations"
]) {
    if (!readme.includes(marker)) {
        throw new Error(`README.md target identity contract is missing marker: ${marker}`);
    }
}
if (/"targetUserIds"\s*:\s*\[\s*"mapping-id/i.test(readme) ||
    /targetRoutes[^\n]*"userId"\s*:\s*"mapping-id/i.test(readme)) {
    throw new Error("README.md must not describe stable mapping-row IDs as target user IDs");
}

const registerRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `POST /HueSync/Register` |"));
if (!registerRow) {
    throw new Error("README.md is missing the bridge registration endpoint contract");
}
for (const marker of ["Link Button", "private", "App Key", "Client Key", "secrets", "unpinned", "certificate"]) {
    if (!registerRow.includes(marker)) {
        throw new Error(`README.md bridge registration contract is missing safety marker: ${marker}`);
    }
}

for (const endpoint of [
    "GET /HueSync/BridgeCertificate?ipAddress=...",
    "POST /HueSync/BridgeCertificate/Trust"
]) {
    const row = readme.split("\n").find(line => line.startsWith("|") && line.includes(`| \`${endpoint}\` |`));
    if (!row || !row.includes("fingerprint") || !row.includes("credential")) {
        throw new Error(`README.md is missing certificate pinning safety guidance: ${endpoint}`);
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

const captureCurrentColorRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `POST /HueSync/Preview/CaptureCurrentColor` |"));
if (!captureCurrentColorRow ||
    !captureCurrentColorRow.includes("credential-free") ||
    !captureCurrentColorRow.includes("ambiguous duplicate user mappings") ||
    !captureCurrentColorRow.includes("before bridge contact")) {
    throw new Error("README.md single current-light capture contract is missing credential-free duplicate fail-closed guidance");
}

const captureCurrentColorsRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `POST /HueSync/Preview/CaptureCurrentColors` |"));
if (!captureCurrentColorsRow ||
    !captureCurrentColorsRow.includes("credential-free") ||
    !captureCurrentColorsRow.includes("all-target capture") ||
    !captureCurrentColorsRow.includes("ambiguous duplicate user mappings")) {
    throw new Error("README.md batch current-light capture contract is missing duplicate fail-closed guidance");
}

const targetDiagnosticsRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `GET /HueSync/TargetDiagnostics` |"));
if (!targetDiagnosticsRow ||
    !targetDiagnosticsRow.includes("duplicateMappingGroups") ||
    !targetDiagnosticsRow.includes("stable row IDs") ||
    !targetDiagnosticsRow.includes("AllTargetsReady") ||
    !targetDiagnosticsRow.includes("before bridge contact") ||
    !targetDiagnosticsRow.includes("shared diagnostic lifecycle lease") ||
    !targetDiagnosticsRow.includes("409 Conflict")) {
    throw new Error("README.md target-diagnostics contract is missing duplicate-group readiness and bridge-safety guidance");
}

const supportBundleRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `GET /HueSync/Diagnostics/SupportBundle` |"));
if (!supportBundleRow ||
    !supportBundleRow.includes("one shared diagnostic lease") ||
    !supportBundleRow.includes("409 Conflict")) {
    throw new Error("README.md support-bundle contract is missing lifecycle serialization and conflict guidance");
}

const statusRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `GET /HueSync/Status` |"));
if (!statusRow || !statusRow.includes("playbackObservedAtUtc")) {
    throw new Error("README.md status contract is missing the playback observation timestamp");
}

const userMappingsRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `GET/POST /HueSync/UserMappings` |"));
if (!userMappingsRow ||
    !userMappingsRow.includes("valid Jellyfin user GUID") ||
    !userMappingsRow.includes("before configuration mutation") ||
    !userMappingsRow.includes("100 mapping rows") ||
    !userMappingsRow.includes("oversized mapping imports")) {
    throw new Error("README.md user-mapping contract is missing GUID, capacity, or fail-before-mutation markers");
}

const userMappingReconcileRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `GET/POST /HueSync/UserMappings/Reconcile` |"));
if (!userMappingReconcileRow ||
    !userMappingReconcileRow.includes("missing") ||
    !userMappingReconcileRow.includes("duplicate") ||
    !userMappingReconcileRow.includes("credential-free") ||
    !userMappingReconcileRow.includes("atomic")) {
    throw new Error("README.md user-mapping reconciliation contract is missing lifecycle and atomicity markers");
}

const userMappingCleanupRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `POST /HueSync/UserMappings/Cleanup` |"));
if (!userMappingCleanupRow ||
    !userMappingCleanupRow.includes("mapping IDs") ||
    !userMappingCleanupRow.includes("report version") ||
    !userMappingCleanupRow.includes("missing") ||
    !userMappingCleanupRow.includes("duplicate") ||
    !userMappingCleanupRow.includes("credential-free") ||
    !userMappingCleanupRow.includes("atomic")) {
    throw new Error("README.md user-mapping cleanup contract is missing exact-row, concurrency, safety, or atomicity markers");
}
if (!userMappingCleanupRow.includes("Stable row IDs")) {
    throw new Error("README.md user-mapping cleanup contract is missing stable row identity guidance");
}

const userMappingDuplicateResolutionRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `POST /HueSync/UserMappings/ResolveDuplicates` |"));
if (!userMappingDuplicateResolutionRow ||
    !userMappingDuplicateResolutionRow.includes("retainMappingId") ||
    !userMappingDuplicateResolutionRow.includes("removeMappingIds") ||
    !userMappingDuplicateResolutionRow.includes("report version") ||
    !userMappingDuplicateResolutionRow.includes("enabled") ||
    !userMappingDuplicateResolutionRow.includes("atomic") ||
    !userMappingDuplicateResolutionRow.includes("credential-free")) {
    throw new Error("README.md duplicate user-mapping resolution contract is missing exact-row, readiness, concurrency, or atomicity markers");
}

const userMappingDeleteRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `DELETE /HueSync/UserMappings/{userId}` |"));
if (!userMappingDeleteRow ||
    !userMappingDeleteRow.includes("mappingId") ||
    !userMappingDeleteRow.includes("multiple") ||
    !userMappingDeleteRow.includes("credential-free")) {
    throw new Error("README.md user-mapping delete contract is missing exact-row and duplicate safety guidance");
}

const userMappingBulkDeleteRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `POST /HueSync/UserMappings/BulkDelete` |"));
if (!userMappingBulkDeleteRow ||
    !userMappingBulkDeleteRow.includes("mappingIds") ||
    !userMappingBulkDeleteRow.includes("userIds") ||
    !userMappingBulkDeleteRow.includes("ambiguous") ||
    !userMappingBulkDeleteRow.includes("exact")) {
    throw new Error("README.md bulk user-mapping delete contract is missing exact-row and legacy duplicate safety guidance");
}

const configurationImportRow = readme.split("\n").find(line => line.startsWith("|") && line.includes("| `POST /HueSync/Configuration/Import` |"));
if (!configurationImportRow ||
    !configurationImportRow.includes("valid Jellyfin user GUIDs") ||
    !configurationImportRow.includes("canonical D-format") ||
    !configurationImportRow.includes("capped at their persisted limits before normalization") ||
    !configurationImportRow.includes("playlist step arrays and target selections") ||
    !configurationImportRow.includes("targetRoutes.userId") ||
    !configurationImportRow.includes("legacy brace/N-format routes resolve") ||
    !configurationImportRow.includes("invalid documents leave the current configuration unchanged")) {
    throw new Error("README.md configuration-import contract is missing capacity, GUID target normalization, and atomic rejection markers");
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
    '[HttpGet("BridgeCertificate")]',
    '[HttpPost("BridgeCertificate/Trust")]',
    "GetBridgeCertificateFingerprint",
    "CertificateFingerprintHeader",
    "HueBridgeCertificatePins"
]) {
    if (!controller.includes(marker) && !pluginConfiguration.includes(marker) && !serviceRegistrator.includes(marker)) {
        throw new Error(`Hue API certificate pinning support is missing source marker: ${marker}`);
    }
}

for (const marker of [
    "var matchingUserMappings = config.UserMappings",
    "multiple mapping rows",
    "selected user-mapping row no longer exists",
    "string.Equals(existing.MappingId?.Trim(), existingMapping.MappingId.Trim()",
    "Supply mappingIds or userIds, not both.",
    "AmbiguousMappingIds",
    "InvalidMappingIds = validationErrors"
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API duplicate user-mapping edit protection is missing source marker: ${marker}`);
    }
}

for (const marker of [
    "[HttpPost(\"UserMappings/Cleanup\")]",
    "ExpectedReportVersion",
    "Only missing or malformed user mappings can be cleaned automatically.",
    "referenced and duplicate rows were protected",
    "Could not persist cleanup of stale Hue user mappings"
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API stale user-mapping cleanup is missing source marker: ${marker}`);
    }
}

for (const marker of [
    "[HttpPost(\"UserMappings/ResolveDuplicates\")]",
    "HueUserMappingDuplicateResolutionRequest",
    "retainMappingId",
    "removeMappingIds",
    "The selected rows do not represent the complete duplicate group",
    "The retained mapping must be enabled before duplicate rows can be resolved.",
    "Could not persist duplicate Hue user-mapping resolution",
    "UserBridgeMappingSummary.ForSupport(retained)"
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API duplicate user-mapping resolution is missing source marker: ${marker}`);
    }
}

for (const marker of [
    "HasAmbiguousCaptureUserMapping",
    "GetAmbiguousCaptureUserMappingIds",
    "All-target current-light capture is blocked while Jellyfin user mapping(s) are ambiguous"
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API current-light capture duplicate protection is missing source marker: ${marker}`);
    }
}

for (const marker of [
    "BuildTargetDiagnosticsDuplicateMappingGroups",
    "DuplicateMappingGroups = duplicateMappingGroups",
    "The Jellyfin user has multiple mapping rows; resolve duplicate mappings before bridge validation.",
    "[ProducesResponseType(StatusCodes.Status409Conflict)]",
    "using var lifecycleLease = _bridgeLifecycleGate.TryEnterDiagnostic();",
    "BuildTargetDiagnosticsAsync(config, diagnosticsCancellationToken)",
    "Another Hue playback or diagnostic operation is already running."
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API target-diagnostics duplicate protection is missing source marker: ${marker}`);
    }
}

if (!controller.includes("Guid.TryParse(mapping.UserId?.Trim(), out var parsedUserId)") ||
    !controller.includes("mapping.UserId = parsedUserId.ToString(\"D\")") ||
    !controller.includes("userId must be a valid Jellyfin user ID.")) {
    throw new Error("Hue API user-mapping endpoint is missing Jellyfin GUID validation or canonicalization");
}

for (const marker of [
    "[HttpGet(\"UserMappings/Reconcile\")]",
    "[HttpPost(\"UserMappings/Reconcile\")]",
    "Jellyfin's user directory is not available.",
    "MissingUser",
    "DuplicateMapping",
    "Could not persist reconciled Hue user mappings"
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API user-mapping reconciliation is missing source marker: ${marker}`);
    }
}

for (const marker of [
    "public const int MaxUserMappings = 100;",
    "if (UserMappings.Count > MaxUserMappings)",
    "No more than {MaxUserMappings} user mappings may be saved"
]) {
    if (!pluginConfiguration.includes(marker)) {
        throw new Error(`PluginConfiguration user-mapping capacity guard is missing source marker: ${marker}`);
    }
}

for (const marker of [
    "var candidateMappings = previousMappings",
    "candidateMappings.Add(mapping);",
    "candidateMappings.Count > PluginConfiguration.MaxUserMappings",
    "No more than {PluginConfiguration.MaxUserMappings} user mappings may be saved."
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API user-mapping save capacity guard is missing source marker: ${marker}`);
    }
}

for (const marker of [
    "var importedMappings = request.UserMappings ?? new List<UserBridgeMappingImport>();",
    "importedMappings.Count > PluginConfiguration.MaxUserMappings",
    "No more than {PluginConfiguration.MaxUserMappings} user mappings may be imported."
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API configuration import capacity guard is missing source marker: ${marker}`);
    }
}

for (const marker of [
    "var importedPresets = request.ColorPresets ?? new List<HueColorPresetRequest>();",
    "var importCapacityErrors = new List<string>();",
    "Imported color preset collection",
    "Imported scene playlist collection",
    "Imported scene schedule collection",
    "AddImportCollectionLimitError(",
    "PluginConfiguration.MaxScenePlaylistItems",
    "PluginConfiguration.MaxSceneScheduleTargetMappings",
    "PluginConfiguration.MaxSceneScheduleExcludedDates",
    "if (importCapacityErrors.Count > 0)"
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API configuration import is missing collection capacity marker: ${marker}`);
    }
}

for (const marker of [
    "var hasValidUserId = Guid.TryParse(sourceUserId, out var parsedUserId)",
    "var normalizedUserId = hasValidUserId",
    "var exactMappingMatches = string.IsNullOrWhiteSpace(sourceMappingId)",
    "var matchingUserMappings = hasValidUserId",
    ".Select(CloneUserMapping)",
    "private static UserBridgeMapping CloneUserMapping(UserBridgeMapping mapping)",
    "matches multiple existing mapping rows; include the exact mappingId before importing.",
    "var seenMappingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)",
    "mapping => mapping.MappingId",
    "merged.FindIndex(existing => string.Equals(",
    "PluginConfiguration.AreSameJellyfinUserId(candidate.UserId, normalizedUserId)",
    "TargetUserId = PluginConfiguration.NormalizeJellyfinUserId(TargetUserId)",
    "UserId = PluginConfiguration.NormalizeJellyfinUserId(mapping.UserId)",
    "duplicates another imported user mapping",
    "user ID must be a valid Jellyfin user ID."
]) {
    if (!controller.includes(marker)) {
        throw new Error(`Hue API configuration import is missing GUID normalization marker: ${marker}`);
    }
}

for (const marker of [
    ".Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()",
    ".Select(PluginConfiguration.NormalizeJellyfinUserId).Distinct(StringComparer.OrdinalIgnoreCase).ToList()",
    "TargetUserId = PluginConfiguration.NormalizeJellyfinUserId(source.TargetUserId)"
]) {
    if (!automationService.includes(marker)) {
        throw new Error(`Hue scheduler credential-free metadata is missing canonical target-ID marker: ${marker}`);
    }
}

for (const marker of [
    "public async Task<ActionResult<HueRegistrationResult>> RegisterBridge(",
    "HueBridgeCertificateValidation.IsValidBridgeAddress(request.IpAddress)",
    "PlaybackObservedAtUtc = runtime?.PlaybackObservedAtUtc",
    "SyncStartedAtUtc = runtime?.SyncStartedAtUtc",
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
