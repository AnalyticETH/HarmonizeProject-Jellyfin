import fs from "node:fs";

const dependabotPath = ".github/dependabot.yml";
const config = fs.readFileSync(dependabotPath, "utf8");

if (!/^version:\s*2\s*$/m.test(config)) {
  throw new Error(`${dependabotPath} must declare Dependabot schema version 2`);
}

const blocks = config
  .split(/\n(?=  - package-ecosystem:\s*)/)
  .filter(block => /^  - package-ecosystem:\s*/m.test(block))
  .map(block => {
    const ecosystem = block.match(/^  - package-ecosystem:\s*"([^"]+)"\s*$/m)?.[1];
    const directory = block.match(/^    directory:\s*"([^"]+)"\s*$/m)?.[1];
    if (!ecosystem || !directory) {
      throw new Error(`${dependabotPath} contains an update block without an ecosystem and directory`);
    }
    return { ecosystem, directory, block };
  });

const nugetDirectories = blocks
  .filter(block => block.ecosystem === "nuget")
  .map(block => block.directory);

function findProjectDirectories(root) {
  const projectDirectories = new Set();
  const entries = fs.readdirSync(root, { withFileTypes: true });
  for (const entry of entries) {
    if (entry.name === ".git" || entry.name === "bin" || entry.name === "obj" || entry.name === "node_modules") {
      continue;
    }
    const path = `${root}/${entry.name}`;
    if (entry.isDirectory()) {
      for (const directory of findProjectDirectories(path)) {
        projectDirectories.add(directory);
      }
    } else if (entry.isFile() && entry.name.endsWith(".csproj")) {
      const directory = root === "." ? "/" : `/${root.replace(/^\.\//, "")}`;
      projectDirectories.add(directory);
    }
  }
  return projectDirectories;
}

const expectedNugetDirectories = [...findProjectDirectories(".")].sort();
if (expectedNugetDirectories.length === 0) {
  throw new Error("No .csproj files were found while checking Dependabot NuGet coverage");
}

if (nugetDirectories.includes("/")) {
  throw new Error(`${dependabotPath} must not use the repository root for NuGet updates; no root .csproj exists`);
}

const actualNugetSet = new Set(nugetDirectories);
if (actualNugetSet.size !== nugetDirectories.length) {
  throw new Error(`${dependabotPath} contains duplicate NuGet directories: ${nugetDirectories.join(", ")}`);
}
const missingNugetDirectories = expectedNugetDirectories.filter(directory => !actualNugetSet.has(directory));
const unexpectedNugetDirectories = nugetDirectories.filter(directory => !expectedNugetDirectories.includes(directory));
if (missingNugetDirectories.length > 0 || unexpectedNugetDirectories.length > 0) {
  throw new Error(
    `${dependabotPath} NuGet coverage mismatch; missing=${missingNugetDirectories.join(", ") || "none"}, `
      + `unexpected=${unexpectedNugetDirectories.join(", ") || "none"}`
  );
}

for (const directory of expectedNugetDirectories) {
  const projectPath = directory === "/" ? "." : directory.slice(1);
  if (!fs.existsSync(projectPath) || !fs.statSync(projectPath).isDirectory()) {
    throw new Error(`${dependabotPath} references missing NuGet project directory: ${directory}`);
  }
}

const abiPinnedPackages = ["System.Text.Json", "System.Text.Encodings.Web"];
for (const block of blocks.filter(candidate => candidate.ecosystem === "nuget")) {
  for (const dependency of abiPinnedPackages) {
    const dependencyPattern = new RegExp(
      `dependency-name:\\s*"${dependency.replaceAll(".", "\\.")}"\\s*\\n\\s*update-types:\\s*\\["version-update:semver-major"\\]`,
    );
    if (!dependencyPattern.test(block.block)) {
      throw new Error(
        `${dependabotPath} must ignore major ${dependency} updates in the ${block.directory} NuGet block to preserve the .NET 8 host ABI`,
      );
    }
  }
}

const requiredNonNugetUpdates = [
  ["github-actions", "/"],
  ["pip", "/.github/semgrep"],
];
for (const [ecosystem, directory] of requiredNonNugetUpdates) {
  if (!blocks.some(block => block.ecosystem === ecosystem && block.directory === directory)) {
    throw new Error(`${dependabotPath} is missing the ${ecosystem} update block for ${directory}`);
  }
}

console.log("Dependabot coverage contract passed");
