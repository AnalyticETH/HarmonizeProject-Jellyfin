import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const workflowNames = [
  "dotnet-ci.yml",
  "pull-request-validation.yml",
  "runner-health.yml",
  "security-scan.yml",
];
const validatorPaths = {
  inventory: path.join(repositoryRoot, "scripts/validate-workflow-inventory.mjs"),
  pullRequest: path.join(repositoryRoot, "scripts/validate-pull-request-workflow.mjs"),
  trusted: path.join(repositoryRoot, "scripts/validate-trusted-workflow.mjs"),
};

function createFixture() {
  const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "hue-workflow-contracts-"));
  const fixtureWorkflowDirectory = path.join(fixtureRoot, ".github/workflows");
  fs.mkdirSync(fixtureWorkflowDirectory, { recursive: true });
  fs.copyFileSync(
    path.join(repositoryRoot, ".github/CODEOWNERS"),
    path.join(fixtureRoot, ".github/CODEOWNERS"),
  );
  for (const workflowName of workflowNames) {
    fs.copyFileSync(
      path.join(repositoryRoot, ".github/workflows", workflowName),
      path.join(fixtureWorkflowDirectory, workflowName),
    );
  }
  return fixtureRoot;
}

function runValidator(fixtureRoot, validatorName) {
  const result = spawnSync(process.execPath, [validatorPaths[validatorName]], {
    cwd: fixtureRoot,
    encoding: "utf8",
  });
  return {
    ...result,
    output: `${result.stdout || ""}${result.stderr || ""}`,
  };
}

function expectPass(fixtureRoot, validatorName) {
  const result = runValidator(fixtureRoot, validatorName);
  assert.equal(
    result.status,
    0,
    `${validatorName} validator unexpectedly failed:\n${result.output}`,
  );
}

function expectFailure(fixtureRoot, validatorName, expectedMessage) {
  const result = runValidator(fixtureRoot, validatorName);
  assert.notEqual(
    result.status,
    0,
    `${validatorName} validator unexpectedly passed the negative fixture`,
  );
  assert.match(result.output, expectedMessage);
}

function mutateWorkflow(fixtureRoot, workflowName, mutate) {
  const workflowPath = path.join(fixtureRoot, ".github/workflows", workflowName);
  const original = fs.readFileSync(workflowPath, "utf8");
  const mutated = mutate(original);
  assert.notEqual(mutated, original, "negative workflow fixture mutation made no change");
  fs.writeFileSync(workflowPath, mutated);
}

function runNegativeFixture(workflowName, mutator, validators, expectedMessage) {
  const fixtureRoot = createFixture();
  try {
    mutator(fixtureRoot, workflowName);
    for (const validatorName of validators) {
      expectFailure(fixtureRoot, validatorName, expectedMessage);
    }
  } finally {
    fs.rmSync(fixtureRoot, { recursive: true, force: true });
  }
}

const baselineFixture = createFixture();
try {
  expectPass(baselineFixture, "inventory");
  expectPass(baselineFixture, "trusted");
} finally {
  fs.rmSync(baselineFixture, { recursive: true, force: true });
}

const hostHealthScript = fs.readFileSync(
  path.join(repositoryRoot, "scripts/check-runner-services.sh"),
  "utf8",
);
for (const runnerUnit of [
  "actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service",
  "actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-runtime.service",
  "actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-release.service",
  "actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-dependabot.service",
  "actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-pr.service",
]) {
  assert.match(hostHealthScript, new RegExp(runnerUnit.replaceAll(".", "\\.")));
}
assert.match(hostHealthScript, /is-enabled --quiet/);
assert.match(hostHealthScript, /is-active --quiet/);
assert.match(hostHealthScript, /systemctl_retry/);
assert.match(hostHealthScript, /attempt.*-ge 3/);
assert.match(hostHealthScript, /sleep \"\$attempt\"/);
assert.match(hostHealthScript, /systemctl_retry show/);
for (const expectedServiceProperty of [
  "User",
  "Group",
  "NoNewPrivileges",
  "PrivateTmp",
  "PrivateDevices",
  "ProtectSystem",
  "ProtectHome",
  "UMask",
  "LimitCORE",
  "harmonize-runner",
  "harmonize-runtime-runner",
  "harmonize-release-runner",
  "harmonize-dependabot-runner",
  "harmonize-pr-runner",
]) {
  assert.match(
    hostHealthScript,
    new RegExp(expectedServiceProperty.replaceAll(".", "\\.")),
    `host runner health check enforces ${expectedServiceProperty}`,
  );
}
assert.match(
  hostHealthScript,
  /All five Harmonize self-hosted runner services are enabled, active, correctly owned, and confined/,
);

function runHostHealthStub(overrides = {}, failures = {}) {
  const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "hue-runner-health-"));
  const fixtureBin = path.join(fixtureRoot, "bin");
  fs.mkdirSync(fixtureBin, { recursive: true });
  // Do not inherit an unrelated temporary-directory package's module type.
  fs.writeFileSync(path.join(fixtureRoot, "package.json"), '{"type":"commonjs"}\n');
  const systemctlPath = path.join(fixtureBin, "systemctl");
  const failureStatePath = path.join(fixtureRoot, "failure-state.json");
  fs.writeFileSync(failureStatePath, JSON.stringify(failures));
  const values = {
    "actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service": {
      User: "harmonize-runner",
      Group: "harmonize-runner",
      NoNewPrivileges: "yes",
      PrivateTmp: "yes",
      PrivateDevices: "yes",
      ProtectSystem: "strict",
      ProtectHome: "yes",
      UMask: "0077",
      LimitCORE: "0",
    },
    "actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-runtime.service": {
      User: "harmonize-runtime-runner",
      Group: "harmonize-runtime-runner",
      NoNewPrivileges: "yes",
      PrivateTmp: "yes",
      PrivateDevices: "yes",
      ProtectSystem: "strict",
      ProtectHome: "tmpfs",
      UMask: "0077",
      LimitCORE: "0",
    },
    "actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-release.service": {
      User: "harmonize-release-runner",
      Group: "harmonize-release-runner",
      NoNewPrivileges: "yes",
      PrivateTmp: "yes",
      PrivateDevices: "yes",
      ProtectSystem: "strict",
      ProtectHome: "yes",
      UMask: "0077",
      LimitCORE: "0",
    },
    "actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-dependabot.service": {
      User: "harmonize-dependabot-runner",
      Group: "harmonize-dependabot-runner",
      NoNewPrivileges: "yes",
      PrivateTmp: "yes",
      PrivateDevices: "yes",
      ProtectSystem: "strict",
      ProtectHome: "tmpfs",
      UMask: "0077",
      LimitCORE: "0",
    },
    "actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-pr.service": {
      User: "harmonize-pr-runner",
      Group: "harmonize-pr-runner",
      NoNewPrivileges: "yes",
      PrivateTmp: "yes",
      PrivateDevices: "yes",
      ProtectSystem: "strict",
      ProtectHome: "tmpfs",
      UMask: "0077",
      LimitCORE: "0",
    },
  };
  fs.writeFileSync(
    systemctlPath,
    `#!/usr/bin/env node
const fs = require("node:fs");
const args = process.argv.slice(2);
const values = ${JSON.stringify(values)};
const overrides = ${JSON.stringify(overrides)};
const failureStatePath = process.env.HUE_RUNNER_HEALTH_FAILURE_STATE;
let failureState = {};
try {
  failureState = JSON.parse(fs.readFileSync(failureStatePath, "utf8"));
} catch {
  process.exit(1);
}
const unit = args[0] === "show" ? args[1] : args[2];
const propertyArg = args.find(arg => arg.startsWith("--property="));
const property = propertyArg ? propertyArg.slice("--property=".length) : "";
const failureKey = args[0] === "show"
  ? args[0] + "|" + unit + "|" + property
  : args[0] + "|" + unit;
if (Number(failureState[failureKey] || 0) > 0) {
  failureState[failureKey] -= 1;
  fs.writeFileSync(failureStatePath, JSON.stringify(failureState));
  process.exit(1);
}
if (args[0] === "is-enabled" || args[0] === "is-active") {
  process.exit(0);
}
if (args[0] !== "show") {
  process.exit(1);
}
const key = unit + "|" + property;
const value = Object.prototype.hasOwnProperty.call(overrides, key)
  ? overrides[key]
  : values[unit] && values[unit][property];
if (value === undefined) {
  process.exit(1);
}
process.stdout.write(String(value) + "\\n");
`,
    { mode: 0o755 },
  );
  try {
    const result = spawnSync("sh", [path.join(repositoryRoot, "scripts/check-runner-services.sh")], {
      cwd: repositoryRoot,
      encoding: "utf8",
      env: {
        ...process.env,
        PATH: `${fixtureBin}${path.delimiter}${process.env.PATH || ""}`,
        HUE_RUNNER_HEALTH_FAILURE_STATE: failureStatePath,
      },
    });
    return {
      ...result,
      output: `${result.stdout || ""}${result.stderr || ""}`,
    };
  } finally {
    fs.rmSync(fixtureRoot, { recursive: true, force: true });
  }
}

const healthyHostHealth = runHostHealthStub();
assert.equal(
  healthyHostHealth.status,
  0,
  `host runner health check unexpectedly failed with healthy services:\n${healthyHostHealth.output}`,
);
const compromisedHostHealth = runHostHealthStub({
  "actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service|User": "root",
});
assert.notEqual(
  compromisedHostHealth.status,
  0,
  "host runner health check passed after service identity was weakened",
);
assert.match(compromisedHostHealth.output, /property=User expected=harmonize-runner actual=root/);
const transientHostHealth = runHostHealthStub({}, {
  "is-enabled|actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service": 1,
  "is-active|actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service": 1,
  "show|actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service|User": 1,
});
assert.equal(
  transientHostHealth.status,
  0,
  `host runner health check did not recover from a transient systemd query failure:\n${transientHostHealth.output}`,
);
const persistentHostHealth = runHostHealthStub({}, {
  "show|actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service|User": 3,
});
assert.notEqual(
  persistentHostHealth.status,
  0,
  "host runner health check passed after a systemd query failed through all retries",
);
assert.match(persistentHostHealth.output, /property=User expected=harmonize-runner actual=<unavailable>/);
const unconstrainedHostHealth = runHostHealthStub({
  "actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service|ProtectSystem": "no",
});
assert.notEqual(
  unconstrainedHostHealth.status,
  0,
  "host runner health check passed after filesystem confinement was weakened",
);
assert.match(unconstrainedHostHealth.output, /property=ProtectSystem expected=strict actual=no/);

for (const unitFile of [
  "ops/systemd/harmonize-runner-health-check.service",
  "ops/systemd/harmonize-runner-health-check.timer",
]) {
  const unitText = fs.readFileSync(path.join(repositoryRoot, unitFile), "utf8");
  assert.match(unitText, /harmonize-runner-health-check/);
  assert.match(unitText, /Timeout|OnUnitActiveSec/);
}
const prRunnerUnit = fs.readFileSync(
  path.join(
    repositoryRoot,
    "ops/systemd/actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-pr.service",
  ),
  "utf8",
);
for (const marker of [
  "User=harmonize-pr-runner",
  "Group=harmonize-pr-runner",
  "ACTIONS_RUNNER_HOOK_JOB_COMPLETED=/usr/local/sbin/harmonize-pr-runner-clean",
  "NoNewPrivileges=true",
  "PrivateTmp=true",
  "PrivateDevices=true",
  "ProtectSystem=strict",
  "ProtectHome=tmpfs",
  "CapabilityBoundingSet=",
  "ReadOnlyPaths=/var/lib/harmonize-pr-runner/actions-runner",
  "MemoryMax=8G",
  "CPUQuota=400%",
  "LimitCORE=0",
]) {
  assert.match(prRunnerUnit, new RegExp(marker.replaceAll(".", "\\.")));
}
const prRunnerCleanup = fs.readFileSync(
  path.join(repositoryRoot, "scripts/cleanup-pr-runner.sh"),
  "utf8",
);
assert.match(prRunnerCleanup, /RUNNER_ROOT/);
assert.match(prRunnerCleanup, /find \"\$work_root\" -depth -mindepth 1 -delete/);
const runnerVersionScript = fs.readFileSync(
  path.join(repositoryRoot, "scripts/check-runner-versions.sh"),
  "utf8",
);
assert.match(runnerVersionScript, /\/var\/lib\/harmonize-pr-runner\/actions-runner/);

runNegativeFixture(
  "dotnet-ci.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "dotnet-ci.yml",
    workflow => workflow.replace(
      "  build-and-test:\n",
      "  build-and-test:\n    permissions:\n      contents: write\n",
    ),
  ),
  ["trusted"],
  /must not grant write permissions/,
);

runNegativeFixture(
  "dotnet-ci.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "dotnet-ci.yml",
    workflow => workflow.replace(
      "    permissions:\n      contents: write\n",
      "    permissions:\n      contents: read\n",
    ),
  ),
  ["trusted"],
  /create-github-release must grant only contents: write/,
);

runNegativeFixture(
  "dotnet-ci.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "dotnet-ci.yml",
    workflow => workflow.replace("    timeout-minutes: 20\n", ""),
  ),
  ["inventory", "trusted"],
  /must declare exactly one timeout-minutes value/,
);

runNegativeFixture(
  "dotnet-ci.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "dotnet-ci.yml",
    workflow => workflow.replace(
      "    - name: Require non-empty test and coverage evidence\n",
      "",
    ),
  ),
  ["trusted"],
  /is missing trusted-workflow marker: Require non-empty test and coverage evidence/,
);

runNegativeFixture(
  "dotnet-ci.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "dotnet-ci.yml",
    workflow => workflow.replace(
      "        if-no-files-found: error\n",
      "        if-no-files-found: warn\n",
    ),
  ),
  ["trusted"],
  /must keep exactly two fail-closed test and coverage artifact uploads/,
);

runNegativeFixture(
  "pull-request-validation.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "pull-request-validation.yml",
    workflow => workflow.replace(
      "permissions:\n  contents: read",
      "permissions:\n  contents: read\n  actions: write",
    ),
  ),
  ["inventory", "pullRequest"],
  /must not grant write permissions/,
);

runNegativeFixture(
  "pull-request-validation.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "pull-request-validation.yml",
    workflow => workflow.replaceAll(
      '[self-hosted, linux, x64, local-docker]',
      '[self-hosted, linux, x64, retired-runner]',
    ),
  ),
  ["inventory", "pullRequest"],
  /missing PR validation marker|dedicated PR runner|replacement local-docker runner|retired-runner/,
);

runNegativeFixture(
  "pull-request-validation.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "pull-request-validation.yml",
    workflow => workflow.replace(
      "  build-test-and-contracts:\n",
      "  build-test-and-contracts:\n    permissions:\n      id-token: write\n",
    ),
  ),
  ["inventory", "pullRequest"],
  /must not grant write permissions/,
);

runNegativeFixture(
  "pull-request-validation.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "pull-request-validation.yml",
    workflow => workflow.replace(
      "  security-analysis:\n",
      "  security-analysis:\n    permissions: write-all\n",
    ),
  ),
  ["inventory", "pullRequest"],
  /must not grant write permissions/,
);

runNegativeFixture(
  "dotnet-ci.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "dotnet-ci.yml",
    workflow => workflow.replace(
      'runs-on: [self-hosted, linux, x64, local-docker]',
      'runs-on: [self-hosted, linux, x64, retired-runner]',
    ),
  ),
  ["inventory", "trusted"],
  /must use runner|missing trusted-workflow marker/,
);

runNegativeFixture(
  "security-scan.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "security-scan.yml",
    workflow => workflow.replace(
      'runs-on: [self-hosted, linux, x64, local-docker]',
      'runs-on: [self-hosted, linux, x64, retired-runner]',
    ),
  ),
  ["inventory", "trusted"],
  /must use runner|missing trusted-workflow marker/,
);

runNegativeFixture(
  "runner-health.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "runner-health.yml",
    workflow => workflow.replace(
      '    runs-on: [self-hosted, linux, x64, local-docker]\n',
      "    runs-on: ubuntu-24.04\n",
    ),
  ),
  ["inventory"],
  /runner-health\.yml job runner-health must use runner \[self-hosted, linux, x64, local-docker\]/,
);

runNegativeFixture(
  "runner-health.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "runner-health.yml",
    workflow => workflow.replace(
      "    if: github.ref == 'refs/heads/main'\n",
      "",
    ),
  ),
  ["inventory"],
  /runner-health\.yml job runner-health has a self-hosted runner without a default-branch guard/,
);

runNegativeFixture(
  "runner-health.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "runner-health.yml",
    workflow => workflow.replace(
      "permissions:\n  actions: read\n  contents: read",
      "permissions:\n  actions: write\n  contents: read",
    ),
  ),
  ["inventory"],
  /runner-health\.yml must declare read-only actions and contents permissions/,
);

runNegativeFixture(
  "runner-health.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "runner-health.yml",
    workflow => workflow.replace(
      "local-docker",
      "local-docker-missing",
    ),
  ),
  ["inventory"],
  /runner-health\.yml is missing health contract marker: local-docker|runner-health\.yml job runner-health must use runner/,
);

runNegativeFixture(
  "dotnet-ci.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "dotnet-ci.yml",
    workflow => workflow.replace(
      'runs-on: [self-hosted, linux, x64, local-docker]',
      'runs-on: [self-hosted, linux, x64, retired-runner]',
    ),
  ),
  ["inventory", "trusted"],
  /must use runner|missing trusted-workflow marker/,
);

console.log("Workflow security contract negative tests passed");
