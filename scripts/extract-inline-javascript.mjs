import fs from "node:fs";

function usage() {
    console.error("Usage: node scripts/extract-inline-javascript.mjs <html-file> <javascript-output>");
    process.exitCode = 2;
}

const [, , inputPath, outputPath] = process.argv;
if (!inputPath || !outputPath) {
    usage();
} else {
    const html = fs.readFileSync(inputPath, "utf8");
    const openingTags = [...html.matchAll(/<script\b[^>]*>/gi)];
    const closingTags = [...html.matchAll(/<\/script\s*>/gi)];
    if (openingTags.length === 0) {
        throw new Error(`${inputPath} does not contain a script element`);
    }
    if (openingTags.length !== closingTags.length) {
        throw new Error(
            `${inputPath} has unbalanced script elements: ${openingTags.length} opening, ${closingTags.length} closing`
        );
    }

    const blocks = [];
    const scriptPattern = /<script\b([^>]*)>([\s\S]*?)<\/script\s*>/gi;
    for (const match of html.matchAll(scriptPattern)) {
        const attributes = match[1] || "";
        if (/\bsrc\s*=/i.test(attributes)) {
            throw new Error(`${inputPath} contains an external script reference; inline extraction is fail-closed`);
        }
        const body = match[2].trim();
        if (!body) {
            throw new Error(`${inputPath} contains an empty inline script element`);
        }
        blocks.push(body);
    }

    if (blocks.length === 0) {
        throw new Error(`${inputPath} does not contain an inline script element`);
    }

    const extracted = blocks
        .map((body, index) => `// Source: ${inputPath} inline <script> block ${index + 1}\n${body}`)
        .join("\n\n");
    fs.writeFileSync(outputPath, `${extracted}\n`, "utf8");
    console.log(`Extracted ${blocks.length} inline JavaScript block(s) from ${inputPath}`);
}
