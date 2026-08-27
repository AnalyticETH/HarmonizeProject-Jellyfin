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
    configPageExcludes.length !== 35 ||
    new Set(configPageExcludes).size !== configPageExcludes.length ||
    configPageExcludes.some(rule => !/^(javascript\.express|typescript\.react)\./.test(rule))
) {
    throw new Error(
        `${configPageExcludesPath} must contain exactly 35 unique Express/React framework-rule exclusions`
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
    'default_config_path="$RUNNER_TEMP/semgrep-default.yml"',
    'javascript_config_path="$RUNNER_TEMP/semgrep-javascript.yml"',
    'python_config_path="$RUNNER_TEMP/semgrep-python.yml"',
    'curl -sSfL --retry 3 --retry-all-errors',
    '"$semgrep_venv/bin/semgrep" validate "$default_config_path"',
    '"$semgrep_venv/bin/semgrep" validate "$javascript_config_path"',
    '"$semgrep_venv/bin/semgrep" validate "$python_config_path"',
    "$SEMGREP_DEFAULT_CONFIG_SHA256",
    "$SEMGREP_JAVASCRIPT_CONFIG_SHA256",
    "$SEMGREP_PYTHON_CONFIG_SHA256",
    "node scripts/extract-inline-javascript.mjs",
    "Jellyfin.Plugin.Hue/Configuration/configPage.html",
    '"$RUNNER_TEMP/config-page-inline.js"',
    'config_rule_prefix="${RUNNER_TEMP#/}"',
    'config_rule_prefix="${config_rule_prefix//\\//.}"',
    "done < .github/semgrep/config-page-excludes.txt",
    'test "${#config_page_rule_excludes[@]}" -eq 35',
    'script_rule_prefix="${RUNNER_TEMP#/}"',
    'script_rule_prefix="${script_rule_prefix//\\//.}"',
    "done < .github/semgrep/script-excludes.txt",
    'test "${#script_rule_excludes[@]}" -eq 1'
];
const productionScanMarkers = [
    "--config \"$RUNNER_TEMP/semgrep-default.yml\" --metrics off --jobs 1 --timeout 300",
    "--include='*.cs' --include='*.yml' --include='*.yaml'",
    "--include='*.json' --include='*.ps1' --include='*.sh'",
    "--json --output semgrep-production.json ."
];
const scriptScanMarkers = [
    "--config \"$RUNNER_TEMP/semgrep-javascript.yml\" --metrics off --timeout 120",
    "--include='*.mjs'",
    "--json --output semgrep-scripts.json scripts"
];
const pythonScanMarkers = [
    "--config \"$RUNNER_TEMP/semgrep-python.yml\" --metrics off --timeout 120",
    "--include='*.py'",
    "--json --output semgrep-python.json scripts"
];
const embeddedJavaScriptScanMarkers = [
    "--config \"$RUNNER_TEMP/semgrep-javascript.yml\" --metrics off --timeout 120",
    "--include='*.js'",
    "--json --output semgrep-config-page.json \"$RUNNER_TEMP/config-page-inline.js\""
];

function countOccurrences(value, marker) {
    return value.split(marker).length - 1;
}

for (const workflowPath of semgrepWorkflows) {
    const workflow = fs.readFileSync(workflowPath, "utf8");
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
        if (!new RegExp(`${hashVariable}: ['\"][0-9a-f]{64}['\"]`).test(workflow)) {
            throw new Error(`${workflowPath}: ${hashVariable} must be a 64-character SHA-256 digest`);
        }
    }
    for (const marker of pinnedConfigRuntimeMarkers) {
        if (!workflow.includes(marker)) {
            throw new Error(`${workflowPath}: Semgrep config pinning is missing marker: ${marker}`);
        }
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
}

console.log(
    `Validated ${packages.length} hash-locked packages; semgrep==${semgrep[0].version}; ` +
    `production source/configuration, JavaScript, embedded JavaScript, and Python scans with timeout gates present in ${semgrepWorkflows.length} workflows`
);
