import fs from "node:fs";

const globalJsonPath = "global.json";
const workflowPaths = [
  ".github/workflows/dotnet-ci.yml",
  ".github/workflows/pull-request-validation.yml",
];
const globalJson = JSON.parse(fs.readFileSync(globalJsonPath, "utf8"));
const expectedVersion = globalJson.sdk?.version;

if (typeof expectedVersion !== "string" || !/^\d+\.\d+\.\d+$/.test(expectedVersion)) {
  throw new Error(`${globalJsonPath} must declare a three-part SDK version under sdk.version`);
}

function workflowSdkVersion(workflowPath) {
  const workflow = fs.readFileSync(workflowPath, "utf8");
  const matches = [...workflow.matchAll(/^  DOTNET_VERSION:\s*(['"]?)([^'"#\s]+)\1\s*$/gm)];
  if (matches.length !== 1) {
    throw new Error(`${workflowPath} must declare exactly one root env DOTNET_VERSION`);
  }
  return matches[0][2];
}

for (const workflowPath of workflowPaths) {
  const actualVersion = workflowSdkVersion(workflowPath);
  if (actualVersion !== expectedVersion) {
    throw new Error(
      `${workflowPath} DOTNET_VERSION (${actualVersion}) must match ${globalJsonPath} sdk.version (${expectedVersion})`,
    );
  }
}

console.log(`.NET SDK parity contract passed (${expectedVersion})`);
