import fs from "node:fs";

const file = ".github/workflows/pull-request-validation.yml";
const workflow = fs.readFileSync(file, "utf8");
const allowedActionRepositories = new Set([
  "actions/checkout",
  "actions/setup-dotnet",
  "actions/cache",
  "actions/upload-artifact",
  "actions/download-artifact",
  "actions/github-script",
  "codecov/codecov-action"
]);

for (const marker of [
  "pull_request:",
  "types: [opened, synchronize, reopened, ready_for_review]",
  "permissions:\n  contents: read",
  "runs-on: ubuntu-24.04",
  "github.event.pull_request.number",
  "Validate pinned .NET SDK parity",
  "node scripts/validate-dotnet-sdk.mjs",
  "dotnet restore --locked-mode",
  "dotnet build --configuration ${{ env.BUILD_CONFIGURATION }} --no-restore",
  "dotnet test --configuration ${{ env.BUILD_CONFIGURATION }} --no-build",
  "dotnet format --verify-no-changes --no-restore",
  "dotnet list Jellyfin.Plugin.Hue.sln package",
  "--vulnerable",
  "gitleaks",
  "node scripts/validate-gitleaks-config.mjs",
  "semgrep",
  "--require-hashes",
  "node scripts/validate-config-page.mjs",
  "node scripts/test-config-page-exports.mjs",
  "node scripts/validate-api-docs.mjs",
  "node scripts/validate-workflow-inventory.mjs",
  "node scripts/validate-trusted-workflow.mjs",
  "Test workflow security contracts",
  "node scripts/test-workflow-contracts.mjs",
]) {
  if (!workflow.includes(marker)) {
    throw new Error(`${file} is missing PR validation marker: ${marker}`);
  }
}

for (const forbidden of [
  "pull_request_target:",
  "workflow_dispatch:",
  "contents: write"
]) {
  if (workflow.includes(forbidden)) {
    throw new Error(`${file} contains a forbidden PR validation capability: ${forbidden}`);
  }
}
if (/\$\{\{[^}]*\bsecrets\./.test(workflow)) {
  throw new Error(`${file} must not access repository secrets`);
}

const runnerLines = [...workflow.matchAll(/^\s*runs-on:\s*(.+)$/gm)].map(match => match[1].trim());
if (runnerLines.length === 0 || runnerLines.some(runner => runner !== "ubuntu-24.04")) {
  throw new Error(`${file} must run every PR job on ubuntu-24.04, never a persistent runner`);
}

for (const match of workflow.matchAll(/^\s*uses:\s*([^\s#]+)$/gm)) {
  const repository = match[1].slice(0, match[1].lastIndexOf("@"));
  if (!allowedActionRepositories.has(repository)) {
    throw new Error(`${file} uses an action outside the repository selected-action policy: ${match[1]}`);
  }
  if (!/@[0-9a-f]{40}$/.test(match[1])) {
    throw new Error(`${file} uses an action without an immutable commit SHA: ${match[1]}`);
  }
}

console.log("Pull-request workflow boundary contract passed");
