import { spawnSync } from "node:child_process";
import fs from "node:fs";

const helper = "scripts/create-deterministic-release-zip.py";
if (!fs.existsSync(helper)) {
  throw new Error(`Deterministic release packager is missing: ${helper}`);
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
