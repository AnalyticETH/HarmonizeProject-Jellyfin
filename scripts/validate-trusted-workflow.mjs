import fs from "node:fs";

const ciPath = ".github/workflows/dotnet-ci.yml";
const securityPath = ".github/workflows/security-scan.yml";
const ci = fs.readFileSync(ciPath, "utf8");
const security = fs.readFileSync(securityPath, "utf8");
const allowedActionRepositories = new Set([
  "actions/checkout",
  "actions/setup-dotnet",
  "actions/cache",
  "actions/upload-artifact",
  "actions/download-artifact",
  "codecov/codecov-action"
]);

for (const marker of [
  "on:\n  push:\n    branches: [ main ]",
  "workflow_dispatch:",
  "runs-on: [\"self-hosted\", \"Linux\", \"X64\", \"harmonizeproject-jellyfin\"]",
  "runs-on: [\"self-hosted\", \"Linux\", \"X64\", \"harmonizeproject-jellyfin-release\"]",
  "if: github.event_name == 'push' && github.ref == 'refs/heads/main'",
  "permissions:\n      contents: write",
  "uses: ./.github/workflows/security-scan.yml",
  "jq -er '.version | strings | select(test(\"^[0-9]+\\\\.[0-9]+\\\\.[0-9]+\\\\.[0-9]+$\"))'",
  'local_tag_ref="refs/tags/${TAG}"',
  'git show-ref --verify --quiet "$local_tag_ref"',
  "CODECOV_TOKEN_PRESENT: ${{ secrets.CODECOV_TOKEN != '' }}",
  "Codecov upload skipped: CODECOV_TOKEN is not configured",
  "if: env.CODECOV_TOKEN_PRESENT == 'true'",
  "CODECOV_TOKEN: ${{ secrets.CODECOV_TOKEN }}",
  "fail_ci_if_error: true",
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
  "--config p/default",
]) {
  if (!security.includes(marker)) {
    throw new Error(`${securityPath} is missing blocking-security marker: ${marker}`);
  }
}

const selfHostedJobCount = (ci.match(/runs-on: \["self-hosted"/g) || []).length;
if (selfHostedJobCount !== 4) {
  throw new Error(`${ciPath} must keep exactly four self-hosted jobs (found ${selfHostedJobCount})`);
}
const mainGuardCount = (ci.match(/if: github\.ref == 'refs\/heads\/main'/g) || []).length;
if (mainGuardCount !== 3) {
  throw new Error(`${ciPath} must keep three direct main-only job guards (found ${mainGuardCount})`);
}
const securityGuardCount = (security.match(/if: github\.ref == 'refs\/heads\/main'/g) || []).length;
if (securityGuardCount !== 2) {
  throw new Error(`${securityPath} must keep both main-only scanner guards (found ${securityGuardCount})`);
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
