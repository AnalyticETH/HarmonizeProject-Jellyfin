import fs from "node:fs";

const readme = fs.readFileSync("README.md", "utf8");
const workflow = fs.readFileSync(".github/workflows/dotnet-ci.yml", "utf8");
const releaseShell = fs.readFileSync("build-release.sh", "utf8");
const releasePowerShell = fs.readFileSync("build-release.ps1", "utf8");
const releasePackager = fs.readFileSync("scripts/create-deterministic-release-zip.py", "utf8");
const releasePackageValidator = fs.readFileSync("scripts/validate-release-package.mjs", "utf8");
const meta = JSON.parse(fs.readFileSync("meta.json", "utf8"));

const requiredReadmeMarkers = [
  "jellyfin-plugin-hue-release.zip",
  "jellyfin-plugin-hue-release.zip.sha256",
  "sha256sum --check --strict jellyfin-plugin-hue-release.zip.sha256",
  "BouncyCastle.Cryptography.dll",
  "Jellyfin.Plugin.Hue.dll",
  "meta.json",
  "HueSync",
];

for (const marker of requiredReadmeMarkers) {
  if (!readme.includes(marker)) {
    throw new Error(`README.md is missing release-installation marker: ${marker}`);
  }
}

for (const staleInstruction of [
  "Download the latest release DLL",
  "You only need this one file",
]) {
  if (readme.includes(staleInstruction)) {
    throw new Error(`README.md still contains stale package guidance: ${staleInstruction}`);
  }
}

const currentVersion = String(meta.version || "").replace(/\.0$/, "");
const currentHeadings = [...readme.matchAll(/^### Version ([^\n]+) \(Current\)$/gm)]
  .map(match => match[1].trim());
if (currentHeadings.length !== 1 || currentHeadings[0] !== currentVersion) {
  throw new Error(
    `README.md must have exactly one current release heading matching meta.json (${currentVersion}); found: ${currentHeadings.join(", ") || "none"}`
  );
}

const requiredWorkflowMarkers = [
  "jellyfin-plugin-hue-release.zip",
  "jellyfin-plugin-hue-release.zip.sha256",
  "python3 scripts/create-deterministic-release-zip.py",
  "--input-dir release-package",
  "--output jellyfin-plugin-hue-release.zip",
  "sha256sum --check --strict jellyfin-plugin-hue-release.zip.sha256",
  '"BouncyCastle.Cryptography.dll Jellyfin.Plugin.Hue.dll meta.json "',
];

for (const marker of requiredWorkflowMarkers) {
  if (!workflow.includes(marker)) {
    throw new Error(`Release workflow is missing package-integrity marker: ${marker}`);
  }
}

for (const [name, script] of [["build-release.sh", releaseShell], ["build-release.ps1", releasePowerShell]]) {
  const markers = [
    "dotnet restore --locked-mode",
    "dotnet publish",
    "publish/Jellyfin.Plugin.Hue.dll",
    "publish/BouncyCastle.Cryptography.dll",
    "publish/meta.json",
    "create-deterministic-release-zip.py",
    "sha256",
  ];
  if (name === "build-release.sh") {
    markers.push(
      "rm -rf ./publish",
      "--input-dir release-package",
      "--output \"$ZIPFILE\"",
      "sha256sum --check --strict");
  } else {
    markers.push(
      "Remove-Item -Recurse -Force \"./publish\"",
      "Get-FileHash -Algorithm SHA256",
      "create-deterministic-release-zip.py",
      "--input-dir \"release-package\"",
      "--output $zipFile",
      "Get-Content -LiteralPath $checksumFile -Raw",
      "Checksum sidecar is malformed or names the wrong archive",
      "$checksumParts[0].ToLowerInvariant() -ne $verifiedHash");
  }
  for (const marker of markers) {
    if (!script.includes(marker)) {
      throw new Error(`${name} is missing publish-authoritative dependency marker: ${marker}`);
    }
  }
}

const requiredPackagerMarkers = [
  "EXPECTED_FILES = (",
  'FIXED_TIMESTAMP = (1980, 1, 1, 0, 0, 0)',
  "COMPRESSION_LEVEL = 9",
  "info.create_system = 0",
  "FIXED_EXTERNAL_ATTR = 0o600 << 16",
  "info.external_attr = FIXED_EXTERNAL_ATTR",
  "zipfile.ZIP_DEFLATED",
  "def run_self_test()",
  "first_hash != second_hash",
  "entry.date_time != FIXED_TIMESTAMP",
];
for (const marker of requiredPackagerMarkers) {
  if (!releasePackager.includes(marker)) {
    throw new Error(`Deterministic release packager is missing contract marker: ${marker}`);
  }
}

for (const marker of [
  "create-deterministic-release-zip.py",
  "--self-test",
  "Release package determinism contract passed",
]) {
  if (!releasePackageValidator.includes(marker)) {
    throw new Error(`Release package validator is missing marker: ${marker}`);
  }
}

console.log("Release documentation and package contracts passed");
