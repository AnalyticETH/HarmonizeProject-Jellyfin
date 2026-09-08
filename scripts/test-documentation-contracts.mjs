import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "hue-documentation-contracts-"));
const fixtureFiles = [
  "README.md", "CONTRIBUTING.md", "SUPPORT.md", "CHANGELOG.md", "SECURITY.md",
  "RELEASE_READINESS.md", "CODE_OF_CONDUCT.md", "LICENSE", "NOTICE",
  "docs/CONFIGURATION.md", "docs/API.md", "docs/HUE_STREAM_PROTOCOL.md", "meta.json", "Directory.Build.props", "MSBuild.rsp",
  "build-release.sh", "build-release.ps1", ".github/CODEOWNERS", ".github/pull_request_template.md",
  ".github/ISSUE_TEMPLATE/bug_report.yml", ".github/ISSUE_TEMPLATE/feature_request.yml",
  ".github/ISSUE_TEMPLATE/config.yml", ".github/workflows/dotnet-ci.yml",
  "scripts/create-deterministic-release-zip.py", "scripts/create-release-manifest.py",
  "scripts/verify-jellyfin-runtime-smoke.py", "scripts/validate-release-package.mjs",
  "Jellyfin.Plugin.Hue/Api/HueApiController.cs",
  "Jellyfin.Plugin.Hue/Configuration/PluginConfiguration.cs",
  "Jellyfin.Plugin.Hue/Service/HueSceneAutomationService.cs",
  "Jellyfin.Plugin.Hue/Service/PluginServiceRegistrator.cs",
];
for (const project of ["Jellyfin.Plugin.Hue", "Jellyfin.Plugin.Hue.Tests", "Jellyfin.Plugin.Hue.Benchmarks"]) {
  fixtureFiles.push(`${project}/${project}.csproj`, `${project}/packages.lock.json`);
}
const validators = {
  layout: "validate-documentation.mjs",
  api: "validate-api-docs.mjs",
  release: "validate-release-docs.mjs",
  openSource: "validate-open-source-release.mjs",
};
let cases = 0;

function check(validator, changes, expectedError = null) {
  const originals = new Map();
  try {
    for (const [file, mutate] of Object.entries(changes)) {
      const target = path.join(fixtureRoot, file);
      const original = fs.readFileSync(target, "utf8");
      originals.set(target, original);
      const modified = mutate(original);
      if (expectedError) assert.notEqual(modified, original, `Fixture mutation did not change ${file}`);
      fs.writeFileSync(target, modified);
    }
    const result = spawnSync(process.execPath, [path.join(repositoryRoot, "scripts", validators[validator])], {
      cwd: fixtureRoot,
      encoding: "utf8",
    });
    assert.ifError(result.error);
    const output = `${result.stdout || ""}${result.stderr || ""}`;
    if (expectedError) {
      assert.notEqual(result.status, 0, `${validator} accepted an invalid documentation fixture`);
      assert.match(output, expectedError);
    } else {
      assert.equal(result.status, 0, `${validator} rejected valid documentation:\n${output}`);
    }
    cases += 1;
  } finally {
    for (const [target, original] of originals) fs.writeFileSync(target, original);
  }
}

try {
  for (const file of fixtureFiles) {
    const target = path.join(fixtureRoot, file);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.copyFileSync(path.join(repositoryRoot, file), target);
  }
  for (const validator of Object.keys(validators)) check(validator, {});

  const lineCount = text => text.replace(/\r?\n$/, "").split(/\r?\n/).length;
  const wordCount = text => text.trim().split(/\s+/).length;
  check("layout", { "README.md": text => text + "\n".repeat(150 - lineCount(text)) });
  check("layout", { "README.md": text => text + "\n".repeat(151 - lineCount(text)) }, /within 150 lines and 900 words/);
  check("layout", { "README.md": text => text.trimEnd() + " extra".repeat(900 - wordCount(text)) + "\n" });
  check("layout", { "README.md": text => text.trimEnd() + " extra".repeat(901 - wordCount(text)) + "\n" }, /within 150 lines and 900 words/);
  check("layout", { "README.md": text => text.replace("docs/API.md", "docs/MISSING.md") }, /broken relative link/);
  check("layout", { "README.md": text => text.replace("docs/API.md", "../README.md") }, /relative link outside the checkout/);
  check("layout", { "README.md": text => `${text}\n[Package](CONTRIBUTING.md#missing-package-section)\n` }, /broken heading link/);
  check("layout", { "docs/API.md": text => text.replace("#initial-setup", "#missing-setup") }, /broken heading link/);
  check("layout", { "CONTRIBUTING.md": text => text + "\n```md\n[Example](missing.md)\n```\n" });
  check("layout", {
    "CONTRIBUTING.md": text => text + "\n```md\n## Phantom heading\n```\n",
    "README.md": text => `${text}\n[Package](CONTRIBUTING.md#phantom-heading)\n`,
  }, /broken heading link/);

  const checkLinks = (markdown, expectedError = null) => check("layout", {
    "CONTRIBUTING.md": text => `${text}\n## Link validation fixture\n\n${markdown}\n`,
  }, expectedError);
  for (const title of ['"API reference"', "'API reference'", "(API reference)"]) {
    checkLinks(`[API](docs/API.md#administrator-api ${title})`);
    checkLinks(`[API](docs/MISSING.md ${title})`, /broken relative link/);
    checkLinks(`[API](docs/API.md#missing-heading ${title})`, /broken heading link/);
  }
  for (const reference of ["[API][api-docs]", "[api-docs][]", "[api-docs]"]) {
    checkLinks(`${reference}\n\n[api-docs]: docs/API.md#administrator-api "API reference"`);
    checkLinks(`${reference}\n\n[api-docs]: docs/MISSING.md "API reference"`, /broken relative link/);
    checkLinks(`${reference}\n\n[api-docs]: docs/API.md#missing-heading "API reference"`, /broken heading link/);
    checkLinks(`${reference}\n\n[api-docs]: ../README.md "API reference"`, /relative link outside the checkout/);
  }
  checkLinks('[API](<docs/API.md#administrator-api> "API reference")');
  checkLinks('[API](<docs/MISSING.md> "API reference")', /broken relative link/);
  checkLinks('[API](<docs/Missing File.md> "API reference")', /broken relative link/);
  checkLinks('[](docs/MISSING.md "API reference")', /broken relative link/);
  checkLinks('[API](docs/%41PI.md#administrator\\-api "API reference")');
  checkLinks('[API][api-docs]\n\n[api-docs]: <docs/API.md#administrator-api>\n  "API reference"');
  checkLinks('[API][api-docs]\n\n[api-docs]:\n  <docs/MISSING.md>\n  "API reference"', /broken relative link/);
  checkLinks('[API][API   Docs]\n\n[api docs]: docs/MISSING.md', /broken relative link/);
  checkLinks('[API][api-docs]\n\n[api-docs]: docs/API.md\n[api-docs]: docs/MISSING.md');
  checkLinks('[api-docs]: docs/MISSING.md "Unused definition"');
  checkLinks('[API][undefined-reference]');
  checkLinks('[API](docs/API.md "Example: [broken](missing.md)")');
  checkLinks('[API][api-docs]\n\n[api-docs]: docs/API.md "Example: [broken](missing.md)"');
  checkLinks('Use `[API](missing.md "Example")` or ``[API][missing] `code` ``.\n\n[missing]: missing.md');
  checkLinks('`[missing]: missing.md`\n\n[missing]');
  checkLinks('~~~md\n[API](missing.md "Example")\n[missing]\n\n[missing]: missing.md\n~~~');
  checkLinks('    [API](missing.md "Example")\n    [missing]\n\n    [missing]: missing.md');
  checkLinks('- Parent\n\n    [API](missing.md "Example")', /broken relative link/);
  checkLinks('- Parent\n\n      [API](missing.md "Example")');
  checkLinks('- Parent\n  - Child\n\n    [API](missing.md "Example")', /broken relative link/);
  checkLinks('\\[API](missing.md "Example")');
  checkLinks('\\\\[API](missing.md "Example")', /broken relative link/);
  checkLinks('Unclosed ` code marker then [API](missing.md "Example")', /broken relative link/);
  checkLinks('Unclosed ` code marker\n\n[API](missing.md "Example")\n\nA later ` marker.', /broken relative link/);

  check("api", {
    "docs/CONFIGURATION.md": text => text.replaceAll("Audio and AllMedia scopes process supported audio playback", "The selected scopes are applied"),
    "README.md": text => text + "\nAudio and AllMedia scopes process supported audio playback\n",
  }, /docs\/CONFIGURATION\.md media-sync documentation is missing marker/);
  check("api", { "README.md": text => text + "\nAudio-only and other non-video playback is ignored safely.\n" }, /stale audio-only playback guidance/);
  check("api", {
    "docs/API.md": text => text.split("\n").filter(line => !line.startsWith("| `POST /HueSync/Register` |")).join("\n"),
  }, /missing the bridge registration endpoint contract/);
  check("api", {
    "docs/API.md": text => text.split("\n").map(line => line.startsWith("| `POST /HueSync/Register` |") ? line.replaceAll("unpinned", "changed") : line).join("\n"),
  }, /bridge registration contract is missing safety marker: unpinned/);
  check("api", {
    "docs/API.md": text => text.split("\n").filter(line => !line.startsWith("| `POST /HueSync/BridgeCertificate/Trust` |")).join("\n"),
  }, /missing certificate pinning safety guidance/);
  check("api", { "docs/API.md": text => text + '\n{"targetUserIds": ["mapping-id"]}\n' }, /must not describe stable mapping-row IDs as target user IDs/);

  check("release", {
    "CONTRIBUTING.md": text => text.replace("resolved hash-locked NuGet graph", "dependency list"),
  }, /CONTRIBUTING\.md is missing release-reference marker/);
  check("release", {
    "CONTRIBUTING.md": text => text.replaceAll("d659991fdbda4d2963c807747fbd1ee237bfd15971a3923158719eb248ddea67", "unverified-image"),
  }, /CONTRIBUTING\.md is missing release-reference marker/);
  const version = JSON.parse(fs.readFileSync(path.join(fixtureRoot, "meta.json"), "utf8")).version.replace(/\.0$/, "");
  check("release", { "CHANGELOG.md": text => text + `\n## [${version}]\n` }, /must have exactly one release heading matching meta\.json/);
  check("release", { "CHANGELOG.md": text => text.replace(`## [${version}]`, "## Missing version") }, /must have exactly one release heading matching meta\.json/);
  check("openSource", { "README.md": text => text.replace("GPL-3.0-only", "unspecified") }, /missing open-source release guidance: GPL-3\.0-only/);

  console.log(`Documentation contract regression tests passed (${cases} cases)`);
} finally {
  fs.rmSync(fixtureRoot, { recursive: true, force: true });
}
