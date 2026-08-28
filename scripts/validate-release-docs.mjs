import fs from "node:fs";

const readme = fs.readFileSync("README.md", "utf8");
const workflow = fs.readFileSync(".github/workflows/dotnet-ci.yml", "utf8");
const releaseShell = fs.readFileSync("build-release.sh", "utf8");
const releasePowerShell = fs.readFileSync("build-release.ps1", "utf8");
const releasePackager = fs.readFileSync("scripts/create-deterministic-release-zip.py", "utf8");
const releaseManifest = fs.readFileSync("scripts/create-release-manifest.py", "utf8");
const releasePackageValidator = fs.readFileSync("scripts/validate-release-package.mjs", "utf8");
const changelog = fs.readFileSync("CHANGELOG.md", "utf8");
const meta = JSON.parse(fs.readFileSync("meta.json", "utf8"));

const requiredReadmeMarkers = [
  "jellyfin-plugin-hue-release.zip",
  "jellyfin-plugin-hue-release.zip.sha256",
  "jellyfin-plugin-hue-release.manifest.json",
  "jellyfin-plugin-hue-release.manifest.json.sha256",
  "sha256sum --check --strict jellyfin-plugin-hue-release.zip.sha256",
  "resolved hash-locked NuGet graph",
  "exact source commit",
  "clean Git checkout",
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

const releaseHeading = `## [${currentVersion}]`;
const releaseStart = changelog.indexOf(releaseHeading);
if (releaseStart < 0) {
  throw new Error(`CHANGELOG.md is missing the current release section: ${releaseHeading}`);
}
const releaseHeadingEnd = changelog.indexOf("\n", releaseStart);
const releaseBodyStart = releaseHeadingEnd < 0 ? changelog.length : releaseHeadingEnd + 1;
const nextRelease = changelog.slice(releaseBodyStart).search(/^## \[/m);
const currentReleaseBody = changelog.slice(
  releaseBodyStart,
  nextRelease < 0 ? changelog.length : releaseBodyStart + nextRelease
);
const currentReleaseBullets = [...currentReleaseBody.matchAll(/^\s*-\s+(.+)$/gm)]
  .map(match => match[1].replace(/\*\*/g, "").replace(/`/g, "").trim())
  .filter(Boolean);
if (currentReleaseBullets.length === 0) {
  throw new Error(`CHANGELOG.md current release section has no release bullets: ${currentVersion}`);
}
const metadataChangelog = String(meta.changelog || "");
if (metadataChangelog.includes("\\n")) {
  throw new Error("meta.json changelog contains a literal \\n escape; use newline-separated release entries");
}
const missingMetadataBullets = currentReleaseBullets.filter(bullet => !metadataChangelog.includes(bullet));
if (missingMetadataBullets.length > 0) {
  throw new Error(
    `meta.json changelog is missing current ${currentVersion} release entries: ${missingMetadataBullets.join(" | ")}`
  );
}

const requiredWorkflowMarkers = [
  "jellyfin-plugin-hue-release.zip",
  "jellyfin-plugin-hue-release.zip.sha256",
  "python3 scripts/create-deterministic-release-zip.py",
  "--input-dir release-package",
  "--output jellyfin-plugin-hue-release.zip",
  "sha256sum --check --strict jellyfin-plugin-hue-release.zip.sha256",
  "sha256sum --check --strict jellyfin-plugin-hue-release.manifest.json.sha256",
  "scripts/create-release-manifest.py",
  "--source-commit \"$GITHUB_SHA\"",
  "--output jellyfin-plugin-hue-release.manifest.json",
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
      "git -c safe.directory=\"$PWD\" rev-parse --verify HEAD",
      "git -c safe.directory=\"$PWD\" status --porcelain=v1 --untracked-files=all",
      "--input-dir release-package",
      "--output \"$ZIPFILE\"",
      "sha256sum --check --strict",
      "create-release-manifest.py",
      "MANIFEST_FILE=",
      "MANIFEST_CHECKSUM_FILE=");
  } else {
    markers.push(
      "Remove-Item -Recurse -Force \"./publish\"",
      "status --porcelain=v1 --untracked-files=all",
      "Get-FileHash -Algorithm SHA256",
      "create-deterministic-release-zip.py",
      "--input-dir \"release-package\"",
      "--output $zipFile",
      "Get-Content -LiteralPath $checksumFile -Raw",
      "Checksum sidecar is malformed or names the wrong archive",
      "$checksumParts[0].ToLowerInvariant() -ne $verifiedHash",
      "create-release-manifest.py",
      "$manifestFile",
      "$manifestChecksumFile");
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
  "FIXED_OUTPUT_MODE = 0o644",
  "info.create_system = 0",
  "FIXED_EXTERNAL_ATTR = 0o600 << 16",
  "info.external_attr = FIXED_EXTERNAL_ATTR",
  "zipfile.ZIP_DEFLATED",
  "def run_self_test()",
  "first_hash != second_hash",
  "entry.date_time != FIXED_TIMESTAMP",
  "os.chmod(output_path, FIXED_OUTPUT_MODE)",
];
for (const marker of requiredPackagerMarkers) {
  if (!releasePackager.includes(marker)) {
    throw new Error(`Deterministic release packager is missing contract marker: ${marker}`);
  }
}

for (const marker of [
  "create-deterministic-release-zip.py",
  "create-release-manifest.py",
  "--self-test",
  "Release package determinism contract passed",
  "Release manifest determinism contract passed",
]) {
  if (!releasePackageValidator.includes(marker)) {
    throw new Error(`Release package validator is missing marker: ${marker}`);
  }
}

for (const marker of [
  "SCHEMA_VERSION = 2",
  "COMMIT_PATTERN =",
  "REQUIRED_PACKAGE_FILES =",
  "def build_manifest(",
  "source_commit: str | None = None",
  "def serialize_manifest(",
  "with zipfile.ZipFile(archive_path)",
  "release archive entry does not match package file",
  "source commit is required for release provenance",
  "def run_self_test()",
  "Deterministic release manifest self-test passed",
]) {
  if (!releaseManifest.includes(marker)) {
    throw new Error(`Release manifest generator is missing marker: ${marker}`);
  }
}

console.log("Release documentation and package contracts passed");
