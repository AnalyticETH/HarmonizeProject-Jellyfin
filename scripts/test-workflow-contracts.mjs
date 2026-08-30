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
      'runs-on: ["self-hosted", "Linux", "X64", "harmonizeproject-jellyfin"]',
      'runs-on: ["self-hosted", "Linux", "X64", "harmonizeproject-jellyfin-release"]',
    ),
  ),
  ["inventory", "trusted"],
  /must use runner/,
);

runNegativeFixture(
  "security-scan.yml",
  fixtureRoot => mutateWorkflow(
    fixtureRoot,
    "security-scan.yml",
    workflow => workflow.replace(
      'runs-on: ["self-hosted", "Linux", "X64", "harmonizeproject-jellyfin"]',
      'runs-on: ["self-hosted", "Linux", "X64", "harmonizeproject-jellyfin-release"]',
    ),
  ),
  ["inventory", "trusted"],
  /must use runner/,
);

console.log("Workflow security contract negative tests passed");
