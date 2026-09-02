import fs from "node:fs";
import path from "node:path";

const workflowDirectory = ".github/workflows";
const MAX_TIMEOUT_MINUTES = 40;
const expectedWorkflows = new Set([
  "dotnet-ci.yml",
  "pull-request-validation.yml",
  "runner-health.yml",
  "security-scan.yml",
]);
const replacementRunner = '[self-hosted, linux, x64, local-docker]';
const trustedBuildRunner = replacementRunner;
const trustedRuntimeRunner = replacementRunner;
const trustedReleaseRunner = replacementRunner;
const pullRequestRunner = replacementRunner;
const allowedActionRepositories = new Set([
  "actions/checkout",
  "actions/setup-dotnet",
  "actions/cache",
  "actions/upload-artifact",
  "actions/download-artifact",
  "actions/github-script",
  "codecov/codecov-action",
]);
const codeownersPath = ".github/CODEOWNERS";
const codeowners = fs.readFileSync(codeownersPath, "utf8");
for (const requiredEntry of [
  "/.github/CODEOWNERS @AnalyticETH",
  "/.github/dependabot.yml @AnalyticETH",
  "/.github/semgrep/ @AnalyticETH",
  "/.github/workflows/ @AnalyticETH",
  "/ops/ @AnalyticETH",
  "/build-release.sh @AnalyticETH",
  "/build-release.ps1 @AnalyticETH",
  "/Directory.Build.props @AnalyticETH",
  "/Jellyfin.Plugin.Hue.sln @AnalyticETH",
  "/global.json @AnalyticETH",
  "/meta.json @AnalyticETH",
  "/.gitleaks.toml @AnalyticETH",
  "/codecov.yml @AnalyticETH",
  "/SECURITY.md @AnalyticETH",
  "/SELF_HOSTED_RUNNERS.md @AnalyticETH",
  "/README.md @AnalyticETH",
  "/CHANGELOG.md @AnalyticETH",
  "/Jellyfin.Plugin.Hue/ @AnalyticETH",
  "/Jellyfin.Plugin.Hue.Tests/ @AnalyticETH",
  "/Jellyfin.Plugin.Hue.Benchmarks/ @AnalyticETH",
  "/scripts/ @AnalyticETH",
]) {
  if (!codeowners.split(/\r?\n/).some((line) => line.trim() === requiredEntry)) {
    throw new Error(`${codeownersPath} is missing owner coverage: ${requiredEntry}`);
  }
}

const entries = fs.readdirSync(workflowDirectory, { withFileTypes: true });
const workflowFiles = entries
  .filter((entry) => entry.isFile() && /\.ya?ml$/i.test(entry.name))
  .map((entry) => entry.name)
  .sort();

const unexpectedWorkflows = workflowFiles.filter((name) => !expectedWorkflows.has(name));
const missingWorkflows = [...expectedWorkflows].filter((name) => !workflowFiles.includes(name));
if (unexpectedWorkflows.length > 0 || missingWorkflows.length > 0) {
  throw new Error(
    `${workflowDirectory} inventory mismatch; missing=${missingWorkflows.join(", ") || "none"}, `
      + `unexpected=${unexpectedWorkflows.join(", ") || "none"}`,
  );
}

function workflowText(name) {
  return fs.readFileSync(path.join(workflowDirectory, name), "utf8");
}

function withoutComment(line) {
  const commentIndex = line.indexOf("#");
  return commentIndex < 0 ? line : line.slice(0, commentIndex);
}

function getJobBlocks(workflow, name) {
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
    throw new Error(`${name} does not declare any parseable jobs under jobs:`);
  }
  return blocks;
}

function getRunsOnValues(block) {
  const values = [];
  for (let index = 0; index < block.lines.length; index += 1) {
    const line = withoutComment(block.lines[index]);
    const match = line.match(/^(\s*)runs-on:\s*(.*)$/);
    if (!match) continue;

    const indent = match[1].length;
    let value = match[2].trim();
    for (let continuation = index + 1; continuation < block.lines.length && !value; continuation += 1) {
      const nextLine = withoutComment(block.lines[continuation]);
      if (!nextLine.trim()) continue;
      const nextIndent = nextLine.match(/^\s*/)[0].length;
      if (nextIndent <= indent) break;
      value += ` ${nextLine.trim()}`;
    }
    values.push(value);
  }
  return values;
}

function isReusableWorkflowJob(block) {
  return block.lines.some(line => /^ {4}uses:\s*\S+/.test(withoutComment(line)));
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
        throw new Error(
          `${block ? `${workflow} job ${block.name}` : workflow} declares multiple permissions blocks`,
        );
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

function validateJobTimeout(block, workflowName) {
  const timeoutLines = block.lines
    .map(withoutComment)
    .filter(line => /^ {4}timeout-minutes:\s*/.test(line));
  if (timeoutLines.length !== 1) {
    throw new Error(
      `${workflowName} job ${block.name} must declare exactly one timeout-minutes value`
    );
  }

  const value = timeoutLines[0].replace(/^ {4}timeout-minutes:\s*/, "").trim();
  if (!/^\d+$/.test(value)) {
    throw new Error(`${workflowName} job ${block.name} timeout-minutes must be a positive integer`);
  }
  const timeoutMinutes = Number(value);
  if (timeoutMinutes < 1 || timeoutMinutes > MAX_TIMEOUT_MINUTES) {
    throw new Error(
      `${workflowName} job ${block.name} timeout-minutes must be between 1 and ${MAX_TIMEOUT_MINUTES}`
    );
  }
}

for (const name of workflowFiles) {
  const workflow = workflowText(name);
  for (const match of workflow.matchAll(/^\s*uses:\s*([^\s#]+)(?:\s+#.*)?$/gm)) {
    const reference = match[1];
    if (reference.startsWith("./")) {
      continue;
    }

    const at = reference.lastIndexOf("@");
    const repository = at > 0 ? reference.slice(0, at) : reference;
    if (!allowedActionRepositories.has(repository)) {
      throw new Error(`${name} uses an action outside the selected-action policy: ${reference}`);
    }
    if (!/@[0-9a-f]{40}$/.test(reference)) {
      throw new Error(`${name} uses an action without an immutable commit SHA: ${reference}`);
    }
  }

  if (/^\s*pull_request_target\s*:/m.test(workflow)) {
    throw new Error(`${name} must not use the privileged pull_request_target trigger`);
  }

  const jobs = getJobBlocks(workflow, name);
  for (const job of jobs) {
    // GitHub's reusable-workflow caller syntax does not accept timeout-minutes;
    // the called workflow's concrete jobs carry their own bounded timeouts.
    if (!isReusableWorkflowJob(job)) {
      validateJobTimeout(job, name);
    }
    const runsOnValues = getRunsOnValues(job);
    if (runsOnValues.some(value => value.includes("${{"))) {
      throw new Error(`${name} job ${job.name} uses a dynamic runner expression; review it explicitly`);
    }

    // Bind every concrete job to the replacement local Docker-backed runner so
    // no workflow can silently fall back to a retired runner or GitHub-hosted
    // execution while still satisfying the generic self-hosted/default-branch
    // checks below.
    if (name === "dotnet-ci.yml" && !isReusableWorkflowJob(job)) {
      const expectedRunner = replacementRunner;
      if (runsOnValues.length !== 1 || runsOnValues[0] !== expectedRunner) {
        throw new Error(`${name} job ${job.name} must use runner ${expectedRunner}`);
      }
    }
    if (name === "security-scan.yml" &&
        (runsOnValues.length !== 1 || runsOnValues[0] !== trustedBuildRunner)) {
      throw new Error(`${name} job ${job.name} must use runner ${trustedBuildRunner}`);
    }

    const selfHosted = runsOnValues.some(value => /\bself-hosted\b/.test(value));
    const hasJobMainGuard = job.lines.some(line =>
      /^\s{4}if:\s*.*github\.ref\s*==\s*['"]refs\/heads\/main['"]/.test(withoutComment(line)));
    if (selfHosted && name === "pull-request-validation.yml" &&
        (runsOnValues.length !== 1 || runsOnValues[0] !== pullRequestRunner)) {
      throw new Error(`${name} job ${job.name} must use dedicated PR runner ${pullRequestRunner}`);
    }
    if (selfHosted && !["dotnet-ci.yml", "security-scan.yml", "runner-health.yml", "pull-request-validation.yml"].includes(name)) {
      throw new Error(`${name} job ${job.name} routes code to a persistent runner outside the trusted workflow set`);
    }
    if (selfHosted && name !== "pull-request-validation.yml" && !hasJobMainGuard) {
      throw new Error(`${name} job ${job.name} has a self-hosted runner without a default-branch guard`);
    }

    if (name === "pull-request-validation.yml" &&
        (runsOnValues.length !== 1 || runsOnValues[0] !== pullRequestRunner)) {
      throw new Error(`${name} job ${job.name} must use dedicated PR runner ${pullRequestRunner}`);
    }

    if (name === "runner-health.yml" &&
        (runsOnValues.length !== 1 || runsOnValues[0] !== trustedBuildRunner)) {
      throw new Error(`${name} job ${job.name} must use runner ${trustedBuildRunner}`);
    }
  }
}

const pullRequestWorkflow = workflowText("pull-request-validation.yml");
const runnerHealthWorkflow = workflowText("runner-health.yml");
const trustedWorkflow = workflowText("dotnet-ci.yml");
for (const marker of [
  "jellyfin_version: '10.9.0'",
  "jellyfin_runtime_image: 'jellyfin/jellyfin@sha256:d659991fdbda4d2963c807747fbd1ee237bfd15971a3923158719eb248ddea67'",
  "jellyfin_version: '10.10.7'",
  "jellyfin_runtime_image: 'jellyfin/jellyfin@sha256:3b38dae4c3ddd6ebc7378538fba4d3f314070ebefbdb3d688166b7c8658fb123'",
  "JELLYFIN_RUNTIME_IMAGE: ${{ matrix.jellyfin_runtime_image }}",
  "JELLYFIN_RUNTIME_VERSION: ${{ matrix.jellyfin_version }}",
  "--container-name-prefix \"jellyfin-hue-runtime-smoke-$JELLYFIN_RUNTIME_VERSION\"",
]) {
  if (!trustedWorkflow.includes(marker)) {
    throw new Error(`dotnet-ci.yml is missing the pinned Jellyfin runtime matrix marker: ${marker}`);
  }
}
for (const workflowName of ["pull-request-validation.yml", "security-scan.yml"]) {
  if (!workflowText(workflowName).includes("node scripts/validate-gitleaks-config.mjs")) {
    throw new Error(`${workflowName} must validate the committed Gitleaks policy before scanning`);
  }
}

if (!runnerHealthWorkflow.includes("schedule:\n    - cron: '*/15 * * * *'") ||
    !runnerHealthWorkflow.includes("workflow_dispatch:")) {
  throw new Error("runner-health.yml must run on the 15-minute schedule and support workflow_dispatch");
}
if (/^\s*(push|pull_request|pull_request_target):\s*$/m.test(runnerHealthWorkflow)) {
  throw new Error("runner-health.yml must not run for pushes or pull requests");
}
const runnerHealthTopLevelPermissions = getPermissionLines(runnerHealthWorkflow);
if (!runnerHealthTopLevelPermissions.some(line => /\bactions\s*:\s*read\b/.test(line)) ||
    !runnerHealthTopLevelPermissions.some(line => /\bcontents\s*:\s*read\b/.test(line)) ||
    getWritePermissionNames(runnerHealthTopLevelPermissions).length > 0) {
  throw new Error("runner-health.yml must declare read-only actions and contents permissions");
}
if (/\$\{\{[^}]*\bsecrets\./.test(runnerHealthWorkflow)) {
  throw new Error("runner-health.yml must not access repository secrets");
}
for (const marker of [
  "actions/runs?status=queued&per_page=100",
  "actions/workflows/dotnet-ci.yml/runs?branch=main&per_page=20",
  "gh_available=true",
  "Queued Actions API was unavailable; recording a diagnostic.",
  "Trusted workflow-runs API was unavailable; recording a diagnostic.",
  "actions/runs/${latest_trusted_run_id}/jobs?per_page=100",
  "Latest successful trusted main run",
  "queued_count=\"$(jq -er '.total_count // empty'",
  "Queued Actions API response failed schema validation.",
  "Trusted workflow-runs API response failed schema validation.",
  "if [ -z \"$latest_trusted_run_id\" ]; then",
  "jobs_ready=false",
  "metadata unavailable",
  "retrying ($attempt/5)",
  "stale_cutoff=$((now_epoch - 3600))",
  "recording an operator diagnostic",
  "exit 1",
  "local-docker"
]) {
  if (!runnerHealthWorkflow.includes(marker)) {
    throw new Error(`runner-health.yml is missing health contract marker: ${marker}`);
  }
}
if (getJobBlocks(pullRequestWorkflow, "pull-request-validation.yml")
  .some(job => {
    const runners = getRunsOnValues(job);
    return runners.length !== 1 || runners[0] !== pullRequestRunner;
  })) {
  throw new Error(`pull-request-validation.yml must use only the dedicated PR runner ${pullRequestRunner}`);
}
if (/\$\{\{[^}]*\bsecrets\./.test(pullRequestWorkflow)) {
  throw new Error("pull-request-validation.yml must not access repository secrets");
}
const pullRequestTopLevelWrites = getWritePermissionNames(getPermissionLines(pullRequestWorkflow));
if (pullRequestTopLevelWrites.length > 0) {
  throw new Error(
    `pull-request-validation.yml must not grant write permissions: ${pullRequestTopLevelWrites.join(", ")}`,
  );
}
const pullRequestJobWrites = [];
for (const job of getJobBlocks(pullRequestWorkflow, "pull-request-validation.yml")) {
  const writes = getWritePermissionNames(getPermissionLines(pullRequestWorkflow, job));
  if (writes.length > 0) pullRequestJobWrites.push(`${job.name} (${writes.join(", ")})`);
}
if (pullRequestJobWrites.length > 0) {
  throw new Error(
    `pull-request-validation.yml jobs must not grant write permissions: ${pullRequestJobWrites.join(", ")}`,
  );
}

console.log(`Workflow inventory contract passed (${workflowFiles.join(", ")}; CODEOWNERS coverage verified)`);
