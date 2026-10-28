// Produce the VSIX and enforce, locally, exactly what CI enforces:
//   1. build (worker + bundle)
//   2. `vsce package` must emit no WARNING
//   3. `vsce ls` must contain every required file and no source/test junk
//
//   node scripts/package.mjs [--out fs-query-studio.vsix] [--skip-worker]

import { pathToFileURL } from "node:url";
import { build } from "./build.mjs";
import {
  parseArgs,
  runCapture,
  assertVsixContents,
  extDir,
  defaultVsixName,
} from "./lib.mjs";

/**
 * @param {{ out?: string, skipWorker?: boolean, configuration?: string }} [opts]
 * @returns {string} path (relative to editors/vscode) of the produced VSIX
 */
export function packageVsix(opts = {}) {
  const out = opts.out ?? defaultVsixName;

  build({ skipWorker: opts.skipWorker, configuration: opts.configuration });

  const packageOutput = runCapture(
    "npx",
    ["--yes", "@vscode/vsce", "package", "--out", out],
    { cwd: extDir },
  );
  // vsce prints its own advisories as uppercase `WARNING` — match that
  // exactly, not a loose /warning/i that would catch Node's own noise.
  if (/\bWARNING\b/.test(packageOutput)) {
    throw new Error("`vsce package` emitted warnings — treat as an error (CI does).");
  }

  const listing = runCapture("npx", ["--yes", "@vscode/vsce", "ls"], { cwd: extDir });
  assertVsixContents(listing);

  console.log(`\x1b[32m✓\x1b[0m packaged ${out}`);
  return out;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const { flags, values } = parseArgs();
  try {
    packageVsix({
      out: values.out,
      skipWorker: flags.has("skip-worker"),
      configuration: values.configuration,
    });
  } catch (err) {
    console.error(`\x1b[31m✗\x1b[0m ${err.message}`);
    process.exit(1);
  }
}
