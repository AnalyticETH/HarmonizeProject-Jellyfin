import fs from "node:fs";

const configPath = ".gitleaks.toml";
const config = fs.readFileSync(configPath, "utf8");

for (const marker of [
    'title = "HarmonizeProject-Jellyfin Gitleaks policy"',
    "[extend]",
    "useDefault = true",
    "[allowlist]",
    'description = "Public examples in reviewed Semgrep registry snapshots"',
    "'''(^|/)\\.github/semgrep/rules/'''"
]) {
    if (!config.includes(marker)) {
        throw new Error(`${configPath} is missing required marker: ${marker}`);
    }
}

const pathEntries = config.match(/'''[^']+'''/g) || [];
if (pathEntries.length !== 1 || pathEntries[0] !== "'''(^|/)\\.github/semgrep/rules/'''") {
    throw new Error(`${configPath} must contain exactly one narrow Semgrep snapshot path allowlist`);
}

if (/paths\s*=\s*\[[\s\S]*\.github(?!\/semgrep\/rules)/.test(config)) {
    throw new Error(`${configPath} contains a path allowlist broader than the Semgrep snapshot directory`);
}

console.log("Gitleaks configuration contract passed (default rules active; only reviewed Semgrep snapshots allowlisted)");
