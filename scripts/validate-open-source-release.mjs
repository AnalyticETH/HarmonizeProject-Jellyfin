import fs from "node:fs";
import "./validate-documentation.mjs";

const requiredFiles = [
  "LICENSE",
  "NOTICE",
  "CONTRIBUTING.md",
  "CODE_OF_CONDUCT.md",
  "SUPPORT.md",
  "RELEASE_READINESS.md",
  "docs/CONFIGURATION.md",
  "docs/API.md",
  ".github/ISSUE_TEMPLATE/bug_report.yml",
  ".github/ISSUE_TEMPLATE/feature_request.yml",
  ".github/ISSUE_TEMPLATE/config.yml",
  ".github/pull_request_template.md",
];

for (const file of requiredFiles) {
  if (!fs.existsSync(file) || !fs.statSync(file).isFile()) {
    throw new Error(`Open-source release file is missing: ${file}`);
  }
}

for (const obsoleteRunnerPath of [
  "SELF_HOSTED_RUNNERS.md",
  "ops",
  "scripts/check-runner-services.sh",
  "scripts/check-runner-versions.sh",
  "scripts/cleanup-pr-runner.sh",
]) {
  if (fs.existsSync(obsoleteRunnerPath)) {
    throw new Error(`Open-source tree contains obsolete self-hosted runner infrastructure: ${obsoleteRunnerPath}`);
  }
}

function read(file) {
  return fs.readFileSync(file, "utf8");
}

const license = read("LICENSE");
for (const marker of [
  "GNU GENERAL PUBLIC LICENSE",
  "Version 3, 29 June 2007",
  "This program comes with ABSOLUTELY NO WARRANTY",
]) {
  if (!license.includes(marker)) {
    throw new Error(`LICENSE is not the complete GPLv3 text; missing marker: ${marker}`);
  }
}

const notice = read("NOTICE");
for (const marker of [
  "Copyright (C) 2026 MCP Capital LLC",
  "BouncyCastle.Cryptography 2.7.0",
  "MIT License",
  "Jellyfin SDK packages 10.9.0",
  "GPL-3.0-only",
]) {
  if (!notice.includes(marker)) {
    throw new Error(`NOTICE is missing required attribution: ${marker}`);
  }
}

const project = read("Jellyfin.Plugin.Hue/Jellyfin.Plugin.Hue.csproj");
for (const marker of [
  "<PackageLicenseExpression>GPL-3.0-only</PackageLicenseExpression>",
  "<Copyright>Copyright (C) 2026 MCP Capital LLC</Copyright>",
  "<RepositoryUrl>https://github.com/AnalyticETH/HarmonizeProject-Jellyfin</RepositoryUrl>",
]) {
  if (!project.includes(marker)) {
    throw new Error(`Project metadata is missing open-source marker: ${marker}`);
  }
}

const meta = JSON.parse(read("meta.json"));
if (meta.version !== "1.5.461.0") {
  throw new Error(`meta.json must carry the prepared release version 1.5.461.0, found ${meta.version}`);
}
if (!String(meta.changelog || "").includes("Open-source licensing: publish the complete GPL-3.0-only license")) {
  throw new Error("meta.json changelog is missing the open-source release entry");
}

const readme = read("README.md");
for (const marker of [
  "GPL-3.0-only",
  "](LICENSE)",
  "[NOTICE](NOTICE)",
  "[Contributing guide](CONTRIBUTING.md)",
  "[Support guide](SUPPORT.md)",
  "[Release readiness](RELEASE_READINESS.md)",
  "[Code of Conduct](CODE_OF_CONDUCT.md)",
  "[Issue templates](.github/ISSUE_TEMPLATE/)",
  "[Configuration reference](docs/CONFIGURATION.md)",
  "[Administrator API](docs/API.md)",
  "[Changelog](CHANGELOG.md)",
]) {
  if (!readme.includes(marker)) {
    throw new Error(`README.md is missing open-source release guidance: ${marker}`);
  }
}

const issueForm = read(".github/ISSUE_TEMPLATE/bug_report.yml");
for (const marker of [
  "name: Bug report",
  "id: plugin-version",
  "id: jellyfin-version",
  "id: diagnostics",
  "I removed credentials, tokens, certificates, and sensitive host details.",
]) {
  if (!issueForm.includes(marker)) {
    throw new Error(`Bug issue form is missing safe reporting guidance: ${marker}`);
  }
}

const featureForm = read(".github/ISSUE_TEMPLATE/feature_request.yml");
for (const marker of ["name: Feature request", "id: problem", "id: proposal"]) {
  if (!featureForm.includes(marker)) {
    throw new Error(`Feature issue form is missing required field: ${marker}`);
  }
}

const issueConfig = read(".github/ISSUE_TEMPLATE/config.yml");
if (!issueConfig.includes("blank_issues_enabled: false") || !issueConfig.includes("security/policy")) {
  throw new Error("Issue configuration must disable blank issues and link the security policy");
}

const pullRequestTemplate = read(".github/pull_request_template.md");
for (const marker of [
  "I ran the relevant .NET build and tests with locked restore.",
  "I updated README/CHANGELOG/API documentation when public behavior changed.",
  "Configuration and status output remain credential-free.",
]) {
  if (!pullRequestTemplate.includes(marker)) {
    throw new Error(`Pull-request template is missing release-safety guidance: ${marker}`);
  }
}

const codeOwners = read(".github/CODEOWNERS");
for (const marker of [
  "/LICENSE @AnalyticETH",
  "/NOTICE @AnalyticETH",
  "/CONTRIBUTING.md @AnalyticETH",
  "/CODE_OF_CONDUCT.md @AnalyticETH",
  "/SUPPORT.md @AnalyticETH",
  "/RELEASE_READINESS.md @AnalyticETH",
  "/docs/ @AnalyticETH",
  "/scripts/validate-open-source-release.mjs @AnalyticETH",
]) {
  if (!codeOwners.includes(marker)) {
    throw new Error(`CODEOWNERS is missing owner coverage: ${marker}`);
  }
}

console.log("Open-source source-tree contract passed; publication gates require separate owner evidence");
