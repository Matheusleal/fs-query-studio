// Read-only preflight for building and, especially, publishing the
// extension. Prints a ✓/✗ table. Exits non-zero if any *hard* check fails
// (a hard failure blocks `npm run publish`; `build` / `package` do not care
// about the publish-only checks and never call this).
//
//   node scripts/doctor.mjs

import { readdirSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import {
  runCapture,
  readExtManifest,
  existsSync,
  readFileSync,
  extDir,
} from "./lib.mjs";

/** @typedef {{ name: string, ok: boolean, hard: boolean, detail: string }} CheckResult */

/** @returns {CheckResult[]} */
export function runDoctor() {
  /** @type {CheckResult[]} */
  const results = [];
  const add = (name, hard, ok, detail) => results.push({ name, ok, hard, detail });

  // --- toolchain -----------------------------------------------------------
  try {
    const out = runCapture("dotnet", ["--version"], {}).trim();
    const major = Number(out.split(".")[0]);
    add(".NET SDK ≥ 10", true, Number.isFinite(major) && major >= 10, out || "not found");
  } catch {
    add(".NET SDK ≥ 10", true, false, "`dotnet` not on PATH");
  }

  const nodeMajor = Number(process.versions.node.split(".")[0]);
  add("Node ≥ 20", true, nodeMajor >= 20, process.versions.node);

  try {
    const out = runCapture("npx", ["--yes", "@vscode/vsce", "--version"], {}).trim();
    add("@vscode/vsce resolvable", true, /\d+\.\d+/.test(out), out || "unknown");
  } catch {
    add("@vscode/vsce resolvable", true, false, "`npx @vscode/vsce` failed");
  }

  // --- manifest / repo consistency --------------------------------------
  const manifest = readExtManifest();
  const expectedId = `${manifest.publisher}.${manifest.name}`;

  const testDir = join(extDir, "test", "integration");
  const testFiles = existsSync(testDir)
    ? readdirSync(testDir).filter((f) => f.endsWith(".test.ts"))
    : [];
  const mismatches = [];
  let idsFound = 0;
  for (const f of testFiles) {
    const src = readFileSync(join(testDir, f), "utf8");
    const m = src.match(/EXTENSION_ID\s*=\s*["']([^"']+)["']/);
    if (!m) continue;
    idsFound++;
    if (m[1] !== expectedId) mismatches.push(`${f}: ${m[1]}`);
  }
  add(
    "EXTENSION_ID matches publisher.name in integration tests",
    true,
    idsFound > 0 && mismatches.length === 0,
    mismatches.length
      ? `expected ${expectedId}; ${mismatches.join(", ")}`
      : idsFound === 0
        ? "no EXTENSION_ID constant found — check skipped"
        : `${idsFound} file(s) agree on ${expectedId}`,
  );

  // --- publish-only assets --------------------------------------------------
  const iconRel = manifest.icon ?? "media/icon.png";
  const iconOk = Boolean(manifest.icon) && existsSync(join(extDir, iconRel));
  add(
    'Marketplace icon (package.json "icon" + file present)',
    true,
    iconOk,
    manifest.icon
      ? existsSync(join(extDir, iconRel))
        ? iconRel
        : `"${iconRel}" declared but file missing`
      : 'no "icon" key in package.json (F6 gap — see docs/vscode-extension-release.md)',
  );

  const patSet = Boolean(process.env.VSCE_PAT);
  add(
    "VSCE_PAT set (needed only for `publish`)",
    false,
    patSet,
    patSet ? "set" : "unset — export it before `npm run publish`",
  );

  return results;
}

/** @param {CheckResult[]} results */
export function printReport(results) {
  const width = Math.max(...results.map((r) => r.name.length));
  console.log("");
  for (const r of results) {
    const mark = r.ok ? "\x1b[32m✓\x1b[0m" : r.hard ? "\x1b[31m✗\x1b[0m" : "\x1b[33m!\x1b[0m";
    console.log(`  ${mark}  ${r.name.padEnd(width)}  \x1b[2m${r.detail}\x1b[0m`);
  }
  console.log("");
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const results = runDoctor();
  printReport(results);
  const hardFails = results.filter((r) => !r.ok && r.hard);
  if (hardFails.length) {
    console.error(
      `\x1b[31m${hardFails.length} blocker(s):\x1b[0m ${hardFails.map((r) => r.name).join("; ")}`,
    );
    process.exit(1);
  }
  console.log("\x1b[32m✓\x1b[0m environment OK");
}
