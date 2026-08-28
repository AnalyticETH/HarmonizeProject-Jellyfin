import fs from "node:fs";
import path from "node:path";

const workflowDirectory = ".github/workflows";
const expectedWorkflows = new Set([
  "dotnet-ci.yml",
  "pull-request-validation.yml",
  "security-scan.yml",
]);
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
    const runsOnValues = getRunsOnValues(job);
    if (runsOnValues.some(value => value.includes("${{"))) {
      throw new Error(`${name} job ${job.name} uses a dynamic runner expression; review it explicitly`);
    }

    const selfHosted = runsOnValues.some(value => /\bself-hosted\b/.test(value));
    const hasJobMainGuard = job.lines.some(line =>
      /^\s{4}if:\s*.*github\.ref\s*==\s*['"]refs\/heads\/main['"]/.test(withoutComment(line)));
    if (selfHosted && name !== "dotnet-ci.yml" && name !== "security-scan.yml") {
      throw new Error(`${name} job ${job.name} routes code to a persistent runner outside the trusted workflow set`);
    }
    if (selfHosted && !hasJobMainGuard) {
      throw new Error(`${name} job ${job.name} has a self-hosted runner without a default-branch guard`);
    }

    if (name === "pull-request-validation.yml" && runsOnValues.some(value => value !== "ubuntu-24.04")) {
      throw new Error(`${name} job ${job.name} must run on the fixed ubuntu-24.04 runner`);
    }
  }
}

const pullRequestWorkflow = workflowText("pull-request-validation.yml");
if (getJobBlocks(pullRequestWorkflow, "pull-request-validation.yml")
  .some(job => getRunsOnValues(job).some(value => /\bself-hosted\b/.test(value)))) {
  throw new Error("pull-request-validation.yml must never use a persistent self-hosted runner");
}
if (/\$\{\{[^}]*\bsecrets\./.test(pullRequestWorkflow)) {
  throw new Error("pull-request-validation.yml must not access repository secrets");
}
if (/^\s*contents:\s*write\s*$/m.test(pullRequestWorkflow)) {
  throw new Error("pull-request-validation.yml must not grant contents: write");
}

console.log(`Workflow inventory contract passed (${workflowFiles.join(", ")}; CODEOWNERS coverage verified)`);
