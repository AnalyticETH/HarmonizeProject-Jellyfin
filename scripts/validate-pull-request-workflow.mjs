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

function withoutComment(line) {
  const commentIndex = line.indexOf("#");
  return commentIndex < 0 ? line : line.slice(0, commentIndex);
}

function getJobBlocks(workflow) {
  const lines = workflow.split(/\r?\n/);
  const blocks = [];
  let inJobs = false;
  let current = null;

  for (const line of lines) {
    if (!inJobs) {
      if (/^jobs:\s*$/.test(withoutComment(line).trimEnd())) {
        inJobs = true;
      }
      continue;
    }

    const jobHeader = line.match(/^  ([A-Za-z0-9_-]+):\s*(?:#.*)?$/);
    if (jobHeader) {
      if (current) blocks.push(current);
      current = { name: jobHeader[1], lines: [line] };
      continue;
    }

    if (current) current.lines.push(line);
  }
  if (current) blocks.push(current);
  if (blocks.length === 0) {
    throw new Error(`${file} does not declare any parseable jobs under jobs:`);
  }
  return blocks;
}

function getPermissionLines(workflow, block = null) {
  const lines = block ? block.lines : workflow.split(/\r?\n/);
  const permissions = [];
  let inPermissions = false;

  for (const rawLine of lines) {
    const line = withoutComment(rawLine);
    const indent = line.match(/^\s*/)[0].length;
    const permission = block
      ? line.match(/^ {4}permissions:\s*(.*)$/)
      : line.match(/^permissions:\s*(.*)$/);
    if (permission) {
      if (inPermissions) {
        throw new Error(`${file}${block ? ` job ${block.name}` : ""} declares multiple permissions blocks`);
      }
      inPermissions = true;
      if (permission[1].trim()) permissions.push(permission[1].trim());
      continue;
    }
    if (inPermissions) {
      if (!line.trim()) continue;
      const minimumChildIndent = block ? 4 : 0;
      if (indent <= minimumChildIndent) {
        inPermissions = false;
        continue;
      }
      permissions.push(line.trim());
    }
  }
  return permissions;
}

function getWritePermissionNames(permissionLines) {
  const names = [];
  for (const line of permissionLines) {
    if (/\bwrite-all\b/.test(line)) names.push("*");
    const inlineNames = [...line.matchAll(
      /\b([A-Za-z0-9_-]+)\s*:\s*["']?write(?:-all)?["']?\b/g,
    )].map(match => match[1]);
    names.push(...inlineNames);
  }
  return names;
}

for (const marker of [
  "pull_request:",
  "types: [opened, synchronize, reopened, ready_for_review]",
  "permissions:\n  contents: read",
  "runs-on: [self-hosted, linux, x64, local-docker]",
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
  "workflow_dispatch:"
]) {
  if (workflow.includes(forbidden)) {
    throw new Error(`${file} contains a forbidden PR validation capability: ${forbidden}`);
  }
}
if (/\$\{\{[^}]*\bsecrets\./.test(workflow)) {
  throw new Error(`${file} must not access repository secrets`);
}

const topLevelWrites = getWritePermissionNames(getPermissionLines(workflow));
if (topLevelWrites.length > 0) {
  throw new Error(`${file} must not grant write permissions: ${topLevelWrites.join(", ")}`);
}

const jobWrites = [];
for (const job of getJobBlocks(workflow)) {
  const writes = getWritePermissionNames(getPermissionLines(workflow, job));
  if (writes.length > 0) jobWrites.push(`${job.name} (${writes.join(", ")})`);
}
if (jobWrites.length > 0) {
  throw new Error(`${file} jobs must not grant write permissions: ${jobWrites.join(", ")}`);
}

const runnerLines = [...workflow.matchAll(/^\s*runs-on:\s*(.+)$/gm)].map(match => match[1].trim());
if (runnerLines.length === 0 || runnerLines.some(
  runner => runner !== "[self-hosted, linux, x64, local-docker]",
)) {
  throw new Error(`${file} must run every PR job on the replacement local-docker runner`);
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
