import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "hue-release-helper-contracts-"));
const checkout = path.join(fixtureRoot, "guard checkout");
const fixtureBin = path.join(fixtureRoot, "bin");
const logPath = path.join(fixtureRoot, "commands.jsonl");
const sourceCommit = "a".repeat(40);
const artifacts = [
  "Jellyfin.Plugin.Hue/bin/Release/prior.dll", "release-package/prior.txt", "publish/prior.dll",
  "jellyfin-plugin-hue-prior.zip", "jellyfin-plugin-hue-prior.zip.sha256",
  "jellyfin-plugin-hue-prior.manifest.json", "jellyfin-plugin-hue-prior.manifest.json.sha256",
];
let guardCases = 0;

function run(command, args, options = {}) {
  const result = spawnSync(command, args, { encoding: "utf8", timeout: 30_000, ...options });
  assert.ifError(result.error);
  return { ...result, output: `${result.stdout || ""}${result.stderr || ""}` };
}

function available(command, args = ["--version"]) {
  return spawnSync(command, args, { encoding: "utf8", timeout: 10_000 }).status === 0;
}

function writeFixture(relative, content) {
  const target = path.join(checkout, relative);
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, content);
}

const commandStub = `#!${process.execPath}
const fs = require("node:fs");
const path = require("node:path");
const command = path.basename(process.argv[1]);
const args = process.argv.slice(2);
fs.appendFileSync(process.env.HUE_RELEASE_TEST_LOG, JSON.stringify({ command, args }) + "\\n");
if (command === "git") {
  const fixture = JSON.parse(process.env.HUE_RELEASE_GIT_FIXTURE);
  const kind = args.includes("--show-toplevel") ? "root"
    : args.includes("status") ? "status"
    : args.includes("--verify") ? "head"
    : args.includes("cat-file") ? "type" : null;
  if (!kind) throw new Error("Unexpected Git request: " + args.join(" "));
  const reply = fixture[kind];
  if (reply.output.length) process.stdout.write(reply.output.join("\\n") + "\\n");
  process.exit(reply.code);
}
if (command === "dotnet") process.exit(86);
if (!["rm", "python3", "unzip"].includes(command)) throw new Error("Unexpected command " + command);
`;

const powershellHarness = String.raw`
$ErrorActionPreference = "Stop"
$fixture = $env:HUE_RELEASE_GIT_FIXTURE | ConvertFrom-Json
function git {
    $kind = if ($args -contains "--show-toplevel") { "root" }
        elseif ($args -contains "status") { "status" }
        elseif ($args -contains "--verify") { "head" }
        elseif ($args -contains "cat-file") { "type" }
        else { throw "Unexpected Git request: $args" }
    @{ command = "git"; args = @($args) } | ConvertTo-Json -Compress |
        Add-Content -LiteralPath $env:HUE_RELEASE_TEST_LOG -Encoding utf8
    $reply = $fixture.$kind
    Write-Output $reply.output
    $global:LASTEXITCODE = [int]$reply.code
}
function Remove-Item {
    @{ command = "rm"; args = @($args) } | ConvertTo-Json -Compress |
        Add-Content -LiteralPath $env:HUE_RELEASE_TEST_LOG -Encoding utf8
}
function dotnet {
    @{ command = "dotnet"; args = @($args) } | ConvertTo-Json -Compress |
        Add-Content -LiteralPath $env:HUE_RELEASE_TEST_LOG -Encoding utf8
    exit 86
}
function python { }
function python3 { }
& $env:HUE_RELEASE_TEST_SCRIPT
exit $LASTEXITCODE
`;

function checkGuard(runner, name, overrides = {}, options = {}) {
  const fixture = {
    root: { code: 0, output: [checkout] },
    status: { code: 0, output: [] },
    head: { code: 0, output: [sourceCommit] },
    type: { code: 0, output: ["commit"] },
    ...overrides,
  };
  fs.writeFileSync(logPath, "");
  if (options.missingFile) fs.renameSync(path.join(checkout, options.missingFile), path.join(checkout, `${options.missingFile}.held`));
  try {
    const result = run(runner.command, runner.args, {
      cwd: options.cwd || checkout,
      env: {
        ...process.env,
        PATH: `${fixtureBin}${path.delimiter}${process.env.PATH || ""}`,
        HUE_RELEASE_GIT_FIXTURE: JSON.stringify(fixture),
        HUE_RELEASE_TEST_LOG: logPath,
        HUE_RELEASE_TEST_ROOT: fixtureRoot,
        HUE_RELEASE_TEST_CWD: options.cwd || checkout,
        HUE_RELEASE_TEST_SCRIPT: path.join(checkout, runner.script),
      },
    });
    const events = fs.readFileSync(logPath, "utf8").split(/\r?\n/).filter(line => line.trim()).map(line => JSON.parse(line.replace(/^\uFEFF/, "")));
    const cleanup = events.filter(event => event.command === "rm");
    const builds = events.filter(event => event.command === "dotnet");
    if (options.accepted) {
      assert.equal(result.status, 86, `${runner.name}: ${name} did not reach the stubbed restore:\n${result.output}`);
      assert(cleanup.length > 0, `${runner.name}: valid provenance never reached cleanup`);
      assert.equal(builds.length, 1);
      assert.deepEqual(builds[0].args, ["restore", "--locked-mode"]);
      assert.equal(events.filter(event => event.command === "git").length, 4);
    } else {
      assert.notEqual(result.status, 0, `${runner.name}: ${name} succeeded unexpectedly`);
      assert.notEqual(result.status, 86, `${runner.name}: ${name} reached the build`);
      assert.equal(cleanup.length, 0, `${runner.name}: ${name} entered artifact cleanup:\n${result.output}`);
      assert.equal(builds.length, 0, `${runner.name}: ${name} entered a build`);
      assert.match(result.output, options.error, `${runner.name}: ${name} did not report the expected guard`);
    }
    for (const artifact of artifacts) {
      assert.equal(fs.readFileSync(path.join(checkout, artifact), "utf8"), `preserve ${artifact}\n`);
    }
    guardCases += 1;
  } finally {
    if (options.missingFile) fs.renameSync(path.join(checkout, `${options.missingFile}.held`), path.join(checkout, options.missingFile));
  }
}

function checkCheckoutBytes() {
  const inputs = [
    ".gitattributes", ".editorconfig", "LICENSE", "NOTICE", "meta.json", "global.json",
    "Directory.Build.props", "MSBuild.rsp", "Jellyfin.Plugin.Hue.sln", "build-release.sh", "build-release.ps1",
  ];
  function collect(directory) {
    for (const entry of fs.readdirSync(path.join(repositoryRoot, directory), { withFileTypes: true })) {
      const relative = `${directory}/${entry.name}`;
      if (entry.isDirectory() && !["bin", "obj", "TestResults", "__pycache__"].includes(entry.name)) collect(relative);
      else if (entry.isFile() && /\.(?:cs|csproj|props|targets|sln|rsp|json|html|py|mjs|sh|ps1)$/.test(entry.name)) inputs.push(relative);
    }
  }
  for (const directory of ["Jellyfin.Plugin.Hue", "Jellyfin.Plugin.Hue.Tests", "Jellyfin.Plugin.Hue.Benchmarks", "scripts"]) collect(directory);

  const source = path.join(fixtureRoot, "checkout-source");
  fs.mkdirSync(source);
  for (const input of inputs) {
    const target = path.join(source, input);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.copyFileSync(path.join(repositoryRoot, input), target);
  }
  fs.writeFileSync(path.join(source, "uncontrolled.txt"), "checkout filter control\n");
  fs.writeFileSync(path.join(source, "binary-control.bin"), Buffer.from([0, 10, 13, 10, 255]));
  const emptyAttributes = path.join(fixtureRoot, "empty-attributes");
  fs.writeFileSync(emptyAttributes, "");
  const gitEnvironment = { ...process.env };
  for (const name of ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_COMMON_DIR"]) delete gitEnvironment[name];
  function git(args, input) {
    const result = run("git", ["-c", `core.attributesFile=${emptyAttributes}`, ...args], { cwd: source, env: gitEnvironment, input });
    assert.equal(result.status, 0, `Fixture Git command failed: ${args.join(" ")}\n${result.output}`);
    return result.stdout;
  }
  // Only this temporary index is written; no commits or main-checkout Git state change.
  git(["init", "--quiet"]);
  git(["-c", "core.autocrlf=false", "add", "--", "."]);
  const attributes = git(["check-attr", "--cached", "-z", "--stdin", "text", "eol"], inputs.join("\0") + "\0").split("\0");
  for (let index = 0; index < attributes.length - 1; index += 3) {
    const [file, attribute, value] = attributes.slice(index, index + 3);
    assert.equal(value, attribute === "text" ? "set" : "lf", `${file} must pin ${attribute} for reproducible checkout bytes`);
  }

  const checkouts = [path.join(fixtureRoot, "lf-checkout"), path.join(fixtureRoot, "autocrlf-checkout")];
  for (const [index, target] of checkouts.entries()) {
    fs.mkdirSync(target);
    git(["-c", `core.autocrlf=${index === 0 ? "false" : "true"}`, "-c", `core.eol=${index === 0 ? "lf" : "crlf"}`,
      "checkout-index", "--all", `--prefix=${target.split(path.sep).join("/")}/`]);
  }
  for (const input of inputs) {
    const bytes = checkouts.map(directory => fs.readFileSync(path.join(directory, input)));
    assert.deepEqual(bytes[0], bytes[1], `${input} changes with core.autocrlf`);
    assert(!bytes[0].includes(Buffer.from("\r\n")), `${input} is not checked out with LF`);
  }
  assert.notDeepEqual(...checkouts.map(directory => fs.readFileSync(path.join(directory, "uncontrolled.txt"))),
    "The control file must prove autocrlf checkout conversion actually ran");
  assert.deepEqual(...checkouts.map(directory => fs.readFileSync(path.join(directory, "binary-control.bin"))));

  const python = (process.platform === "win32" ? ["python", "python3"] : ["python3", "python"]).find(command => available(command));
  assert(python, "Python 3 is required for fixture package reproducibility checks");
  for (const directory of checkouts) {
    const packageDirectory = path.join(directory, "release-package");
    fs.mkdirSync(packageDirectory);
    for (const file of ["LICENSE", "NOTICE", "meta.json"]) fs.copyFileSync(path.join(directory, file), path.join(packageDirectory, file));
    fs.writeFileSync(path.join(packageDirectory, "BouncyCastle.Cryptography.dll"), "fixture managed dependency\n");
    fs.writeFileSync(path.join(packageDirectory, "Jellyfin.Plugin.Hue.dll"), Buffer.concat([
      Buffer.from("fixture plugin embedding\n"), fs.readFileSync(path.join(directory, "Jellyfin.Plugin.Hue/Configuration/configPage.html")),
    ]));
    const version = JSON.parse(fs.readFileSync(path.join(directory, "meta.json"), "utf8")).version;
    for (const [script, args] of [
      ["create-deterministic-release-zip.py", ["--input-dir", "release-package", "--output", "fixture.zip"]],
      ["create-release-manifest.py", ["--package-dir", "release-package", "--lock-file", "Jellyfin.Plugin.Hue/packages.lock.json",
        "--archive", "fixture.zip", "--version", version, "--source-commit", sourceCommit, "--output", "fixture.manifest.json"]],
    ]) {
      const result = run(python, [path.join(directory, "scripts", script), ...args], { cwd: directory });
      assert.equal(result.status, 0, `${script} failed:\n${result.output}`);
    }
  }
  for (const file of ["fixture.zip", "fixture.manifest.json"]) {
    assert.deepEqual(...checkouts.map(directory => fs.readFileSync(path.join(directory, file))), `${file} changes with core.autocrlf`);
  }
  console.log(`Checkout contracts passed (${inputs.length} LF-pinned inputs; identical fixture ZIP and manifest; no compiled-binary/native-Windows claim)`);
}

try {
  fs.mkdirSync(fixtureBin);
  fs.writeFileSync(path.join(fixtureRoot, "package.json"), '{"type":"commonjs"}\n');
  for (const command of ["git", "rm", "dotnet", "python3", "unzip"]) fs.writeFileSync(path.join(fixtureBin, command), commandStub, { mode: 0o755 });
  for (const file of ["build-release.sh", "build-release.ps1", "meta.json", "Jellyfin.Plugin.Hue.sln", "Jellyfin.Plugin.Hue/Jellyfin.Plugin.Hue.csproj"]) {
    writeFixture(file, fs.readFileSync(path.join(repositoryRoot, file)));
  }
  for (const artifact of artifacts) writeFixture(artifact, `preserve ${artifact}\n`);
  fs.mkdirSync(path.join(fixtureRoot, "other-root"));
  fs.mkdirSync(path.join(checkout, "subdirectory"));
  const powershellHarnessPath = path.join(fixtureRoot, "powershell-harness.ps1");
  fs.writeFileSync(powershellHarnessPath, powershellHarness);

  const runners = [];
  if (process.platform !== "win32" && available("bash")) runners.push({ name: "Bash", command: "bash", args: [path.join(checkout, "build-release.sh")], script: "build-release.sh" });
  const powershell = (process.env.HUE_RELEASE_TEST_POWERSHELL ? [process.env.HUE_RELEASE_TEST_POWERSHELL] : ["pwsh", "powershell"])
    .find(command => available(command, ["-NoLogo", "-NoProfile", "-Command", "exit 0"]));
  if (powershell) runners.push({ name: "PowerShell", command: powershell, args: ["-NoLogo", "-NoProfile", "-File", powershellHarnessPath], script: "build-release.ps1" });
  else console.log("PowerShell guard execution unavailable: install/use PowerShell or set HUE_RELEASE_TEST_POWERSHELL; native Windows remains unverified");
  assert(runners.length, "At least one release-helper shell is required");
  for (const runner of runners) {
    checkGuard(runner, "valid provenance", {}, { accepted: true });
    for (const [name, override, error] of [
      ["root command error", { root: { code: 128, output: [] } }, /Unable to determine the Git checkout root/],
      ["root error with plausible output", { root: { code: 128, output: [checkout] } }, /Unable to determine the Git checkout root/],
      ["empty root", { root: { code: 0, output: [] } }, /must run from its plugin repository root/],
      ["multiple roots", { root: { code: 0, output: [checkout, checkout] } }, /must run from its plugin repository root/],
      ["different root", { root: { code: 0, output: [path.join(fixtureRoot, "other-root")] } }, /must run from its plugin repository root/],
      ["missing root", { root: { code: 0, output: [path.join(fixtureRoot, "missing-root")] } }, /must run from its plugin repository root/],
      ["status command error", { status: { code: 128, output: [] } }, /Unable to establish a clean Git checkout/],
      ["dirty tracked source", { status: { code: 0, output: [" M source.cs"] } }, /requires a clean Git checkout/],
      ["untracked source", { status: { code: 0, output: ["?? source.cs"] } }, /requires a clean Git checkout/],
      ["HEAD command error", { head: { code: 128, output: [] } }, /Unable to resolve the checked-out source commit/],
      ["HEAD error with plausible output", { head: { code: 128, output: [sourceCommit] } }, /Unable to resolve the checked-out source commit/],
      ["missing HEAD", { head: { code: 0, output: [] } }, /must be a 40-character Git SHA/],
      ["malformed HEAD", { head: { code: 0, output: ["not-a-sha"] } }, /must be a 40-character Git SHA/],
      ["multiple HEAD values", { head: { code: 0, output: [sourceCommit, sourceCommit] } }, /must be a 40-character Git SHA/],
      ["object lookup error", { type: { code: 128, output: ["commit"] } }, /must identify a commit object/],
      ["non-commit HEAD", { type: { code: 0, output: ["tree"] } }, /must identify a commit object/],
    ]) checkGuard(runner, name, override, { error });
    checkGuard(runner, "subdirectory invocation", {}, { cwd: path.join(checkout, "subdirectory"), error: /must run from its plugin repository root/ });
    checkGuard(runner, "missing project metadata", {}, { missingFile: "meta.json", error: /must run from its plugin repository root/ });
  }
  console.log(`Release helper provenance contracts passed (${guardCases} cases; ${runners.map(runner => runner.name).join(" and ")}; cleanup/build commands stubbed)`);
  checkCheckoutBytes();
} finally {
  fs.rmSync(fixtureRoot, { recursive: true, force: true });
}
