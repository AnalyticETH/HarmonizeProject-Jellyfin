import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const workflow = fs.readFileSync(path.join(repositoryRoot, ".github/workflows/dotnet-ci.yml"), "utf8").replaceAll("\r\n", "\n");
const releaseJob = workflow.split(/\n(?=  [A-Za-z0-9_-]+:\n)/)
  .find(block => block.startsWith("  create-github-release:\n"));
assert(releaseJob, "The publication job is missing");
const tagStep = releaseJob.split(/(?=^    - (?:name|uses):)/m)
  .find(block => block.startsWith("    - name: Pin release tag to workflow commit\n"));
assert(tagStep, "The release tag step is missing");
const stepLines = tagStep.split("\n");
const runIndex = stepLines.indexOf("      run: |");
assert(runIndex >= 0, "The release tag step must contain an executable shell block");
const tagScript = stepLines.slice(runIndex + 1).map(line => line.slice(8)).join("\n");

const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "hue-release-tag-contracts-"));
const sourceCommit = "a".repeat(40);
const priorCommit = "b".repeat(40);
const otherCommit = "c".repeat(40);
const releaseTag = "v1.2.3.4";
let cases = 0;

function releaseJson({ target, draft = false, prerelease = false }) {
  return JSON.stringify([{
    id: 123,
    tag_name: releaseTag,
    target_commitish: target,
    draft,
    prerelease,
  }]);
}

function runCase(name, {
  remoteTagSha = "",
  gitLookupStatus = 0,
  releaseResponse = "[]",
  expectedStatus,
  expectSkip = false,
  expectPush = false,
}) {
  const caseRoot = fs.mkdtempSync(path.join(fixtureRoot, "case-"));
  const bin = path.join(caseRoot, "bin");
  fs.mkdirSync(bin);
  const outputPath = path.join(caseRoot, "github-output");
  const logPath = path.join(caseRoot, "git-log");
  const responsePath = path.join(caseRoot, "release.json");
  const statePath = path.join(caseRoot, "git-pushed");
  fs.writeFileSync(path.join(caseRoot, "remote-tag"), remoteTagSha
    ? `${remoteTagSha}\trefs/tags/${releaseTag}\n`
    : "");
  fs.writeFileSync(responsePath, releaseResponse);
  fs.writeFileSync(logPath, "");
  fs.writeFileSync(
    path.join(bin, "git"),
    `#!/bin/sh
set -eu
case "$1" in
  ls-remote)
    if [ "${gitLookupStatus}" -ne 0 ] && [ ! -f "$FAKE_GIT_PUSH_STATE" ]; then
      exit "${gitLookupStatus}"
    fi
    if [ -f "$FAKE_GIT_PUSH_STATE" ]; then
      printf '%s\\trefs/tags/%s\\n' "$FAKE_GIT_CREATED_SHA" "$RELEASE_TAG"
    else
      cat "$FAKE_GIT_REMOTE_TAG"
    fi
    ;;
  show-ref)
    exit 1
    ;;
  tag)
    printf 'tag %s\\n' "$*" >> "$FAKE_GIT_LOG"
    ;;
  push)
    printf 'push %s\\n' "$*" >> "$FAKE_GIT_LOG"
    : > "$FAKE_GIT_PUSH_STATE"
    ;;
  *)
    echo "unexpected git invocation: $*" >&2
    exit 1
    ;;
esac
`, { mode: 0o755 },
  );
  fs.writeFileSync(
    path.join(bin, "gh"),
    `#!/bin/sh
set -eu
if [ "$1" != "api" ]; then
  echo "unexpected gh invocation: $*" >&2
  exit 1
fi
cat "$FAKE_GH_RESPONSE"
`, { mode: 0o755 },
  );

  const result = spawnSync("bash", ["-eo", "pipefail", "-c", tagScript], {
    cwd: caseRoot,
    encoding: "utf8",
    timeout: 10_000,
    env: {
      ...process.env,
      PATH: `${bin}${path.delimiter}${process.env.PATH || ""}`,
      FAKE_GIT_REMOTE_TAG: path.join(caseRoot, "remote-tag"),
      FAKE_GIT_LOG: logPath,
      FAKE_GIT_PUSH_STATE: statePath,
      FAKE_GIT_CREATED_SHA: sourceCommit,
      FAKE_GH_RESPONSE: responsePath,
      GITHUB_OUTPUT: outputPath,
      GITHUB_REPOSITORY: "AnalyticETH/HarmonizeProject-Jellyfin",
      GITHUB_SHA: sourceCommit,
      GITHUB_TOKEN: "fixture-token",
      RELEASE_TAG: releaseTag,
      RELEASE_SHA: sourceCommit,
    },
  });
  assert.ifError(result.error);
  const output = `${result.stdout || ""}${result.stderr || ""}`;
  assert.equal(result.status, expectedStatus, `${name} returned an unexpected status:\n${output}`);
  const stepOutput = fs.existsSync(outputPath) ? fs.readFileSync(outputPath, "utf8") : "";
  assert.equal(stepOutput.includes("skip=true"), expectSkip, `${name} had unexpected skip output`);
  assert.equal(fs.readFileSync(logPath, "utf8").includes("push "), expectPush, `${name} had unexpected tag push activity`);
  cases += 1;
}

try {
  runCase("new tag publication", {
    expectedStatus: 0,
    expectPush: true,
  });
  runCase("already-published prior version is a no-op", {
    remoteTagSha: priorCommit,
    releaseResponse: releaseJson({ target: priorCommit }),
    expectedStatus: 0,
    expectSkip: true,
  });
  runCase("already-published current version is a no-op", {
    remoteTagSha: sourceCommit,
    releaseResponse: releaseJson({ target: sourceCommit }),
    expectedStatus: 0,
    expectSkip: true,
  });
  runCase("current tag without a release remains recoverable", {
    remoteTagSha: sourceCommit,
    expectedStatus: 0,
  });
  runCase("current stable draft remains resumable", {
    remoteTagSha: sourceCommit,
    releaseResponse: releaseJson({ target: sourceCommit, draft: true }),
    expectedStatus: 0,
  });
  runCase("prior draft fails closed", {
    remoteTagSha: priorCommit,
    releaseResponse: releaseJson({ target: priorCommit, draft: true }),
    expectedStatus: 1,
  });
  runCase("conflicting release target fails closed", {
    remoteTagSha: priorCommit,
    releaseResponse: releaseJson({ target: otherCommit }),
    expectedStatus: 1,
  });
  runCase("tag lookup failure fails closed", {
    gitLookupStatus: 2,
    expectedStatus: 2,
  });
  console.log(`Release tag contract tests passed (${cases} cases)`);
} finally {
  fs.rmSync(fixtureRoot, { recursive: true, force: true });
}
