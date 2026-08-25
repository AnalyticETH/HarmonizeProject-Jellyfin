import fs from "node:fs";

const readme = fs.readFileSync("README.md", "utf8");
const workflow = fs.readFileSync(".github/workflows/dotnet-ci.yml", "utf8");

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

const requiredWorkflowMarkers = [
  "jellyfin-plugin-hue-release.zip",
  "jellyfin-plugin-hue-release.zip.sha256",
  "sha256sum --check --strict jellyfin-plugin-hue-release.zip.sha256",
  '"BouncyCastle.Cryptography.dll Jellyfin.Plugin.Hue.dll meta.json "',
];

for (const marker of requiredWorkflowMarkers) {
  if (!workflow.includes(marker)) {
    throw new Error(`Release workflow is missing package-integrity marker: ${marker}`);
  }
}

console.log("Release documentation and package contracts passed");
