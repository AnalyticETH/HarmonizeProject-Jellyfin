import { spawnSync } from "node:child_process";
import fs from "node:fs";

const helper = "scripts/create-deterministic-release-zip.py";
const manifestHelper = "scripts/create-release-manifest.py";
if (!fs.existsSync(helper)) {
  throw new Error(`Deterministic release packager is missing: ${helper}`);
}
if (!fs.existsSync(manifestHelper)) {
  throw new Error(`Deterministic release manifest generator is missing: ${manifestHelper}`);
}

const candidates = process.platform === "win32" ? ["python", "python3"] : ["python3", "python"];
let result;
let selected;
for (const candidate of candidates) {
  result = spawnSync(candidate, [helper, "--self-test"], {
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  });
  if (!result.error || result.error.code !== "ENOENT") {
    selected = candidate;
    break;
  }
}

if (!selected) {
  throw new Error(`Python 3 is required to validate ${helper}`);
}
if (result.error) {
  throw new Error(`${selected} could not run ${helper}: ${result.error.message}`);
}
if (result.status !== 0) {
  const output = `${result.stdout || ""}${result.stderr || ""}`.trim();
  throw new Error(`${helper} self-test failed (${result.status}): ${output}`);
}

process.stdout.write(result.stdout);
console.log("Release package determinism contract passed");

let manifestResult;
let manifestSelected;
for (const candidate of candidates) {
  manifestResult = spawnSync(candidate, [manifestHelper, "--self-test"], {
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  });
  if (!manifestResult.error || manifestResult.error.code !== "ENOENT") {
    manifestSelected = candidate;
    break;
  }
}

if (!manifestSelected) {
  throw new Error(`Python 3 is required to validate ${manifestHelper}`);
}
if (manifestResult.error) {
  throw new Error(`${manifestSelected} could not run ${manifestHelper}: ${manifestResult.error.message}`);
}
if (manifestResult.status !== 0) {
  const output = `${manifestResult.stdout || ""}${manifestResult.stderr || ""}`.trim();
  throw new Error(`${manifestHelper} self-test failed (${manifestResult.status}): ${output}`);
}

process.stdout.write(manifestResult.stdout);
console.log("Release manifest determinism contract passed");
