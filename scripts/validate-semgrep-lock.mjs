import crypto from "node:crypto";
import fs from "node:fs";

const lockPath = process.argv[2] || ".github/semgrep/requirements.txt";
const expectedSemgrepVersion = process.argv[3] || "";
const lock = fs.readFileSync(lockPath, "utf8");
const lines = lock.split(/\r?\n/);
const packages = [];
let current = null;

function finishPackage() {
    if (!current) {
        return;
    }

    const hashes = current.lines.join("\n").match(/--hash=sha256:[0-9a-f]{64}/g) || [];
    if (hashes.length === 0) {
        throw new Error(`${lockPath}: ${current.name}==${current.version} has no SHA-256 hash`);
    }

    packages.push({
        name: current.name.toLowerCase().replace(/[-_.]+/g, "-"),
        version: current.version
    });
    current = null;
}

for (const line of lines) {
    const packageMatch = line.match(/^([A-Za-z0-9][A-Za-z0-9_.-]*)==([^\s\\]+)\s*(?:\\)?\s*$/);
    if (packageMatch) {
        finishPackage();
        current = {
            name: packageMatch[1],
            version: packageMatch[2],
            lines: [line]
        };
        continue;
    }

    if (current) {
        current.lines.push(line);
        continue;
    }

    if (line.trim() === "" || line.trim().startsWith("#") || line.trim().startsWith("--")) {
        continue;
    }

    throw new Error(`${lockPath}: unexpected content before the first pinned package: ${line}`);
}

finishPackage();

if (packages.length === 0) {
    throw new Error(`${lockPath}: no pinned packages found`);
}

const names = new Set();
for (const pkg of packages) {
    if (names.has(pkg.name)) {
        throw new Error(`${lockPath}: duplicate package entry: ${pkg.name}`);
    }
    names.add(pkg.name);
}

const semgrep = packages.filter((pkg) => pkg.name === "semgrep");
if (semgrep.length !== 1) {
    throw new Error(`${lockPath}: expected exactly one semgrep entry, found ${semgrep.length}`);
}

if (expectedSemgrepVersion && semgrep[0].version !== expectedSemgrepVersion) {
    throw new Error(
        `${lockPath}: semgrep is locked to ${semgrep[0].version}, expected ${expectedSemgrepVersion}`
    );
}

const semgrepWorkflows = [
    ".github/workflows/security-scan.yml",
    ".github/workflows/pull-request-validation.yml"
];
const configPageExcludesPath = ".github/semgrep/config-page-excludes.txt";
const configPageExcludes = fs
    .readFileSync(configPageExcludesPath, "utf8")
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(line => line && !line.startsWith("#"));
if (
    configPageExcludes.length !== 36 ||
    new Set(configPageExcludes).size !== configPageExcludes.length ||
    configPageExcludes.some(rule =>
        !/^(javascript\.express|typescript\.react)\./.test(rule) &&
        rule !== "javascript.lang.security.audit.code-string-concat.code-string-concat"
    ) ||
    !configPageExcludes.includes("javascript.lang.security.audit.code-string-concat.code-string-concat")
) {
    throw new Error(
        `${configPageExcludesPath} must contain exactly 36 unique framework-rule exclusions, including the server-only eval-taint rule`
    );
}
const scriptExcludesPath = ".github/semgrep/script-excludes.txt";
const scriptExcludes = fs
    .readFileSync(scriptExcludesPath, "utf8")
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(line => line && !line.startsWith("#"));
if (
    scriptExcludes.length !== 1 ||
    new Set(scriptExcludes).size !== scriptExcludes.length ||
    !/^javascript\.express\./.test(scriptExcludes[0])
) {
    throw new Error(
        `${scriptExcludesPath} must contain exactly one unique Express framework-rule exclusion`
    );
}
const timeoutCountExpression = "jq '(.time.fixpoint_timeouts // []) | length'";
const timeoutSummaryMarker = "Analysis timeouts:";
const timeoutGateMarker = "|| [ \"$TIMEOUTS\" -gt 0 ]";
const pinnedConfigSources = [
    ["SEMGREP_DEFAULT_CONFIG_URL", "https://semgrep.dev/c/p/default"],
    ["SEMGREP_JAVASCRIPT_CONFIG_URL", "https://semgrep.dev/c/p/javascript"],
    ["SEMGREP_PYTHON_CONFIG_URL", "https://semgrep.dev/c/p/python"]
];
const pinnedConfigRuntimeMarkers = [
    'semgrep_work_dir="$RUNNER_TEMP/semgrep-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}"',
    'semgrep_venv="$semgrep_work_dir/venv"',
    'echo "SEMGREP_WORK_DIR=$semgrep_work_dir" >> "$GITHUB_ENV"',
    'echo "SEMGREP_BIN=$SEMGREP_BIN" >> "$GITHUB_ENV"',
    'default_source_path="$GITHUB_WORKSPACE/$SEMGREP_DEFAULT_CONFIG_PATH"',
    'javascript_source_path="$GITHUB_WORKSPACE/$SEMGREP_JAVASCRIPT_CONFIG_PATH"',
    'python_source_path="$GITHUB_WORKSPACE/$SEMGREP_PYTHON_CONFIG_PATH"',
    'default_config_path="$semgrep_work_dir/semgrep-default.yml"',
    'javascript_config_path="$semgrep_work_dir/semgrep-javascript.yml"',
    'python_config_path="$semgrep_work_dir/semgrep-python.yml"',
    'test -s "$default_source_path"',
    'test -s "$javascript_source_path"',
    'test -s "$python_source_path"',
    'cp -- "$default_source_path" "$default_config_path"',
    'cp -- "$javascript_source_path" "$javascript_config_path"',
    'cp -- "$python_source_path" "$python_config_path"',
    '"$SEMGREP_BIN" validate "$default_config_path"',
    '"$SEMGREP_BIN" validate "$javascript_config_path"',
    '"$SEMGREP_BIN" validate "$python_config_path"',
    "$SEMGREP_DEFAULT_CONFIG_SHA256",
    "$SEMGREP_JAVASCRIPT_CONFIG_SHA256",
    "$SEMGREP_PYTHON_CONFIG_SHA256",
    "node scripts/extract-inline-javascript.mjs",
    "Jellyfin.Plugin.Hue/Configuration/configPage.html",
    '"$semgrep_work_dir/config-page-inline.js"',
    'config_rule_prefix="${SEMGREP_WORK_DIR#/}"',
    'config_rule_prefix="${config_rule_prefix//\\//.}"',
    "done < .github/semgrep/config-page-excludes.txt",
    'test "${#config_page_rule_excludes[@]}" -eq 36',
    'script_rule_prefix="${SEMGREP_WORK_DIR#/}"',
    'script_rule_prefix="${script_rule_prefix//\\//.}"',
    "done < .github/semgrep/script-excludes.txt",
    'test "${#script_rule_excludes[@]}" -eq 1'
];
const productionScanMarkers = [
    "--config \"$SEMGREP_WORK_DIR/semgrep-default.yml\" --metrics off --jobs 1 --timeout 300",
    "--include='*.cs' --include='*.yml' --include='*.yaml'",
    "--include='*.json' --include='*.ps1' --include='*.sh'",
    "--exclude .github/semgrep/rules",
    "--json --output \"$SEMGREP_WORK_DIR/semgrep-production.json\" ."
];
const scriptScanMarkers = [
    "--config \"$SEMGREP_WORK_DIR/semgrep-javascript.yml\" --metrics off --timeout 120",
    "--include='*.mjs'",
    "--json --output \"$SEMGREP_WORK_DIR/semgrep-scripts.json\" scripts"
];
const pythonScanMarkers = [
    "--config \"$SEMGREP_WORK_DIR/semgrep-python.yml\" --metrics off --timeout 120",
    "--include='*.py'",
    "--json --output \"$SEMGREP_WORK_DIR/semgrep-python.json\" scripts"
];
const embeddedJavaScriptScanMarkers = [
    "--config \"$SEMGREP_WORK_DIR/semgrep-javascript.yml\" --metrics off --timeout 120 --max-target-bytes=2MB",
    "--include='*.js'",
    "--json --output \"$SEMGREP_WORK_DIR/semgrep-config-page.json\" \"$SEMGREP_WORK_DIR/config-page-inline.js\""
];
const embeddedJavaScriptTargetGateMarkers = [
    "TARGET_SCANNED=$(jq --arg target \"$SEMGREP_WORK_DIR/config-page-inline.js\"",
    "any((.paths.scanned // [])[]; . == $target)",
    "if [ \"$TARGET_SCANNED\" != \"true\" ]; then",
    "did not scan the extracted target; refusing a false-clean result."
];

function countOccurrences(value, marker) {
    return value.split(marker).length - 1;
}

let baselineWorkflowHashes = null;
let baselineWorkflowPaths = null;
for (const workflowPath of semgrepWorkflows) {
    const workflow = fs.readFileSync(workflowPath, "utf8");
    const workflowHashes = [];
    const workflowPaths = [];
    for (const marker of [timeoutCountExpression, timeoutSummaryMarker, timeoutGateMarker]) {
        if (countOccurrences(workflow, marker) < 4) {
            throw new Error(`${workflowPath}: Semgrep timeout gate is missing marker: ${marker}`);
        }
    }
    for (const [variable, source] of pinnedConfigSources) {
        if (!workflow.includes(`${variable}: '${source}'`)) {
            throw new Error(`${workflowPath}: Semgrep config source is not pinned to ${source}`);
        }
        const hashVariable = `${variable.replace(/_URL$/, "")}_SHA256`;
        const hashMatch = workflow.match(new RegExp(`${hashVariable}: ['\"]([0-9a-f]{64})['\"]`));
        if (!hashMatch) {
            throw new Error(`${workflowPath}: ${hashVariable} must be a 64-character SHA-256 digest`);
        }
        workflowHashes.push(hashMatch[1]);
        const pathVariable = `${variable.replace(/_URL$/, "")}_PATH`;
        const pathMatch = workflow.match(new RegExp(`${pathVariable}: ['\"]([^'\"]+)['\"]`));
        if (!pathMatch || !/^\.github\/semgrep\/rules\/(default|javascript|python)\.yml$/.test(pathMatch[1])) {
            throw new Error(`${workflowPath}: ${pathVariable} must point to a reviewed local Semgrep snapshot`);
        }
        const configPath = pathMatch[1];
        const configHash = crypto
            .createHash("sha256")
            .update(fs.readFileSync(configPath))
            .digest("hex");
        if (configHash !== hashMatch[1]) {
            throw new Error(
                `${workflowPath}: ${configPath} hash ${configHash} does not match ${hashVariable} ${hashMatch[1]}`
            );
        }
        workflowPaths.push(configPath);
    }
    if (baselineWorkflowHashes && workflowHashes.some((hash, index) => hash !== baselineWorkflowHashes[index])) {
        throw new Error(`${workflowPath}: Semgrep SHA-256 pins must match the other blocking workflow`);
    }
    if (baselineWorkflowPaths && workflowPaths.some((path, index) => path !== baselineWorkflowPaths[index])) {
        throw new Error(`${workflowPath}: Semgrep snapshot paths must match the other blocking workflow`);
    }
    baselineWorkflowHashes = baselineWorkflowHashes || workflowHashes;
    baselineWorkflowPaths = baselineWorkflowPaths || workflowPaths;
    if (countOccurrences(workflow, "if: always() && steps.install-semgrep.outcome == 'success'") < 3) {
        throw new Error(`${workflowPath}: Semgrep scans must run after scan failures but skip a failed install`);
    }
    for (const marker of pinnedConfigRuntimeMarkers) {
        if (!workflow.includes(marker)) {
            throw new Error(`${workflowPath}: Semgrep config pinning is missing marker: ${marker}`);
        }
    }
    if (workflow.includes('semgrep_venv="$RUNNER_TEMP/semgrep-venv"') ||
        workflow.includes('"$RUNNER_TEMP/config-page-inline.js"')) {
        throw new Error(`${workflowPath}: Semgrep scanner state must not use shared RUNNER_TEMP paths`);
    }
    for (const marker of [
        ...productionScanMarkers,
        ...scriptScanMarkers,
        ...pythonScanMarkers,
        ...embeddedJavaScriptScanMarkers
    ]) {
        if (!workflow.includes(marker)) {
            throw new Error(`${workflowPath}: Semgrep target split is missing marker: ${marker}`);
        }
    }
    for (const marker of embeddedJavaScriptTargetGateMarkers) {
        if (!workflow.includes(marker)) {
            throw new Error(`${workflowPath}: embedded JavaScript scan is missing target-coverage gate marker: ${marker}`);
        }
    }
}

console.log(
    `Validated ${packages.length} hash-locked packages; semgrep==${semgrep[0].version}; ` +
    `production source/configuration, JavaScript, embedded JavaScript, and Python scans with timeout gates present in ${semgrepWorkflows.length} workflows`
);
