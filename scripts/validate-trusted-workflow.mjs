import fs from "node:fs";

const ciPath = ".github/workflows/dotnet-ci.yml";
const securityPath = ".github/workflows/security-scan.yml";
const MAX_TIMEOUT_MINUTES = 30;
const trustedBuildRunner = '["self-hosted", "Linux", "X64", "harmonizeproject-jellyfin"]';
const trustedReleaseRunner = '["self-hosted", "Linux", "X64", "harmonizeproject-jellyfin-release"]';
const ci = fs.readFileSync(ciPath, "utf8");
const security = fs.readFileSync(securityPath, "utf8");
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

function getJobPermissionLines(block) {
  const lines = [];
  let inPermissions = false;
  for (const rawLine of block.lines) {
    const line = withoutComment(rawLine);
    const indent = line.match(/^\s*/)[0].length;
    const permissions = line.match(/^ {4}permissions:\s*(.*)$/);
    if (permissions) {
      if (inPermissions) {
        throw new Error(`job ${block.name} declares multiple permissions blocks`);
      }
      inPermissions = true;
      if (permissions[1].trim()) lines.push(permissions[1].trim());
      continue;
    }
    if (inPermissions) {
      if (!line.trim()) continue;
      if (indent <= 4) {
        inPermissions = false;
        continue;
      }
      lines.push(line.trim());
    }
  }
  return lines;
}

function getTopLevelPermissionLines(workflow) {
  const lines = workflow.split(/\r?\n/);
  const permissionIndex = lines.findIndex(line => /^permissions:\s*$/.test(withoutComment(line)));
  if (permissionIndex < 0) return [];

  const permissions = [];
  for (let index = permissionIndex + 1; index < lines.length; index += 1) {
    const line = withoutComment(lines[index]);
    if (!line.trim()) continue;
    if (!/^\s+/.test(line)) break;
    permissions.push(line.trim());
  }
  return permissions;
}

function getWritePermissionNames(permissionLines) {
  const names = [];
  for (const line of permissionLines) {
    const inlineNames = [...line.matchAll(/\b([A-Za-z0-9_-]+)\s*:\s*write(?:-all)?\b/g)]
      .map(match => match[1]);
    names.push(...inlineNames);
    if (inlineNames.length === 0 && /\bwrite-all\b/.test(line)) {
      names.push("*");
    }
  }
  return names;
}

function isReusableWorkflowJob(block) {
  return block.lines.some(line => /^ {4}uses:\s*\.\//.test(withoutComment(line)));
}

function getRunsOnValues(block) {
  const values = [];
  for (let index = 0; index < block.lines.length; index += 1) {
    const line = withoutComment(block.lines[index]);
    const match = line.match(/^\s*runs-on:\s*(.*)$/);
    if (!match) continue;

    const indent = match[0].match(/^\s*/)[0].length;
    let value = match[1].trim();
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

for (const marker of [
  "on:\n  push:\n    branches: [ main ]",
  "workflow_dispatch:",
  "runs-on: [\"self-hosted\", \"Linux\", \"X64\", \"harmonizeproject-jellyfin\"]",
  "runs-on: [\"self-hosted\", \"Linux\", \"X64\", \"harmonizeproject-jellyfin-release\"]",
  "if: github.event_name == 'push' && github.ref == 'refs/heads/main'",
  "uses: ./.github/workflows/security-scan.yml",
  "Validate pinned .NET SDK parity",
  "node scripts/validate-dotnet-sdk.mjs",
  "Require non-empty test and coverage evidence",
  "test_results=\"$(find ./TestResults -type f -name '*.trx' -size +0c -print -quit)\"",
  "coverage_results=\"$(find ./TestResults -type f -name 'coverage.cobertura.xml' -size +0c -print -quit)\"",
  "No non-empty TRX test result was produced",
  "No non-empty Cobertura coverage report was produced",
  "if-no-files-found: error",
  "jq -er '.version | strings | select(test(\"^[0-9]+\\\\.[0-9]+\\\\.[0-9]+\\\\.[0-9]+$\"))'",
  'local_tag_ref="refs/tags/${TAG}"',
  'git show-ref --verify --quiet "$local_tag_ref"',
  "find_release_json()",
  "gh api --paginate \"repos/${GITHUB_REPOSITORY}/releases?per_page=100\"",
  "jq -sc --arg tag \"$RELEASE_TAG\" 'add | [.[] | select(.tag_name == $tag)] | first'",
  "jq -e '.draft == true' <<<\"$release_json\"",
  'if [ "$target_sha" != "$RELEASE_SHA" ]; then',
  "--source-commit \"$GITHUB_SHA\"",
  "(.schemaVersion == 2)",
  ".sourceCommit == $source_commit",
  "CODECOV_TOKEN_PRESENT: ${{ secrets.CODECOV_TOKEN != '' }}",
  "Codecov upload skipped: CODECOV_TOKEN is not configured",
  "if: env.CODECOV_TOKEN_PRESENT == 'true'",
  "CODECOV_TOKEN: ${{ secrets.CODECOV_TOKEN }}",
  "fail_ci_if_error: true",
  "validate-release-helper:",
  "Run documented Linux release helper",
  "chmod +x ./build-release.sh",
  "./build-release.sh",
  "path: ${{ runner.temp }}/trusted-release-package",
  "CANONICAL_PACKAGE_DIR=\"$RUNNER_TEMP/trusted-release-package\"",
  "Validate workflow inventory and runner boundaries",
  "node scripts/validate-workflow-inventory.mjs",
  "Test workflow security contracts",
  "node scripts/test-workflow-contracts.mjs",
  "needs: [create-release-package, validate-release-helper]",
  "local_zip_digest=",
  "local_checksum_digest=",
  "local_manifest_digest=",
  "local_manifest_checksum_digest=",
  "expected_zip_digest=",
  "expected_manifest_digest=",
  "test \"$expected_zip_digest\" = \"$local_zip_digest\"",
  "test \"$expected_manifest_digest\" = \"$local_manifest_digest\"",
  "RELEASE_ID=",
  "gh api \"repos/${GITHUB_REPOSITORY}/releases/${RELEASE_ID}\"",
  "remote_zip_digest=",
  "remote_checksum_digest=",
  "remote_manifest_digest=",
  "remote_manifest_checksum_digest=",
  "release ZIP asset must have exactly one digest",
  "release checksum asset must have exactly one digest",
  "release manifest asset must have exactly one digest",
  "release manifest checksum asset must have exactly one digest",
  "test \"$remote_zip_digest\" = \"sha256:${local_zip_digest}\"",
  "test \"$remote_checksum_digest\" = \"sha256:${local_checksum_digest}\"",
  "test \"$remote_manifest_digest\" = \"sha256:${local_manifest_digest}\"",
  "test \"$remote_manifest_checksum_digest\" = \"sha256:${local_manifest_checksum_digest}\"",
  '--arg zip_digest "sha256:${local_zip_digest}"',
  '--arg checksum_digest "sha256:${local_checksum_digest}"',
  '--arg manifest_digest "sha256:${local_manifest_digest}"',
  '--arg manifest_checksum_digest "sha256:${local_manifest_checksum_digest}"',
  '(.digest == $zip_digest)',
  '(.digest == $checksum_digest)',
  "-F draft=false",
  "-F prerelease=false",
  "target_commitish == $sha",
]) {
  if (!ci.includes(marker)) {
    throw new Error(`${ciPath} is missing trusted-workflow marker: ${marker}`);
  }
}

for (const marker of [
  "workflow_call:",
  "schedule:",
  "if: github.ref == 'refs/heads/main'",
  "runs-on: [\"self-hosted\", \"Linux\", \"X64\", \"harmonizeproject-jellyfin\"]",
  "--redact --exit-code 1",
  "SEMGREP_DEFAULT_CONFIG_URL: 'https://semgrep.dev/c/p/default'",
  "SEMGREP_DEFAULT_CONFIG_SHA256:",
  "sha256sum --check --strict -",
  "Validate workflow inventory and runner boundaries",
  "node scripts/validate-workflow-inventory.mjs",
]) {
  if (!security.includes(marker)) {
    throw new Error(`${securityPath} is missing blocking-security marker: ${marker}`);
  }
}

const selfHostedJobCount = (ci.match(/runs-on: \["self-hosted"/g) || []).length;
if (selfHostedJobCount !== 5) {
  throw new Error(`${ciPath} must keep exactly five self-hosted jobs (found ${selfHostedJobCount})`);
}
const mainGuardCount = (ci.match(/if: github\.ref == 'refs\/heads\/main'/g) || []).length;
if (mainGuardCount !== 3) {
  throw new Error(`${ciPath} must keep three direct main-only job guards (found ${mainGuardCount})`);
}
const securityGuardCount = (security.match(/if: github\.ref == 'refs\/heads\/main'/g) || []).length;
if (securityGuardCount !== 2) {
  throw new Error(`${securityPath} must keep both main-only scanner guards (found ${securityGuardCount})`);
}

const ciJobs = getJobBlocks(ci, ciPath);
const securityJobs = getJobBlocks(security, securityPath);

const buildAndTestJob = ciJobs.find(job => job.name === "build-and-test");
if (!buildAndTestJob) {
  throw new Error(`${ciPath} is missing the build-and-test job required for test and coverage evidence`);
}
const failClosedArtifactUploads = buildAndTestJob.lines.filter(line =>
  /^\s+if-no-files-found:\s*error\s*$/.test(withoutComment(line)),
).length;
if (failClosedArtifactUploads !== 2) {
  throw new Error(
    `${ciPath} build-and-test must keep exactly two fail-closed test and coverage artifact uploads `
      + `(found ${failClosedArtifactUploads})`,
  );
}

for (const job of ciJobs) {
  if (isReusableWorkflowJob(job)) continue;
  const expectedRunner = job.name === "create-github-release"
    ? trustedReleaseRunner
    : trustedBuildRunner;
  const runsOnValues = getRunsOnValues(job);
  if (runsOnValues.length !== 1 || runsOnValues[0] !== expectedRunner) {
    throw new Error(`${ciPath} job ${job.name} must use runner ${expectedRunner}`);
  }
}
for (const job of securityJobs) {
  const runsOnValues = getRunsOnValues(job);
  if (runsOnValues.length !== 1 || runsOnValues[0] !== trustedBuildRunner) {
    throw new Error(`${securityPath} job ${job.name} must use runner ${trustedBuildRunner}`);
  }
}

for (const [workflowName, jobs] of [[ciPath, ciJobs], [securityPath, securityJobs]]) {
  for (const job of jobs) {
    // GitHub's reusable-workflow caller syntax does not accept timeout-minutes;
    // the called workflow's concrete jobs carry their own bounded timeouts.
    if (!isReusableWorkflowJob(job)) {
      validateJobTimeout(job, workflowName);
    }
  }
}

const ciPermissionWrites = [];
for (const job of ciJobs) {
  const writes = getWritePermissionNames(getJobPermissionLines(job));
  if (job.name === "create-github-release") {
    if (writes.length !== 1 || writes[0] !== "contents") {
      throw new Error(`${ciPath} job create-github-release must grant only contents: write`);
    }
  } else if (writes.length > 0) {
    ciPermissionWrites.push(job.name);
  }
}
if (ciPermissionWrites.length > 0) {
  throw new Error(
    `${ciPath} jobs other than create-github-release must not grant write permissions: ${ciPermissionWrites.join(", ")}`
  );
}

const securityPermissionWrites = [];
for (const job of securityJobs) {
  const writes = getWritePermissionNames(getJobPermissionLines(job));
  if (writes.length > 0) securityPermissionWrites.push(job.name);
}
if (securityPermissionWrites.length > 0) {
  throw new Error(
    `${securityPath} jobs must not grant write permissions: ${securityPermissionWrites.join(", ")}`
  );
}

for (const [workflowName, workflow] of [[ciPath, ci], [securityPath, security]]) {
  const topLevelPermissions = getTopLevelPermissionLines(workflow);
  if (!topLevelPermissions.some(line => /\bcontents\s*:\s*read\b/.test(line)) ||
      getWritePermissionNames(topLevelPermissions).length > 0) {
    throw new Error(`${workflowName} must declare read-only top-level permissions`);
  }
}

for (const [file, workflow] of [[ciPath, ci], [securityPath, security]]) {
  for (const match of workflow.matchAll(/^\s*uses:\s*([^\s#]+)$/gm)) {
    const reference = match[1];
    if (reference.startsWith("./")) {
      continue;
    }
    const repository = reference.slice(0, reference.lastIndexOf("@"));
    if (!allowedActionRepositories.has(repository)) {
      throw new Error(`${file} uses an action outside the repository selected-action policy: ${reference}`);
    }
    if (!/@[0-9a-f]{40}$/.test(reference)) {
      throw new Error(`${file} uses an action without an immutable commit SHA: ${reference}`);
    }
  }
}

if (/^\s*pull_request\s*:/m.test(ci) || /^\s*pull_request_target\s*:/m.test(ci)) {
  throw new Error(`${ciPath} must not execute trusted jobs for pull requests`);
}
if (!/contents:\s*read/.test(ci) || !/contents:\s*write/.test(ci)) {
  throw new Error(`${ciPath} must declare read-only defaults and isolated release write permissions`);
}

console.log("Trusted workflow boundary contract passed");
