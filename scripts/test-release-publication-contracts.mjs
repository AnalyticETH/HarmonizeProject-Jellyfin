import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const workflow = fs.readFileSync(path.join(repositoryRoot, ".github/workflows/dotnet-ci.yml"), "utf8").replaceAll("\r\n", "\n");
const releaseJob = workflow.split(/\n(?=  [A-Za-z0-9_-]+:\n)/)
  .find(block => block.startsWith("  create-github-release:\n"));
assert(releaseJob, "The publication job is missing");
const verificationSteps = releaseJob.split(/(?=^    - (?:name|uses):)/m)
  .filter(block => block.startsWith("    - name: Verify release package checksum\n"));
assert.equal(verificationSteps.length, 1, "Publication must have exactly one package verification step");
const verificationStep = verificationSteps[0];
const stepLines = verificationStep.split("\n");
const runIndex = stepLines.indexOf("      run: |");
assert(runIndex >= 0, "The package verification step must contain an executable shell block");
const runLines = stepLines.slice(runIndex + 1);
assert(runLines.every(line => !line.trim() || line.startsWith("        ")), "Unexpected package verification block structure");
const verificationScript = runLines.map(line => line.slice(8)).join("\n");

const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "hue-publication-contracts-"));
const version = "1.2.3.4";
const sourceCommit = "a".repeat(40);
const packageNames = ["BouncyCastle.Cryptography.dll", "Jellyfin.Plugin.Hue.dll", "LICENSE", "NOTICE", "meta.json"];
const archiveName = "jellyfin-plugin-hue-release.zip";
const manifestName = "jellyfin-plugin-hue-release.manifest.json";
const archivePath = path.join(fixtureRoot, archiveName);
const manifestPath = path.join(fixtureRoot, manifestName);
let archiveBytes;
let manifestText;
let cases = 0;

function checksum(name) {
  const digest = createHash("sha256").update(fs.readFileSync(path.join(fixtureRoot, name))).digest("hex");
  fs.writeFileSync(path.join(fixtureRoot, `${name}.sha256`), `${digest}  ${name}\n`);
}

function writeManifest(text) {
  fs.writeFileSync(manifestPath, text);
  checksum(manifestName);
}

function changeManifest(change) {
  const manifest = JSON.parse(manifestText);
  change(manifest);
  writeManifest(JSON.stringify(manifest));
}

function check(name, accepted, prepare = () => {}) {
  fs.writeFileSync(archivePath, archiveBytes);
  checksum(archiveName);
  writeManifest(manifestText);
  prepare();
  const result = spawnSync("bash", ["-eo", "pipefail", "-c", verificationScript], {
    cwd: fixtureRoot,
    encoding: "utf8",
    env: { ...process.env, GITHUB_SHA: sourceCommit, RELEASE_VERSION: version },
    timeout: 10_000,
  });
  assert.ifError(result.error);
  const output = `${result.stdout || ""}${result.stderr || ""}`;
  if (accepted) assert.equal(result.status, 0, `${name} was rejected:\n${output}`);
  else assert.notEqual(result.status, 0, `${name} was accepted:\n${output}`);
  cases += 1;
}

try {
  const python = (process.platform === "win32" ? ["python", "python3"] : ["python3", "python"])
    .find(command => spawnSync(command, ["--version"], { encoding: "utf8" }).status === 0);
  assert(python, "Python 3 is required for producer-to-publication contract tests");
  const packageDirectory = path.join(fixtureRoot, "release-package");
  fs.mkdirSync(packageDirectory);
  for (const name of packageNames) {
    fs.writeFileSync(path.join(packageDirectory, name), name === "meta.json"
      ? JSON.stringify({ version }) : `fixture bytes for ${name}\n`);
  }
  fs.writeFileSync(path.join(fixtureRoot, "packages.lock.json"), JSON.stringify({
    version: 1,
    dependencies: { "net8.0": { Example: { type: "Direct", resolved: "1.0.0", contentHash: "fixture-content-hash" } } },
  }));
  for (const [script, args] of [
    ["create-deterministic-release-zip.py", ["--input-dir", "release-package", "--output", archiveName]],
    ["create-release-manifest.py", ["--package-dir", "release-package", "--lock-file", "packages.lock.json", "--archive", archiveName, "--version", version, "--source-commit", sourceCommit, "--output", manifestName]],
  ]) {
    const result = spawnSync(python, [path.join(repositoryRoot, "scripts", script), ...args], { cwd: fixtureRoot, encoding: "utf8", timeout: 10_000 });
    assert.ifError(result.error);
    assert.equal(result.status, 0, `${script} failed:\n${result.stdout || ""}${result.stderr || ""}`);
  }
  archiveBytes = fs.readFileSync(archivePath);
  manifestText = fs.readFileSync(manifestPath, "utf8");

  check("Current producers' five-file package", true);
  check("Reordered file inventory", true, () => changeManifest(manifest => manifest.packageFiles.reverse()));
  for (const name of packageNames) {
    check(`Missing ${name}`, false, () => changeManifest(manifest => {
      manifest.packageFiles = manifest.packageFiles.filter(file => file.name !== name);
    }));
  }
  check("Extra file", false, () => changeManifest(manifest => manifest.packageFiles.push({ name: "extra.txt" })));
  check("Wrong filename at the same count", false, () => changeManifest(manifest => { manifest.packageFiles[0].name = "wrong.dll"; }));
  check("Duplicate filename at the same count", false, () => changeManifest(manifest => { manifest.packageFiles[0] = manifest.packageFiles[1]; }));
  check("Object instead of inventory array", false, () => changeManifest(manifest => { manifest.packageFiles = { ...manifest.packageFiles }; }));
  check("Wrong source", false, () => changeManifest(manifest => { manifest.sourceCommit = "b".repeat(40); }));
  check("Missing source", false, () => changeManifest(manifest => { delete manifest.sourceCommit; }));
  check("Wrong version", false, () => changeManifest(manifest => { manifest.pluginVersion = "9.9.9.9"; }));
  check("Wrong schema", false, () => changeManifest(manifest => { manifest.schemaVersion = 1; }));
  check("Empty dependency graph", false, () => changeManifest(manifest => { manifest.nuget.packages = []; }));
  check("Non-array dependency graph", false, () => changeManifest(manifest => { manifest.nuget.packages = { ...manifest.nuget.packages }; }));
  check("Invalid JSON with matching checksum", false, () => writeManifest("{"));
  check("Empty manifest with matching checksum", false, () => writeManifest(""));
  check("Multiple JSON documents", false, () => writeManifest(`${manifestText}\n${manifestText}`));
  check("Changed archive bytes", false, () => fs.appendFileSync(archivePath, "changed"));
  check("Changed manifest bytes", false, () => fs.appendFileSync(manifestPath, " "));
  check("Missing archive checksum", false, () => fs.unlinkSync(`${archivePath}.sha256`));
  check("Missing manifest checksum", false, () => fs.unlinkSync(`${manifestPath}.sha256`));

  assert.match(verificationStep, /^        RELEASE_VERSION: \$\{\{ steps\.version\.outputs\.version \}\}$/m,
    "The actual verification step must receive the extracted release version");
  console.log(`Release publication contract tests passed (${cases} cases; actual workflow shell and current package producers)`);
} finally {
  fs.rmSync(fixtureRoot, { recursive: true, force: true });
}
