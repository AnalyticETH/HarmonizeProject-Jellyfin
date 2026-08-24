import fs from "node:fs";

const lockPath = process.argv[2] || ".github/semgrep/requirements.txt";
const expectedSemgrepVersion = process.argv[3] || "";
const lock = fs.readFileSync(lockPath, "utf8");
const lines = lock.split(/\r?\n/);
const packages = [];
let current = null;

function finishPackage() {
    if (!current) {
        return;
    }

    const hashes = current.lines.join("\n").match(/--hash=sha256:[0-9a-f]{64}/g) || [];
    if (hashes.length === 0) {
        throw new Error(`${lockPath}: ${current.name}==${current.version} has no SHA-256 hash`);
    }

    packages.push({
        name: current.name.toLowerCase().replace(/[-_.]+/g, "-"),
        version: current.version
    });
    current = null;
}

for (const line of lines) {
    const packageMatch = line.match(/^([A-Za-z0-9][A-Za-z0-9_.-]*)==([^\s\\]+)\s*(?:\\)?\s*$/);
    if (packageMatch) {
        finishPackage();
        current = {
            name: packageMatch[1],
            version: packageMatch[2],
            lines: [line]
        };
        continue;
    }

    if (current) {
        current.lines.push(line);
        continue;
    }

    if (line.trim() === "" || line.trim().startsWith("#") || line.trim().startsWith("--")) {
        continue;
    }

    throw new Error(`${lockPath}: unexpected content before the first pinned package: ${line}`);
}

finishPackage();

if (packages.length === 0) {
    throw new Error(`${lockPath}: no pinned packages found`);
}

const names = new Set();
for (const pkg of packages) {
    if (names.has(pkg.name)) {
        throw new Error(`${lockPath}: duplicate package entry: ${pkg.name}`);
    }
    names.add(pkg.name);
}

const semgrep = packages.filter((pkg) => pkg.name === "semgrep");
if (semgrep.length !== 1) {
    throw new Error(`${lockPath}: expected exactly one semgrep entry, found ${semgrep.length}`);
}

if (expectedSemgrepVersion && semgrep[0].version !== expectedSemgrepVersion) {
    throw new Error(
        `${lockPath}: semgrep is locked to ${semgrep[0].version}, expected ${expectedSemgrepVersion}`
    );
}

console.log(`Validated ${packages.length} hash-locked packages; semgrep==${semgrep[0].version}`);
