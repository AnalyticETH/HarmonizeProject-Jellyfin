import fs from "node:fs";
import path from "node:path";

const readme = fs.readFileSync("README.md", "utf8");
const lines = readme.replace(/\r?\n$/, "").split(/\r?\n/).length;
const words = readme.trim().split(/\s+/).filter(Boolean).length;
if (lines > 150 || words > 900) {
  throw new Error(`README.md must stay within 150 lines and 900 words; found ${lines} lines and ${words} words`);
}

function prose(text) {
  let fence = "";
  let previousBlank = true;
  let indentedCode = false;
  const listIndents = [];
  return text.split(/\r?\n/).map(line => {
    if (!line.trim()) {
      previousBlank = true;
      return "";
    }
    const indent = line.match(/^[ \t]*/)[0].replace(/\t/g, "    ").length;
    while (listIndents.length && indent < listIndents.at(-1)) listIndents.pop();
    const codeIndent = (listIndents.at(-1) || 0) + 4;
    indentedCode = !fence && indent >= codeIndent && (previousBlank || indentedCode);
    previousBlank = false;
    if (indentedCode) return "";
    const list = line.match(/^[ \t]*(?:[-+*]|\d+[.)])[ \t]+/);
    if (list && !fence) listIndents.push(list[0].replace(/\t/g, "    ").length);
    const marker = line.match(/^\s*(`{3,}|~{3,})/);
    if (marker) {
      if (!fence) fence = marker[1];
      else if (marker[1][0] === fence[0] && marker[1].length >= fence.length) fence = "";
      return "";
    }
    return fence ? "" : line;
  }).join("\n");
}

function anchors(text) {
  const ids = new Set();
  for (const match of prose(text).matchAll(/^#{1,6}\s+(.+?)\s*#*$/gm)) {
    const slug = match[1].trim().toLowerCase()
      .replace(/[^\p{L}\p{M}\p{N}_\-\s]/gu, "").replace(/\s/g, "-");
    let id = slug;
    for (let suffix = 1; ids.has(id); suffix += 1) id = `${slug}-${suffix}`;
    ids.add(id);
  }
  return ids;
}

function linkTargets(text) {
  const targets = [];
  const references = new Map();
  const label = String.raw`\[((?:\\.|[^\]\\])*)\]`;
  const destination = String.raw`(?:<((?:\\.|[^<>\\\n])*)>|((?:\\.|[^\s()<>\\]|\((?:\\.|[^()\\])*\))+))`;
  const title = String.raw`(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|\((?:\\.|[^)\\])*\))`;
  const unescape = value => value.replace(/\\([!-/:-@[-`{-~])/g, "$1");
  const referenceKey = value => unescape(value).trim().replace(/\s+/g, " ").toLowerCase();

  // Mask code spans, then consume definitions and inline links before resolving references.
  let remaining = text.split(/(\n[ \t]*\n)/)
    .map(block => block.replace(/(?<![\\`])(`+)(?!`)[\s\S]*?(?<!`)\1(?!`)/g, " "))
    .join("");
  remaining = remaining.replace(new RegExp(
    `^ {0,3}${label}:[ \\t]*(?:\\n[ \\t]*)?${destination}(?:[ \\t]+${title}|[ \\t]*\\n[ \\t]*${title})?[ \\t]*$`, "gm",
  ), (_, name, angled, bare) => {
    const key = referenceKey(name);
    if (key && !references.has(key)) references.set(key, unescape(angled ?? bare));
    return "";
  });
  remaining = remaining.replace(new RegExp(
    `(?<!\\\\)(?:\\\\\\\\)*${label}\\(\\s*${destination}(?:\\s+${title})?\\s*\\)`, "g",
  ), (_, name, angled, bare) => {
    targets.push(unescape(angled ?? bare));
    return "";
  });
  for (const match of remaining.matchAll(new RegExp(
    `(?<!\\\\)(?:\\\\\\\\)*${label}(?:\\[((?:\\\\.|[^\\]\\\\]){0,999})\\])?`, "g",
  ))) {
    const target = references.get(referenceKey(match[2] || match[1]));
    if (target !== undefined) targets.push(target);
  }
  return targets;
}

const documentationFiles = [
  "README.md", "docs/CONFIGURATION.md", "docs/API.md", "docs/HUE_STREAM_PROTOCOL.md", "CONTRIBUTING.md",
  "SUPPORT.md", "CHANGELOG.md", "RELEASE_READINESS.md", "SECURITY.md",
  "SELF_HOSTED_RUNNERS.md", "CODE_OF_CONDUCT.md",
];
for (const file of documentationFiles) {
  const text = prose(fs.readFileSync(file, "utf8"));
  for (const target of linkTargets(text)) {
    if (/^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test(target)) continue;
    const [filename, fragment] = target.split("#");
    const resolved = path.resolve(path.dirname(file), decodeURIComponent(filename || path.basename(file)));
    const relative = path.relative(process.cwd(), resolved);
    if (relative === ".." || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
      throw new Error(`${file} has a relative link outside the checkout: ${target}`);
    }
    if (!fs.existsSync(resolved)) {
      throw new Error(`${file} has a broken relative link: ${target}`);
    }
    if (fragment && resolved.endsWith(".md") && !anchors(fs.readFileSync(resolved, "utf8")).has(decodeURIComponent(fragment))) {
      throw new Error(`${file} has a broken heading link: ${target}`);
    }
  }
}

console.log(`Documentation layout and local links passed (${lines} README lines, ${words} words)`);
